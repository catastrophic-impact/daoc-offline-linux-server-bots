using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.Styles;
using DOL.Logging;

namespace DOL.GS;

/// <summary>
/// Opt-in server-side pilot for a real player character. All combat, loot,
/// experience and inventory changes go through the normal live game systems.
/// No progress is fabricated by this controller.
/// </summary>
public static class AutonomousPlayerPilot
{
    private const int TickMilliseconds = 250;
    private const int HudMilliseconds = 3500;
    private const int TargetRefreshMilliseconds = 2000;
    private const int SearchRadius = 12000;
    private const int LootRadius = 260;
    private const int StopShortOfTarget = 145;
    private const int MeaningfulMovementDistance = 96;
    private const int DeathReleaseDelayMilliseconds = 10000;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly ConcurrentDictionary<GamePlayer, PilotState> Pilots = new();
    private static System.Threading.Timer _timer;
    private static int _polling;

    private sealed class PilotState
    {
        public readonly GamePlayer Player;
        public readonly Group PlayerLedGroupAtStart;
        public GameLiving Target;
        public Vector3? RoamDestination;
        public string Goal = "Choosing a live goal";
        public string Detail = "Reading the nearby world";
        public long NextHudTick;
        public long NextTargetRefreshTick;
        public long NextCombatChoiceTick;
        public long NextLootTick;
        public long NextDeployablePetTick;
        public GameLiving PetLeadTarget;
        public long PetLeadOwnerUntil;
        public int TickQueued;
        public AutonomousPlayerQuickbarProfile Quickbar;
        public long NextQuickbarRefreshTick;
        public DateTime LastProgressUtc;
        public ushort ProgressRegionId;
        public int ProgressX;
        public int ProgressY;
        public int ProgressZ;
        public long ProgressExperience;
        public long ProgressRealmPoints;
        public long ProgressMoney;
        public int ProgressInventoryCount;
        public bool DeathRecorded;
        public bool ReleaseRequested;
        public int DeathSafetySteps;

        public PilotState(GamePlayer player)
        {
            Player = player;
            PlayerLedGroupAtStart = player.Group;
        }
    }

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = new System.Threading.Timer(Poll, null, TickMilliseconds, TickMilliseconds);
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        _timer?.Dispose();
        _timer = null;
        Pilots.Clear();
    }

    public static bool IsActive(GamePlayer player) => player != null && Pilots.ContainsKey(player);

    public static bool Toggle(GamePlayer player)
    {
        if (IsActive(player))
        {
            Disable(player, "Manual control restored.", true);
            return false;
        }

        return Enable(player);
    }

    public static bool Enable(GamePlayer player)
    {
        if (player?.Client == null || player.ObjectState is not GameObject.eObjectState.Active || !player.IsAlive)
        {
            player?.Out.SendMessage("Bot mode can only start on a living character in the world.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return false;
        }

        if (player.Steed != null || player.IsOnHorse)
        {
            player.Out.SendMessage("Finish or dismount from the horse route before enabling bot mode.", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return false;
        }

        if (!AutonomousPlayerQuickbarProfile.TryLoad(player, out AutonomousPlayerQuickbarProfile quickbar, out string quickbarError))
        {
            player.Out.SendMessage($"BOT MODE did not start: {quickbarError}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            return false;
        }

        PilotState state = new(player) { Quickbar = quickbar };
        CaptureProgress(state, DateTime.UtcNow);
        if (!Pilots.TryAdd(player, state))
            return true;

        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        player.CurrentSpeed = 0;
        SetGoal(state, "Choosing a live goal", $"Using {quickbar.ActionCount} read-only quickbar actions", true);
        PreserveSpawnedParty(state);
        player.Out.SendMessage(
            state.PlayerLedGroupAtStart == null
                ? "BOT MODE ON — type /bot again at any time to return to manual control."
                : "BOT MODE ON — your current party is preserved and will continue following you. Type /bot again for manual control.",
            eChatType.CT_System,
            eChatLoc.CL_SystemWindow);
        QueueTick(state);
        return true;
    }

    public static void Disable(GamePlayer player, string reason = "Manual control restored.", bool save = true)
    {
        if (player == null || !Pilots.TryRemove(player, out _))
            return;

        player.CurrentSpeed = 0;
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        player.styleComponent.NextCombatStyle = null;
        player.styleComponent.NextCombatBackupStyle = null;

        if (player.IsAlive && player.IsSitting)
            player.Sit(false);

        if (player.ObjectState is GameObject.eObjectState.Active && player.Client?.ClientState is GameClient.eClientState.Playing)
        {
            player.Out.SendPlayerJump(false);
            player.Out.SendMessage($"BOT MODE OFF — {reason}", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
            player.Out.SendMessage($"Bot mode disabled. {reason}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
            if (save)
                player.SaveIntoDatabase();
        }
    }

    public static string GetStatus(GamePlayer player)
    {
        return player != null && Pilots.TryGetValue(player, out PilotState state)
            ? $"BOT MODE ON — Goal: {state.Goal} — {state.Detail}"
            : "BOT MODE OFF — manual control";
    }

    /// <summary>
    /// While opted in, keep the connection alive but do not let idle client
    /// position packets overwrite the server pilot's position.
    /// </summary>
    public static bool SuppressClientMovement(GamePlayer player)
    {
        if (!IsActive(player))
            return false;

        player.LastPositionUpdatePacketReceivedTime = GameLoop.GameLoopTime;
        return true;
    }

    public static bool ShouldRest(byte healthPercent, byte manaPercent, byte endurancePercent) =>
        healthPercent < 50 || manaPercent < 22 || endurancePercent < 20;

    public static bool IsEligibleExperienceTarget(ConColor con, int targetLevel, int averageGroupLevel, int groupSize)
    {
        if (con <= ConColor.GREY)
            return false;

        int advantage = groupSize switch
        {
            >= 6 => 6,
            5 => 5,
            4 => 4,
            3 => 3,
            2 => 2,
            _ => 1,
        };
        return targetLevel <= averageGroupLevel + advantage;
    }

    private static void Poll(object state)
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
            return;

        try
        {
            foreach (PilotState pilot in Pilots.Values)
                QueueTick(pilot);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static void QueueTick(PilotState state)
    {
        if (Interlocked.Exchange(ref state.TickQueued, 1) != 0)
            return;
        NpcService.Instance.Post(TickOnGameLoop, state);
    }

    private static void TickOnGameLoop(PilotState state)
    {
        try
        {
            GamePlayer player = state.Player;
            if (!Pilots.ContainsKey(player))
                return;

            if (player.Client?.ClientState is not GameClient.eClientState.Playing || player.ObjectState is not GameObject.eObjectState.Active)
            {
                Pilots.TryRemove(player, out _);
                return;
            }

            if (!player.IsAlive)
            {
                HandleDeath(state);
                return;
            }

            state.DeathRecorded = false;
            state.ReleaseRequested = false;
            PreserveSpawnedParty(state);

            if (player.Steed != null || player.IsOnHorse)
            {
                CaptureProgress(state, DateTime.UtcNow);
                StopPilotMovement(player);
                SetGoal(state, "Traveling by stable route", "Waiting for the real horse path to finish");
                DrawHud(state);
                return;
            }

            if (ObserveProgressAndRecover(state))
                return;

            if (player.IsCrowdControlled)
            {
                StopPilotMovement(player);
                SetGoal(state, "Recovering control", player.IsMezzed ? "Mesmerized" : "Stunned");
                DrawHud(state);
                return;
            }

            RefreshQuickbar(state);

            // Pet classes get their permanent companion back before ordinary
            // resting, travel or combat work. Human bot mode still uses only
            // summon/heal/buff actions present on the saved quickbars.
            if (AutonomousPetSupport.Maintain(
                    player,
                    state.Target,
                    ref state.NextDeployablePetTick,
                    out string petActivity,
                    state.Quickbar.Allows))
            {
                SetGoal(state, "Managing class pet", petActivity);
                DrawHud(state);
                return;
            }

            if (!player.InCombat && (ShouldRest(player.HealthPercent, player.ManaPercent, player.EndurancePercent) ||
                                     player.IsSitting && !Recovered(player)))
            {
                Rest(state);
                DrawHud(state);
                return;
            }

            if (player.IsSitting)
                player.Sit(false);

            TryLoot(state);

            if (!IsValidTarget(state, state.Target) || GameLoop.GameLoopTime >= state.NextTargetRefreshTick)
            {
                state.Target = FindTarget(state);
                state.NextTargetRefreshTick = GameLoop.GameLoopTime + TargetRefreshMilliseconds;
            }

            if (state.Target == null)
            {
                SearchForCamp(state);
                DrawHud(state);
                return;
            }

            state.RoamDestination = null;
            player.TargetObject = state.Target;
            int distance = player.GetDistanceTo(state.Target);
            SpellChoice spell = FindDamageSpell(state, state.Target);
            int desiredRange = spell.Spell != null ? Math.Max(145, player.castingComponent.CalculateSpellRange(spell.Spell) - 80) : StopShortOfTarget;

            if (distance > desiredRange)
            {
                player.attackComponent.StopAttack();
                player.StopCurrentSpellcast();
                SetGoal(state, $"Hunting {state.Target.Name}", $"Traveling to level {state.Target.EffectiveLevel} target — {distance} units away");
                StepToward(state, new(state.Target.X, state.Target.Y, state.Target.Z), desiredRange);
                DrawHud(state);
                return;
            }

            StopPilotMovement(player);
            Face(player, state.Target);
            if (AutonomousPetSupport.LetMainPetLead(
                    player,
                    state.Target,
                    ref state.PetLeadTarget,
                    ref state.PetLeadOwnerUntil))
            {
                SetGoal(state, $"Pet pulling {state.Target.Name}", "Holding briefly while the pet establishes contact");
                DrawHud(state);
                return;
            }

            if (spell.Spell != null && !player.IsCasting && GameLoop.GameLoopTime >= state.NextCombatChoiceTick)
            {
                player.attackComponent.StopAttack();
                if (player.CastSpell(spell.Spell, spell.Line))
                {
                    AnimistSingleTargetPolicy.CastOwnerSpell(player, spell.Spell);
                    state.NextCombatChoiceTick = GameLoop.GameLoopTime + Math.Max(900, spell.Spell.CastTime + 250);
                }
            }
            else if (!player.IsCasting)
            {
                QueueUsableStyle(state);
                if (!player.attackComponent.AttackState)
                    player.attackComponent.RequestStartAttack(state.Target);
            }

            string action = player.IsCasting ? "Casting" : "Fighting";
            SetGoal(state, $"Grinding {state.Target.Name}", $"{action} level {state.Target.EffectiveLevel} — {state.Target.HealthPercent}% health");
            DrawHud(state);
        }
        catch (Exception exception)
        {
            Log.Error($"Player pilot tick failed for {state.Player?.Name}", exception);
            Disable(state.Player, "The pilot hit an error and released control safely.", true);
        }
        finally
        {
            Volatile.Write(ref state.TickQueued, 0);
        }
    }

    private static void PreserveSpawnedParty(PilotState state)
    {
        GamePlayer player = state.Player;
        Group protectedGroup = state.PlayerLedGroupAtStart;
        if (protectedGroup == null || player.Group != protectedGroup)
            return;

        // Only reassert leadership for members still in the protected group.
        // A manual kick remains authoritative and temporary helpers still vanish.
        foreach (GameBot bot in protectedGroup.GetMembersInTheGroup().OfType<GameBot>())
            bot.EnterPlayerLedGroup(player);
    }

    private static bool Recovered(GamePlayer player) =>
        player.HealthPercent >= 100 && (player.MaxMana <= 0 || player.ManaPercent >= 100) && player.EndurancePercent >= 100;

    private static void Rest(PilotState state)
    {
        GamePlayer player = state.Player;
        StopPilotMovement(player);
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        if (!player.IsSitting)
            player.Sit(true);
        SetGoal(state, "Resting", $"HP {player.HealthPercent}% • power {player.ManaPercent}% • endurance {player.EndurancePercent}%");
    }

    private static void TryLoot(PilotState state)
    {
        if (GameLoop.GameLoopTime < state.NextLootTick)
            return;

        state.NextLootTick = GameLoop.GameLoopTime + 1000;
        foreach (GameStaticItem item in state.Player.GetItemsInRadius(LootRadius))
        {
            if (item is WorldInventoryItem or GameMoney)
                state.Player.PickupObject(item, false);
        }
    }

    private static GameLiving FindTarget(PilotState state)
    {
        GamePlayer player = state.Player;
        GameLiving selected = null;
        int selectedScore = int.MaxValue;
        int groupSize = Math.Max(1, (int)(player.Group?.MemberCount ?? 1));
        int averageLevel = AverageGroupLevel(player);

        foreach (GameNPC npc in player.GetNPCsInRadius(SearchRadius))
        {
            if (!IsValidTarget(state, npc))
                continue;

            ConColor con = ConLevels.GetConColor(player.GetConLevel(npc));
            if (con > MaximumTargetCon(state, groupSize) ||
                !IsEligibleExperienceTarget(con, npc.EffectiveLevel, averageLevel, groupSize))
                continue;

            int distance = player.GetDistanceTo(npc);
            int conPenalty = Math.Abs((int)con) * 250;
            int score = distance + conPenalty;
            if (score < selectedScore)
            {
                selected = npc;
                selectedScore = score;
            }
        }

        return selected;
    }

    private static bool IsValidTarget(PilotState state, GameLiving target)
    {
        GamePlayer player = state.Player;
        if (target == null || target == player || !target.IsAlive || target.ObjectState is not GameObject.eObjectState.Active ||
            target.CurrentRegion != player.CurrentRegion || !GameServer.ServerRules.IsAllowedToAttack(player, target, true) ||
            player.IsObjectGreyCon(target))
            return false;

        int groupSize = Math.Max(1, (int)(player.Group?.MemberCount ?? 1));
        if (ConLevels.GetConColor(player.GetConLevel(target)) > MaximumTargetCon(state, groupSize))
            return false;

        if (target is GameNPC npc && (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0)
            return false;

        return true;
    }

    private static int AverageGroupLevel(GamePlayer player)
    {
        if (player.Group == null)
            return player.Level;

        GameLiving[] members = player.Group.GetMembersInTheGroup().Where(member => member?.IsAlive == true).ToArray();
        return members.Length == 0 ? player.Level : (int)Math.Round(members.Average(member => member.EffectiveLevel));
    }

    private static void SearchForCamp(PilotState state)
    {
        GamePlayer player = state.Player;
        if (state.RoamDestination == null || Distance(player, state.RoamDestination.Value) < 80)
        {
            Vector3 current = new(player.X, player.Y, player.Z);
            Vector3? random = PathfindingProvider.Instance.GetRandomPoint(
                player.CurrentZone,
                current,
                1400,
                PathfindingProvider.Instance.DefaultFilters);

            if (random.HasValue && Vector3.DistanceSquared(random.Value, current) > 6400)
                state.RoamDestination = random;
            else
            {
                double angle = Random.Shared.NextDouble() * Math.PI * 2;
                state.RoamDestination = new(
                    player.X + (float)(Math.Cos(angle) * 700),
                    player.Y + (float)(Math.Sin(angle) * 700),
                    player.Z);
            }
        }

        SetGoal(state, $"Searching {player.CurrentZone?.Description ?? "the zone"}", "Looking for a reachable monster that is not grey");
        StepToward(state, state.RoamDestination.Value, 20);
    }

    private static void StepToward(PilotState state, Vector3 destination, int stopDistance)
    {
        GamePlayer player = state.Player;
        Vector3 current = new(player.X, player.Y, player.Z);
        Vector3 delta = destination - current;
        float distance = delta.Length();
        if (distance <= stopDistance || distance < 1f)
        {
            StopPilotMovement(player);
            return;
        }

        float step = Math.Min(distance - stopDistance, Math.Max(24, player.MaxSpeed * TickMilliseconds / 1000f));
        Vector3 desired = current + Vector3.Normalize(delta) * step;
        Vector3 navmeshStep = PathfindingProvider.Instance.GetMoveAlongSurface(
            player.CurrentZone,
            current,
            desired,
            PathfindingProvider.Instance.DefaultFilters) ?? desired;
        AutonomousThreatAwarePathing.SafeStep safeStep = AutonomousThreatAwarePathing.ChooseStep(
            player,
            current,
            navmeshStep,
            step,
            state.Target);
        Vector3 next = safeStep.Position;
        if (safeStep.Detouring && !string.IsNullOrWhiteSpace(safeStep.Reason))
            state.Detail = safeStep.Reason;

        Zone zone = player.CurrentRegion?.GetZone((int)next.X, (int)next.Y);
        if (zone == null)
        {
            StopPilotMovement(player);
            return;
        }

        player.Heading = player.GetHeading((int)next.X, (int)next.Y);
        player.CurrentSpeed = player.MaxSpeed;
        player.X = (int)Math.Round(next.X);
        player.Y = (int)Math.Round(next.Y);
        player.Z = (int)Math.Round(next.Z);
        player.ClearObjectsInRadiusCache();
        player.SubZoneObject?.CheckForRelocation();
        player.Out.SendPlayerJump(false);
        PlayerPositionUpdateHandler.BroadcastPosition(player.Client);
    }

    private static ConColor MaximumTargetCon(PilotState state, int groupSize)
    {
        ConColor naturalLimit = groupSize switch
        {
            >= 6 => ConColor.PURPLE,
            >= 4 => ConColor.RED,
            >= 2 => ConColor.ORANGE,
            _ => ConColor.YELLOW,
        };
        return (ConColor)Math.Max((int)ConColor.GREEN, (int)naturalLimit - state.DeathSafetySteps);
    }

    private static void HandleDeath(PilotState state)
    {
        GamePlayer player = state.Player;
        if (!state.DeathRecorded)
        {
            state.DeathRecorded = true;
            state.DeathSafetySteps++;
            state.Target = null;
            state.RoamDestination = null;
            ConColor cap = MaximumTargetCon(state, Math.Max(1, (int)(player.Group?.MemberCount ?? 1)));
            SetGoal(state, "Recovering from death", $"Next grind capped at {cap.ToString().ToLowerInvariant()} monsters", true);
            player.SaveIntoDatabase();
        }

        long elapsed = GameLoop.GameLoopTime - player.DeathTick;
        if (elapsed >= DeathReleaseDelayMilliseconds && !state.ReleaseRequested)
        {
            state.ReleaseRequested = true;
            player.Release(eReleaseType.Normal, true);
            CaptureProgress(state, DateTime.UtcNow);
        }
        DrawHud(state);
    }

    private static bool ObserveProgressAndRecover(PilotState state)
    {
        GamePlayer player = state.Player;
        DateTime now = DateTime.UtcNow;
        long dx = (long)player.X - state.ProgressX;
        long dy = (long)player.Y - state.ProgressY;
        long dz = (long)player.Z - state.ProgressZ;
        bool changed = player.CurrentRegionID != state.ProgressRegionId ||
                       dx * dx + dy * dy + dz * dz >= MeaningfulMovementDistance * MeaningfulMovementDistance ||
                       player.Experience != state.ProgressExperience ||
                       player.RealmPoints != state.ProgressRealmPoints ||
                       player.GetCurrentMoney() != state.ProgressMoney ||
                       (player.Inventory?.AllItems?.Count() ?? 0) != state.ProgressInventoryCount;

        if (changed || player.InCombat || player.IsAttacking || player.IsCasting)
        {
            CaptureProgress(state, now);
            return false;
        }

        if (now - state.LastProgressUtc < AutonomousStuckWatchdog.StuckThreshold)
            return false;

        AutonomousStuckWatchdog.CapitalLocation capital = AutonomousStuckWatchdog.CapitalFor(player.Realm);
        if (capital.RegionId == 0)
            return false;

        StopPilotMovement(player);
        player.attackComponent.StopAttack();
        player.StopCurrentSpellcast();
        player.TargetObject = null;
        state.Target = null;
        state.RoamDestination = null;
        player.MoveTo(capital.RegionId, capital.X, capital.Y, capital.Z, capital.Heading);
        CaptureProgress(state, now);
        SetGoal(state, $"Recovering in {capital.Name}", "15 minutes without real progress; choosing a new reachable grind goal", true);
        player.SaveIntoDatabase();
        DrawHud(state);
        return true;
    }

    private static void CaptureProgress(PilotState state, DateTime now)
    {
        GamePlayer player = state.Player;
        state.ProgressRegionId = player.CurrentRegionID;
        state.ProgressX = player.X;
        state.ProgressY = player.Y;
        state.ProgressZ = player.Z;
        state.ProgressExperience = player.Experience;
        state.ProgressRealmPoints = player.RealmPoints;
        state.ProgressMoney = player.GetCurrentMoney();
        state.ProgressInventoryCount = player.Inventory?.AllItems?.Count() ?? 0;
        state.LastProgressUtc = now;
    }

    private static int Distance(GamePlayer player, Vector3 point)
    {
        Vector3 current = new(player.X, player.Y, player.Z);
        return (int)Vector3.Distance(current, point);
    }

    private static void StopPilotMovement(GamePlayer player)
    {
        if (player.CurrentSpeed == 0)
            return;
        player.CurrentSpeed = 0;
        player.Out.SendPlayerJump(false);
        PlayerPositionUpdateHandler.BroadcastPosition(player.Client);
    }

    private static void Face(GamePlayer player, GameObject target)
    {
        ushort heading = player.GetHeading(target.X, target.Y);
        if (player.Heading == heading)
            return;
        player.Heading = heading;
        player.Out.SendPlayerJump(true);
        PlayerHeadingUpdateHandler.BroadcastHeading(player.Client);
    }

    private readonly record struct SpellChoice(Spell Spell, SpellLine Line);

    private static SpellChoice FindDamageSpell(PilotState state, GameLiving target)
    {
        GamePlayer player = state.Player;
        if (player.ManaPercent < 18)
            return default;

        Spell best = null;
        SpellLine bestLine = null;
        foreach ((SpellLine line, System.Collections.Generic.List<Skill> skills) in player.GetAllUsableListSpells())
        {
            foreach (Spell spell in skills.OfType<Spell>())
            {
                if (!state.Quickbar.Allows(spell) || !AnimistSingleTargetPolicy.AllowsAutomatedSpell(spell) ||
                    !spell.IsHarmful || spell.Damage <= 0 || spell.Range <= 0 || spell.NeedInstrument ||
                    spell.Level > player.Level || !player.IsWithinRadius(target, player.castingComponent.CalculateSpellRange(spell)))
                    continue;

                if (best == null || spell.Level > best.Level || spell.Level == best.Level && spell.Damage > best.Damage)
                {
                    best = spell;
                    bestLine = line;
                }
            }
        }
        return new(best, bestLine);
    }

    private static void QueueUsableStyle(PilotState state)
    {
        GamePlayer player = state.Player;
        if (player.styleComponent.NextCombatStyle != null || player.EndurancePercent < 15 || player.ActiveWeapon == null)
            return;

        AttackData lastAttack = player.attackComponent.attackAction.LastAttackData;
        Style style = player.GetAllUsableSkills()
            .Select(entry => entry.Item1)
            .OfType<Style>()
            .Where(state.Quickbar.Allows)
            .Where(candidate => StyleProcessor.CanUseStyle(lastAttack, player, candidate, player.ActiveWeapon))
            .OrderByDescending(candidate => candidate.Level)
            .FirstOrDefault();
        if (style != null)
            player.styleComponent.NextCombatStyle = style;
    }

    private static void RefreshQuickbar(PilotState state)
    {
        if (GameLoop.GameLoopTime < state.NextQuickbarRefreshTick)
            return;
        state.NextQuickbarRefreshTick = GameLoop.GameLoopTime + 5000;
        if (!AutonomousPlayerQuickbarProfile.TryLoad(state.Player, out AutonomousPlayerQuickbarProfile latest, out _))
            return;
        if (state.Quickbar == null || latest.SourcePath != state.Quickbar.SourcePath || latest.LastWriteUtc != state.Quickbar.LastWriteUtc)
        {
            state.Quickbar = latest;
            SetGoal(state, state.Goal, $"Quickbar refreshed read-only: {latest.ActionCount} allowed actions", true);
        }
    }

    private static void SetGoal(PilotState state, string goal, string detail, bool forceHud = false)
    {
        bool changed = !string.Equals(state.Goal, goal, StringComparison.Ordinal) ||
                       !string.Equals(state.Detail, detail, StringComparison.Ordinal);
        state.Goal = goal;
        state.Detail = detail;
        if (forceHud || changed)
            state.NextHudTick = 0;
    }

    private static void DrawHud(PilotState state)
    {
        if (GameLoop.GameLoopTime < state.NextHudTick)
            return;

        state.NextHudTick = GameLoop.GameLoopTime + HudMilliseconds;
        state.Player.Out.SendMessage($"BOT MODE • Goal: {state.Goal} • {state.Detail}", eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
    }
}
