using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using DOL.AI.Brain;

namespace DOL.GS;

/// <summary>
/// In-memory coordination for already-assigned RvR groups. A committed force
/// remains attached through travel, deaths, and regrouping until the objective
/// resolves or its four-hour battle window expires. This layer neither writes
/// objective tenure nor changes keeps, relics, damage, or
/// rewards.  It only tells existing groups where real world combat can happen.
/// </summary>
public static partial class AutonomousRvrEventLayer
{
    public const long BattleLifetimeMilliseconds = 4 * 60 * 60_000L;

    [DOL.GS.ServerProperties.ServerProperty("autonomous", "rvr_siege_staged_assault",
        "Keep sieges gather the attackers at a siege camp and assault together (108 present, or a viable force at the one-hour deadline) instead of each warband walking in alone.", true)]
    public static bool StagedAssault = true;
    public const long AttendanceFreshnessMilliseconds = 15_000;
    /// <summary>
    /// A bot siege ends as defended once its attackers, having reached the keep, have been gone from it for ten minutes
    /// (run 2026-10-07: Midgard was wiped at Caer Erasleigh and Albion at Blendrake Faste, yet both events held 55+
    /// defenders on post for the rest of a four-hour timer). A player's own attack keeps its defense rules.
    /// </summary>
    public const long RepelMilliseconds = 10 * 60_000;
    public const long StragglerTimeoutMilliseconds = 8 * 60_000L;
    public const long TargetCooldownMilliseconds = 12 * 60_000;
    public const int OrdinaryAssaultCap = 128;
    public const int RelicAssaultCap = 240;
    public const int RelicCarrierRealmCap = 240;

    public enum Intent { Roam, HuntEnemy, AssaultKeep, AssaultRelicKeep, DefendEvent }

    public sealed record Force(string GroupId, eRealm Realm, int MemberCount, int AverageLevel, int HealerCount,
        bool CanSupplySiege = true, bool RoamingReserve = false, long[] MemberIds = null, int MinimumMemberLevel = 50);
    public sealed record LiveObjective(string Id, string Name, Intent Kind, eRealm OwningRealm, ushort RegionId,
        int X, int Y, int Z, bool IsRelicKeep, int EnemyCount, int FriendlyCount, int GuardStrength, int ClosedDoors,
        bool IsRelicCarrier = false, bool IsPortalKeep = false, bool UnderAttack = false);
    public sealed record Plan(Intent Intent, string TargetId, string Name, ushort RegionId, int X, int Y, int Z,
        bool IsSharedEvent, string Reason);

    private sealed class ActiveEvent
    {
        public required string TargetId;
        public required LiveObjective Target;
        public required eRealm AttackerRealm;
        public required eRealm DefenderRealm;
        public required bool RelicKeep;
        public required long ExpiresTick;
        public bool BattleStarted;
        public bool PreparationNoticeSent;
        public long CreatedTick;
        public bool AttackObserved;
        public bool ThirdRealmIntervenes = Random.Shared.NextDouble() < RealmEventPolicy.ThirdRealmChance;
        public bool DefenseMustering, DefenseMarching;
        public long DefenseMusterDeadlineTick, DefenseMusterStartTick;
        /// <summary>The losing realm's one counterattack after a player took its keep (StartRetake).</summary>
        public bool Retake;
        public int RetakeCap;
        public bool DefenseMusterExtended;
        public readonly Dictionary<long, (string Force, long Tick, GameBot Bot)> DefenseMustered = new();
        public long BattleStartedTick;
        /// <summary>Attackers gathered when the army marched (sizes the defense, DefenderCapFor).</summary>
        public int MarchedAttackers;
        /// <summary>Last time any attacker was present at the target after the battle began (RepelMilliseconds).</summary>
        public long AttackersLastSeenTick;
        public bool DefenseReaction;
        public DefenseScale DefenseScale;
        public long LastPressureTick;
        public string PlayerAccount;
        // Staged assault, first stage: the attacking warbands gather at their own border keep and
        // march together once enough are there (or at the muster deadline).
        public bool Marching = true;
        public long MusterDeadlineTick;
        public bool MusterExtended;
        public readonly Dictionary<long, (string Force, long Tick, GameBot Bot, Vector3 Position)> Mustered = new();
        // The marching column: one leader walks the route, the mustered army keeps with it (run 17: armies that
        // walked 50,000+ units one by one lost half their strength to roaming enemies before the siege camp).
        public GameBot MarchLeader;
        public readonly Dictionary<long, (string Force, eRealm Realm, long Tick, GameBot Bot, Vector3 Position, ushort Region)> Present = new();
        public readonly Dictionary<string, int[]> Slots = new(StringComparer.Ordinal);
        public readonly Dictionary<long, (string Force, long ProgressTick, double BestDistance, Vector3 Position, ushort Region)> Travel = new();
        public readonly Dictionary<string, int> Attackers = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Defenders = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ThirdRealm = new(StringComparer.Ordinal);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, ActiveEvent> Events = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (long ExpiresTick, Dictionary<eRealm, Dictionary<string, int>> Participants)> CarrierEvents = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LiveObjective> CarrierTargets = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> Cooldowns = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ReleasedForces = new(StringComparer.Ordinal);
    private static long _nextStragglerSweep;
    private static readonly HashSet<string> SelectedKeeps = new(StringComparer.Ordinal);
    private static readonly HashSet<string> SelectedRelics = new(StringComparer.Ordinal);

    public static bool ForceStart(LiveObjective target, eRealm attacker, long now, out string reason)
    {
        lock (Sync)
        {
            if (target == null || target.IsPortalKeep || target.IsRelicCarrier ||
                attacker is not (eRealm.Albion or eRealm.Midgard or eRealm.Hibernia) ||
                target.OwningRealm == attacker)
            { reason = "Choose an enemy capturable keep and an attacking realm."; return false; }
            if (Events.ContainsKey(target.Id) || OnCooldown(target.Id, now))
            { reason = "This objective already has an event or an active cooldown."; return false; }
            // A player may force as many sieges as they like (owner, 2026-10-07); the automatic
            // scheduler's one-attack-per-realm and start-chance limits never apply here.
            Events[target.Id] = new ActiveEvent
            {
                TargetId = target.Id, Target = target, AttackerRealm = attacker,
                DefenderRealm = target.OwningRealm, RelicKeep = target.IsRelicKeep,
                CreatedTick = now, ExpiresTick = now + RealmEventPolicy.RecruitmentMilliseconds(Random.Shared.NextDouble())
            };
            RealmEventNotices.Queue(target.Id, attacker, $"Warbands are marching on {target.Name}. Join the assault as you arrive!");
            RealmEventRecords.Begin(target.Id, target.Name, target.IsRelicKeep ? "Relic keep" : "Keep", GlobalConstants.RealmToName(attacker), "Forced continuous siege; defender: " + GlobalConstants.RealmToName(target.OwningRealm));
            // Staged assault: the attackers gather at their physical siege camp and the battle opens
            // when enough are there (or at the one-hour deadline with a viable force). Arriving one
            // at a time, attackers fed a defended keep piecemeal and no keep was ever taken.
            if (!StagedAssault)
                StartBattle(Events[target.Id], now, "forced assault opened; forces converge without formation staging");
            else
            {
                BeginMuster(Events[target.Id], now);
                RealmEventRecords.Progress(target.Id, "Rally", "Forced siege: attackers muster at their border keep, then march to the siege camp", 0, 0);
            }
            reason = "Assault opened. Reinforcements travel immediately; existing battles and roaming reserves are preserved.";
            return true;
        }
    }

    public static bool ResetCooldown(string id)
    {
        lock (Sync)
        {
            if (Events.ContainsKey(id) || CarrierEvents.ContainsKey(id)) return false;
            Cooldowns.Remove(id);
            return true;
        }
    }

    public static long CooldownRemaining(string id, long now)
    {
        lock (Sync) return Math.Max(0, Cooldowns.GetValueOrDefault(id) - now);
    }

    public static Dictionary<string, string> ForceTargets()
    {
        lock (Sync)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in Events.Values)
                foreach (string force in e.Attackers.Keys.Concat(e.Defenders.Keys).Concat(e.ThirdRealm.Keys))
                    result[force] = e.TargetId;
            foreach (var e in CarrierEvents)
                foreach (string force in e.Value.Participants.Values.SelectMany(p => p.Keys)) result[force] = e.Key;
            return result;
        }
    }

    public static LiveObjective ChooseVariedTarget(IEnumerable<LiveObjective> candidates, ISet<string> used, Random random = null)
    {
        var eligible = candidates.ToArray();
        if (eligible.Length == 0) return null;
        var fresh = eligible.Where(candidate => !used.Contains(candidate.Id)).ToArray();
        if (fresh.Length == 0) { used.Clear(); fresh = eligible; }
        return fresh[(random ?? Random.Shared).Next(fresh.Length)];
    }

    public static double MajorAssaultWeight(int memberCount, int averageLevel)
    {
        double size = Math.Clamp((memberCount - 2d) / 6d, 0d, 1d);
        double level = Math.Clamp((averageLevel - 25d) / 25d, 0d, 1d);
        return Math.Clamp(size * 0.55d + level * 0.45d, 0d, 1d);
    }

    /// <summary>
    /// Event notices create an inclination, never conscription.  The ceiling
    /// deliberately leaves a roaming reserve even while an event has open
    /// capacity; a force that opened an assault is already recorded and is not
    /// evaluated by this gate again.
    /// </summary>
    public static bool ShouldJoinActiveEvent(Force force, int existingParticipants, int cap,
        bool relicImportance, bool defending, double roll)
    {
        if (force == null || cap <= 0 || existingParticipants + force.MemberCount > cap)
            return false;

        double forceStrength = MajorAssaultWeight(force.MemberCount, force.AverageLevel);
        double importance = relicImportance ? 0.33d : 0.19d;
        double defenderNeed = defending ? 0.12d : 0d;
        double attendance = Math.Clamp(existingParticipants / (double)cap, 0d, 1d);
        // Attendance raises the attraction so established rallies fill before
        // new forces scatter across sparse objectives. A roaming reserve still
        // remains outside the event system.
        double probability = Math.Clamp(0.18d + importance + defenderNeed + forceStrength * 0.20d + attendance * 0.36d,
            0.12d, 0.78d);
        return Math.Clamp(roll, 0d, 1d) < probability;
    }

    public static Intent ChooseIntent(Force force, bool hasEnemy, bool hasOrdinaryKeep, bool hasRelicKeep, double roll)
    {
        roll = Math.Clamp(roll, 0d, 1d);
        if (force.MemberCount < 4 || force.AverageLevel < 50 || !force.CanSupplySiege)
            return hasEnemy ? Intent.HuntEnemy : Intent.Roam;

        double weight = MajorAssaultWeight(force.MemberCount, force.AverageLevel);
        if (hasRelicKeep && force.MemberCount >= 6 && force.AverageLevel >= 50 && roll < weight * 0.15d)
            return Intent.AssaultRelicKeep;
        if (hasOrdinaryKeep && roll < weight)
            return Intent.AssaultKeep;
        return hasEnemy ? Intent.HuntEnemy : Intent.Roam;
    }

    public const double RelicSiegeShare = 0.12;

    // Chance-based concurrent sieges (one attack per realm). Off in the legacy event-layer tests, which
    // document the one-siege-at-a-time ChooseIntent rules.
    public static bool SiegeScheduling = true;
    private static long _nextSiegeRoll, _lastSiegeActiveTick;
    private static bool _siegeStartReady;

    /// <summary>Chance per minute that a new siege starts, by how many are running and how long none has.</summary>
    public static double SiegeStartChancePerMinute(int active, double idleMinutes) => active switch
    {
        0 => Math.Min(1, 0.25 + 0.25 * Math.Max(0, idleMinutes)),
        1 => 0.04,
        2 => 0.012,
        _ => 0,
    };

    // Called under Sync. One roll a minute server-wide; a won roll waits for the next siege-capable force.
    private static bool SiegeStartAllowed(long nowTick)
    {
        int active = Events.Values.Count(entry => !entry.DefenseReaction);
        if (active > 0) _lastSiegeActiveTick = nowTick;
        if (_siegeStartReady) return true;
        if (nowTick < _nextSiegeRoll) return false;
        _nextSiegeRoll = nowTick + 60_000;
        double idleMinutes = active == 0 ? (nowTick - _lastSiegeActiveTick) / 60_000d : 0;
        _siegeStartReady = Random.Shared.NextDouble() < SiegeStartChancePerMinute(active, idleMinutes);
        return _siegeStartReady;
    }

    public static Plan ChooseOrJoin(Force force, IReadOnlyCollection<LiveObjective> objectives, long nowTick, double roll)
    {
        lock (Sync)
        {
            // Defensive boundary: protected hubs cannot become new events,
            // reinforcements or reserve patrols, even if a caller supplies one.
            Plan plan = ChooseCore(force, objectives?.Where(objective => !objective.IsPortalKeep).ToArray(), nowTick, roll);
            if (force != null)
            {
                if (plan?.IsSharedEvent == true && force.MemberIds != null && Events.TryGetValue(plan.TargetId, out var joined))
                    foreach (long id in force.MemberIds)
                        joined.Travel.TryAdd(id, (force.GroupId, nowTick, double.PositiveInfinity, default, 0));
                // A warband occupies exactly one event reservation, including
                // when switching from the siege to a moving relic carrier.
                foreach (ActiveEvent active in Events.Values.Where(active => active.TargetId != plan?.TargetId || plan?.IsSharedEvent != true))
                {
                    active.Attackers.Remove(force.GroupId);
                    active.Defenders.Remove(force.GroupId);
                    active.ThirdRealm.Remove(force.GroupId);
                    active.Slots.Remove(force.GroupId);
                }
                foreach (var entry in CarrierEvents.Where(entry => entry.Key != plan?.TargetId || plan?.IsSharedEvent != true))
                    foreach (var realm in entry.Value.Participants.Values) realm.Remove(force.GroupId);
            }
            return plan;
        }
    }

    private static Plan ChooseCore(Force force, IReadOnlyCollection<LiveObjective> objectives, long nowTick, double roll)
    {
        if (force == null || string.IsNullOrWhiteSpace(force.GroupId) || force.Realm == eRealm.None || objectives == null)
            return null;

        lock (Sync)
        {
            Cleanup(nowTick, objectives);
            if (force.AverageLevel < 50 || force.MinimumMemberLevel < 50)
                return ReservePlan(force, objectives.Where(o => !o.IsRelicCarrier && o.Kind is not Intent.AssaultKeep and not Intent.AssaultRelicKeep).ToArray());
            if (ReleasedForces.ContainsKey(force.GroupId)) return null;

            Plan committed = CommittedPlan(force, objectives);
            if (committed != null)
                return committed;

            if (force.RoamingReserve)
                return ReservePlan(force, objectives);

            LiveObjective carrier = objectives.FirstOrDefault(objective => objective.IsRelicCarrier && !OnCooldown(objective.Id, nowTick));
            if (carrier != null)
            {
                if (TryJoinCarrier(carrier, force, nowTick, roll))
                    return ToPlan(force.Realm == carrier.OwningRealm ? Intent.DefendEvent : Intent.HuntEnemy, carrier, true,
                        force.Realm == carrier.OwningRealm
                            ? "Escorting the live allied relic carrier with capped support."
                            : "Contesting the live enemy relic carrier with capped three-realm participation.");
                return ReservePlan(force, objectives.Where(objective => objective.Id != carrier.Id).ToArray());
            }

            // Prefer completing the best-attended compatible rally instead of
            // spreading warbands across several nearly empty objectives.
            foreach (ActiveEvent active in Events.Values
                         .Where(active => !active.DefenseReaction)
                         .OrderBy(active => active.BattleStarted)
                         .ThenByDescending(active => JoinPriority(active, force))
                         .ThenBy(active => active.TargetId, StringComparer.Ordinal))
            {
                LiveObjective target = objectives.FirstOrDefault(objective => objective.Id == active.TargetId);
                if (target == null)
                    continue;
                if (!IsLatestDefenseFocus(active)) continue;
                if (target.UnderAttack) active.AttackObserved = true;
                // The attacking commitment raises the alarm before combat. Defenders
                // form next; the third realm joins after both primary forces exist.
                if (force.Realm != active.AttackerRealm && !(SiegeScheduling
                        ? RealmEventPolicy.CanRespond(force.Realm == active.DefenderRealm, active.AttackObserved,
                            active.BattleStarted, active.ThirdRealmIntervenes, nowTick - active.BattleStartedTick)
                        : RealmEventPolicy.CanRecruitRealm(force.Realm == active.DefenderRealm, active.AttackObserved || active.BattleStarted,
                            active.Attackers.Values.Sum(), active.Defenders.Values.Sum(), Capacity(active))))
                    continue;
                if (force.Realm == active.AttackerRealm)
                {
                    if (force.AverageLevel >= 50 && (!active.BattleStarted || active.Attackers.ContainsKey(force.GroupId) ||
                        ShouldJoinActiveEvent(force, active.Attackers.Values.Sum(), Capacity(active), active.RelicKeep, false, roll)) &&
                        TryJoin(active.Attackers, force, Capacity(active)))
                        return ToPlan(Intent: active.RelicKeep ? Intent.AssaultRelicKeep : Intent.AssaultKeep, target, true,
                            $"Choosing to rally to the active {(active.RelicKeep ? "relic-keep" : "keep")} assault; participation is capped.");
                    continue;
                }
                if (force.Realm == active.DefenderRealm)
                {
                    if (force.AverageLevel >= 50 && (!active.BattleStarted || active.Defenders.ContainsKey(force.GroupId) ||
                        ShouldJoinActiveEvent(force, active.Defenders.Values.Sum(), Capacity(active), active.RelicKeep, true, roll)) &&
                        TryJoin(active.Defenders, force, DefenderCapFor(active)))
                    {
                        if (SiegeScheduling && !active.DefenseMustering) BeginDefenseMuster(active, nowTick);
                        return ToPlan(Intent.DefendEvent, target, true,
                            "Choosing to converge to defend the live opposing-realm assault; participation is capped.");
                    }
                    continue;
                }
                // The third realm contests the same event under its own cap;
                // it must never overwrite the first attacker's registration.
                if (force.AverageLevel >= 50 && (!active.BattleStarted || active.ThirdRealm.ContainsKey(force.GroupId) ||
                    ShouldJoinActiveEvent(force, active.ThirdRealm.Values.Sum(), Capacity(active), active.RelicKeep, false, roll)) &&
                    TryJoin(active.ThirdRealm, force, SiegeScheduling ? RealmEventPolicy.ThirdRealmCap(active.RelicKeep) : Capacity(active)))
                    return ToPlan(active.RelicKeep ? Intent.AssaultRelicKeep : Intent.AssaultKeep,
                        target, true, "Third-realm force contesting the siege under its own participation cap.");
            }

            // Concentrate the available siege forces on one objective. Realms
            // waiting for their alarm retain roaming, not a second empty rally.
            // Several sieges can run at once, one attack per realm (Midgard on Albion while Hibernia hits
            // Midgard). A new one starts on a once-a-minute roll: likely within minutes when none is
            // running, occasional with one, rare with two (owner, 2026-10-07).
            if (CarrierEvents.Count != 0 || Events.Values.Any(active => !active.DefenseReaction &&
                    (!SiegeScheduling || active.AttackerRealm == force.Realm)))
                return ReservePlan(force, objectives);

            bool hasEnemy = objectives.Any(objective => objective.Kind == Intent.HuntEnemy && objective.EnemyCount > 0);
            LiveObjective relic = ChooseVariedTarget(objectives.Where(objective => objective.Kind == Intent.AssaultRelicKeep && objective.IsRelicKeep &&
                                                               objective.OwningRealm != force.Realm && !Events.ContainsKey(objective.Id) && !OnCooldown(objective.Id, nowTick))
                , SelectedRelics);
            LiveObjective keep = ChooseVariedTarget(objectives.Where(objective => objective.Kind == Intent.AssaultKeep && !objective.IsRelicKeep &&
                                                              objective.OwningRealm != force.Realm && !Events.ContainsKey(objective.Id) && !OnCooldown(objective.Id, nowTick))
                , SelectedKeeps);
            bool siegeCapable = force.MemberCount >= 4 && force.AverageLevel >= 50 && force.CanSupplySiege;
            if (SiegeScheduling && (keep != null || relic != null) && siegeCapable && !SiegeStartAllowed(nowTick))
                return ReservePlan(force, objectives);
            // Relic sieges are the larger, rarer event: about one new siege in eight, strong warbands only.
            Intent intent = !SiegeScheduling ? ChooseIntent(force, hasEnemy, keep != null, relic != null, roll)
                : !siegeCapable || keep == null && relic == null ? ChooseIntent(force, hasEnemy, false, false, roll)
                : relic != null && force.MemberCount >= 6 && force.AverageLevel >= 50 && (keep == null || Random.Shared.NextDouble() < RelicSiegeShare)
                    ? Intent.AssaultRelicKeep : keep != null ? Intent.AssaultKeep : ChooseIntent(force, hasEnemy, false, false, roll);
            LiveObjective selected = intent switch
            {
                Intent.AssaultRelicKeep => relic,
                Intent.AssaultKeep => keep,
                Intent.HuntEnemy => objectives.Where(objective => objective.Kind == Intent.HuntEnemy && objective.EnemyCount > 0)
                    .OrderBy(objective => Math.Abs(objective.EnemyCount - force.MemberCount)).FirstOrDefault(),
                _ => objectives.Where(objective => objective.Kind == Intent.Roam).OrderBy(_ => Random.Shared.Next()).FirstOrDefault() ??
                     objectives.Where(objective => objective.Kind == Intent.HuntEnemy).OrderBy(_ => Random.Shared.Next()).FirstOrDefault(),
            };
            if (selected == null)
                return null;

            if (intent is Intent.AssaultKeep or Intent.AssaultRelicKeep)
            {
                ActiveEvent active = new()
                {
                    TargetId = selected.Id,
                    Target = selected,
                    AttackerRealm = force.Realm,
                    DefenderRealm = selected.OwningRealm,
                    RelicKeep = selected.IsRelicKeep,
                    ExpiresTick = nowTick + RealmEventPolicy.RecruitmentMilliseconds(Random.Shared.NextDouble()),
                    CreatedTick = nowTick,
                };
                active.Attackers[force.GroupId] = force.MemberCount;
                Events[selected.Id] = active;
                _siegeStartReady = false;
                RealmEventRecords.Begin(selected.Id, selected.Name, selected.IsRelicKeep ? "Relic keep" : "Keep", GlobalConstants.RealmToName(force.Realm), "Automatic continuous siege; defender: " + GlobalConstants.RealmToName(selected.OwningRealm));
                if (!StagedAssault)
                    StartBattle(active, nowTick, "automatic assault opened; each assigned bot converges without formation staging");
                else
                {
                    BeginMuster(active, nowTick);
                    RealmEventRecords.Progress(selected.Id, "Rally", "Automatic siege: attackers muster at their border keep, then march to the siege camp", force.MemberCount, 0);
                }
                RealmEventNotices.Queue(active.TargetId, force.Realm, StagedAssault
                    ? $"Warbands are gathering to assault {selected.Name}. Join them at the siege camp!"
                    : $"Warbands are marching on {selected.Name}. Reinforcements join the battle on arrival.");
                (selected.IsRelicKeep ? SelectedRelics : SelectedKeeps).Add(selected.Id);
                return ToPlan(intent, selected, true,
                    $"Opening a time-limited {(selected.IsRelicKeep ? "relic-keep" : "keep")} assault; same-realm warbands may rally and defenders may answer.");
            }

            return ToPlan(intent, selected, false,
                intent == Intent.HuntEnemy ? "Small or lower-level warband is hunting a live opposing force." :
                "No major assault selected; roaming a live frontier objective.");
        }
    }

    private static Plan ToPlan(Intent Intent, LiveObjective target, bool shared, string reason) =>
        new(Intent, target.Id, target.Name, target.RegionId, target.X, target.Y, target.Z, shared, reason);

    private static Plan ReservePlan(Force force, IReadOnlyCollection<LiveObjective> objectives)
    {
        LiveObjective target = objectives.Where(objective => objective.Kind == Intent.Roam)
            .OrderBy(_ => Random.Shared.Next()).FirstOrDefault()
            ?? objectives.Where(objective => objective.Kind == Intent.HuntEnemy && !objective.IsRelicCarrier && objective.EnemyCount > 0)
                .OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        return target == null ? null : ToPlan(target.Kind == Intent.HuntEnemy ? Intent.HuntEnemy : Intent.Roam, target, false,
            "Keeping a deliberate roaming reserve instead of automatically joining the active event.");
    }

    /// <summary>
    /// Each realm's share of a bot siege. Run 2026-10-07: battle reinforcements filled defenders and the third realm to
    /// the attacker cap (64 each), so armies that marched with 33-34 met 54-56 defenders plus the keep's guards and every
    /// siege failed. Defenders answer the army that actually marched (DefenseRatio of it, at least 8); the third realm
    /// keeps its own cap.
    /// </summary>
    private static int SiegeRealmCap(ActiveEvent active, eRealm realm) =>
        realm == active.AttackerRealm ? Capacity(active) : realm == active.DefenderRealm ? DefenderCapFor(active) :
        SiegeScheduling ? RealmEventPolicy.ThirdRealmCap(active.RelicKeep) : Capacity(active);

    private static int DefenderCapFor(ActiveEvent active)
    {
        if (!SiegeScheduling) return Capacity(active);
        int cap = RealmEventPolicy.DefenderCap(active.RelicKeep);
        return active.MarchedAttackers <= 0 ? cap
            : Math.Clamp((int)Math.Round(active.MarchedAttackers * Math.Clamp(RealmEventPolicy.DefenseRatio, 0.25, 2.0)), 8, cap);
    }

    private static int Capacity(ActiveEvent active) => active.DefenseReaction ? 240 : active.Retake ? active.RetakeCap :
        SiegeScheduling ? RealmEventPolicy.AttackerCap(active.RelicKeep) : active.RelicKeep ? RelicAssaultCap : OrdinaryAssaultCap;

    // Battle start: the dynamic sieges use 85% of their attacker cap at the posts (half on a strike or at the
    // deadline); the legacy tests keep the 108/170 and 32/48 rules.
    private static bool Ready(ActiveEvent active, bool deadline, long nowTick) => SiegeScheduling
        ? Attendance(active, active.AttackerRealm, nowTick) >= RealmEventPolicy.DynamicStartThreshold(active.RelicKeep, deadline)
        : RealmEventPolicy.SiegeReady(active.RelicKeep, deadline, Attendance(active, active.AttackerRealm, nowTick),
            Attendance(active, active.DefenderRealm, nowTick));

    private static Plan CommittedPlan(Force force, IReadOnlyCollection<LiveObjective> objectives)
    {
        foreach (ActiveEvent active in Events.Values)
        {
            // A local route/candidate refresh is not an event cancellation.
            // Keep the committed destination while the normal route recovery
            // handles any temporary inability to reach it.
            LiveObjective target = objectives.FirstOrDefault(objective => objective.Id == active.TargetId) ?? active.Target;
            if (active.Attackers.ContainsKey(force.GroupId))
            {
                TryJoin(active.Attackers, force, Capacity(active));
                return ToPlan(active.RelicKeep ? Intent.AssaultRelicKeep : Intent.AssaultKeep, target, true,
                    "This force remains committed to the siege through defeat and regrouping.");
            }
            if (active.Defenders.ContainsKey(force.GroupId))
            {
                TryJoin(active.Defenders, force, DefenderCapFor(active));
                return ToPlan(Intent.DefendEvent, target, true,
                    "This force remains committed to defending the siege through defeat and regrouping.");
            }
            if (active.ThirdRealm.ContainsKey(force.GroupId))
            {
                TryJoin(active.ThirdRealm, force, SiegeRealmCap(active, OtherRealm(active)));
                return ToPlan(active.RelicKeep ? Intent.AssaultRelicKeep : Intent.AssaultKeep, target, true,
                    "This third-realm force remains committed to the siege through defeat and regrouping.");
            }
        }

        foreach (var entry in CarrierEvents)
        foreach (var realm in entry.Value.Participants)
        {
            if (!realm.Value.ContainsKey(force.GroupId))
                continue;
            LiveObjective target = objectives.FirstOrDefault(objective => objective.Id == entry.Key) ?? CarrierTargets.GetValueOrDefault(entry.Key);
            if (target == null)
                continue;
            TryJoin(realm.Value, force, RelicCarrierRealmCap);
            return ToPlan(force.Realm == target.OwningRealm ? Intent.DefendEvent : Intent.HuntEnemy, target, true,
                force.Realm == target.OwningRealm
                    ? "This force remains committed to the relic escort through defeat and regrouping."
                    : "This force remains committed to intercepting the relic through defeat and regrouping.");
        }
        return null;
    }

    public sealed record BattleNotice(string TargetId, string Kind, eRealm Attacker, eRealm Defender,
        int Attackers, int Defenders, int ThirdRealm, int CapPerRealm, long RemainingMilliseconds,
        bool BattleStarted = false, int PresentAttackers = 0, int PresentDefenders = 0, int PresentThirdRealm = 0,
        long MusterRemainingMilliseconds = -1);

    public static BattleNotice[] Snapshot()
    {
        lock (Sync)
        {
            Expire(GameLoop.GameLoopTime);
            return Events.Values.Where(entry => entry.ExpiresTick > GameLoop.GameLoopTime)
                .Select(entry => new BattleNotice(entry.TargetId, entry.DefenseReaction ? "SIEGE — immediate defense response" : entry.BattleStarted ? "SIEGE — ongoing" : entry.RelicKeep ? "Relic siege rally" : "Keep siege rally",
                    entry.AttackerRealm, entry.DefenderRealm, entry.Attackers.Values.Sum(), entry.Defenders.Values.Sum(),
                    entry.ThirdRealm.Values.Sum(), Capacity(entry),
                    Math.Max(0, entry.ExpiresTick - GameLoop.GameLoopTime), entry.BattleStarted,
                    Attendance(entry, entry.AttackerRealm, GameLoop.GameLoopTime),
                    Attendance(entry, entry.DefenderRealm, GameLoop.GameLoopTime),
                    Attendance(entry, OtherRealm(entry), GameLoop.GameLoopTime),
                    // gathering: time to the march order; 0 = the army is marching; -1 = no muster stage
                    entry.BattleStarted || entry.DefenseReaction || entry.MusterDeadlineTick == 0 ? -1
                        : entry.Marching ? 0 : Math.Max(1, entry.MusterDeadlineTick - GameLoop.GameLoopTime))).ToArray();
        }
    }

    public sealed record RallyOrder(string TargetId, eRealm Attacker, eRealm Defender, int[] Slots, long RemainingMilliseconds,
        bool HomeMuster = false);

    // Quicker rallies (owner 2026-10-07: relic rallies took over an hour): 12 minutes to gather, one 5-minute extension.
    public const long HomeMusterMilliseconds = 12 * 60_000;
    public const long MusterExtensionMilliseconds = 5 * 60_000;
    // The army marches once it can start the battle on arrival (108 for a keep, 170 for a relic keep,
    // or 85% of the attackers assigned when fewer). Run 14: marching at 64 sent half an army against
    // full-strength defenders; it lost the field before the battle could start (34 present, then 8).
    public static int MarchThreshold(bool relic, int assigned) =>
        Math.Max(8, Math.Min(relic ? 170 : 108, assigned * 85 / 100));

    /// <summary>The march leader reached the keep: the marched army attacks at once.</summary>
    public static void ReportArmyArrived(string targetId, long nowTick)
    {
        lock (Sync)
            if (Events.TryGetValue(targetId, out var active) && !active.BattleStarted && MarchedArmy(active))
                StartBattle(active, nowTick, "the marching army reached the keep and attacks at once; defenders and third-realm reinforcements may respond");
    }

    /// <summary>An army that mustered at home and has been given the march order (the start threshold already met).</summary>
    private static bool MarchedArmy(ActiveEvent active) =>
        active.Marching && active.MusterDeadlineTick != 0 && !active.DefenseReaction;

    /// <summary>
    /// The realm that lost a keep to a player's attack makes one organised attempt to take it back: a short muster at
    /// its outpost, a march, then the strike on arrival, sized like the defense that failed. It ends when the keep is
    /// retaken or after 30 minutes of battle; it never repeats (a retake is not a player defense).
    /// </summary>
    private static void StartRetake(ActiveEvent lost, eRealm newOwner, long nowTick)
    {
        eRealm retaker = lost.DefenderRealm;
        if (retaker is not (eRealm.Albion or eRealm.Midgard or eRealm.Hibernia) || retaker == newOwner) return;
        Cooldowns.Remove(lost.TargetId);
        var target = lost.Target with { OwningRealm = newOwner };
        var retake = new ActiveEvent
        {
            TargetId = target.Id, Target = target, AttackerRealm = retaker, DefenderRealm = newOwner,
            RelicKeep = lost.RelicKeep, CreatedTick = nowTick, Retake = true,
            RetakeCap = Math.Max(64, ResponseCap(true, lost.DefenseScale)),
            ExpiresTick = nowTick + RetakeMusterMilliseconds + RealmEventPolicy.RecruitmentMilliseconds(0),
        };
        Events[target.Id] = retake;
        BeginMuster(retake, nowTick, RetakeMusterMilliseconds);
        string loser = GlobalConstants.RealmToName(retaker);
        RealmEventNotices.Queue(target.Id, retaker, $"{target.Name} has fallen! Rally at the outpost: we take it back.");
        RealmEventNotices.QueueScreen(target.Id, retaker, $"{loser} rallies to retake {target.Name}!", AnnouncementKind.RvrBattleground);
        RealmEventNotices.QueueScreen(target.Id, newOwner, $"{loser} is rallying to take back {target.Name}!", AnnouncementKind.RvrBattleground);
        RealmEventRecords.Begin(target.Id, target.Name, target.IsRelicKeep ? "Relic keep" : "Keep", loser,
            "Retake attempt after a player captured the keep; one try");
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_RETAKE_STARTED target={target.Id} keep=\"{target.Name}\" retaker={loser} " +
            $"holder={GlobalConstants.RealmToName(newOwner)} cap={retake.RetakeCap} musterMinutes={RetakeMusterMilliseconds / 60_000}");
    }

    private static void BeginMuster(ActiveEvent active, long nowTick, long milliseconds = HomeMusterMilliseconds)
    {
        active.Marching = false;
        active.MusterDeadlineTick = nowTick + milliseconds;
        // The forward rally keeps its full hour after the march order.
        active.ExpiresTick = Math.Max(active.ExpiresTick, nowTick + HomeMusterMilliseconds + RealmEventPolicy.RecruitmentMilliseconds(0));
    }

    // The defense gathers as one quick rally (nearest friendly keep) and goes in together: it marches at
    // 85% of its cap or after six minutes. Trickling defenders were picked off one at a time.
    public const long DefenseMusterMilliseconds = 6 * 60_000;
    public const long DefenseMusterExtensionMilliseconds = 4 * 60_000;
    /// <summary>A player's attack: the defenders gather (80% of the force, at most ten minutes), then march in together.</summary>
    public const long PlayerDefenseMusterMilliseconds = 10 * 60_000;
    /// <summary>The losing realm musters (80% of its army, at most ten minutes) for its one retake attempt, then fights for 30.</summary>
    public const long RetakeMusterMilliseconds = 10 * 60_000;
    public const long RetakeBattleMilliseconds = 30 * 60_000;

    /// <summary>
    /// How many gathered defenders a player-triggered defense (and the retake that follows a lost keep) waits for
    /// before it marches (owner 2026-10-07: a huge force matters more than speed): 80% of the force, or the
    /// 10-minute muster runs out, whichever comes first. The third realm never musters (it stays chaotic).
    /// </summary>
    public static int DefenseWaveSize(int called) => Math.Max(Math.Min(called, 6), called * 80 / 100);

    private static void BeginDefenseMuster(ActiveEvent active, long nowTick, long milliseconds = DefenseMusterMilliseconds)
    {
        active.DefenseMustering = true;
        active.DefenseMarching = false;
        active.DefenseMusterDeadlineTick = nowTick + milliseconds;
        active.DefenseMusterStartTick = nowTick;
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_DEFENSE_MUSTER target={active.TargetId} defenders={GlobalConstants.RealmToName(active.DefenderRealm)} " +
            $"cap={(active.DefenseReaction ? ResponseCap(true, active.DefenseScale) : DefenderCapFor(active))} minutes={milliseconds / 60_000}");
    }

    private static int DefenseMusterAttendance(ActiveEvent active, long nowTick) => active.DefenseMustered.Values
        .Count(entry => nowTick - entry.Tick <= AttendanceFreshnessMilliseconds && entry.Bot?.IsAlive == true &&
            entry.Bot.ObjectState == GameObject.eObjectState.Active && active.Defenders.ContainsKey(entry.Force));

    private static void GiveDefenseMarch(ActiveEvent active, long nowTick, string reason)
    {
        if (active.DefenseMarching) return;
        active.DefenseMarching = true;
        RealmEventNotices.Queue(active.TargetId, active.DefenderRealm, $"The defense rally marches to relieve {active.Target.Name}!");
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_DEFENSE_MARCH target={active.TargetId} mustered={DefenseMusterAttendance(active, nowTick)} reason=\"{reason}\"");
    }

    private static int MusterAttendance(ActiveEvent active, long nowTick) => active.Mustered.Values
        .Count(entry => nowTick - entry.Tick <= AttendanceFreshnessMilliseconds && entry.Bot?.IsAlive == true &&
            entry.Bot.ObjectState == GameObject.eObjectState.Active && active.Attackers.ContainsKey(entry.Force));

    private static void GiveMarchOrder(ActiveEvent active, long nowTick, string reason)
    {
        if (active.Marching) return;
        active.Marching = true;
        active.MarchedAttackers = MusterAttendance(active, nowTick);
        active.ExpiresTick = Math.Max(active.ExpiresTick, nowTick + RealmEventPolicy.RecruitmentMilliseconds(0));
        RealmEventNotices.Queue(active.TargetId, active.AttackerRealm, $"The army marches on {active.Target.Name}! Form up at the siege camp.");
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_SIEGE_MARCH target={active.TargetId} mustered={MusterAttendance(active, nowTick)} reason=\"{reason}\"");
    }

    /// <summary>
    /// The live leader of a marching army (the mustered attacker with the lowest id; the next one takes over if it
    /// falls). Null before the march, once the battle has started, or for an event without a muster.
    /// </summary>
    public static GameBot MarchLeader(string targetId)
    {
        lock (Sync)
        {
            if (!Events.TryGetValue(targetId, out var active) || active.BattleStarted || !active.Marching || active.Mustered.Count == 0)
                return null;
            if (IsLiveArmyMember(active, active.MarchLeader)) return active.MarchLeader;
            active.MarchLeader = active.Mustered.Values.Select(entry => entry.Bot).Where(bot => IsLiveArmyMember(active, bot))
                .OrderBy(bot => bot.DatabaseID).FirstOrDefault();
            return active.MarchLeader;
        }
    }

    /// <summary>The mustered attackers still alive and in the world (the marching column).</summary>
    public static GameBot[] ArmyMembers(string targetId)
    {
        lock (Sync)
            return Events.TryGetValue(targetId, out var active) && !active.BattleStarted
                ? active.Mustered.Values.Select(entry => entry.Bot).Where(bot => IsLiveArmyMember(active, bot)).Distinct().ToArray()
                : [];
    }

    private static bool IsLiveArmyMember(ActiveEvent active, GameBot bot) =>
        bot != null && bot.IsAlive && bot.ObjectState == GameObject.eObjectState.Active &&
        active.Attackers.ContainsKey(bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}");

    /// <summary>An attacker at (or leaving) its border-keep muster post.</summary>
    public static void ReportMusterAttendance(string targetId, string forceId, long botId, bool inPosition, long nowTick,
        GameBot bot, Vector3 position)
    {
        lock (Sync)
        {
            if (!Events.TryGetValue(targetId, out var active)) return;
            if (active.DefenseMustering && !active.DefenseMarching && active.Defenders.ContainsKey(forceId))
            {
                if (inPosition) active.DefenseMustered[botId] = (forceId, nowTick, bot);
                else active.DefenseMustered.Remove(botId);
                int needed = active.DefenseReaction
                    ? DefenseWaveSize(active.Defenders.Values.Sum())
                    : Math.Max(4, DefenderCapFor(active) * 85 / 100);
                if (DefenseMusterAttendance(active, nowTick) >= needed)
                    GiveDefenseMarch(active, nowTick, "the defense rally has gathered");
                return;
            }
            if (active.Marching || !active.Attackers.ContainsKey(forceId)) return;
            if (inPosition) active.Mustered[botId] = (forceId, nowTick, bot, position);
            else active.Mustered.Remove(botId);
            int assigned = active.Attackers.Values.Sum();
            if (MusterAttendance(active, nowTick) >= (active.Retake ? DefenseWaveSize(assigned) : MarchThreshold(active.RelicKeep, assigned)))
                GiveMarchOrder(active, nowTick, "enough attackers mustered at the border keep");
        }
    }

    private static eRealm OtherRealm(ActiveEvent active) =>
        new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }.First(realm => realm != active.AttackerRealm && realm != active.DefenderRealm);

    private static Dictionary<string, int> Participants(ActiveEvent active, eRealm realm) =>
        realm == active.AttackerRealm ? active.Attackers : realm == active.DefenderRealm ? active.Defenders : active.ThirdRealm;

    public static RallyOrder GetRallyOrder(string forceId, eRealm realm, long nowTick)
    {
        lock (Sync)
        {
            Expire(nowTick);
            // Owner 2026-10-07: a player-triggered defense must answer as one organised wave, not a trickle (run 24:
            // 240 Midgard defenders came in a few at a time and 109 died before forming a line). Its defenders get
            // muster orders although the battle is already on; allied helpers and the third realm stay chaotic.
            var active = Events.Values.FirstOrDefault(entry => (!entry.BattleStarted ||
                    entry.DefenseReaction && realm == entry.DefenderRealm && entry.DefenseMustering && !entry.DefenseMarching) &&
                Participants(entry, realm).ContainsKey(forceId));
            if (active == null) return null;
            var participants = Participants(active, realm);
            int count = participants[forceId];
            // Reuse freed posts even when differently sized warbands leave
            // gaps. Exact 128 attendance must not depend on divisibility by eight.
            if (!active.Slots.TryGetValue(forceId, out int[] slots) || slots.Length != count)
            {
                var used = participants.Keys.Where(id => id != forceId && active.Slots.ContainsKey(id))
                    .SelectMany(id => active.Slots[id]).ToHashSet();
                slots = Enumerable.Range(0, Capacity(active)).Where(slot => !used.Contains(slot)).Take(count).ToArray();
                active.Slots[forceId] = slots;
            }
            return new(active.TargetId, active.AttackerRealm, active.DefenderRealm, slots, active.ExpiresTick - nowTick,
                !active.Marching && realm == active.AttackerRealm ||
                (SiegeScheduling || active.DefenseReaction) && realm == active.DefenderRealm && active.DefenseMustering && !active.DefenseMarching);
        }
    }

    public static bool IsRallying(string forceId, long nowTick)
    {
        lock (Sync)
            return Events.Values.Any(entry => !entry.BattleStarted && entry.ExpiresTick > nowTick &&
                (entry.Attackers.ContainsKey(forceId) || entry.Defenders.ContainsKey(forceId) || entry.ThirdRealm.ContainsKey(forceId)));
    }

    public static bool IsBattleForce(string forceId, long nowTick)
    {
        lock (Sync)
            return Events.Values.Any(entry => entry.BattleStarted && entry.ExpiresTick > nowTick &&
                (entry.Attackers.ContainsKey(forceId) || entry.Defenders.ContainsKey(forceId) || entry.ThirdRealm.ContainsKey(forceId))) ||
                CarrierEvents.Values.Any(entry => entry.ExpiresTick > nowTick && entry.Participants.Values.Any(realm => realm.ContainsKey(forceId)));
    }

    public static Plan KeepPlan(string forceId, eRealm realm, long nowTick)
    {
        lock (Sync)
        {
            var active = Events.Values.FirstOrDefault(entry => entry.ExpiresTick > nowTick && Participants(entry, realm).ContainsKey(forceId));
            return active == null ? null : ToPlan(realm == active.DefenderRealm ? Intent.DefendEvent :
                active.RelicKeep ? Intent.AssaultRelicKeep : Intent.AssaultKeep, active.Target, true,
                active.BattleStarted ? "Return to the ongoing siege" : "Assemble at the physical siege rally");
        }
    }

    private static int Attendance(ActiveEvent active, eRealm realm, long nowTick) => active.Present.Values
        .Where(entry => entry.Realm == realm && nowTick - entry.Tick <= AttendanceFreshnessMilliseconds && entry.Tick <= nowTick &&
            (entry.Bot == null || entry.Bot.IsAlive && entry.Bot.ObjectState == GameObject.eObjectState.Active &&
                entry.Bot.CurrentRegionID == entry.Region && (active.BattleStarted || FightingAtTarget(active, entry.Bot) ||
                !entry.Bot.InCombat && !entry.Bot.IsMoving && (entry.Bot.Brain as BotBrain)?.HasAggro != true &&
                Vector3.DistanceSquared(new(entry.Bot.X, entry.Bot.Y, entry.Bot.Z), entry.Position) <= AutonomousRvrRally.ArrivalRadius * AutonomousRvrRally.ArrivalRadius)))
        .GroupBy(entry => entry.Force).Sum(group => Math.Min(group.Count(), Participants(active, realm).GetValueOrDefault(group.Key)));

    /// <summary>
    /// Fighting inside the rally-post ring of the target counts as physically there (run 12/13: attackers
    /// fought defenders 2,000-9,500 units from the keep and were not counted because they had left
    /// their posts to fight).
    /// </summary>
    public const int FightingAttendanceRadius = 9_500;

    private static bool FightingAtTarget(ActiveEvent active, GameBot bot) =>
        (bot.InCombat || bot.IsAttacking || (bot.Brain as BotBrain)?.HasAggro == true) &&
        bot.CurrentRegionID == active.Target.RegionId &&
        Vector2.DistanceSquared(new(bot.X, bot.Y), new(active.Target.X, active.Target.Y)) <=
        (float)FightingAttendanceRadius * FightingAttendanceRadius;

    public static bool ProtectsParticipantFromInactivity(GameBot bot, long nowTick)
    {
        if (bot?.IsAutonomousWorldBot != true || !bot.IsAlive ||
            bot.ObjectState != GameObject.eObjectState.Active ||
            !AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.RvR)) return false;
        string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
        lock (Sync)
        {
            foreach (var active in Events.Values)
            {
                if (active.ExpiresTick <= nowTick || !Participants(active, bot.Realm).ContainsKey(force)) continue;
                if (active.BattleStarted)
                {
                    // Real combat is intentional participation, even without a kill.
                    if (IsParticipatingInBattleCombat(bot)) return true;
                    // The army may be waiting at its assigned rally post, or a
                    // defender may be holding an interior wall with no current
                    // attacker. Neither is an abandoned travel objective.
                    if (active.Present.TryGetValue(bot.DatabaseID, out var staged) &&
                        staged.Force == force && ReferenceEquals(staged.Bot, bot) &&
                        nowTick >= staged.Tick && nowTick - staged.Tick <= AttendanceFreshnessMilliseconds &&
                        bot.CurrentRegionID == staged.Region &&
                        Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), staged.Position) <=
                            AutonomousRvrRally.ArrivalRadius * AutonomousRvrRally.ArrivalRadius)
                        return true;
                    if (IsHoldingSiegeDefense(bot.Realm == active.DefenderRealm, bot.CurrentRegionID == active.Target.RegionId,
                        Vector2.DistanceSquared(new(bot.X, bot.Y), new(active.Target.X, active.Target.Y)),
                        Math.Abs(bot.Z - active.Target.Z))) return true;
                    continue;
                }
                if (active.Present.TryGetValue(bot.DatabaseID, out var present) &&
                    present.Force == force && present.Realm == bot.Realm && ReferenceEquals(present.Bot, bot) &&
                    present.Tick <= nowTick && nowTick - present.Tick <= AttendanceFreshnessMilliseconds &&
                    bot.CurrentRegionID == present.Region &&
                    Vector3.DistanceSquared(new(bot.X, bot.Y, bot.Z), present.Position) <=
                        AutonomousRvrRally.ArrivalRadius * AutonomousRvrRally.ArrivalRadius)
                    return true;
            }
            return IsParticipatingInBattleCombat(bot) && CarrierEvents.Values.Any(entry =>
                entry.ExpiresTick > nowTick && entry.Participants.TryGetValue(bot.Realm, out var forces) && forces.ContainsKey(force));
        }
    }

    private static bool IsParticipatingInBattleCombat(GameBot bot) => bot.InCombat || bot.IsAttacking ||
        bot.Group?.GetMembersInTheGroup().Any(member => member != bot && member.IsAlive &&
            member.CurrentRegionID == bot.CurrentRegionID && bot.IsWithinRadius(member, 2000) &&
            (member.InCombat || member.IsAttacking)) == true;

    public static bool IsHoldingSiegeDefense(bool defender, bool sameRegion, float distanceSquared, int heightDifference) =>
        defender && sameRegion && float.IsFinite(distanceSquared) && distanceSquared >= 0 &&
        distanceSquared <= 3000 * 3000 && heightDifference >= 0 && heightDifference <= 1500;

    public static void ReportAttendance(string targetId, string forceId, eRealm realm, long botId, bool inPosition, long nowTick,
        GameBot bot = null, Vector3 position = default)
    {
        lock (Sync)
        {
            Expire(nowTick);
            if (!Events.TryGetValue(targetId, out var active) ||
                !Participants(active, realm).ContainsKey(forceId)) return;
            if (inPosition) active.Present[botId] = (forceId, realm, nowTick, bot, position, bot?.CurrentRegionID ?? 0);
            else active.Present.Remove(botId);
            if (active.BattleStarted) return;
            // Run 18: the army that marched in (43 mustered, 48 at the posts) then sat at Nottmoor's posts
            // waiting for 85% of the cap (54) while it was whittled down to 19. Owner 2026-10-07 (all RvR, relic and
            // battleground sieges): once the threshold is reached the siege starts, no waiting at the keep. The
            // muster threshold was met before the march, so the army attacks as soon as it reaches the keep.
            if (MarchedArmy(active) && inPosition && realm == active.AttackerRealm)
                StartBattle(active, nowTick, "the marching army reached the keep and attacks at once; defenders and third-realm reinforcements may respond");
            else if (nowTick - active.CreatedTick >= RealmEventPolicy.EarliestAssaultMilliseconds &&
                Ready(active, active.Marching, nowTick))
                StartBattle(active, nowTick, "the attacking force is physically staged; defenders and third-realm reinforcements may respond");
        }
    }

    public static bool TryConsumeRelease(string forceId, long nowTick, out string reason)
    {
        // Called for every solo actor. With no siege, carrier event or pending
        // release there is nothing Expire could end or release, so answer
        // without the layer-wide lock. (Unsynchronized Count reads: a stale
        // zero only defers a brand-new release to the next check.)
        if (Events.Count == 0 && CarrierEvents.Count == 0 && ReleasedForces.Count == 0)
        {
            reason = null;
            return false;
        }
        lock (Sync)
        {
            Expire(nowTick);
            return ReleasedForces.Remove(forceId, out reason);
        }
    }

    public static void ReportTravel(string targetId, string forceId, long memberId, double distance, bool arrived, long nowTick, GameBot bot = null)
    {
        lock (Sync)
        {
            if (!Events.TryGetValue(targetId, out var active) || active.BattleStarted ||
                !(active.Attackers.ContainsKey(forceId) || active.Defenders.ContainsKey(forceId) || active.ThirdRealm.ContainsKey(forceId))) return;
            if (!active.Travel.TryGetValue(memberId, out var previous))
                previous = (forceId, nowTick, double.PositiveInfinity, default, 0);
            Vector3 position = bot == null ? previous.Position : new(bot.X, bot.Y, bot.Z);
            ushort region = bot?.CurrentRegionID ?? previous.Region;
            // Long multi-region journeys and real horse travel must not look
            // stalled merely because their coordinates are on another map.
            bool moved = bot?.IsAlive == true && (region != previous.Region ||
                Vector3.DistanceSquared(position, previous.Position) >= 256 * 256);
            if (arrived || moved || distance < previous.BestDistance - 128)
                active.Travel[memberId] = (forceId, nowTick, distance, position, region);
            else active.Travel[memberId] = previous;
        }
    }

    public static void RemoveForce(string forceId)
    {
        lock (Sync)
        {
            foreach (var active in Events.Values)
            {
                active.Attackers.Remove(forceId);
                active.Defenders.Remove(forceId);
                active.ThirdRealm.Remove(forceId);
                active.Slots.Remove(forceId);
                foreach (long id in active.Travel.Where(pair => pair.Value.Force == forceId).Select(pair => pair.Key).ToArray())
                    active.Travel.Remove(id);
                foreach (long id in active.Present.Where(pair => pair.Value.Force == forceId).Select(pair => pair.Key).ToArray())
                    active.Present.Remove(id);
            }
            foreach (var active in CarrierEvents.Values)
                foreach (var realm in active.Participants.Values) realm.Remove(forceId);
        }
    }

    /// <summary>
    /// Siege recruits who joined alone ("rvr-&lt;id&gt;" forces), per event and side, for the coordinator to merge into full
    /// parties (owner 2026-10-07: attackers and defenders move as full groups). Marching: the army has left its muster
    /// (or the battle is on), so smaller leftover parties are formed too.
    /// </summary>
    public sealed record SoloSiegeRoster(string EventId, eRealm Realm, bool Marching, long[] MemberIds);

    public static List<SoloSiegeRoster> SoloSiegeRosters(long nowTick)
    {
        var result = new List<SoloSiegeRoster>();
        lock (Sync)
        {
            foreach (ActiveEvent active in Events.Values.Where(entry => entry.ExpiresTick > nowTick))
            {
                bool marching = active.BattleStarted || active.Marching;
                foreach ((eRealm realm, Dictionary<string, int> side) in new[]
                         { (active.AttackerRealm, active.Attackers), (active.DefenderRealm, active.Defenders), (OtherRealm(active), active.ThirdRealm) })
                {
                    long[] solos = side.Keys.Where(force => force.StartsWith("rvr-", StringComparison.Ordinal))
                        .Select(force => long.TryParse(force.AsSpan(4), out long id) ? id : 0).Where(id => id > 0).ToArray();
                    if (solos.Length > 0) result.Add(new(active.TargetId, realm, marching, solos));
                }
            }
            foreach (var entry in CarrierEvents.Where(entry => entry.Value.ExpiresTick > nowTick))
                foreach ((eRealm realm, Dictionary<string, int> side) in entry.Value.Participants)
                {
                    long[] solos = side.Keys.Where(force => force.StartsWith("rvr-", StringComparison.Ordinal))
                        .Select(force => long.TryParse(force.AsSpan(4), out long id) ? id : 0).Where(id => id > 0).ToArray();
                    if (solos.Length > 0) result.Add(new(entry.Key, realm, true, solos));
                }
        }
        return result;
    }

    /// <summary>
    /// The coordinator formed a party from solo recruits of one event: their reservations become the party's (same cap),
    /// and every per-bot record (travel, presence, muster) now names the party's force.
    /// </summary>
    public static bool MergeSoloForces(string eventId, eRealm realm, long[] memberIds, string partyForceId)
    {
        lock (Sync)
        {
            string[] solos = memberIds.Select(id => $"rvr-{id}").ToArray();
            Dictionary<string, int> side = null;
            if (Events.TryGetValue(eventId, out ActiveEvent active))
                side = realm == active.AttackerRealm ? active.Attackers : realm == active.DefenderRealm ? active.Defenders : active.ThirdRealm;
            else if (CarrierEvents.TryGetValue(eventId, out var carrier))
                carrier.Participants.TryGetValue(realm, out side);
            if (side == null || solos.Any(force => !side.ContainsKey(force))) return false;
            foreach (string force in solos) side.Remove(force);
            side[partyForceId] = solos.Length;
            ReleasedForces.Remove(partyForceId);
            if (active != null)
            {
                foreach (string force in solos) active.Slots.Remove(force);
                var ids = memberIds.ToHashSet();
                foreach (long id in active.Travel.Keys.Where(ids.Contains).ToArray())
                    active.Travel[id] = active.Travel[id] with { Force = partyForceId };
                foreach (long id in active.Present.Keys.Where(ids.Contains).ToArray())
                    active.Present[id] = active.Present[id] with { Force = partyForceId };
                foreach (long id in active.Mustered.Keys.Where(ids.Contains).ToArray())
                    active.Mustered[id] = active.Mustered[id] with { Force = partyForceId };
                foreach (long id in active.DefenseMustered.Keys.Where(ids.Contains).ToArray())
                    active.DefenseMustered[id] = active.DefenseMustered[id] with { Force = partyForceId };
            }
            return true;
        }
    }

    public static long CarrierRemainingMilliseconds(string targetId, long nowTick)
    {
        lock (Sync)
            return CarrierEvents.TryGetValue(targetId, out var active) ? Math.Max(0, active.ExpiresTick - nowTick) : 0;
    }

    private static void StartBattle(ActiveEvent active, long nowTick, string reason)
    {
        active.BattleStarted = true;
        active.BattleStartedTick = nowTick;
        active.ExpiresTick = nowTick + (active.Retake ? RetakeBattleMilliseconds : BattleLifetimeMilliseconds);
        RealmEventRecords.Progress(active.TargetId, "Battle", reason,
            active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum(),
            Attendance(active, active.AttackerRealm, nowTick) + Attendance(active, active.DefenderRealm, nowTick) + Attendance(active, OtherRealm(active), nowTick));
        RealmEventNotices.Queue(active.TargetId, active.AttackerRealm,
            $"The assault on {active.Target.Name} is advancing. Reinforcements are welcome.");
        RealmEventNotices.Queue(active.TargetId, active.DefenderRealm,
            $"The assault on {active.Target.Name} has begun! Hold the walls and stand by your realm.");
        if (active.ThirdRealm.Count > 0) RealmEventNotices.Queue(active.TargetId, OtherRealm(active),
            $"The armies at {active.Target.Name} are committed to battle. Watch their flanks, warbands!");
        string keepKind = active.RelicKeep ? "relic keep" : "keep";
        string attacker = GlobalConstants.RealmToName(active.AttackerRealm);
        RealmEventNotices.QueueScreen(active.TargetId, active.AttackerRealm, $"Our army has laid siege to the {keepKind} {active.Target.Name}!", AnnouncementKind.RvrBattleground);
        RealmEventNotices.QueueScreen(active.TargetId, active.DefenderRealm, $"{active.Target.Name} is under siege by {attacker}!", AnnouncementKind.RvrBattleground);
        RealmEventNotices.QueueScreen(active.TargetId, OtherRealm(active),
            $"{attacker} has laid siege to the {GlobalConstants.RealmToName(active.DefenderRealm)} {keepKind} {active.Target.Name}.", AnnouncementKind.RvrBattleground);
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_SIEGE_STARTED target={active.TargetId} reason=\"{reason}\" " +
            $"present={Attendance(active, active.AttackerRealm, nowTick)}/{Attendance(active, active.DefenderRealm, nowTick)}/{Attendance(active, OtherRealm(active), nowTick)}");
    }

    private static void Expire(long nowTick)
    {
        foreach (var active in Events.Values)
        {
            if (!RealmEventBanter.ReminderDue(active.BattleStarted, active.PreparationNoticeSent, active.ExpiresTick - nowTick)) continue;
            active.PreparationNoticeSent = true;
            RealmEventNotices.Queue(active.TargetId, active.AttackerRealm, RealmEventBanter.SiegeReminder(active.Target.Name, false));
            RealmEventNotices.Queue(active.TargetId, active.DefenderRealm, RealmEventBanter.SiegeReminder(active.Target.Name, true));
            if (active.ThirdRealm.Count > 0) RealmEventNotices.Queue(active.TargetId, OtherRealm(active),
                $"The armies at {active.Target.Name} have about twenty minutes left to muster. Keep watch for an opening, scouts.");
        }
        foreach (var active in Events.Values.Where(entry => !entry.Marching && nowTick >= entry.MusterDeadlineTick).ToArray())
        {
            // Run 15: the 20-minute deadline sent 21 of 64 to Caer Erasleigh while the rest were still on the
            // road (medallions, frontier teleporter); too few to ever start the battle. A thin muster waits
            // once more, ten minutes, for the army that is genuinely on its way.
            int gathered = active.Mustered.Count;
            if (!active.Retake && !active.MusterExtended && gathered < RealmEventPolicy.DynamicStartThreshold(active.RelicKeep, true))
            {
                active.MusterExtended = true;
                active.MusterDeadlineTick = nowTick + MusterExtensionMilliseconds;
                active.ExpiresTick = Math.Max(active.ExpiresTick, active.MusterDeadlineTick + RealmEventPolicy.RecruitmentMilliseconds(0));
                var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
                if (log.IsInfoEnabled) log.Info($"RVR_MUSTER_EXTENDED target={active.TargetId} mustered={gathered} " +
                    $"needed={RealmEventPolicy.DynamicStartThreshold(active.RelicKeep, true)} minutes={MusterExtensionMilliseconds / 60_000}");
                continue;
            }
            GiveMarchOrder(active, nowTick, "muster deadline reached; marching with the attackers gathered");
        }
        foreach (var active in Events.Values.Where(entry => entry.DefenseMustering && !entry.DefenseMarching &&
                     nowTick >= entry.DefenseMusterDeadlineTick).ToArray())
        {
            // Run 16: the 6-minute defense muster marched 4 of 32 to Nottmoor; the owner wants one quick rally
            // that goes in together, not a trickle. A muster under half strong waits once more (4 minutes).
            // Run 25: a 3-minute player-defense muster marched with 1 of 175 (the rest were still walking in from 60k
            // away). A player's attack now musters until 80% have gathered or ten minutes pass (owner: a huge force
            // matters more than speed); there is no further extension.
            if (!active.DefenseReaction && !active.DefenseMusterExtended && active.DefenseMustered.Count < DefenderCapFor(active) / 2)
            {
                active.DefenseMusterExtended = true;
                active.DefenseMusterDeadlineTick = nowTick + DefenseMusterExtensionMilliseconds;
                var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
                if (log.IsInfoEnabled) log.Info($"RVR_DEFENSE_MUSTER_EXTENDED target={active.TargetId} mustered={active.DefenseMustered.Count} " +
                    $"cap={DefenderCapFor(active)} minutes={DefenseMusterExtensionMilliseconds / 60_000}");
                continue;
            }
            GiveDefenseMarch(active, nowTick, "defense muster deadline reached; marching with the defenders gathered");
        }
        foreach (var active in Events.Values.Where(entry => entry.BattleStarted && !entry.DefenseReaction).ToArray())
        {
            // Only an army that reached the keep and was then driven off: a battle can begin before its attackers
            // arrive (continuous assault, deadline start), and an army still on the road is not repelled.
            if (Attendance(active, active.AttackerRealm, nowTick) > 0) { active.AttackersLastSeenTick = nowTick; continue; }
            if (active.AttackersLastSeenTick > 0 && nowTick - active.AttackersLastSeenTick >= RepelMilliseconds)
                EndEvent(active, nowTick, $"Siege defended: no attacker reached the keep for {RepelMilliseconds / 60_000} minutes", active.DefenderRealm);
        }
        foreach (var active in Events.Values.Where(entry => entry.ExpiresTick <= nowTick).ToArray())
        {
            if (active.BattleStarted)
                EndEvent(active, nowTick, active.DefenseReaction ? "Siege defended: four-hour defense response expired" : "Siege defended: four-hour battle timer expired");
            else if (Ready(active, true, nowTick))
                StartBattle(active, nowTick, "preparation deadline reached with a viable attacking force");
            else
                EndEvent(active, nowTick, "Rally failed: insufficient physical attackers at the one-hour deadline");
        }
        if (nowTick >= _nextStragglerSweep)
        {
            _nextStragglerSweep = nowTick + 10_000;
            foreach (var active in Events.Values.Where(entry => !entry.BattleStarted).ToArray())
            foreach (var stalled in active.Travel.Where(pair => nowTick - pair.Value.ProgressTick >= StragglerTimeoutMilliseconds)
                         .GroupBy(pair => pair.Value.Force).ToArray())
            {
                // Missing members remain assigned so they can reinforce the
                // deadline-started battle. Keep bounded diagnostics, not eviction.
                string reason = $"Assigned members {string.Join(",", stalled.Select(pair => pair.Key))} made no approach progress for eight minutes; retaining siege assignment";
                foreach (var member in stalled)
                    active.Travel[member.Key] = member.Value with { ProgressTick = nowTick };
                var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
                if (log.IsWarnEnabled) log.Warn($"RVR_RALLY_DELAY target={active.TargetId} force={stalled.Key} reason=\"{reason}\"");
            }
        }
        foreach (var entry in CarrierEvents.Where(pair => pair.Value.ExpiresTick <= nowTick).ToArray())
        {
            foreach (eRealm realm in entry.Value.Participants.Keys)
                RealmEventNotices.Queue(entry.Key, realm, $"Our time in the struggle for {CarrierTargets.GetValueOrDefault(entry.Key)?.Name ?? "the relic"} has run out. The escort and interception forces are standing down.");
            RealmEventRecords.Finish(entry.Key, "Timed out", "Relic escort/interception: four-hour battle timer expired.");
            foreach (string force in entry.Value.Participants.Values.SelectMany(realm => realm.Keys))
                ReleasedForces[force] = "Relic event ended: four-hour battle timer expired";
            CarrierEvents.Remove(entry.Key);
            CarrierTargets.Remove(entry.Key);
            Cooldowns[entry.Key] = nowTick + TargetCooldownMilliseconds;
        }
    }

    private static void EndEvent(ActiveEvent active, long nowTick, string reason, eRealm winner = eRealm.None)
    {
        foreach (eRealm realm in new[] { active.AttackerRealm, active.DefenderRealm, OtherRealm(active) })
            RealmEventNotices.Queue(active.TargetId, realm, RealmEventBanter.SiegeOutcome(active.Target.Name,
                realm == active.DefenderRealm, reason.StartsWith("Siege defended"), reason.StartsWith("Rally failed"), winner, realm));
        RealmEventRecords.Finish(active.TargetId, reason.StartsWith("Rally failed") ? "Failed rally" : reason.StartsWith("Keep captured") ? "Captured" :
            reason.StartsWith("Siege defended") ? reason.Contains("expired") ? "Defended (timeout)" : "Defended" : "Ended (unconfirmed)", reason,
            active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum());
        foreach (string force in active.Attackers.Keys.Concat(active.Defenders.Keys).Concat(active.ThirdRealm.Keys))
            ReleasedForces[force] = reason;
        Events.Remove(active.TargetId);
        Cooldowns[active.TargetId] = nowTick + TargetCooldownMilliseconds;
        var log = DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrEventLayer));
        if (log.IsInfoEnabled) log.Info($"RVR_EVENT_ENDED target={active.TargetId} reason=\"{reason}\"");
    }

    private static bool TryJoin(Dictionary<string, int> participants, Force force, int cap)
    {
        if (participants.Values.Sum() - participants.GetValueOrDefault(force.GroupId) + force.MemberCount > cap)
            return false;
        participants[force.GroupId] = force.MemberCount;
        return true;
    }

    private static int AssaultPressure(Force force, LiveObjective objective) =>
        objective.EnemyCount * 4 + objective.GuardStrength * 2 + objective.ClosedDoors * 3 - force.MemberCount * 2;

    private static double JoinPriority(ActiveEvent active, Force force)
    {
        Dictionary<string, int> compatible = force.Realm == active.AttackerRealm ? active.Attackers :
            force.Realm == active.DefenderRealm ? active.Defenders : active.ThirdRealm;
        double compatibleFill = compatible.Values.Sum() / (double)Capacity(active);
        double totalFill = (active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum()) /
                           (double)(Capacity(active) * 3);
        return compatibleFill * 0.8d + totalFill * 0.2d;
    }

    private static bool OnCooldown(string targetId, long nowTick) => Cooldowns.TryGetValue(targetId, out long until) && until > nowTick;

    private static bool TryJoinCarrier(LiveObjective carrier, Force force, long nowTick, double roll)
    {
        string carrierId = carrier.Id;
        CarrierTargets[carrierId] = carrier;
        if (!CarrierEvents.TryGetValue(carrierId, out var active))
        {
            active = (nowTick + BattleLifetimeMilliseconds, new Dictionary<eRealm, Dictionary<string, int>>());
            CarrierEvents[carrierId] = active;
            RealmEventRecords.Begin(carrierId, carrier.Name, "Relic", GlobalConstants.RealmToName(carrier.OwningRealm), "Relic escort / interception");
            RealmEventRecords.Progress(carrierId, "Battle", "Relic escort / interception", 0, 0);
        }
        if (!active.Participants.TryGetValue(force.Realm, out Dictionary<string, int> realmParticipants))
            active.Participants[force.Realm] = realmParticipants = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!realmParticipants.ContainsKey(force.GroupId) &&
            !ShouldJoinActiveEvent(force, realmParticipants.Values.Sum(), RelicCarrierRealmCap, true, false, roll))
            return false;
        return TryJoin(realmParticipants, force, RelicCarrierRealmCap);
    }

    private static void Cleanup(long nowTick, IReadOnlyCollection<LiveObjective> objectives)
    {
        Expire(nowTick);
        foreach (LiveObjective carrier in objectives.Where(o => o.IsRelicCarrier && CarrierEvents.ContainsKey(o.Id)))
            CarrierTargets[carrier.Id] = carrier;
        foreach (ActiveEvent active in Events.Values)
        {
            if (objectives.Any(target => target.Id == active.TargetId && target.UnderAttack))
            {
                if (!active.AttackObserved)
                {
                    active.AttackObserved = true;
                    RealmEventNotices.Queue(active.TargetId, active.DefenderRealm,
                        $"{active.Target.Name} is under attack! Available warbands can reinforce the defenders.");
                    RealmEventNotices.Queue(active.TargetId, OtherRealm(active),
                        $"Fighting has broken out at {active.Target.Name}. There is an opportunity to intervene.");
                }
                // The marching army striking the keep starts the siege once the minimum deadline force is
                // there (32 for a keep, 48 for a relic keep); it no longer waits for 108 at rally posts.
                // Incidental combat before the march still cannot start it.
                if (!active.BattleStarted && (MarchedArmy(active) || active.Marching && !active.DefenseReaction &&
                        nowTick - active.CreatedTick >= RealmEventPolicy.EarliestAssaultMilliseconds && Ready(active, true, nowTick)))
                    StartBattle(active, nowTick, "the marching army's attack on the keep has begun");
            }
        }
        // Each caller supplies only objectives it can reach, not an authoritative
        // world inventory. An absent row cannot cancel another force's expedition.
        foreach (string id in Events.Where(pair => pair.Value.ExpiresTick <= nowTick ||
                     objectives.Any(target => target.Id == pair.Key && target.OwningRealm != pair.Value.DefenderRealm))
                     .Select(pair => pair.Key).ToArray())
        {
            var changed = objectives.FirstOrDefault(target => target.Id == id && target.OwningRealm != Events[id].DefenderRealm);
            EndEvent(Events[id], nowTick, changed == null ? "Siege objective is no longer available" : "Keep captured: ownership changed",
                changed?.OwningRealm ?? eRealm.None);
        }
        foreach (string id in Cooldowns.Where(pair => pair.Value <= nowTick).Select(pair => pair.Key).ToArray())
            Cooldowns.Remove(id);
        foreach (string id in CarrierEvents.Where(pair => pair.Value.ExpiresTick <= nowTick)
                     .Select(pair => pair.Key).ToArray())
        {
            RealmEventRecords.Finish(id, "Timed out", "Relic event expired during objective cleanup.");
            foreach (string force in CarrierEvents[id].Participants.Values.SelectMany(realm => realm.Keys))
                ReleasedForces[force] = "Relic captured or event no longer available";
            CarrierEvents.Remove(id);
            CarrierTargets.Remove(id);
            Cooldowns[id] = nowTick + TargetCooldownMilliseconds;
        }
    }

    public static bool IsForceCommitted(string forceId, long nowTick)
    {
        if (string.IsNullOrWhiteSpace(forceId))
            return false;
        lock (Sync)
            return Events.Values.Any(active => active.ExpiresTick > nowTick &&
                       (active.Attackers.ContainsKey(forceId) || active.Defenders.ContainsKey(forceId) ||
                        active.ThirdRealm.ContainsKey(forceId))) ||
                   CarrierEvents.Values.Any(active => active.ExpiresTick > nowTick &&
                       active.Participants.Values.Any(realm => realm.ContainsKey(forceId)));
    }

    public static bool IsPlayerDefenseResponse(string targetId, long nowTick)
    {
        if (string.IsNullOrWhiteSpace(targetId)) return false;
        lock (Sync)
            return Events.TryGetValue(targetId, out var active) && active.ExpiresTick > nowTick &&
                (active.DefenseReaction || !string.IsNullOrEmpty(active.PlayerAccount));
    }

    public static bool IsTargetActive(string targetId, long nowTick)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return false;
        lock (Sync)
            return (Events.TryGetValue(targetId, out ActiveEvent active) && active.ExpiresTick > nowTick) ||
                   (CarrierEvents.TryGetValue(targetId, out var carrier) && carrier.ExpiresTick > nowTick);
    }

    public static void EndTarget(string targetId, long nowTick, eRealm winner = eRealm.None)
    {
        if (string.IsNullOrWhiteSpace(targetId))
            return;
        lock (Sync)
        {
            if (Events.TryGetValue(targetId, out var active))
            {
                EndEvent(active, nowTick, "Keep captured: siege ended", winner);
                // Owner 2026-10-07: when a player takes the keep, the realm that lost it comes back once to retake it.
                if (active.DefenseReaction && winner != eRealm.None && winner == active.AttackerRealm)
                    StartRetake(active, winner, nowTick);
            }
            if (CarrierEvents.TryGetValue(targetId, out var escort))
            {
                string name = CarrierTargets.GetValueOrDefault(targetId)?.Name ?? "The relic";
                foreach (eRealm realm in escort.Participants.Keys.Append(winner).Where(r => r != eRealm.None).Distinct())
                    RealmEventNotices.Queue(targetId, realm, winner == eRealm.None
                        ? $"{name} has been secured at a shrine. The struggle on the road has ended."
                        : realm == winner ? $"{name} is safe in our shrine! Honour to its bearers and defenders."
                        : $"{GlobalConstants.RealmToName(winner)} has secured {name} at a shrine. Our struggle on the road is over.");
            }
            bool removed = CarrierEvents.Remove(targetId, out var carrier);
            if (removed) RealmEventRecords.Finish(targetId, "Captured / returned", "Relic mounted or recovered; escort/interception ended.");
            CarrierTargets.Remove(targetId);
            if (removed)
                foreach (string force in carrier.Participants.Values.SelectMany(realm => realm.Keys))
                    ReleasedForces[force] = "Relic captured: event ended";
            if (removed)
                Cooldowns[targetId] = nowTick + TargetCooldownMilliseconds;
        }
    }

    public static void TransferToRelicCarrier(string keepId, string carrierId, long nowTick, LiveObjective carrierAnchor = null)
    {
        lock (Sync)
        {
            if (!Events.TryGetValue(keepId, out var active) || active.ExpiresTick <= nowTick) return;
            // Even if the relic left before the battle formally began, every side follows the relic now (owner
            // 2026-10-07: all sides focus the relic wherever it is, carried with an escort or not).
            if (!active.BattleStarted) active.ExpiresTick = Math.Max(active.ExpiresTick, nowTick + BattleLifetimeMilliseconds);
            CarrierEvents[carrierId] = (active.ExpiresTick, new()
            {
                [active.AttackerRealm] = new(active.Attackers, StringComparer.Ordinal),
                [active.DefenderRealm] = new(active.Defenders, StringComparer.Ordinal),
                [OtherRealm(active)] = new(active.ThirdRealm, StringComparer.Ordinal)
            });
            // Retain the last known native location through partial route
            // snapshots. Capture/expiry still releases the whole escort.
            CarrierTargets[carrierId] = carrierAnchor ?? active.Target with
                { Id = carrierId, IsRelicCarrier = true, OwningRealm = active.AttackerRealm };
            foreach (eRealm realm in new[] { active.AttackerRealm, active.DefenderRealm, OtherRealm(active) })
                RealmEventNotices.Queue(keepId, realm, $"The relic has left {active.Target.Name}! The battle continues on the road; its fate is not yet decided.");
            RealmEventRecords.Finish(keepId, "Relic taken", "Relic removed; the battle continued as an escort/interception event.");
            RealmEventRecords.Begin(carrierId, carrierAnchor?.Name ?? active.Target.Name, "Relic", GlobalConstants.RealmToName(active.AttackerRealm), "Continued from " + active.Target.Name);
            RealmEventRecords.Progress(carrierId, "Battle", "Relic escort / interception", active.Attackers.Values.Sum() + active.Defenders.Values.Sum() + active.ThirdRealm.Values.Sum(), 0);
            Events.Remove(keepId);
            Cooldowns[keepId] = nowTick + TargetCooldownMilliseconds;
        }
    }

    public static string RelicCarrierTargetId(GameRelic relic)
    {
        return relic == null ? string.Empty : $"rvr-relic-carrier-{(int)relic.OriginalRealm}-{(int)relic.RelicType}";
    }
}
