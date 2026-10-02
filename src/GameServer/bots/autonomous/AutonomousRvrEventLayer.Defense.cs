using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;

namespace DOL.GS
{
    public static partial class AutonomousRvrEventLayer
    {
        public const long DefenseResponseMilliseconds = 4 * 60 * 60_000L;
        public const long DefenseQuietMilliseconds = 3 * 60_000L;
        private sealed record Alarm(AbstractGameKeep Keep, GamePlayer Player, long Tick);
        private sealed record ResponseForce(Force Force, GameBot[] Members);
        private sealed record Warning(GamePlayer Player, string Text, long Until, long Next);
        private static readonly ConcurrentDictionary<string, Alarm> DefenseAlarms = new();
        private static readonly ConcurrentDictionary<string, long> KeepCombatPressure = new();
        private static readonly Dictionary<string, Warning> DefenseWarnings = new();
        private static long _nextDefensePulse;

        // Damage callbacks only replace a bounded, per-keep alarm. Recruitment,
        // chat and all population work run in the coordinator service phase.
        public static void ObserveKeepAttack(AbstractGameKeep keep, GameObject source)
        {
            if (!AutonomousRvrKeepPolicy.IsSiegeObjective(keep)) return;
            string id = $"rvr-keep-{keep.KeepID}";
            long now = GameLoop.GameLoopTime;
            if (source != null && source.Realm != eRealm.None && source.Realm != keep.Realm)
                KeepCombatPressure[id] = now;
            GamePlayer player = PlayerInstigator(source);
            if (player == null || player.Realm == eRealm.None || player.Realm == keep.Realm) return;
            if (DefenseAlarms.TryGetValue(id, out var previous) && now - previous.Tick < 1000) return;
            DefenseAlarms[id] = new(keep, player, now);
        }

        public static GamePlayer PlayerInstigator(GameObject source)
        {
            for (int depth = 0; source != null && depth < 8; depth++)
            {
                if (source is GamePlayer player) return player;
                source = source switch
                {
                    GameBot bot when !bot.IsAutonomousWorldBot => bot.Owner ?? bot.PlayerGroupLeader,
                    GameSiegeWeapon siege => siege.Owner,
                    GameNPC npc when npc.Brain is IControlledBrain controlled => controlled.Owner,
                    _ => null
                };
            }
            return null;
        }

        public static int ResponseCap(bool defending) => defending ? 240 : 96;
        public static bool ResponseReserve(long id) => unchecked((ulong)id * 2654435761UL) % 100 < 30;

        public static bool CanRedirectDefense(eRealm previousDefender, eRealm nextDefender,
            string previousPlayer, string nextPlayer, long previousPressure, long nextPressure) =>
            previousDefender == nextDefender && previousDefender != eRealm.None &&
            !string.IsNullOrEmpty(previousPlayer) && previousPlayer == nextPlayer && nextPressure > previousPressure;

        // Different defending realms keep independent four-hour commitments.
        private static bool IsLatestDefenseFocus(ActiveEvent active) => !active.DefenseReaction ||
            !Events.Values.Any(next => next.DefenseReaction && next != active &&
                CanRedirectDefense(active.DefenderRealm, next.DefenderRealm, active.PlayerAccount,
                    next.PlayerAccount, active.LastPressureTick, next.LastPressureTick));

        public static bool BeginDefenseResponse(LiveObjective target, eRealm attacker, string playerAccount, long now)
        {
            if (target == null || target.IsPortalKeep || target.IsRelicCarrier || target.OwningRealm == eRealm.None ||
                attacker is not (eRealm.Albion or eRealm.Midgard or eRealm.Hibernia) || attacker == target.OwningRealm) return false;
            lock (Sync)
            {
                if (!Events.TryGetValue(target.Id, out var active))
                {
                    active = new ActiveEvent { TargetId = target.Id, Target = target, AttackerRealm = attacker,
                        DefenderRealm = target.OwningRealm, RelicKeep = target.IsRelicKeep, CreatedTick = now,
                        ExpiresTick = now + DefenseResponseMilliseconds, BattleStarted = true, DefenseReaction = true };
                    Events[target.Id] = active;
                    RealmEventRecords.Begin(target.Id, target.Name, target.IsRelicKeep ? "Relic keep" : "Keep",
                        GlobalConstants.RealmToName(attacker), "Player attack: immediate four-hour defense response");
                    RealmEventRecords.Progress(target.Id, "Battle", "Defenders, friendly helpers and third-realm forces converging", 0, 0);
                }
                bool firstAlarm = active.LastPressureTick == 0;
                active.AttackObserved = true;
                active.LastPressureTick = now;
                active.PlayerAccount = playerAccount;
                return firstAlarm;
            }
        }

        public static void PulseDefense(long now)
        {
            if (now < _nextDefensePulse) return;
            _nextDefensePulse = now + 5000;
            lock (Sync)
            {
                Expire(now);
                foreach (var pair in DefenseAlarms.ToArray())
                {
                    if (now - pair.Value.Tick > DefenseQuietMilliseconds)
                    {
                        DefenseAlarms.TryRemove(pair.Key, out _);
                        continue;
                    }
                    var alarm = pair.Value;
                    if (alarm.Keep.Realm == alarm.Player.Realm) continue;
                    var keep = alarm.Keep;
                    var target = new LiveObjective(pair.Key, keep.Name, keep.IsRelic ? Intent.AssaultRelicKeep : Intent.AssaultKeep,
                        keep.Realm, keep.Region, keep.X, keep.Y, keep.Z, keep.IsRelic, 0, 0, 0, 0, UnderAttack: true);
                    bool firstAlarm = BeginDefenseResponse(target, alarm.Player.Realm,
                        alarm.Player.Client?.Account?.Name ?? alarm.Player.Name, alarm.Tick);
                    var active = Events.GetValueOrDefault(pair.Key);
                    if (active == null) continue;
                    if (firstAlarm)
                    {
                        string text = $"Your attack on {alarm.Keep.Name} has raised the alarm! {GlobalConstants.RealmToName(alarm.Keep.Realm)} is mustering its forces.";
                        DefenseWarnings[pair.Key] = new(alarm.Player, text, now + 15_000, now);
                        RealmEventNotices.Queue(pair.Key, active.DefenderRealm, $"{alarm.Keep.Name} is under attack! Rally to its defense immediately!");
                        RealmEventNotices.Queue(pair.Key, alarm.Player.Realm, $"Our forces are attacking {alarm.Keep.Name}. Nearby warbands, lend them aid!");
                        RealmEventNotices.Queue(pair.Key, OtherRealm(active), $"Enemy armies are clashing at {alarm.Keep.Name}. Scouts and warbands, seize your opportunity!");
                    }
                }
                // Quiet travel or an attack in another realm does not cancel
                // this defense. Capture or its fixed deadline still ends it.
                foreach (var stale in KeepCombatPressure.Where(p => now - p.Value > DefenseResponseMilliseconds).ToArray())
                    KeepCombatPressure.TryRemove(stale.Key, out _);
                foreach (var pair in DefenseWarnings.ToArray())
                {
                    var warning = pair.Value;
                    if (now >= warning.Until || warning.Player.ObjectState != GameObject.eObjectState.Active)
                    { DefenseWarnings.Remove(pair.Key); continue; }
                    if (now >= warning.Next)
                    {
                        warning.Player.Out.SendMessage(warning.Text, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
                        DefenseWarnings[pair.Key] = warning with { Next = now + 5000 };
                    }
                }
                if (!Events.Values.Any(e => e.BattleStarted)) return;
            }

            // Resolve coordinator IDs BEFORE taking the event lock (the group
            // coordinator consults event state while holding its own lock).
            var forces = AutonomousBotRegistry.Snapshot()
                .Where(b => b.IsAutonomousWorldBot && !b.IsTemporaryGroupHelper && !b.IsPlayerLedGroup &&
                    b.Level == 50 && b.IsAlive && b.ObjectState == GameObject.eObjectState.Active &&
                    AutonomousObjectiveAssignments.Is(b, eAutonomousObjectiveKind.RvR))
                .GroupBy(AutonomousBotGroupCoordinator.RvrForceId)
                .Select(g => new ResponseForce(new Force(g.Key, g.First().Realm, g.First().Group?.MemberCount ?? g.Count(), 50, 0,
                    MemberIds: g.Select(b => b.DatabaseID).ToArray()), g.ToArray())).ToArray();
            lock (Sync)
            {
                foreach (var candidate in forces)
                foreach (var active in Events.Values.Where(e => e.BattleStarted && Participants(e, candidate.Force.Realm).ContainsKey(candidate.Force.GroupId)))
                foreach (var bot in candidate.Members)
                {
                    if (bot.CurrentRegionID == active.Target.RegionId &&
                        Vector2.DistanceSquared(new(bot.X, bot.Y), new(active.Target.X, active.Target.Y)) <= 9000 * 9000 &&
                        Math.Abs(bot.Z - active.Target.Z) <= 2000)
                        active.Present[bot.DatabaseID] = (candidate.Force.GroupId, bot.Realm, now, bot, new(bot.X, bot.Y, bot.Z), bot.CurrentRegionID);
                    else active.Present.Remove(bot.DatabaseID);
                }
                foreach (var active in Events.Values.Where(e => e.BattleStarted && IsLatestDefenseFocus(e))
                             .OrderByDescending(e => e.LastPressureTick).ToArray())
                {
                    int before = active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum();
                    foreach (var candidate in forces.OrderBy(f => f.Members.Min(b => b.CurrentRegionID == active.Target.RegionId ?
                                 Vector2.DistanceSquared(new(b.X, b.Y), new(active.Target.X, active.Target.Y)) : float.MaxValue)))
                    {
                        Force force = candidate.Force;
                        if (Participants(active, force.Realm).ContainsKey(force.GroupId)) continue;
                        if (ReleasedForces.ContainsKey(force.GroupId) ||
                            CarrierEvents.Values.Any(c => c.Participants.Values.Any(r => r.ContainsKey(force.GroupId)))) continue;
                        // Keep a deterministic 30% reserve of whole warbands;
                        // incidental nearby combat remains handled by normal AI.
                        if (ResponseReserve(candidate.Members.Min(b => b.DatabaseID))) continue;
                        if (candidate.Members.Any(b => b.InCombat || b.IsAttacking || b.IsStunned || b.IsMezzed ||
                            (b.Brain as BotBrain)?.HasAggro == true || GameRelic.IsPlayerCarryingRelic(b))) continue;
                        // Missing/dead members may catch up. Only mixed-level,
                        // non-RvR or player-led parties are ineligible.
                        if (candidate.Members.Any(b => b.Group != null && b.Group.GetMembersInTheGroup().Any(m =>
                            m is not GameBot member || member.Level != 50 || member.IsPlayerLedGroup ||
                            !member.IsAutonomousWorldBot || !AutonomousObjectiveAssignments.Is(member, eAutonomousObjectiveKind.RvR)))) continue;
                        var previous = Events.Values.FirstOrDefault(e => Participants(e, force.Realm).ContainsKey(force.GroupId));
                        if (previous != null && (!previous.DefenseReaction || !active.DefenseReaction ||
                            !CanRedirectDefense(previous.DefenderRealm, active.DefenderRealm,
                                previous.PlayerAccount, active.PlayerAccount, previous.LastPressureTick, active.LastPressureTick))) continue;
                        int cap = active.DefenseReaction ? ResponseCap(force.Realm == active.DefenderRealm) : Capacity(active);
                        if (!TryJoin(Participants(active, force.Realm), force, cap)) continue;
                        if (previous != null)
                        {
                            Participants(previous, force.Realm).Remove(force.GroupId);
                            previous.Slots.Remove(force.GroupId);
                            foreach (long id in force.MemberIds) previous.Present.Remove(id);
                        }
                        foreach (GameBot bot in candidate.Members)
                            bot.TempProperties.SetProperty("RvrEventForce", force.GroupId);
                    }
                    int assigned = active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum();
                    if (assigned != before)
                    {
                        string detail = $"Reinforcements converging: attackers={active.Attackers.Values.Sum()}, defenders={active.Defenders.Values.Sum()}, third realm={active.ThirdRealm.Values.Sum()}";
                        RealmEventRecords.Progress(active.TargetId, "Battle", detail, assigned,
                            Attendance(active, active.AttackerRealm, now) + Attendance(active, active.DefenderRealm, now) + Attendance(active, OtherRealm(active), now));
                        DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer)).Info($"RVR_REINFORCEMENTS event={active.TargetId} {detail}");
                    }
                }
            }
        }
    }
}
