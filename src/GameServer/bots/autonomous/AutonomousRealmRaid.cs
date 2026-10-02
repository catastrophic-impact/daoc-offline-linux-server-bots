using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DOL.AI.Brain;

namespace DOL.GS
{
    /// <summary>A raid owns commitments, never merges the underlying eight-person parties.</summary>
    public static class AutonomousRealmRaid
    {
        public sealed record Definition(string Id, string Name, eRealm Realm, ushort Region, string BossType, int SuggestedBots)
        {
            public bool IsDungeon => Region is 60 or 160 or 191;
            public Vector3 Trigger => Region switch
            {
                60 => new(29462, 25240, 19490),
                160 => new(34542, 57121, 11881),
                191 => new(39652, 60831, 11893),
                _ => default
            };
            public string[] FinalTypes => Region switch
            {
                60 => ["Apocalypse"], 160 => ["KingTuscar", "QueenKula"],
                191 => ["Olcasgean"], _ => [BossType]
            };
        }
        public sealed record View(string EventId, string State, AutonomousBotGroupCoordinator.SharedCamp Camp, bool Hold, bool Muster = false, bool Crossing = false);
        public sealed record Summary(string Id, string Name, string Realm, string State, int Assigned, int Present, int Suggested, long Remaining, string Phase = "");
        private sealed class Raid
        {
            public Definition Definition;
            public GameNPC Boss;
            public long Deadline;
            public bool Started;
            public bool PreparationNoticeSent;
            public bool Forced;
            public long Created;
            public RealmRaidMuster.Hub Hub;
            public bool HubDeparted;
            public long NextDefenseBroadcast;
            public readonly Dictionary<int, Vector3> HubPosts = new();
            public Vector3[] OutboundSeams = [];
            public readonly List<GameBot[]> ForcedParties = new();
            public readonly Dictionary<int, Vector3> StagingPosts = new();
            public RealmRaidDungeonRoute DungeonRoute;
            public readonly RealmRaidLootOwner.Ledger LootLedger = new();
            public readonly Dictionary<Group, Party> Parties = new();
            public GameLiving[] Support = [];
        }
        private sealed class Party
        {
            public GameBot[] Members;
            public int FormationSlot;
            public Vector3 Staging;
            public Vector3 HubPost;
            public bool Departed;
            public int OutboundLeg;
            public Vector3? Anchor;
            public Vector3 AnchorCenter;
            public ushort AnchorRegion;
            public View View;
            public View DestinationView;
            public readonly HashSet<long> CatchingUp = new();
            public readonly Dictionary<long, long> CorpseSince = new();
        }
        private static readonly object Sync = new();
        private static readonly Dictionary<string, Raid> Raids = new();
        private static readonly Dictionary<Group, Raid> Membership = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Group, byte> DefenseGroups = new();
        private static readonly Dictionary<Group, string> Released = new();
        private static readonly Dictionary<string, long> Cooldowns = new();
        private static readonly Dictionary<string, GameNPC> Bosses = new();
        private sealed record FinishedLoot(GameBot[] Members, RealmRaidLootOwner.Ledger Ledger, long Expires);
        private static readonly Dictionary<GameNPC, FinishedLoot> CompletedLoot = new();
        private static long _nextPulse;
        private static long _nextForcedRecruitment;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> Reservations = new();
        public static bool IsReserved(GameBot bot) => bot != null && Reservations.ContainsKey(bot.DatabaseID);
        public static bool IsReservedFor(GameBot bot, string id) => bot != null && Reservations.GetValueOrDefault(bot.DatabaseID) == id;
        public static bool IsForcedExpedition(string id)
        {
            lock (Sync) return Raids.TryGetValue(id, out var raid) && raid.Forced;
        }
        public static bool IsEligible(GameBot bot) => bot != null && RealmRaidRecruitmentPolicy.Eligible(
            bot.Level, bot.IsAutonomousWorldBot, bot.IsTemporaryGroupHelper, bot.IsPlayerLedGroup);
        public static readonly Definition[] Definitions =
        [
            new("dragon-albion", "Golestandt", eRealm.Albion, 1, "AlbGolestandt", 200),
            new("dragon-midgard", "Gjalpinulva", eRealm.Midgard, 100, "MidGjalpinulva", 200),
            new("dragon-hibernia", "Cuuldurach", eRealm.Hibernia, 200, "HibCuuldurach", 200),
            new("epic-albion", "Caer Sidi", eRealm.Albion, 60, "ApocInitializator", 200),
            new("epic-midgard", "Tuscaran Glacier", eRealm.Midgard, 160, "KingTuscar", 200),
            new("epic-hibernia", "Galladoria", eRealm.Hibernia, 191, "Olcasgean", 200)
        ];

        public static void Pulse(long now)
        {
            lock (Sync)
            {
                if (now < _nextPulse) return;
                _nextPulse = now + 10_000;
                foreach (GameNPC expired in CompletedLoot.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
                    CompletedLoot.Remove(expired);
                foreach (Definition definition in Definitions)
                {
                    // Remember dead instances: their actual IsRespawning flag is
                    // authoritative even after they leave the region object list.
                    if (!Bosses.TryGetValue(definition.Id, out var remembered) || remembered.ObjectState != GameObject.eObjectState.Active)
                    {
                        var boss = WorldMgr.GetRegion(definition.Region)?.Objects.OfType<GameNPC>()
                            .FirstOrDefault(n => n.GetType().Name == definition.BossType && n.IsAlive);
                        if (boss != null) Bosses[definition.Id] = boss;
                    }
                }
                foreach (var raid in Raids.Values.ToArray())
                {
                    foreach (Group group in raid.Parties.Keys.ToArray())
                    {
                        Party party = raid.Parties[group];
                        foreach (var alive in party.Members.Where(b => b.IsAlive)) party.CorpseSince.Remove(alive.DatabaseID);
                        if (party.Members.Any(b => !IsEligible(b) || b.Group != group || !AutonomousBotRegistry.Contains(b.DatabaseID)))
                            RemoveParty(group);
                    }
                    raid.Support = raid.Parties.Values.SelectMany(p => p.Members).Cast<GameLiving>().ToArray();
                    int presentAtHub = raid.Parties.Values.Sum(p => PresentAtHub(raid, p));
                    bool departing = !raid.HubDeparted && RealmRaidRecruitmentPolicy.DepartHub(false, presentAtHub);
                    raid.HubDeparted |= departing;
                    if (departing) RealmEventNotices.Queue(raid.Definition.Id, raid.Definition.Realm,
                        $"{presentAtHub} adventurers present at {raid.Hub.Name}; departing together for {raid.Definition.Name}. Late arrivals will join the expedition directly.");
                    if (raid.HubDeparted)
                        foreach (Party p in raid.Parties.Values)
                            if (!p.Departed)
                            {
                                foreach (var b in p.Members.Where(b => !IsAtHub(raid, p, b))) p.CatchingUp.Add(b.DatabaseID);
                                p.Departed = true;
                            }
                    if (!raid.Definition.IsDungeon && (!raid.Boss.IsAlive || raid.Boss.ObjectState != GameObject.eObjectState.Active))
                    { End(raid, now, "The dragon encounter has ended; checking actual respawn before the next expedition."); continue; }
                    raid.DungeonRoute?.ObserveCompletion();
                    if (raid.DungeonRoute?.Complete == true)
                    { End(raid, now, "The final dungeon encounter has been defeated."); continue; }
                    if (raid.Started && now >= raid.Deadline)
                    { End(raid, now, "The four-hour expedition window ended."); continue; }
                    if (!raid.Started)
                    {
                        long noticeDeadline = raid.Created + (raid.Forced ? RealmRaidRecruitmentPolicy.ForcedStagingMilliseconds : RealmRaidRecruitmentPolicy.AutonomousStagingLimitMilliseconds);
                        if (RealmEventBanter.ReminderDue(raid.Started, raid.PreparationNoticeSent, noticeDeadline - now))
                        {
                            raid.PreparationNoticeSent = true;
                            RealmEventNotices.Queue(raid.Definition.Id, raid.Definition.Realm,
                                RealmEventBanter.RaidReminder(raid.Definition.Name, raid.Forced));
                        }
                        int present = PresentAtStaging(raid);
                        if (RealmRaidRecruitmentPolicy.Ready(raid.Forced, now - raid.Created, present, DragonLanded(raid)))
                        {
                            raid.Started = true;
                            raid.Deadline = now + RealmRaidRecruitmentPolicy.BattleMilliseconds;
                            RealmEventRecords.Progress(raid.Definition.Id, "Battle", "Expedition began advancing to the encounter.", raid.Support.Length, present);
                            RealmEventNotices.Queue(raid.Definition.Id, raid.Definition.Realm,
                                $"The {raid.Definition.Name} expedition is advancing with {present} staged level-50 adventurers.");
                        }
                        else if (RealmRaidRecruitmentPolicy.StagingExpired(raid.Forced, now - raid.Created, present, DragonLanded(raid)))
                        { End(raid, now, $"Staging failed: {present} adventurers arrived; no undersized or airborne assault was ordered."); continue; }
                    }
                    if (raid.Started && raid.DungeonRoute != null)
                    {
                        raid.DungeonRoute.Advance(raid.Support, now);
                        if (raid.DungeonRoute.Complete)
                        { End(raid, now, $"The {raid.Definition.Name} expedition defeated the final encounter."); continue; }
                    }
                    foreach (Party party in raid.Parties.Values) UpdateView(raid, party);
                }
                foreach (string id in Cooldowns.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) Cooldowns.Remove(id);
            }
        }

        public static bool TryJoin(GameBot leader, out View view)
        {
            view = null;
            if (leader?.Group == null || leader.Group.LivingLeader != leader) return false;
            GameBot[] members = leader.Group.GetMembersInTheGroup().OfType<GameBot>().ToArray();
            if (members.Length != 8 || members.Any(b => !IsEligible(b) || IsReserved(b) ||
                b.Realm != leader.Realm || !AutonomousObjectiveAssignments.Is(b, eAutonomousObjectiveKind.GroupPve))) return false;
            lock (Sync)
            {
                if (Membership.TryGetValue(leader.Group, out Raid joined)) { view = joined.Parties[leader.Group].View; return true; }
                Raid raid = Raids.Values.FirstOrDefault(r => r.Definition.Realm == leader.Realm && !r.Forced && r.Parties.Count < RealmRaidRecruitmentPolicy.MaximumParties);
                if (raid == null && Raids.Values.Any(r => r.Definition.Realm == leader.Realm)) return false;
                if (raid == null)
                {
                    // Decisions occur once when a party chooses a new task, not
                    // once per member or AI tick; keep ordinary PvE populated.
                    if (Random.Shared.NextDouble() >= RealmRaidRecruitmentPolicy.NewEventChance) return false;
                    if (AutonomousBotRegistry.Snapshot().Count(b => IsEligible(b) && b.Realm == leader.Realm &&
                        AutonomousObjectiveAssignments.Is(b, eAutonomousObjectiveKind.GroupPve)) < RealmRaidRecruitmentPolicy.AutonomousMinimumPresent) return false;
                    var definition = Definitions.Where(d => d.Realm == leader.Realm && Available(d.Id)).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                    if (definition == null || !Start(definition.Id, leader.Realm, out _)) return false;
                    raid = Raids[definition.Id];
                }
                else if (Random.Shared.NextDouble() >= RealmRaidRecruitmentPolicy.JoinExistingChance) return false;
                // Removing a party must not make its count reuse a surviving
                // party's staging slot and pile reinforcements on top of it.
                if (raid.Support.Length + members.Length > RealmRaidRecruitmentPolicy.MaximumBots) return false;
                int slot = Enumerable.Range(0, RealmRaidRecruitmentPolicy.MaximumParties).FirstOrDefault(candidate =>
                    raid.Parties.Values.All(p => p.FormationSlot != candidate), -1);
                if (slot < 0 || !TryStaging(raid, slot, out Vector3 staging) || !TryHubPost(raid, slot, out var hubPost)) return false;
                var party = new Party { Members = members, FormationSlot = slot, Staging = staging, HubPost = hubPost };
                raid.Parties[leader.Group] = party;
                Membership[leader.Group] = raid;
                DefenseGroups[leader.Group] = 0;
                raid.Support = raid.Parties.Values.SelectMany(p => p.Members).Cast<GameLiving>().ToArray();
                UpdateView(raid, party);
                view = party.View;
                return true;
            }
        }

        public static bool Start(string id, eRealm realm, out string reason, bool forced = false)
        {
            GameBot[][] recruits = forced ? AutonomousBotGroupCoordinator.PlanForcedRaid(realm) : [];
            if (forced && recruits.Sum(p => p.Length) < RealmRaidRecruitmentPolicy.MaximumBots)
            {
                reason = $"Not started: only {recruits.Sum(p => p.Length)}/300 eligible level-50 bots could be reserved. No tasks were cancelled.";
                return false;
            }
            lock (Sync)
            {
                Definition definition = Definitions.FirstOrDefault(d => d.Id == id && d.Realm == realm);
                if (definition == null || !Available(id)) { reason = "The encounter is not alive/available, or this event is on cooldown."; return false; }
                if (!RealmRaidRecruitmentPolicy.CanOpenEvent(forced, Raids.Values.Any(r => r.Definition.Realm == realm), Raids.ContainsKey(id)))
                { reason = "This realm already has an autonomous PvE expedition, or this encounter is already assigned."; return false; }
                if (forced && recruits.SelectMany(p => p).Any(b => IsReserved(b) || GetView(b.Group) != null))
                { reason = "A planned recruit joined another expedition. Nothing was reassigned; please retry."; return false; }
                long now = GameLoop.GameLoopTime;
                var raid = new Raid { Definition = definition, Boss = Bosses[id], Forced = forced, Created = now, Hub = RealmRaidMuster.Hubs.Single(h => h.Event == id),
                    Deadline = now + (forced ? RealmRaidRecruitmentPolicy.ForcedStagingMilliseconds : RealmRaidRecruitmentPolicy.AutonomousStagingLimitMilliseconds) };
                if (definition.IsDungeon)
                {
                    if (!RealmRaidDungeonRoute.TryCreate(definition.Region, definition.Trigger, definition.FinalTypes, out var route))
                    { reason = "The dungeon entrance/final approach could not be validated. No party was assigned."; return false; }
                    raid.DungeonRoute = route;
                }
                if (!TryStaging(raid, 0, out var destination) || !TryHubPost(raid, 0, out var origin) ||
                    !RealmRaidMuster.TryRoute(WorldMgr.GetRegion(raid.Hub.Region), PathfindingProvider.Instance, realm,
                        origin, destination, out raid.OutboundSeams, raid.Hub.Via))
                { reason = "No complete connected hub-to-encounter route was found. No party was assigned."; return false; }
                Raids[id] = raid;
                RealmEventRecords.Begin(id, definition.Name, definition.IsDungeon ? "Epic dungeon" : "Dragon",
                    GlobalConstants.RealmToName(realm), (forced ? "Forced" : "Automatic") + " rally via " + raid.Hub.Name);
                raid.ForcedParties.AddRange(recruits);
                foreach (GameBot bot in recruits.SelectMany(p => p)) Reservations[bot.DatabaseID] = id;
                RealmEventNotices.Queue(id, realm, forced
                    ? $"{definition.Name}: {recruits.Sum(p => p.Length)} level-50 adventurers reserved; 45-minute preparation begins at {raid.Hub.Name}. At least 200 must arrive and the dragon must land."
                    : $"{definition.Name}: recruiting up to 300 level-50 adventurers via {raid.Hub.Name}; at least 200 must arrive before assault.");
                reason = forced ? $"Reserved {recruits.Sum(p => p.Length)} level-50 bots. Everyone travels to {raid.Hub.Name}; 45-minute preparation countdown started. At least 200 must arrive and the dragon must land." :
                    $"Recruiting up to 300 level-50 bots via {raid.Hub.Name}. At least 200 must arrive before assault; staging can last up to {RealmRaidRecruitmentPolicy.AutonomousStagingLimitMilliseconds / 60_000} minutes.";
                return true;
            }
        }

        private static bool DragonLanded(Raid raid) => raid.Definition.IsDungeon ||
            (raid.Boss.Flags & GameNPC.eFlags.FLYING) == 0 &&
            raid.Boss.IsWithinRadius(DragonLairPlacement.Home(raid.Definition.Realm), 2000);

        private static int PresentAtHub(Raid raid, Party party) => party.Members.Count(b => IsAtHub(raid, party, b));

        private static bool IsAtHub(Raid raid, Party party, GameBot b) => IsEligible(b) && b.IsAlive && !b.IsReturningAfterRelease && !b.IsOnStableMasterRoute &&
                b.CurrentRegionID == raid.Hub.Region && b.Group != null &&
                b.IsWithinRadius(new Point3D((int)party.HubPost.X, (int)party.HubPost.Y, (int)party.HubPost.Z), 600);

        private static bool TryHubPost(Raid raid, int slot, out Vector3 post)
        {
            if (raid.HubPosts.TryGetValue(slot, out post)) return true;
            var hub = raid.Hub;
            var region = WorldMgr.GetRegion(hub.Region);
            var zone = region?.GetZone((int)hub.Center.X, (int)hub.Center.Y);
            // Cached once per party, not a scan on each bot's AI turn.
            var nearby = region?.Objects.OfType<GameNPC>().Where(n => n is not GameBot && n.IsAlive &&
                Vector3.DistanceSquared(new(n.X,n.Y,n.Z),hub.Center) < 3000*3000).ToArray() ?? [];
            bool Safe(Vector3 p) => !nearby.Any(n =>
                Vector3.DistanceSquared(new(n.X,n.Y,n.Z),p) <
                (n.Realm == eRealm.None && n.Brain is StandardMobBrain { AggroLevel: > 0 } ? 750*750 : 100*100));
            if (!RealmRaidMuster.TryPost(PathfindingProvider.Instance, zone, hub, slot,
                raid.HubPosts.Values.ToArray(), out post, Safe)) return false;
            raid.HubPosts[slot] = post;
            return true;
        }

        private static int PresentAtStaging(Raid raid) => raid.Parties.Values
            .Sum(p =>
            p.Members.Count(b => IsEligible(b) && b.IsAlive && !b.IsOnStableMasterRoute && b.Group != null &&
                b.CurrentRegionID == (raid.Definition.IsDungeon ? raid.DungeonRoute.Entrance.SourceRegion : raid.Definition.Region) &&
                AtStaging(raid, b)));

        private static bool AtStaging(Raid raid, GameBot bot)
        {
            var home = raid.Definition.IsDungeon ? null : DragonLairPlacement.Home(raid.Definition.Realm);
            return DragonRallyRoute.AtAssembly(new(bot.X,bot.Y,bot.Z), raid.StagingPosts.Values,
                home == null ? null : new Vector3(home.X,home.Y,home.Z));
        }

        public static bool TryPendingDragonRally(GameBot bot, out Vector3 home)
        {
            home=default;
            if (bot?.IsAutonomousWorldBot != true || bot.Group == null) return false;
            lock(Sync)
            {
                if (!Membership.TryGetValue(bot.Group,out var raid) || raid.Started || raid.Definition.IsDungeon ||
                    !raid.HubDeparted || bot.CurrentRegionID != raid.Definition.Region) return false;
                var point=DragonLairPlacement.Home(raid.Definition.Realm);
                home=new(point.X,point.Y,point.Z);
                return true;
            }
        }

        public static bool IsPendingDragonTarget(GameBot bot, GameLiving target)
        {
            if(bot?.IsAutonomousWorldBot != true || bot.Group == null || target == null) return false;
            lock(Sync) return Membership.TryGetValue(bot.Group,out var raid) && !raid.Started &&
                !raid.Definition.IsDungeon && ReferenceEquals(raid.Boss,target);
        }

        // Called outside the raid lock by the single coordinator. Only one
        // party is rebuilt per five seconds; never 300 native route probes at once.
        public static void RecruitForcedParty(long now)
        {
            string id = null;
            GameBot[] members = null;
            lock (Sync)
            {
                if (now < _nextForcedRecruitment) return;
                _nextForcedRecruitment = now + 5_000;
                foreach (var raid in Raids.Values)
                {
                    if (!raid.Forced)
                        foreach (var stale in raid.ForcedParties.Where(p => p.Any(b =>
                                     !IsEligible(b) || !AutonomousBotRegistry.Contains(b.DatabaseID) || b.Group != null ||
                                     !AutonomousObjectiveAssignments.Is(b, eAutonomousObjectiveKind.GroupPve))).ToArray())
                        {
                            raid.ForcedParties.Remove(stale);
                            foreach (var b in stale)
                                if (Reservations.GetValueOrDefault(b.DatabaseID) == raid.Definition.Id)
                                    Reservations.TryRemove(b.DatabaseID, out _);
                        }
                    if (!raid.Forced && raid.Support.Length < RealmRaidRecruitmentPolicy.MaximumBots && raid.ForcedParties.Count == 0)
                    {
                        int size = Math.Min(8, RealmRaidRecruitmentPolicy.MaximumBots - raid.Support.Length);
                        var waiting = AutonomousBotGroupCoordinator.PlanWaitingRaidParty(raid.Definition.Realm, size);
                        if (waiting != null)
                        {
                            raid.ForcedParties.Add(waiting);
                            foreach (var b in waiting) Reservations[b.DatabaseID] = raid.Definition.Id;
                        }
                    }
                    members = raid.ForcedParties.FirstOrDefault(p => p.All(b => IsEligible(b) && b.IsAlive &&
                        AutonomousBotRegistry.Contains(b.DatabaseID) && !b.InCombat && !b.IsAttacking &&
                        (b.Brain as BotBrain)?.HasAggro != true && !b.IsOnStableMasterRoute));
                    if (members == null) continue;
                    id = raid.Definition.Id;
                    raid.ForcedParties.Remove(members);
                    raid.ForcedParties.Add(members); // A bad route cannot starve the other parties.
                    break;
                }
            }
            if (members == null) return;
            if (!AutonomousBotGroupCoordinator.FormForcedRaidParty(id, members)) return;
            lock (Sync)
            {
                if (Raids.TryGetValue(id, out var raid)) raid.ForcedParties.Remove(members);
                foreach (GameBot bot in members) Reservations.TryRemove(bot.DatabaseID, out _);
            }
        }

        public static bool TryGetForcedStaging(string id, out AutonomousBotGroupCoordinator.SharedCamp camp)
        {
            camp = null;
            lock (Sync)
            {
                if (!Raids.TryGetValue(id, out var raid) || raid.Parties.Count >= RealmRaidRecruitmentPolicy.MaximumParties) return false;
                int slot = Enumerable.Range(0, RealmRaidRecruitmentPolicy.MaximumParties).First(i => raid.Parties.Values.All(p => p.FormationSlot != i));
                if (!TryStaging(raid, slot, out _) || !TryHubPost(raid, slot, out var staging)) return false;
                camp = new($"realm-event-{id}", raid.Definition.Name, raid.Hub.Name,
                    raid.Hub.Region,
                    (int)staging.X, (int)staging.Y, (int)staging.Z, false, false, 50);
                return true;
            }
        }

        public static bool CommitForcedParty(string id, Group group)
        {
            lock (Sync)
            {
                if (!Raids.TryGetValue(id, out var raid) ||
                    raid.Parties.Count >= RealmRaidRecruitmentPolicy.MaximumParties || Membership.ContainsKey(group)) return false;
                var members = group.GetMembersInTheGroup().OfType<GameBot>().ToArray();
                if (members.Length is not (4 or 8) || raid.Support.Length + members.Length > RealmRaidRecruitmentPolicy.MaximumBots ||
                    members.Any(b => !IsEligible(b) || b.Realm != raid.Definition.Realm ||
                    Reservations.GetValueOrDefault(b.DatabaseID) != id)) return false;
                int slot = Enumerable.Range(0, RealmRaidRecruitmentPolicy.MaximumParties).First(i => raid.Parties.Values.All(p => p.FormationSlot != i));
                if (!TryStaging(raid, slot, out var staging) || !TryHubPost(raid, slot, out var hubPost)) return false;
                var party = new Party { Members = members, FormationSlot = slot, Staging = staging, HubPost = hubPost };
                raid.Parties[group] = party;
                Membership[group] = raid;
                DefenseGroups[group] = 0;
                raid.Support = raid.Parties.Values.SelectMany(p => p.Members).Cast<GameLiving>().ToArray();
                UpdateView(raid, party);
                return true;
            }
        }

        private static bool Available(string id) => !Raids.ContainsKey(id) &&
            Cooldowns.GetValueOrDefault(id) <= GameLoop.GameLoopTime && Bosses.TryGetValue(id, out var boss) &&
            boss.IsAlive && !boss.IsRespawning && boss.ObjectState == GameObject.eObjectState.Active &&
            (id != "epic-albion" || !ApocInitializator.PickedTarget);

        private static bool TryStaging(Raid raid, int slot, out Vector3 staging)
        {
            if (raid.StagingPosts.TryGetValue(slot, out staging)) return true;
            staging = default;
            var nav = PathfindingProvider.Instance;
            if (raid.DungeonRoute != null)
            {
                var edge = raid.DungeonRoute.Entrance;
                Zone exterior = WorldMgr.GetRegion(edge.SourceRegion)?.GetZone(edge.SourceX, edge.SourceY);
                Vector3 portal = new(edge.SourceX, edge.SourceY, edge.SourceZ);
                if (RealmRaidStaging.TryDungeonPost(nav,exterior,portal,slot,raid.StagingPosts.Values.ToArray(),out staging))
                {
                    raid.StagingPosts[slot] = staging;
                    return true;
                }
                return false;
            }
            Point3D home = DragonLairPlacement.Home(raid.Definition.Realm);
            Region region = WorldMgr.GetRegion(raid.Definition.Region);
            if (RealmRaidStaging.TryDragonPost(nav, region?.GetZone(home.X, home.Y), new(home.X, home.Y, home.Z), slot,
                raid.StagingPosts.Values.ToArray(), out staging))
            {
                raid.StagingPosts[slot] = staging;
                return true;
            }
            return false;
        }

        private static void UpdateView(Raid raid, Party party)
        {
            if (raid.HubDeparted && !party.Departed)
            {
                foreach (var b in party.Members) party.CatchingUp.Add(b.DatabaseID);
                party.Departed = true;
            }
            if (!party.Departed)
            {
                Vector3 p = party.HubPost;
                party.View = new(raid.Definition.Id, $"Assembling at {raid.Hub.Name}",
                    new($"realm-event-{raid.Definition.Id}", raid.Definition.Name, raid.Hub.Name, raid.Hub.Region,
                        (int)p.X, (int)p.Y, (int)p.Z, false, false, 50), true, true);
                return;
            }
            UpdateDestinationView(raid, party);
            if (raid.Started || party.Members.All(b => party.CatchingUp.Contains(b.DatabaseID)))
                party.OutboundLeg = raid.OutboundSeams.Length;
            // Follow the retained, validated zone sequence instead of repeatedly
            // choosing a new shortest zone route that can bounce at blocked seams.
            while (party.OutboundLeg < raid.OutboundSeams.Length)
            {
                Vector3 p = raid.OutboundSeams[party.OutboundLeg];
                Zone targetZone = WorldMgr.GetRegion(raid.Hub.Region)?.GetZone((int)p.X,(int)p.Y);
                if (party.Members.Any(b => b.IsAlive && !party.CatchingUp.Contains(b.DatabaseID) &&
                    b.CurrentRegionID == raid.Hub.Region && b.CurrentZone == targetZone &&
                    b.IsWithinRadius(new Point3D((int)p.X,(int)p.Y,(int)p.Z),175)))
                { party.OutboundLeg++; continue; }
                party.View = new(raid.Definition.Id, $"Traveling from {raid.Hub.Name} — leg {party.OutboundLeg+1}/{raid.OutboundSeams.Length}",
                    new($"realm-event-{raid.Definition.Id}",raid.Definition.Name,raid.Definition.Name,raid.Hub.Region,
                        (int)p.X,(int)p.Y,(int)p.Z,false,false,50),true,false,true);
                return;
            }
            party.View = party.DestinationView;
        }

        private static void UpdateDestinationView(Raid raid, Party party)
        {
            if (raid.DungeonRoute is { } route)
            {
                Vector3 destination = raid.Started ? route.Destination : party.Staging;
                ushort region = raid.Started ? raid.Definition.Region : route.Entrance.SourceRegion;
                string target = raid.Started ? route.TargetName : raid.Definition.Name;
                bool queued = false;
                if (raid.Started && !TryBattlePost(raid, party, destination, region, out destination))
                {
                    queued = true;
                    region = party.Anchor.HasValue ? party.AnchorRegion : route.Entrance.SourceRegion;
                    destination = party.Anchor ?? party.Staging;
                }
                party.DestinationView = new(raid.Definition.Id, queued ? "RAID — waiting for a clear formation post" : raid.Started ? "RAID — " + route.Status : "Raid rally",
                    new($"realm-event-{raid.Definition.Id}", target, raid.Definition.Name, region,
                        (int)destination.X, (int)destination.Y, (int)destination.Z, region == raid.Definition.Region, false,
                        raid.Started ? route.TargetLevel : 50), !raid.Started || route.Hold || queued);
                return;
            }
            bool landed = (raid.Boss.Flags & GameNPC.eFlags.FLYING) == 0 &&
                raid.Boss.IsWithinRadius(DragonLairPlacement.Home(raid.Definition.Realm), 2000);
            bool hold = !raid.Started || !landed;
            Vector3 point = hold ? party.Staging : new(raid.Boss.X, raid.Boss.Y, raid.Boss.Z);
            if (!hold && !TryBattlePost(raid, party, point, raid.Definition.Region, out point))
            { point = party.Anchor ?? party.Staging; hold = true; }
            string state = !raid.Started ? !landed
                ? "Raid rally — dragon airborne" : "Raid rally"
                : !landed ? "Waiting for dragon landing" : hold ? "RAID — waiting for a clear formation post" : "RAID — fighting";
            party.DestinationView = new(raid.Definition.Id, state,
                new($"realm-event-{raid.Definition.Id}", raid.Boss.Name, raid.Boss.CurrentZone?.Description ?? raid.Definition.Name,
                    raid.Definition.Region, (int)point.X, (int)point.Y, (int)point.Z, false, false, raid.Boss.Level), hold);
        }

        private static bool TryBattlePost(Raid raid, Party party, Vector3 center, ushort region, out Vector3 point)
        {
            if (party.Anchor.HasValue && party.AnchorRegion == region &&
                Vector3.DistanceSquared(center, party.AnchorCenter) < 128 * 128)
            { point = party.Anchor.Value; return true; }
            Zone zone = WorldMgr.GetRegion(region)?.GetZone((int)center.X, (int)center.Y);
            Vector3[] occupied = raid.Parties.Values.Where(p => p != party && p.Anchor.HasValue && p.AnchorRegion == region)
                .Select(p => p.Anchor.Value).ToArray();
            if (!RealmRaidFormation.TryResolve(PathfindingProvider.Instance, zone, center, party.FormationSlot, occupied, out point))
                return false;
            party.Anchor = point; party.AnchorRegion = region; party.AnchorCenter = center;
            return true;
        }

        public static View GetView(Group group)
        {
            if (group == null) return null;
            lock (Sync) return Membership.TryGetValue(group, out var raid) && raid.Parties.TryGetValue(group, out var party) ? party.View : null;
        }

        // A late or released member follows the current encounter, never an
        // obsolete town muster or a dead leader's old position.
        public static View GetTravelView(GameBot bot)
        {
            Group group = bot?.Group;
            if (group == null) return null;
            lock (Sync)
            {
                if (!Membership.TryGetValue(group, out var raid) || !raid.Parties.TryGetValue(group, out var party)) return null;
                return raid.HubDeparted && party.CatchingUp.Contains(bot.DatabaseID)
                    ? party.DestinationView ?? party.View : party.View;
            }
        }

        // Late/released members skip muster attendance, not the connected exterior route.
        // Navigation is performed by their controller outside the expedition lock.
        public static bool TryIndependentApproach(GameBot bot, out Vector3 destination, out Vector3? via)
        {
            destination = default; via = null;
            Group group = bot?.Group;
            if (group == null) return false;
            lock (Sync)
            {
                if (!Membership.TryGetValue(group, out var raid) || !raid.Parties.TryGetValue(group, out var party) ||
                    !raid.HubDeparted || bot.CurrentRegionID != raid.Hub.Region ||
                    !(raid.Started || party.CatchingUp.Contains(bot.DatabaseID))) return false;
                destination = raid.DungeonRoute != null ? party.Staging :
                    new Vector3(party.DestinationView.Camp.X, party.DestinationView.Camp.Y, party.DestinationView.Camp.Z);
                via = raid.Hub.Via;
                return true;
            }
        }

        public static void RejoinAfterRelease(GameBot bot)
        {
            Group group = bot?.Group;
            if (group == null) return;
            lock (Sync)
                if (Membership.TryGetValue(group, out var raid) && raid.Parties.TryGetValue(group, out var party))
                {
                    party.CatchingUp.Add(bot.DatabaseID);
                    party.CorpseSince.Remove(bot.DatabaseID);
                }
        }

        public static void ClearCorpseWait(GameBot bot)
        {
            Group group = bot?.Group;
            if (group == null) return;
            lock (Sync)
                if (Membership.TryGetValue(group, out var raid) && raid.Parties.TryGetValue(group, out var party))
                    party.CorpseSince.Remove(bot.DatabaseID);
        }

        public static AutonomousBotGroupCoordinator.PveCorpseDisposition CorpseRecovery(GameBot bot)
        {
            Group group = bot?.Group;
            lock (Sync)
            {
                if (group == null || !Membership.TryGetValue(group, out var raid) ||
                    !raid.Parties.TryGetValue(group, out var party))
                    return AutonomousBotGroupCoordinator.PveCorpseDisposition.NotManaged;
                long now = GameLoop.GameLoopTime;
                if (!party.CorpseSince.TryGetValue(bot.DatabaseID, out long since))
                    party.CorpseSince[bot.DatabaseID] = since = now;
                // Bounded per-corpse wait, even during an endless nearby fight.
                // Shared resurrection claims still select one safe caster.
                long deadline = since + 90_000;
                bool casting = raid.Support.OfType<GameBot>().Any(b => b.IsAlive && b.IsCasting &&
                    b.castingComponent.SpellHandler is { } cast && cast.Target == bot &&
                    cast.Spell.SpellType == eSpellType.Resurrect &&
                    BotGroupSupport.CanFinishResurrectionBeforeRelease(cast.CastStartTick, deadline, now, cast.Spell.CastTime));
                if (now < deadline || casting)
                    return AutonomousBotGroupCoordinator.PveCorpseDisposition.HoldForResurrection;
                party.CorpseSince.Remove(bot.DatabaseID);
                return AutonomousBotGroupCoordinator.PveCorpseDisposition.ReleaseAndRejoin;
            }
        }

        public static object SupportScope(GameBot bot)
        {
            Group group = bot?.Group;
            lock (Sync)
                if (group != null && Membership.TryGetValue(group, out var raid)) return raid;
            return RealmWarbandSupport.Get(bot)?.Scope ?? bot?.Group;
        }

        public static bool HasSharedSupport(GameBot bot) => GetView(bot?.Group) != null || RealmWarbandSupport.Get(bot) != null;

        public static IGameStaticItemOwner LootOwner(GameNPC victim, IEnumerable<GameBot> contributors)
        {
            lock (Sync)
            {
                GameBot[] damaging = contributors.ToArray();
                Raid raid = damaging.Select(b => b?.Group).Where(group => group != null).Select(group => Membership.GetValueOrDefault(group))
                    .FirstOrDefault(r => r != null && r.Started && r.Definition.Region == victim.CurrentRegionID);
                if (raid == null)
                {
                    // Completion can race the native death/loot callback. Keep
                    // only the final dead bosses' former roster briefly, not an
                    // ongoing entitlement to unrelated kills after disbanding.
                    if (!CompletedLoot.TryGetValue(victim, out var completed) || completed.Expires <= GameLoop.GameLoopTime ||
                        !damaging.Any(b => completed.Members.Contains(b))) return null;
                    return new RealmRaidLootOwner(completed.Members, completed.Ledger);
                }
                GameBot[] nearby = raid.Support.OfType<GameBot>().Where(b => b.CurrentRegionID == victim.CurrentRegionID &&
                    b.IsWithinRadius(victim, WorldMgr.VISIBILITY_DISTANCE)).Distinct().ToArray();
                return nearby.Length == 0 ? null : new RealmRaidLootOwner(nearby, raid.LootLedger);
            }
        }

        public static GameLiving[] SupportMembers(GameBot bot)
        {
            Group group = bot?.Group;
            lock (Sync)
                if (group != null && Membership.TryGetValue(group, out var raid)) return raid.Support;
            // Never acquire a group lock while holding the expedition lock;
            // group disbanding calls RemoveParty in the opposite direction.
            return RealmWarbandSupport.Get(bot)?.Members ?? bot?.Group?.GetMembersInTheGroup().ToArray() ?? (bot == null ? [] : [bot]);
        }

        // At most four bounded roster scans per expedition per second, even
        // when hundreds of combatants are hit. Normal eight-person defense is
        // immediate and unchanged; this only wakes nearby sister parties.
        public static GameLiving[] ClaimNearbyDefenseBroadcast(GameBot victim, long now)
        {
            if (victim?.Group == null || !DefenseGroups.ContainsKey(victim.Group)) return [];
            lock (Sync)
            {
                if (victim?.Group == null || !Membership.TryGetValue(victim.Group, out var raid) ||
                    now < raid.NextDefenseBroadcast) return [];
                raid.NextDefenseBroadcast = now + 250;
                return raid.Support;
            }
        }

        public static bool SameExpedition(GameBot first, GameBot second)
        {
            if (first?.IsAutonomousWorldBot != true || second?.IsAutonomousWorldBot != true) return false;
            lock (Sync) return first.Group != null && second.Group != null &&
                Membership.TryGetValue(first.Group, out var raid) && Membership.GetValueOrDefault(second.Group) == raid;
        }

        public static bool Protects(GameBot bot)
        {
            if (bot?.IsAlive != true || !bot.IsAutonomousWorldBot) return false;
            lock(Sync)
                if(bot.Group != null && Membership.TryGetValue(bot.Group,out var raid) &&
                    raid.HubDeparted && !raid.Started && !bot.IsOnStableMasterRoute &&
                    bot.CurrentRegionID == (raid.Definition.IsDungeon ? raid.DungeonRoute.Entrance.SourceRegion : raid.Definition.Region) &&
                    AtStaging(raid,bot)) return true;
            View view = GetTravelView(bot);
            return view != null && bot.CurrentRegionID == view.Camp.RegionId &&
                (view.Hold && bot.IsWithinRadius(new Point3D(view.Camp.X, view.Camp.Y, view.Camp.Z), 600) ||
                 bot.InCombat && bot.IsWithinRadius(new Point3D(view.Camp.X, view.Camp.Y, view.Camp.Z), 4000));
        }

        public static IEnumerable<GameLiving> SupportPets(GameBot bot, int range)
        {
            if (!HasSharedSupport(bot)) yield break;
            var visited = new HashSet<IControlledBrain>();
            foreach (GameLiving owner in SupportMembers(bot))
                foreach (GameNPC pet in BotGroupPetBuffTargets.AttachedTree(owner.ControlledBrain, owner, visited))
                    if (pet.IsAlive && pet.Realm == bot.Realm && pet.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(pet, range))
                        yield return pet;
        }

        public static void RemoveParty(Group group)
        {
            if (group == null) return;
            lock (Sync)
            {
                Released.Remove(group);
                if (Membership.Remove(group, out Raid raid))
                {
                    DefenseGroups.TryRemove(group, out _);
                    raid.Parties.Remove(group);
                    raid.Support = raid.Parties.Values.SelectMany(p => p.Members).Cast<GameLiving>().ToArray();
                }
            }
        }

        public static bool TryConsumeRelease(Group group, out string reason)
        {
            lock (Sync) return Released.Remove(group, out reason);
        }

        private static void End(Raid raid, long now, string reason)
        {
            string outcome = reason.StartsWith("Staging failed") ? "Failed rally" : reason.Contains("four-hour") ? "Timed out" :
                raid.Definition.IsDungeon ? raid.DungeonRoute?.Complete == true ? "Boss defeated" : "Ended (unconfirmed)" :
                !raid.Boss.IsAlive ? "Boss defeated" : "Encounter unavailable";
            RealmEventRecords.Finish(raid.Definition.Id, outcome, reason, raid.Support.Length);
            GameBot[] recipients = raid.Support.OfType<GameBot>().ToArray();
            foreach (GameNPC final in (raid.DungeonRoute?.FinalBosses ?? [raid.Boss]).Where(n => n != null && !n.IsAlive))
            {
                if (CompletedLoot.Count >= 32) CompletedLoot.Remove(CompletedLoot.MinBy(p => p.Value.Expires).Key);
                CompletedLoot[final] = new(recipients, raid.LootLedger, now + 60_000);
            }
            foreach (Group group in raid.Parties.Keys) { Membership.Remove(group); DefenseGroups.TryRemove(group, out _); Released[group] = reason; }
            raid.Parties.Clear();
            raid.Support = [];
            Raids.Remove(raid.Definition.Id);
            foreach (var reservation in Reservations.Where(p => p.Value == raid.Definition.Id).ToArray()) Reservations.TryRemove(reservation.Key, out _);
            Cooldowns[raid.Definition.Id] = now + (30 + Random.Shared.Next(61)) * 60_000L;
            RealmEventNotices.Queue(raid.Definition.Id, raid.Definition.Realm, RealmEventBanter.RaidOutcome(raid.Definition.Name, outcome));
        }

        public static bool ResetCooldown(string id)
        {
            lock (Sync) { if (Raids.ContainsKey(id)) return false; Cooldowns.Remove(id); return true; }
        }

        public static Summary[] Snapshot()
        {
            lock (Sync) return Definitions.Select(d =>
            {
                if (Raids.TryGetValue(d.Id, out var r)) return new Summary(d.Id, d.Name, GlobalConstants.RealmToName(d.Realm),
                    r.Started ? "RAID — " + (r.DungeonRoute?.Status ?? (DragonLanded(r) ? "underway" : "waiting for dragon landing")) :
                    $"Raid rally — {(r.Forced ? "forced" : "automatic")} — {(r.HubDeparted ? "travel/final staging" : r.Hub.Name)}" +
                        (GameLoop.GameLoopTime >= r.Deadline ? !DragonLanded(r) ? " — waiting for dragon landing" : " — waiting for arrivals" : ""),
                    r.Support.Length + r.ForcedParties.Sum(p => p.Count(IsEligible)),
                    !r.HubDeparted ? r.Parties.Values.Sum(p => PresentAtHub(r,p)) : !r.Started ? PresentAtStaging(r) :
                    r.Parties.Values.Sum(p => p.Members.Count(b => b.IsAlive && b.CurrentRegionID == p.View.Camp.RegionId &&
                        b.IsWithinRadius(new Point3D(p.View.Camp.X, p.View.Camp.Y, p.View.Camp.Z), 1500))),
                    RealmRaidRecruitmentPolicy.AutonomousMinimumPresent,
                    Math.Max(0, r.Deadline - GameLoop.GameLoopTime), r.Started ? "Battle" :
                        GameLoop.GameLoopTime >= r.Deadline ? "Waiting" : !r.HubDeparted ? "Muster" : "Staging");
                GameNPC boss = Bosses.GetValueOrDefault(d.Id);
                long respawn = boss is ApocInitializator initializer ? initializer.EncounterRespawnRemainingMilliseconds : boss?.RespawnRemainingMilliseconds ?? 0;
                long cooldown = Math.Max(0, Cooldowns.GetValueOrDefault(d.Id) - GameLoop.GameLoopTime);
                string state = Available(d.Id) ? "Available" : respawn > 0 ? "Respawning" : cooldown > 0 ? "Event cooldown" : "Encounter unavailable";
                return new Summary(d.Id, d.Name, GlobalConstants.RealmToName(d.Realm), state, 0, 0,
                    d.SuggestedBots, Math.Max(respawn, cooldown));
            }).ToArray();
        }
    }
}
