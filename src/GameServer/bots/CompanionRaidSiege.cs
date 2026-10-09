using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS.Keeps;

namespace DOL.GS
{
    /// <summary>
    /// Goal 11 (owner 2026-10-07): when the player attacks an enemy keep, or defends a friendly keep under attack, a
    /// /raid 40 or /raid 80 stops trailing the player and fights the siege on its own until it plays out. The raid
    /// splits into squads of up to eight (by raid slot; healers stay with their squad) with jobs: a bodyguard squad
    /// stays with the player; ram crews ram the outer gate, then the inner keep door; artillery crews set up a catapult
    /// or trebuchet on its firing ring; a ballista crew counters enemy siege; a wall-clearing squad shoots archers and
    /// enemies off the walls; assault squads hold the gate approach and storm the lord once the doors fall. Defenders
    /// hold the doors from inside with a ballista and catapult in the courtyard. Each crew hops onto any idle friendly
    /// engine first (the player's dropped ram included) and otherwise gets synthetic siege equipment placed at a proper
    /// spot (SiegePlacement). Squads help the player only within a short range of their post, and the leash recall is off
    /// until the event ends. Log tags RAID_BOTS_DISPERSED, RAID_SIEGE_PLACED, RAID_SIEGE_FIRING, RAID_BOTS_REGROUPED.
    /// </summary>
    public static class CompanionRaidSiege
    {
        public enum Mode { Assault, Defense }
        public enum Job { Bodyguard, Ram, Catapult, Trebuchet, Ballista, WallClear, Assault, Defend }

        public const int SquadSize = 8;
        public const int AssistRadius = 1500;
        private const long RequestFreshMilliseconds = 30_000;
        private const long QuietEndMilliseconds = 5 * 60_000;
        private const long AwayEndMilliseconds = 90_000;
        private const long MaxMilliseconds = 90 * 60_000;

        private static readonly DOL.Logging.Logger Log = DOL.Logging.LoggerManager.Create(typeof(CompanionRaidSiege));

        private sealed class Squad
        {
            public Job Job;
            public Vector3? Post;
            public Vector2 Facing = new(1, 0); // toward the keep, for the squad's block
            public GameLiving PostTarget;
            public long NextPost;
            // The navmesh-validated engine spot (ChooseSiegePosition) for SpotTarget.
            public Vector3? Spot;
            public GameLiving SpotTarget;
            public long SpotUntil;
        }

        private sealed class Session
        {
            public GamePlayer Owner;
            public AbstractGameKeep Keep;
            public Mode Mode;
            public long Started, LastPressure, AwaySince;
            public readonly Dictionary<int, Squad> Squads = new();
            public readonly List<GameSiegeWeapon> Synthetic = new();
            public readonly Dictionary<GameBot, long> NextAction = new();
            public readonly HashSet<GameBot> Operating = new();
            public GameRelic FreeRelic;
            public long FreeRelicSince;
            public long NextStatusLog;
            public readonly Dictionary<Squad, Vector3> Reserved = new();
        }

        private static readonly ConcurrentDictionary<GamePlayer, Session> Sessions = new();
        private static readonly ConcurrentDictionary<GamePlayer, (AbstractGameKeep Keep, long Tick)> AssaultRequests = new();
        private static readonly ConcurrentDictionary<AbstractGameKeep, long> FriendlyKeepsUnderAttack = new();
        // Melee raid bots drop a target they cannot walk to (a wall archer) and leave it alone for a while.
        private static readonly ConcurrentDictionary<(GameBot Bot, GameLiving Target), long> Unreachable = new();
        private static readonly ConcurrentDictionary<GameBot, (GameLiving Target, int Failures, long Next)> ReachWatch = new();
        private const long UnreachableCheckMilliseconds = 4_000;
        private const long UnreachableIgnoreMilliseconds = 2 * 60_000;

        public static int SquadOf(int groupIndex) => Math.Max(0, groupIndex - 1) / SquadSize;

        /// <summary>Squad jobs by raid size: a /raid 40 has five squads, a /raid 80 ten.</summary>
        public static Job JobFor(Mode mode, int squad, int squads)
        {
            if (squad == 0) return Job.Bodyguard;
            if (mode == Mode.Defense)
                return squad switch { 1 => Job.Ballista, 2 => Job.Catapult, _ => Job.Defend };
            if (squads >= 8)
                return squad switch
                {
                    1 or 4 => Job.Ram, 2 => Job.Catapult, 3 => Job.WallClear, 5 => Job.Trebuchet, 6 => Job.Ballista, _ => Job.Assault,
                };
            return squad switch { 1 => Job.Ram, 2 => Job.Catapult, 3 => Job.WallClear, _ => Job.Assault };
        }

        public static BotSiegeKind? EngineFor(Job job) => job switch
        {
            Job.Ram => BotSiegeKind.Ram, Job.Catapult => BotSiegeKind.Catapult, Job.Trebuchet => BotSiegeKind.Trebuchet,
            Job.Ballista => BotSiegeKind.Ballista, _ => null,
        };

        // ---- Triggers (cheap; called from damage callbacks) ----

        /// <summary>The player (or one of their raid) hit an enemy keep's guards or doors.</summary>
        public static void RequestAssault(GamePlayer player, AbstractGameKeep keep)
        {
            if (player?.Group?.IsCompanionRaid == true && keep != null)
                AssaultRequests[player] = (keep, GameLoop.GameLoopTime);
        }

        /// <summary>Any enemy damage to a keep; raids of that keep's realm nearby may rally to defend it.</summary>
        public static void ObserveKeepUnderAttack(AbstractGameKeep keep)
        {
            if (keep != null && keep.Realm != eRealm.None) FriendlyKeepsUnderAttack[keep] = GameLoop.GameLoopTime;
        }

        // ---- Queries used by the follow, engagement and recall code ----

        private static Session SessionOf(GameBot bot) =>
            CompanionRaid.IsMember(bot) && bot.Owner != null && Sessions.TryGetValue(bot.Owner, out var s) ? s : null;

        public static bool Active(GamePlayer owner) => owner != null && Sessions.ContainsKey(owner);

        /// <summary>A raid bot on a siege job is not recalled to the player until the event ends.</summary>
        public static bool Holds(GameBot bot) => SessionOf(bot) is { } s && JobOf(s, bot) != Job.Bodyguard;

        /// <summary>Squads help the player only near their own post; the bodyguard squad always.</summary>
        public static bool MayAssist(GameBot bot, GameLiving focus)
        {
            Session s = SessionOf(bot);
            if (s == null || focus == null) return true;
            Job job = JobOf(s, bot);
            if (job == Job.Bodyguard) return true;
            if (AutonomousSiegeOwnership.All(bot).Length > 0) return false;
            Vector3? post = PostOf(s, bot);
            return post == null || Vector2.Distance(new(post.Value.X, post.Value.Y), new(focus.X, focus.Y)) <= AssistRadius;
        }

        /// <summary>Where a raid bot stands instead of its formation slot (null: follow the player).</summary>
        public static Vector3? FormationOverride(GameBot bot)
        {
            Session s = SessionOf(bot);
            if (s == null || JobOf(s, bot) == Job.Bodyguard) return null;
            Vector3? post = PostOf(s, bot);
            if (post == null) return null;
            // A squad stands as a small block facing the keep: two rows of four, 90 apart.
            int slot = Math.Max(0, bot.GroupIndex - 1) % SquadSize;
            Vector2 facing;
            lock (s) facing = s.Squads.TryGetValue(SquadOf(bot.GroupIndex), out var q) ? q.Facing : new(1, 0);
            Vector2 right = new(-facing.Y, facing.X);
            Vector2 offset = right * ((slot % 4 - 1.5f) * 90) - facing * (slot / 4 * 90);
            return post.Value + new Vector3(offset.X, offset.Y, 0);
        }

        private static Job JobOf(Session s, GameBot bot)
        {
            lock (s) return s.Squads.TryGetValue(SquadOf(bot.GroupIndex), out var q) ? q.Job : Job.Bodyguard;
        }

        private static Vector3? PostOf(Session s, GameBot bot)
        {
            lock (s) return s.Squads.TryGetValue(SquadOf(bot.GroupIndex), out var q) ? q.Post : null;
        }

        // ---- Per-bot think (BotBrain.Think) ----

        /// <summary>True when this raid bot spent its turn on its siege job (operating or carrying an engine).</summary>
        public static bool Think(BotBrain brain)
        {
            GameBot bot = brain?.BotBody;
            if (bot == null || !CompanionRaid.IsMember(bot) || bot.Owner is not GamePlayer owner || !bot.IsAlive) return false;
            long now = GameLoop.GameLoopTime;
            if (AutonomousWorldBotController.IsBattleground(bot)) BreakUnreachableTarget(brain, bot, now);
            if (bot.TargetObject is GuardLord { ShieldedFromBots: true } shielded)
            {
                // The lord is off limits while the keep door stands (GuardLord.ShieldedFromBots).
                brain.RemoveFromAggroList(shielded);
                bot.StopAttack();
                bot.StopCurrentSpellcast();
                bot.TargetObject = null;
            }
            Session s = Sessions.GetValueOrDefault(owner) ?? TryBegin(owner, now);
            if (s == null) return false;
            if (Ended(s, now, out string reason)) { End(s, reason); return false; }

            int squadIndex = SquadOf(bot.GroupIndex);
            Squad squad;
            lock (s)
            {
                if (!s.Squads.TryGetValue(squadIndex, out squad)) return false;
                // Between turns an operator keeps its own path (to its engine spot), not the follow code's.
                if (s.NextAction.GetValueOrDefault(bot) > now) return s.Operating.Contains(bot) && !MeleeThreat(bot);
                s.NextAction[bot] = now + 2_000;
            }
            LogStatus(s, owner, now);
            if (squad.Job == Job.Bodyguard) return false;
            if (TryTakeRelic(s, bot, now)) return true;

            AbstractGameKeep keep = s.Keep;
            bool attacking = s.Mode == Mode.Assault;
            GameKeepDoor door = attacking ? AutonomousWorldBotController.SiegeDoor(bot, keep) : null;
            GameNPC[] nearby = bot.GetNPCsInRadius(5000).ToArray();
            GameSiegeWeapon[] engines = nearby.OfType<GameSiegeWeapon>().Where(w => w.IsAlive && w.ObjectState == GameObject.eObjectState.Active).ToArray();
            UpdatePost(s, squad, bot, door, engines, now);

            BotSiegeKind? kind = EngineFor(squad.Job);
            bool operatorBot = kind != null && IsSquadLeader(owner, bot, squadIndex);
            // Wall archers keep everyone near the gate in combat; only an enemy in melee reach at the crew's own height
            // stops the crew (same rule as gamebot siege crews).
            bool operating = operatorBot && !MeleeThreat(bot) && Operate(s, squad, bot, kind.Value, door, nearby, engines, now);
            lock (s) { if (operating) s.Operating.Add(bot); else s.Operating.Remove(bot); }
            if (operating) return true;

            // Everyone else works their job through the normal combat AI.
            if (brain.HasAggro || bot.InCombat) return false;
            GameLiving target = squad.Job switch
            {
                Job.WallClear => WallTarget(bot, keep, nearby),
                Job.Assault => AssaultTarget(bot, keep, door, nearby),
                Job.Defend or Job.Ballista or Job.Catapult when !attacking => Attacker(bot, keep, nearby),
                _ => Attacker(bot, keep, nearby, 600),
            };
            if (target != null) brain.AssistPlayerAttack(target);
            return false;
        }

        private static Session TryBegin(GamePlayer owner, long now)
        {
            if (owner.Group?.IsCompanionRaid != true || owner.Group.MemberCount <= SquadSize || !owner.IsAlive) return null;
            Mode mode;
            AbstractGameKeep keep = null;
            if (AssaultRequests.TryGetValue(owner, out var request) && now - request.Tick <= RequestFreshMilliseconds &&
                request.Keep.Realm != owner.Realm && owner.CurrentRegionID == request.Keep.Region &&
                owner.IsWithinRadius(new Point3D(request.Keep.X, request.Keep.Y, request.Keep.Z), 4000))
            {
                mode = Mode.Assault;
                keep = request.Keep;
            }
            else
            {
                mode = Mode.Defense;
                foreach (var pair in FriendlyKeepsUnderAttack)
                {
                    if (now - pair.Value > RequestFreshMilliseconds) { FriendlyKeepsUnderAttack.TryRemove(pair.Key, out _); continue; }
                    if (pair.Key.Realm == owner.Realm && owner.CurrentRegionID == pair.Key.Region &&
                        owner.IsWithinRadius(new Point3D(pair.Key.X, pair.Key.Y, pair.Key.Z), 3500))
                    { keep = pair.Key; break; }
                }
            }
            if (keep == null || !AutonomousRvrKeepPolicy.IsSiegeObjective(keep)) return null;
            var session = new Session { Owner = owner, Keep = keep, Mode = mode, Started = now, LastPressure = now };
            int squads = (owner.Group.MemberCount - 2) / SquadSize + 1;
            for (int i = 0; i < squads; i++) session.Squads[i] = new Squad { Job = JobFor(mode, i, squads) };
            if (!Sessions.TryAdd(owner, session)) return Sessions.GetValueOrDefault(owner);
            AssaultRequests.TryRemove(owner, out _);
            Log.Info($"RAID_BOTS_DISPERSED player={owner.Name} keep=\"{keep.Name}\" mode={mode} raid={owner.Group.MemberCount} " +
                     $"squads={string.Join(",", session.Squads.Select(p => $"{p.Key}:{p.Value.Job}"))}");
            owner.Out.SendMessage(mode == Mode.Assault
                    ? $"Your raid breaks into squads for the assault on {keep.Name}: rams, siege engines, wall-clearers and storming parties."
                    : $"Your raid spreads out to defend {keep.Name}: door holders, siege crews and a bodyguard.",
                PacketHandler.eChatType.CT_Important, PacketHandler.eChatLoc.CL_SystemWindow);
            return session;
        }

        private static bool Ended(Session s, long now, out string reason)
        {
            reason = null;
            GamePlayer owner = s.Owner;
            AbstractGameKeep keep = s.Keep;
            if (owner.ObjectState != GameObject.eObjectState.Active || owner.Group?.IsCompanionRaid != true) reason = "raid closed";
            else if (s.Mode == Mode.Assault && keep.Realm == owner.Realm) reason = "keep taken";
            else if (s.Mode == Mode.Defense && keep.Realm != owner.Realm) reason = "keep lost";
            // Relic rule: the player carries a relic, so the whole raid closes in and escorts them.
            else if (GameRelic.IsPlayerCarryingRelic(owner)) reason = "escorting your relic";
            else if (owner.Group.GetMembersInTheGroup().OfType<GameBot>().Any(GameRelic.IsPlayerCarryingRelic)) reason = "escorting the raid's relic";
            else if (now - s.Started > MaxMilliseconds) reason = "time limit";
            else
            {
                bool near = owner.CurrentRegionID == keep.Region && owner.IsWithinRadius(new Point3D(keep.X, keep.Y, keep.Z), 9000);
                lock (s) { if (near) s.AwaySince = 0; else if (s.AwaySince == 0) s.AwaySince = now; }
                if (s.AwaySince != 0 && now - s.AwaySince > AwayEndMilliseconds) reason = "player left the keep";
                long pressure = s.Mode == Mode.Defense ? FriendlyKeepsUnderAttack.GetValueOrDefault(keep)
                    : AutonomousRvrEventLayer.LastKeepPressure(keep);
                if (pressure > s.LastPressure) s.LastPressure = pressure;
                if (reason == null && now - s.LastPressure > QuietEndMilliseconds) reason = "the fighting is over";
            }
            return reason != null;
        }

        private static void End(Session s, string reason)
        {
            if (!Sessions.TryRemove(s.Owner, out _)) return;
            lock (s)
            {
                foreach (GameSiegeWeapon weapon in s.Synthetic)
                {
                    weapon.ReleaseControl();
                    if (weapon.ObjectState == GameObject.eObjectState.Active) weapon.Delete();
                }
                s.Synthetic.Clear();
            }
            foreach (GameBot bot in s.Owner.Group?.GetMembersInTheGroup().OfType<GameBot>() ?? [])
                foreach (GameSiegeWeapon weapon in AutonomousSiegeOwnership.All(bot)) weapon.ReleaseControl();
            Log.Info($"RAID_BOTS_REGROUPED player={s.Owner.Name} keep=\"{s.Keep.Name}\" mode={s.Mode} reason=\"{reason}\" " +
                     $"minutes={(GameLoop.GameLoopTime - s.Started) / 60_000}");
            if (s.Owner.ObjectState == GameObject.eObjectState.Active)
                s.Owner.Out.SendMessage($"Your raid regroups on you ({reason}).", PacketHandler.eChatType.CT_Important, PacketHandler.eChatLoc.CL_SystemWindow);
        }

        public static void Close(GamePlayer owner)
        {
            if (owner != null && Sessions.TryGetValue(owner, out var s)) End(s, "raid closed");
        }

        private static bool IsSquadLeader(GamePlayer owner, GameBot bot, int squad) =>
            owner.Group.GetMembersInTheGroup().OfType<GameBot>()
                .Where(member => member.IsAlive && SquadOf(member.GroupIndex) == squad)
                .OrderBy(member => AutonomousSiegeJobs.CanOperate((eCharacterClass)(member.CharacterClass?.ID ?? 0)) ? 0 : 1)
                .ThenBy(member => member.GroupIndex).FirstOrDefault() == bot;

        /// <summary>Every 30 s: each squad's job, leader, whether it is fighting, distance to its post and its engine.</summary>
        private static void LogStatus(Session s, GamePlayer owner, long now)
        {
            lock (s)
            {
                if (now < s.NextStatusLog) return;
                s.NextStatusLog = now + 30_000;
            }
            var parts = new List<string>();
            foreach (var pair in s.Squads.OrderBy(p => p.Key))
            {
                if (pair.Value.Job == Job.Bodyguard) continue;
                GameBot leader = owner.Group?.GetMembersInTheGroup().OfType<GameBot>().Where(m => m.IsAlive && SquadOf(m.GroupIndex) == pair.Key)
                    .OrderBy(m => AutonomousSiegeJobs.CanOperate((eCharacterClass)(m.CharacterClass?.ID ?? 0)) ? 0 : 1).ThenBy(m => m.GroupIndex).FirstOrDefault();
                if (leader == null) { parts.Add($"{pair.Key}:{pair.Value.Job}:none"); continue; }
                Vector3? post = pair.Value.Post;
                int toPost = post == null ? -1 : (int)Vector3.Distance(new(leader.X, leader.Y, leader.Z), post.Value);
                GameSiegeWeapon engine = AutonomousSiegeOwnership.All(leader).FirstOrDefault();
                parts.Add($"{pair.Key}:{pair.Value.Job}:{leader.Name}:{(leader.InCombat ? "fighting" : "free")}" +
                          $":melee={MeleeThreat(leader)}:post={toPost}:engine={(engine == null ? "none" : $"{BotSiegeRuntime.Kind(engine)}/{engine.CurrentState}")}");
            }
            Log.Info($"RAID_SQUAD_STATUS player={owner.Name} keep=\"{s.Keep.Name}\" {string.Join(" ", parts)}");
        }

        private static bool MeleeThreat(GameBot bot) =>
            bot.GetNPCsInRadius(450).Any(n => n.IsAlive && n.TargetObject == bot && n.IsAttacking &&
                AutonomousWorldBotController.IsSiegeOperatorThreat(Vector2.Distance(new(bot.X, bot.Y), new(n.X, n.Y)), n.Z - bot.Z)) ||
            bot.GetPlayersInRadius(450).Any(p => p.IsAttacking && p.TargetObject == bot &&
                AutonomousWorldBotController.IsSiegeOperatorThreat(Vector2.Distance(new(bot.X, bot.Y), new(p.X, p.Y)), p.Z - bot.Z));

        // ---- Relic rule ----

        /// <summary>A takeable relic at the keep (dropped, or on an enemy shrine the raid can lift) that the player has
        /// left for 20 seconds: the nearest raid bot grabs it, and the raid then escorts it (Ended).</summary>
        private static bool TryTakeRelic(Session s, GameBot bot, long now)
        {
            if (s.Mode != Mode.Assault || !s.Keep.IsRelic) return false;
            GameRelic relic = RelicMgr.GetRelics().FirstOrDefault(r => r.ObjectState == GameObject.eObjectState.Active &&
                r.CurrentRegionID == bot.CurrentRegionID && r.CurrentCarrier == null &&
                Vector2.Distance(new(r.X, r.Y), new(s.Keep.X, s.Keep.Y)) <= 2500 &&
                (!r.IsMounted || r.Realm != bot.Realm && RelicMgr.CanPickupRelicFromShrine(bot, r)));
            lock (s)
            {
                if (relic != s.FreeRelic) { s.FreeRelic = relic; s.FreeRelicSince = now; }
                if (relic == null || now - s.FreeRelicSince < 20_000) return false;
            }
            GameBot nearest = s.Owner.Group.GetMembersInTheGroup().OfType<GameBot>()
                .Where(member => member.IsAlive && member.CurrentRegionID == relic.CurrentRegionID)
                .OrderBy(member => member.GetDistanceTo(relic)).FirstOrDefault();
            if (nearest != bot) return false;
            if (!bot.IsWithinRadius(relic, WorldMgr.INTERACT_DISTANCE))
            { bot.PathTo(new Vector3(relic.X, relic.Y, relic.Z), bot.MaxSpeed); return true; }
            bot.StopMovingOnPath(); bot.StopMoving();
            if (relic.TryPickup(bot))
                Log.Info($"RAID_RELIC_PICKUP player={s.Owner.Name} bot={bot.Name} relic=\"{relic.Name}\" keep=\"{s.Keep.Name}\"");
            return true;
        }

        // ---- Posts ----

        private static void UpdatePost(Session s, Squad squad, GameBot bot, GameKeepDoor door, GameSiegeWeapon[] engines, long now)
        {
            AbstractGameKeep keep = s.Keep;
            GameLiving anchor = door ?? (GameLiving)keep.Doors.Values.Where(d => d.IsAlive)
                .OrderBy(d => AutonomousWorldBotController.PosternLike(keep, d) ? 1 : 0).ThenBy(d => d.DoorIndex)
                .ThenByDescending(d => Vector2.DistanceSquared(new(d.X, d.Y), new(keep.X, keep.Y))).FirstOrDefault();
            lock (s)
            {
                if (squad.PostTarget == anchor && squad.Post != null && now < squad.NextPost) return;
                squad.PostTarget = anchor;
                squad.NextPost = now + 30_000;
            }
            Vector3? post = null;
            var nav = PathfindingProvider.Instance;
            Vector2 centre = new(keep.X, keep.Y);
            if (s.Mode == Mode.Assault && door == null)
            {
                // Every door is down: storm the lord.
                GuardLord lord = keep.Guards.Values.OfType<GuardLord>().FirstOrDefault(l => l.IsAlive);
                if (lord != null) post = new(lord.X, lord.Y, lord.Z);
            }
            else if (anchor != null)
            {
                Vector2 outward = SiegePlacement.OutwardNormal(new(anchor.X, anchor.Y), anchor.Heading, centre,
                    AutonomousWorldBotController.InnerDoorApproach(keep, anchor));
                float side = s.Mode == Mode.Assault ? 1 : -1;
                // Owner 2026-10-07: posts all on one line out from the gate made the raid a giant line. Squads fan out
                // around the gate instead: assault squads at +-35/+-60 degrees, wall-clearers off to the sides where
                // they see the walls, artillery on opposite flanks.
                int index = Math.Max(0, Array.IndexOf(s.Squads.Where(p => p.Value.Job == squad.Job).OrderBy(p => p.Key)
                    .Select(p => p.Value).ToArray(), squad));
                float[] fan = { -35, 35, -60, 60, 0, -15, 15 };
                (float distance, float degrees) = squad.Job switch
                {
                    Job.Ram => (420f, index == 0 ? -12f : 12f),
                    Job.Assault => (650f, fan[index % fan.Length]),
                    Job.WallClear => (1100f, index % 2 == 0 ? -50f : 50f),
                    Job.Catapult => (1600f, -25f),
                    Job.Trebuchet => (2400f, 25f),
                    Job.Ballista => (s.Mode == Mode.Assault ? 1800f : 400f, 40f),
                    _ => (s.Mode == Mode.Assault ? 250f : 450f, fan[index % fan.Length]),
                };
                double radians = degrees * Math.PI / 180;
                Vector2 dir = new((float)(outward.X * Math.Cos(radians) - outward.Y * Math.Sin(radians)),
                                  (float)(outward.X * Math.Sin(radians) + outward.Y * Math.Cos(radians)));
                Vector2 spot = new Vector2(anchor.X, anchor.Y) + dir * side * distance;
                lock (s) squad.Facing = Vector2.Normalize(new Vector2(anchor.X, anchor.Y) - spot);
                Vector3 raw = new(spot.X, spot.Y, anchor.Z);
                post = nav.IsAvailable && nav.HasNavmesh(bot.CurrentZone)
                    ? nav.GetClosestPoint(bot.CurrentZone, raw, 256, 256, 512, nav.DefaultFilters) ?? raw : raw;
            }
            lock (s) squad.Post = post ?? squad.Post;
        }

        // ---- Engines ----

        private static bool Operate(Session s, Squad squad, GameBot bot, BotSiegeKind kind, GameKeepDoor door,
            GameNPC[] nearby, GameSiegeWeapon[] engines, long now)
        {
            AbstractGameKeep keep = s.Keep;
            GameLiving target = AutonomousWorldBotController.SelectSiegeTarget(bot, keep, door, kind, nearby, engines);
            GameSiegeWeapon owned = AutonomousSiegeOwnership.All(bot).FirstOrDefault();
            if (target == null)
            {
                // A ballista with no enemy siege about: release it and help clear the walls.
                owned?.ReleaseControl();
                return false;
            }
            if (owned != null && !AutonomousWorldBotController.InSiegeRange(owned, target))
            {
                // Run 24: crews abandoned working engines whenever their chosen target moved or died. Re-aim at anything
                // in reach first; only a ram (one door, cannot roll) or an engine with nothing in reach is left behind.
                GameLiving reachable = kind == BotSiegeKind.Ram ? null : InReach(bot, owned, kind, keep, door, nearby, engines);
                if (reachable != null) target = reachable;
                else { owned.ReleaseControl(); owned = null; }
            }
            if (owned == null)
            {
                // Hop onto any idle friendly engine in reach of the target first (the player's dropped ram included).
                GameSiegeWeapon idle = engines.Where(w => w.Realm == bot.Realm && w.Owner == null && w.Health > w.DecayedHp &&
                        BotSiegeRuntime.Kind(w) == kind && AutonomousWorldBotController.InSiegeRange(w, target))
                    .OrderBy(bot.GetDistanceTo).FirstOrDefault();
                if (idle != null)
                {
                    if (!bot.IsWithinRadius(idle, Math.Max(32, idle.SIEGE_WEAPON_CONTROLE_DISTANCE - 20)))
                    { bot.PathTo(new Vector3(idle.X, idle.Y, idle.Z), bot.MaxSpeed); return true; }
                    bot.StopMovingOnPath(); bot.StopMoving();
                    if (idle.TryTakeControl(bot))
                    {
                        owned = idle;
                        Log.Info($"RAID_SIEGE_PLACED player={s.Owner.Name} bot={bot.Name} kind={BotSiegeRuntime.Kind(idle)} source=idle_engine target=\"{target.Name}\"");
                    }
                }
            }
            if (owned == null)
            {
                int ours = engines.Count(w => w.Realm == bot.Realm && w.Health > w.DecayedHp && BotSiegeRuntime.Kind(w) == kind &&
                    (kind != BotSiegeKind.Ram || w.IsWithinRadius(target, 600)));
                if (ours >= 2) return false; // two rams per door, two of each engine at most
                Vector3? spot;
                string blocked = null;
                lock (s) spot = squad.SpotTarget == target && now < squad.SpotUntil ? squad.Spot : null;
                spot ??= AutonomousWorldBotController.ChooseSiegePosition(bot, target, kind, engines, keep, out blocked);
                if (spot == null)
                {
                    lock (s) s.NextAction[bot] = now + 15_000;
                    Log.Info($"RAID_SIEGE_BLOCKED player={s.Owner.Name} bot={bot.Name} kind={kind} target=\"{target.Name}\" {blocked}");
                    return false;
                }
                // Remember the validated spot (UpdatePost leaves it alone while NextPost is ahead).
                lock (s)
                {
                    squad.Spot = spot; squad.SpotTarget = target; squad.SpotUntil = now + 120_000;
                    squad.Post = spot; squad.PostTarget = target; squad.NextPost = now + 120_000;
                }
                // Run 22: crews stopped 40-50 short of a 30-unit arrival test and never spawned. The engine goes on the
                // validated spot itself once the crew leader is close enough to take control of it.
                // A ballista is controlled from 75 at most (the others 256).
                if (Vector3.Distance(new(bot.X, bot.Y, bot.Z), spot.Value) > (kind == BotSiegeKind.Ballista ? 55 : 90))
                { bot.PathTo(spot.Value, bot.MaxSpeed); return true; }
                // Run 24/25: two ram crews picked the same spot in the same tick and stacked their rams. Each crew
                // reserves its spot under the session lock; a spot near an existing engine or another reservation
                // is chosen again.
                Vector3 chosen = spot.Value;
                float crowd = kind == BotSiegeKind.Ram ? 120 : 400; // two rams fit a narrow keep-door approach
                lock (s)
                {
                    bool taken = engines.Any(w => w.IsAlive && Vector3.Distance(chosen, new(w.X, w.Y, w.Z)) < crowd) ||
                        s.Synthetic.Any(w => w.IsAlive && Vector3.Distance(chosen, new(w.X, w.Y, w.Z)) < crowd) ||
                        s.Reserved.Any(r => r.Key != squad && Vector3.Distance(chosen, r.Value) < crowd);
                    if (taken) { squad.SpotUntil = 0; return false; }
                    s.Reserved[squad] = chosen;
                }
                bot.StopMovingOnPath(); bot.StopMoving();
                GameSiegeWeapon weapon = AutonomousWorldBotController.CreateSiegeWeapon(kind);
                weapon.CurrentRegion = bot.CurrentRegion;
                weapon.X = (int)spot.Value.X; weapon.Y = (int)spot.Value.Y; weapon.Z = (int)spot.Value.Z;
                weapon.Realm = bot.Realm;
                // Run 23: a new engine reports position (0,0) until it is in the world, so the range check comes after.
                string failed = !weapon.AddToWorld() ? "could not be added to the world" : null;
                if (failed == null)
                {
                    weapon.Heading = weapon.GetHeading(target);
                    if (!AutonomousWorldBotController.InSiegeRange(weapon, target))
                    { failed = $"out of range ({(int)weapon.GetDistanceTo(target)})"; weapon.Delete(); }
                    else if (!weapon.TryTakeControl(bot)) { weapon.Delete(); failed = "crew leader could not take control"; }
                }
                if (failed != null)
                {
                    lock (s) { s.NextAction[bot] = now + 15_000; squad.SpotUntil = 0; }
                    Log.Info($"RAID_SIEGE_SPAWN_FAILED player={s.Owner.Name} bot={bot.Name} kind={kind} target=\"{target.Name}\" reason=\"{failed}\"");
                    return false;
                }
                lock (s) s.Synthetic.Add(weapon);
                owned = weapon;
                Log.Info($"RAID_SIEGE_PLACED player={s.Owner.Name} bot={bot.Name} kind={kind} source=synthetic target=\"{target.Name}\" " +
                         $"range={(int)weapon.GetDistanceTo(target)} at={bot.X},{bot.Y},{bot.Z}");
            }
            if (!bot.IsWithinRadius(owned, Math.Max(32, owned.SIEGE_WEAPON_CONTROLE_DISTANCE - 20)))
            { bot.PathTo(new Vector3(owned.X, owned.Y, owned.Z), bot.MaxSpeed); return true; }
            bot.StopMovingOnPath(); bot.StopMoving(); bot.TargetObject = target;
            if (owned.TargetObject != target && !owned.SiegeWeaponTimer.IsAlive)
            { owned.TargetObject = target; owned.CurrentState &= ~GameSiegeWeapon.eState.Aimed; }
            if (!owned.SiegeWeaponTimer.IsAlive)
            {
                (owned as GameSiegeRam)?.UpdateRamStatus(); // the squad standing at the ram crews it
                if ((owned.CurrentState & GameSiegeWeapon.eState.Armed) == 0) owned.Arm();
                else if ((owned.CurrentState & GameSiegeWeapon.eState.Aimed) == 0) owned.Aim();
                else
                {
                    owned.Fire();
                    if (owned.ShotsFired % 10 == 1)
                        Log.Info($"RAID_SIEGE_FIRING player={s.Owner.Name} bot={bot.Name} kind={BotSiegeRuntime.Kind(owned)} target=\"{target.Name}\" " +
                                 $"targetHp={target.HealthPercent} shots={owned.ShotsFired} hits={owned.ConfirmedHits}");
                }
            }
            return true;
        }

        /// <summary>Anything this engine can hit from where it stands: enemy siege, defenders (wall archers too), the door.</summary>
        private static GameLiving InReach(GameBot bot, GameSiegeWeapon engine, BotSiegeKind kind, AbstractGameKeep keep,
            GameKeepDoor door, GameNPC[] nearby, GameSiegeWeapon[] engines)
        {
            var candidates = engines.Where(w => BotSiegeRuntime.LegalEnemy(bot, w)).Cast<GameLiving>()
                .Concat(Enemies(bot, keep, nearby).Where(t => t is not GuardLord || door == null));
            if (door != null && kind != BotSiegeKind.Ballista) candidates = candidates.Prepend(door);
            return candidates.Where(t => AutonomousWorldBotController.InSiegeRange(engine, t) &&
                    (kind != BotSiegeKind.Ballista || t.Z - engine.Z > 150 || BotSiegeRuntime.Visible(engine, t)))
                .OrderBy(t => t == door && kind == BotSiegeKind.Trebuchet ? 0 : 1).ThenBy(engine.GetDistanceTo).FirstOrDefault();
        }

        // ---- Targets for the squads without an engine ----

        private static IEnumerable<GameLiving> Enemies(GameBot bot, AbstractGameKeep keep, GameNPC[] nearby) =>
            nearby.OfType<GameLiving>().Concat(bot.GetPlayersInRadius(3000)).Where(t =>
                (t is GameBot or GamePlayer || t is GameKeepGuard guard && guard.Component?.Keep == keep && !guard.IsPortalKeepGuard) &&
                BotSiegeRuntime.LegalEnemy(bot, t) && !t.IsStealthed);

        private static bool FiringOnRealm(GameBot bot, GameLiving t) =>
            (t.IsAttacking || t.IsCasting) && t.TargetObject is GameLiving victim && victim.Realm == bot.Realm;

        /// <summary>Wall-clearers: archers and enemies up on the walls, those shooting at the realm first; melee members
        /// take only what they can walk to.</summary>
        private static GameLiving WallTarget(GameBot bot, AbstractGameKeep keep, GameNPC[] nearby)
        {
            bool ranged = AutonomousRvrDefense.IsRangedDefender(bot) || BotPartyRoles.IsSupport(bot);
            return Enemies(bot, keep, nearby).Where(t => t is not GuardLord && bot.IsWithinRadius(t, 2200))
                .Where(t => ranged ? FiringOnRealm(bot, t) || BotSiegeRuntime.Visible(bot, t) : Reachable(bot, t))
                .OrderByDescending(t => FiringOnRealm(bot, t) ? 1 : 0).ThenByDescending(t => t.Z).ThenBy(bot.GetDistanceTo)
                .FirstOrDefault();
        }

        /// <summary>Assault squads: defenders at the gate while a door stands; the lord once every door is down.</summary>
        private static GameLiving AssaultTarget(GameBot bot, AbstractGameKeep keep, GameKeepDoor door, GameNPC[] nearby)
        {
            if (door == null)
            {
                GuardLord lord = keep.Guards.Values.OfType<GuardLord>().FirstOrDefault(l => l.IsAlive);
                if (lord != null && Reachable(bot, lord)) return lord;
            }
            return Attacker(bot, keep, nearby, 900, reachableOnly: true);
        }

        private static GameLiving Attacker(GameBot bot, AbstractGameKeep keep, GameNPC[] nearby, int radius = 1500, bool reachableOnly = false) =>
            Enemies(bot, keep, nearby).Where(t => t is not GuardLord && bot.IsWithinRadius(t, radius))
                .Where(t => !reachableOnly || AutonomousRvrDefense.IsRangedDefender(bot) || Reachable(bot, t))
                .OrderBy(bot.GetDistanceTo).FirstOrDefault();

        /// <summary>True while this raid bot is leaving a target alone that it could not walk to.</summary>
        public static bool Ignores(GameBot bot, GameLiving target)
        {
            if (bot == null || target == null || !Unreachable.TryGetValue((bot, target), out long until)) return false;
            if (GameLoop.GameLoopTime < until) return true;
            Unreachable.TryRemove((bot, target), out _);
            return false;
        }

        /// <summary>
        /// Owner 2026-10-07: raid bots turned up inside Fensalir Faste's walls. A melee bot chasing a wall archer had no
        /// walking path, and the generic NPC mover then jumps an NPC to the nearest node reachable from its target, up
        /// on the wall (NpcMovementComponent no longer does that for bots in the frontier). A melee raid bot whose target
        /// stands well above or below it with no walkable route, on two checks 4 s apart, drops it for two minutes
        /// instead of standing under the wall (same rule as gamebots, AUTONOMOUS_UNREACHABLE_TARGET).
        /// </summary>
        private static void BreakUnreachableTarget(BotBrain brain, GameBot bot, long now)
        {
            ReachWatch.TryGetValue(bot, out var watch);
            if (now < watch.Next) return;
            if (AutonomousRvrDefense.IsRangedDefender(bot) || BotPartyRoles.IsSupport(bot) || bot.TargetObject is not GameLiving target ||
                !target.IsAlive || target.Realm == bot.Realm || target is GameSiegeWeapon or GameKeepDoor ||
                bot.IsWithinRadius(target, 250) || Math.Abs(target.Z - bot.Z) < 150 || Reachable(bot, target))
            {
                ReachWatch[bot] = (null, 0, now + UnreachableCheckMilliseconds);
                return;
            }
            int failures = watch.Target == target ? watch.Failures + 1 : 1;
            if (failures < 2) { ReachWatch[bot] = (target, failures, now + UnreachableCheckMilliseconds); return; }
            ReachWatch[bot] = (null, 0, now + UnreachableCheckMilliseconds);
            Unreachable[(bot, target)] = now + UnreachableIgnoreMilliseconds;
            brain.RemoveFromAggroList(target);
            bot.StopAttack();
            bot.StopMovingOnPath();
            bot.TargetObject = null;
            Log.Info($"RAID_UNREACHABLE_TARGET player={bot.Owner?.Name} bot={bot.Name} target=\"{target.Name}\" heightAbove={target.Z - bot.Z} position={bot.X},{bot.Y},{bot.Z}");
        }

        // A walkable route that does not pass a closed enemy door (melee never chases a wall-top archer).
        private static bool Reachable(GameBot bot, GameLiving target)
        {
            var nav = AutonomousKeepApproachNavigation.ForRealm(PathfindingProvider.Instance, bot.CurrentRegion, bot.Realm);
            if (!nav.IsAvailable || !nav.HasNavmesh(bot.CurrentZone)) return false;
            Vector3? floor = nav.GetClosestPoint(bot.CurrentZone, new(target.X, target.Y, target.Z), 64, 64, 96, nav.DefaultFilters);
            return floor.HasValue && Math.Abs(floor.Value.Z - target.Z) <= 96 &&
                AutonomousZoneItinerary.HasCompleteCorridor(nav, bot.CurrentZone, new(bot.X, bot.Y, bot.Z), floor.Value);
        }
    }
}
