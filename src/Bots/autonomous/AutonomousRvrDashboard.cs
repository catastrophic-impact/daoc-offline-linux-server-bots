using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using DOL.Events;
using DOL.GS.Keeps;

namespace DOL.GS;

/// <summary>Small read-only world summary; no database scan and no per-bot writer.</summary>
public static class AutonomousRvrDashboard
{
    public sealed record Objective(string Kind, string Name, string Owner, string State, string Location,
        string Carrier, string Forces, string Id = "", long CooldownMilliseconds = 0, long PhaseRemainingMilliseconds = 0, string Phase = "");
    public sealed record Participant(string EventId, string GroupId, string Name, string Realm, string Location, string Activity, int X, int Y, int Z);
    public sealed record Battleground(string Name, int MinLevel, int MaxLevel, string CentralKeep, string Owner,
        int AlbionInside, int MidgardInside, int HiberniaInside, int AlbionTravelling, int MidgardTravelling,
        int HiberniaTravelling, int Monsters);
    public sealed record Snapshot(DateTime UpdatedUtc, bool Running, Objective[] Objectives, Participant[] Participants = null,
        Battleground[] Battlegrounds = null);
    private static Timer _timer;
    private static int _writing;
    private static readonly object HandlerSync = new();
    private static bool _handlersRegistered;
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "rvr-world.json");
    public static void RequestPublish()
    {
        try { _timer?.Change(1, 30_000); }
        catch (ObjectDisposedException) { } // Server shutdown won the race; no new snapshot is needed.
    }

    [GameServerStartedEvent]
    public static void Start(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        lock (HandlerSync)
        {
            if (!_handlersRegistered)
            {
                // A fresh global event collection may already contain other
                // keep/relic listeners but not these handlers. Removing an
                // absent delegate from that chain throws in the legacy event
                // implementation, so registration must be additive and unique.
                GameEventMgr.AddHandlerUnique(KeepEvent.KeepTaken, OnKeepTaken);
                GameEventMgr.AddHandlerUnique(RelicPadEvent.RelicStolen, OnRelicChanged);
                GameEventMgr.AddHandlerUnique(RelicPadEvent.RelicMounted, OnRelicChanged);
                _handlersRegistered = true;
            }
        }
        _timer = new Timer(Publish, null, 1000, 30_000);
    }

    [GameServerStoppedEvent]
    public static void Stop(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = null;
        lock (HandlerSync)
        {
            if (_handlersRegistered)
            {
                GameEventMgr.RemoveHandler(KeepEvent.KeepTaken, OnKeepTaken);
                GameEventMgr.RemoveHandler(RelicPadEvent.RelicStolen, OnRelicChanged);
                GameEventMgr.RemoveHandler(RelicPadEvent.RelicMounted, OnRelicChanged);
                _handlersRegistered = false;
            }
        }
    }

    /// <summary>The launcher's Battlegrounds tab: bracket, central keep owner, bots inside and on their way.</summary>
    private static Battleground[] BattlegroundStatus()
    {
        GameBot[] bots = AutonomousBotRegistry.Snapshot();
        var result = new Battleground[BattlegroundBrackets.Count];
        bool logStatus = GameLoop.GameLoopTime >= _nextBattlegroundKeepLog;
        if (logStatus) _nextBattlegroundKeepLog = GameLoop.GameLoopTime + 5 * 60_000;
        for (int index = 0; index < result.Length; index++)
        {
            BattlegroundBrackets.Bracket bracket = BattlegroundBrackets.Get(index);
            AbstractGameKeep central = GameServer.KeepManager.GetKeepsOfRegion(bracket.RegionId)
                .FirstOrDefault(keep => !keep.IsPortalKeep);
            int Inside(eRealm realm) => bots.Count(bot => bot.IsAlive && bot.Realm == realm && bot.CurrentRegionID == bracket.RegionId);
            int Travelling(eRealm realm) => bots.Count(bot => bot.Realm == realm && bot.CurrentRegionID != bracket.RegionId &&
                AutonomousObjectiveAssignments.Is(bot, eAutonomousObjectiveKind.Battleground) && BattlegroundBrackets.Allows(bracket, bot.Level));
            Region region = WorldMgr.GetRegion(bracket.RegionId);
            int monsters = region == null ? 0 : region.Objects.OfType<GameNPC>().Count(npc =>
                npc.IsAlive && npc.ObjectState == GameObject.eObjectState.Active && npc.Realm == eRealm.None &&
                npc is not GameBot && npc is not GameKeepGuard && (npc.Flags & GameNPC.eFlags.PEACE) == 0);
            result[index] = new(bracket.Name, bracket.MinLevel, bracket.MaxLevel, central?.Name ?? string.Empty,
                central == null ? string.Empty : central.Realm == eRealm.None ? "Neutral" : GlobalConstants.RealmToName(central.Realm),
                Inside(eRealm.Albion), Inside(eRealm.Midgard), Inside(eRealm.Hibernia),
                Travelling(eRealm.Albion), Travelling(eRealm.Midgard), Travelling(eRealm.Hibernia), monsters);
            if (central != null && logStatus)
                LogBattlegroundKeep(bracket, central, bots);
        }
        return result;
    }

    private static long _nextBattlegroundKeepLog;

    private static string ActivityKind(string activity)
    {
        if (string.IsNullOrEmpty(activity)) return "-";
        int cut = activity.IndexOf(" on ", StringComparison.Ordinal);
        if (cut < 0) cut = activity.IndexOf(" to ", StringComparison.Ordinal);
        if (cut < 0 && activity.StartsWith("Fighting ", StringComparison.Ordinal)) cut = "Fighting".Length;
        return cut > 0 ? activity[..cut] : activity.Length > 28 ? activity[..28] : activity;
    }

    /// <summary>
    /// BG_KEEP_STATUS every five minutes per battleground: the central keep's holder, gates, guards and lord, and the
    /// bots of each realm near it (run 2026-10-13: two hours of battleground fighting and no keep changed hands, with
    /// nothing in the log to say how far any assault got).
    /// </summary>
    private static void LogBattlegroundKeep(BattlegroundBrackets.Bracket bracket, AbstractGameKeep central, GameBot[] bots)
    {
        GameKeepDoor[] doors = central.Doors.Values.ToArray();
        GameKeepGuard[] guards = central.Guards.Values.ToArray();
        GameKeepGuard lord = guards.FirstOrDefault(guard => guard is GuardLord);
        var keepPoint = new Point3D(central.X, central.Y, central.Z);
        GameBot[] inside = bots.Where(bot => bot.IsAlive && bot.CurrentRegionID == bracket.RegionId).ToArray();
        int Near(eRealm realm) => bots.Count(bot => bot.IsAlive && bot.Realm == realm && bot.CurrentRegionID == bracket.RegionId &&
            bot.IsWithinRadius(keepPoint, 2500));
        DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrDashboard)).Info(
            $"BG_KEEP_STATUS battleground={bracket.Name} keep=\"{central.Name}\" holder={(central.Realm == eRealm.None ? "Neutral" : GlobalConstants.RealmToName(central.Realm))} " +
            $"doors={doors.Count(door => door.IsAlive)}/{doors.Length} doorHealth={(doors.Length == 0 ? 0 : doors.Min(door => door.HealthPercent))} " +
            $"guards={guards.Count(guard => guard.IsAlive)}/{guards.Length} lord={(lord == null ? "none" : lord.IsAlive ? lord.HealthPercent + "%" : "dead")} " +
            $"near={Near(eRealm.Albion)}/{Near(eRealm.Midgard)}/{Near(eRealm.Hibernia)} " +
            // How many bots stand by the lord and how many have him targeted (gates fell for hours, lord never hit).
            $"atLord={(lord == null ? 0 : inside.Count(bot => bot.IsWithinRadius(lord, 800)))} " +
            $"onLord={(lord == null ? 0 : inside.Count(bot => bot.TargetObject == lord))} " +
            // Attackers only (atLord counts the holder's defenders too), and those on the lord's own floor.
            $"attackersAtLord={(lord == null ? 0 : inside.Count(bot => bot.Realm != central.Realm && bot.IsWithinRadius(lord, 800)))} " +
            $"attackersOnLordFloor={(lord == null ? 0 : inside.Count(bot => bot.Realm != central.Realm && bot.IsWithinRadius(lord, 800) && Math.Abs(bot.Z - lord.Z) < 200))} " +
            // Live, not the saved roster rows (positions there are saved only every few minutes).
            $"inside={inside.Length} moving={inside.Count(bot => bot.IsMoving)} inCombat={inside.Count(bot => bot.InCombat)} " +
            $"medianToKeep={(inside.Length == 0 ? 0 : (int)inside.Select(bot => bot.GetDistanceTo(keepPoint)).OrderBy(d => d).ElementAt(inside.Length / 2))} " +
            $"activities=\"{string.Join("; ", inside.GroupBy(bot => ActivityKind(bot.PersistentRecord?.Activity)).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}:{g.Count()}"))}\" " +
            // Where the lord and the standing doors are, and how close the bots storming him get (run 18: they stopped
            // 1,300-1,500 from the keep centre, the lord 577 above it).
            $"lordAt={(lord == null ? "none" : $"{lord.X},{lord.Y},{lord.Z}")} " +
            $"doorsUp=\"{string.Join(";", doors.Where(door => door.IsAlive).Select(door => $"{door.DoorId}@{door.X},{door.Y},{door.Z}"))}\" " +
            $"stormNearest={(lord == null ? -1 : inside.Where(bot => bot.PersistentRecord?.Activity?.StartsWith("Storming") == true).Select(bot => (int)bot.GetDistanceTo(lord)).DefaultIfEmpty(-1).Min())}");
    }

    private static void Publish(object state)
    {
        if (Interlocked.Exchange(ref _writing, 1) != 0) return;
        try
        {
            var battles = AutonomousRvrEventLayer.Snapshot().ToDictionary(battle => battle.TargetId);
            var keeps = new ushort[] { 1, 100, 200 }.SelectMany(region => GameServer.KeepManager.GetKeepsOfRegion(region).ToArray())
                .Where(AutonomousRvrKeepPolicy.IsSiegeObjective)
                .Select(keep =>
                {
                    battles.TryGetValue($"rvr-keep-{keep.KeepID}", out var battle);
                    bool attacked = (keep.LastAttackedByEnemyTick > 0 &&
                        keep.CurrentRegion.Time - keep.LastAttackedByEnemyTick < 120_000) ||
                        keep.Guards.Values.Any(guard => guard.IsAlive && guard.LastAttackedByEnemyTick > 0 && guard.InCombat);
                    string forces = FormatForces(battle);
                    return new Objective(keep.IsRelic ? "Relic keep" : "Keep", keep.Name, GlobalConstants.RealmToName(keep.Realm),
                        battle?.Kind ?? (attacked ? "Under attack — no organized siege" : "Secure"), keep.CurrentRegion.Description,
                        "", forces, $"rvr-keep-{keep.KeepID}",
                        AutonomousRvrEventLayer.CooldownRemaining($"rvr-keep-{keep.KeepID}", GameLoop.GameLoopTime),
                        battle?.RemainingMilliseconds ?? 0, battle == null ? "" : battle.BattleStarted ? "Battle" : "Muster");
                });
            var relics = RelicMgr.GetRelics().Select(relic =>
            {
                GameLiving carrier = relic.CurrentCarrier;
                long remaining = AutonomousRvrEventLayer.CarrierRemainingMilliseconds(
                    AutonomousRvrEventLayer.RelicCarrierTargetId(relic), GameLoop.GameLoopTime);
                return new Objective("Relic", relic.Name, relic.Realm == eRealm.None ? "In transit" : GlobalConstants.RealmToName(relic.Realm),
                    carrier != null ? remaining > 0 ? "SIEGE — relic escort / interception" : "ESCORT / INTERCEPTION" : relic.IsMounted ? "At shrine" : "DROPPED — recoverable",
                    carrier?.CurrentZone?.Description ?? relic.CurrentZone?.Description ?? "Unknown", carrier?.Name ?? "",
                    $"Original realm: {GlobalConstants.RealmToName(relic.OriginalRealm)} · {relic.RelicType}" +
                    (remaining > 0 ? $" · Battle: {(int)TimeSpan.FromMilliseconds(remaining).TotalMinutes}m left" : ""),
                    AutonomousRvrEventLayer.RelicCarrierTargetId(relic));
            });
            var forceTargets = AutonomousRvrEventLayer.ForceTargets();
            var raids = AutonomousRealmRaid.Snapshot().Select(r => new Objective(r.Id.StartsWith("epic-") ? "Epic dungeon" : r.Id.StartsWith("summoners-") || r.Id.StartsWith("darkness-") ? "Neutral raid" : "Dragon", r.Name, r.Realm, r.State,
                r.Name, "", $"Assigned: {r.Assigned}/{RealmRaidRecruitmentPolicy.MaximumBots} · Present: {r.Present} · Start requires: {r.Suggested}" +
                    (r.Phase == "Waiting" ? " · Holding for arrivals / dragon landing (not attacking)" : ""),
                r.Id, r.State is "Respawning" or "Event cooldown" ? r.Remaining : 0,
                r.Phase.Length > 0 ? r.Remaining : 0, r.Phase));
            var participants = AutonomousBotRegistry.Snapshot().Where(AutonomousRealmRaid.IsEligible).Select(bot =>
            {
                string force = bot.TempProperties.GetProperty<string>("RvrEventForce") ?? $"rvr-{bot.DatabaseID}";
                var raid = AutonomousRealmRaid.GetView(bot.Group);
                if (raid != null) return new Participant(raid.EventId, $"Party: {bot.Group?.LivingLeader?.Name}", bot.Name,
                    GlobalConstants.RealmToName(bot.Realm), bot.CurrentZone?.Description ?? "Unknown", bot.PersistentRecord?.Activity ?? "", bot.X, bot.Y, bot.Z);
                return forceTargets.TryGetValue(force, out string target)
                    ? new Participant(target, force, bot.Name, GlobalConstants.RealmToName(bot.Realm), bot.CurrentZone?.Description ?? "Unknown",
                        bot.PersistentRecord?.Activity ?? "", bot.X, bot.Y, bot.Z) : null;
            }).Where(p => p != null).ToArray();
            string json = JsonSerializer.Serialize(new Snapshot(DateTime.UtcNow, true, keeps.Concat(relics).Concat(raids).ToArray(), participants,
                BattlegroundStatus()));
            File.WriteAllText(FilePath + ".tmp", json);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch (Exception exception)
        {
            DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrDashboard)).Warn("RvR dashboard snapshot failed", exception);
        }
        finally { Volatile.Write(ref _writing, 0); }
    }

    private static eRealm ThirdRealm(eRealm attacker, eRealm defender)
    {
        return new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }
            .FirstOrDefault(realm => realm != attacker && realm != defender);
    }

    private static string FormatForces(AutonomousRvrEventLayer.BattleNotice battle)
    {
        if (battle == null)
            return "No organized rally";
        eRealm thirdRealm = ThirdRealm(battle.Attacker, battle.Defender);
        TimeSpan remaining = TimeSpan.FromMilliseconds(battle.RemainingMilliseconds);
        if (battle.BattleStarted)
            return $"{GlobalConstants.RealmToName(battle.Attacker)}: {battle.Attackers} assigned / {battle.PresentAttackers} present · " +
                $"{GlobalConstants.RealmToName(battle.Defender)}: {battle.Defenders} assigned / {battle.PresentDefenders} present · " +
                $"{GlobalConstants.RealmToName(thirdRealm)}: {battle.ThirdRealm} assigned / {battle.PresentThirdRealm} present · " +
                $"Battle: {(int)remaining.TotalHours}h {remaining.Minutes:00}m left";
        return $"Present / cap (assigned)\n{GlobalConstants.RealmToName(battle.Attacker)}: {battle.PresentAttackers}/{battle.CapPerRealm} ({battle.Attackers})\n" +
               $"{GlobalConstants.RealmToName(battle.Defender)}: {battle.PresentDefenders}/{battle.CapPerRealm} ({battle.Defenders})\n" +
               $"{GlobalConstants.RealmToName(thirdRealm)}: {battle.PresentThirdRealm}/{battle.CapPerRealm} ({battle.ThirdRealm})\n" +
               "Attackers prepare first; opposing realms respond to actual fighting\n" +
               (battle.MusterRemainingMilliseconds > 0
                   ? $"Gathering: marching in {Math.Max(1, (int)Math.Ceiling(battle.MusterRemainingMilliseconds / 60_000d))}m\n"
                   : battle.MusterRemainingMilliseconds == 0 ? "Marching to the keep\n" : "") +
               $"Attack window: {Math.Max(0, (int)remaining.TotalHours)}h {remaining.Minutes:00}m left (present = at the keep)";
    }

    private static void OnKeepTaken(DOLEvent e, object sender, EventArgs args)
    {
        if (args is KeepEventArgs { Keep: not null } taken)
        {
            // Every capture, frontier and battleground alike (battleground keeps had no capture line at all).
            DOL.Logging.LoggerManager.Create(typeof(AutonomousRvrDashboard)).Info(
                $"RVR_KEEP_TAKEN keep=\"{taken.Keep.Name}\" id={taken.Keep.KeepID} region={taken.Keep.Region} " +
                $"realm={GlobalConstants.RealmToName(taken.Keep.Realm)} battleground={BattlegroundBrackets.IsBattlegroundRegion(taken.Keep.Region)}");
            AutonomousRvrEventLayer.EndTarget($"rvr-keep-{taken.Keep.KeepID}", GameLoop.GameLoopTime, taken.Keep.Realm);
        }
    }

    private static void OnRelicChanged(DOLEvent e, object sender, EventArgs args)
    {
        if (args is not RelicPadEventArgs { Relic: not null } changed)
            return;
        if (e == RelicPadEvent.RelicMounted)
        {
            AutonomousRvrEventLayer.EndTarget(
                AutonomousRvrEventLayer.RelicCarrierTargetId(changed.Relic), GameLoop.GameLoopTime,
                sender is GameRelicPad destination ? destination.Realm : eRealm.None);
            return;
        }
        if (e != RelicPadEvent.RelicStolen || sender is not GameRelicPad pad)
            return;

        AbstractGameKeep relicKeep = GameServer.KeepManager.GetKeepsOfRegion(pad.CurrentRegionID)
            .Where(keep => keep.IsRelic)
            .OrderBy(keep => (long)(keep.X - pad.X) * (keep.X - pad.X) + (long)(keep.Y - pad.Y) * (keep.Y - pad.Y))
            .FirstOrDefault();
        if (relicKeep != null)
        {
            GameRelic relic = changed.Relic;
            GameLiving carrier = relic.CurrentCarrier;
            string id = AutonomousRvrEventLayer.RelicCarrierTargetId(relic);
            AutonomousRvrEventLayer.TransferToRelicCarrier($"rvr-keep-{relicKeep.KeepID}",
                id, GameLoop.GameLoopTime, new(id, relic.Name, AutonomousRvrEventLayer.Intent.HuntEnemy,
                    carrier?.Realm ?? relic.OriginalRealm, carrier?.CurrentRegionID ?? relic.CurrentRegionID,
                    carrier?.X ?? relic.X, carrier?.Y ?? relic.Y, carrier?.Z ?? relic.Z, false, 1, 0, 0, 0, true));
        }
    }
}
