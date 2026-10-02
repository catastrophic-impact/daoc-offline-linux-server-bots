using System;
using System.Collections.Generic;
using System.Linq;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;

namespace DOL.GS.Quests.Hibernia;

/// <summary>
/// Persistent reward gates for the Sluaghbinder's quest-earned Epic Spells
/// line.  Quest completion is the source of truth, so no second progress
/// table can drift out of sync with the normal quest journal.
/// </summary>
public static class SluaghbinderEpicQuestState
{
    public static bool HasFirstReward(GamePlayer player) =>
        player != null && player.HasFinishedQuest(typeof(SluaghbinderEpic10)) > 0;

    public static bool HasReward(GamePlayer player, int spellId) => spellId switch
    {
        59080 => player?.HasFinishedQuest(typeof(SluaghbinderEpic10)) > 0,
        59081 => player?.HasFinishedQuest(typeof(SluaghbinderEpic20)) > 0,
        59082 => player?.HasFinishedQuest(typeof(SluaghbinderEpic30)) > 0,
        59083 => player?.HasFinishedQuest(typeof(SluaghbinderEpic40)) > 0,
        59084 => player?.HasFinishedQuest(typeof(SluaghbinderEpic50)) > 0,
        _ => false,
    };

    public static void NotifyReward(GamePlayer player, int spellId)
    {
        if (player == null)
            return;

        player.RefreshSpecDependantSkills(true);
        player.GetAllUsableListSpells(true);
        player.GetAllUsableSkills(true);
        player.Out.SendNonHybridSpellLines();
        player.Out.SendMessage("Your Epic Spells page has been updated with a new skeletal service.",
            eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
    }
}

/// <summary>
/// Shared journal and progression behavior for the five chained quests.
/// </summary>
public abstract class SluaghbinderEpicQuest : BaseQuest
{
    protected SluaghbinderEpicQuest() : base() { }
    protected SluaghbinderEpicQuest(GamePlayer player) : base(player) { }
    protected SluaghbinderEpicQuest(GamePlayer player, int step) : base(player, step) { }
    protected SluaghbinderEpicQuest(GamePlayer player, DbQuest quest) : base(player, quest) { }

    protected abstract Type Prerequisite { get; }
    protected abstract int RequiredLevel { get; }

    protected SluaghbinderEpicQuestDefinition Definition =>
        SluaghbinderEpicQuestRuntime.GetDefinition(GetType());

    public override int Level => RequiredLevel;
    public override string Name => Definition.Title;

    public override bool CheckQuestQualification(GamePlayer player)
    {
        if (player == null || player.Realm != eRealm.Hibernia)
            return false;

        if (player.CharacterClass?.ID is not ((int)eCharacterClass.Acolyte or (int)eCharacterClass.Sluaghbinder))
            return false;

        // Once accepted, level and chain checks must not make an active quest
        // disappear from the journal if the player levels during it.
        if (player.IsDoingQuest(GetType()) != null)
            return true;

        if (player.Level < RequiredLevel)
            return false;

        return Prerequisite == null || player.HasFinishedQuest(Prerequisite) > 0;
    }

    public override string Description => Step switch
    {
        1 => $"{Definition.Clue} Defeat {Definition.TargetName}.",
        2 => "Return to Muirenn in Tir na Nog for the skeletal service reward.",
        _ => base.Description,
    };

    public override void OnQuestAssigned(GamePlayer player)
    {
        base.OnQuestAssigned(player);
        SluaghbinderEpicQuestRuntime.StartQuest(GetType());
        player.Out.SendMessage(Definition.Clue, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    public override void Notify(DOLEvent e, object sender, EventArgs args)
    {
    }

    public override void FinishQuest()
    {
        if (Step != 2)
            return;

        base.FinishQuest();
        SluaghbinderEpicQuestState.NotifyReward(m_questPlayer, Definition.RewardSpellId);
        SluaghbinderEpicQuestRuntime.UpdateTrainerIndicator(m_questPlayer);
    }
}

public sealed class SluaghbinderEpic10 : SluaghbinderEpicQuest
{
    public SluaghbinderEpic10() : base() { }
    public SluaghbinderEpic10(GamePlayer player) : base(player) { }
    public SluaghbinderEpic10(GamePlayer player, int step) : base(player, step) { }
    public SluaghbinderEpic10(GamePlayer player, DbQuest quest) : base(player, quest) { }
    protected override Type Prerequisite => null;
    protected override int RequiredLevel => 10;
}

public sealed class SluaghbinderEpic20 : SluaghbinderEpicQuest
{
    public SluaghbinderEpic20() : base() { }
    public SluaghbinderEpic20(GamePlayer player) : base(player) { }
    public SluaghbinderEpic20(GamePlayer player, int step) : base(player, step) { }
    public SluaghbinderEpic20(GamePlayer player, DbQuest quest) : base(player, quest) { }
    protected override Type Prerequisite => typeof(SluaghbinderEpic10);
    protected override int RequiredLevel => 20;
}

public sealed class SluaghbinderEpic30 : SluaghbinderEpicQuest
{
    public SluaghbinderEpic30() : base() { }
    public SluaghbinderEpic30(GamePlayer player) : base(player) { }
    public SluaghbinderEpic30(GamePlayer player, int step) : base(player, step) { }
    public SluaghbinderEpic30(GamePlayer player, DbQuest quest) : base(player, quest) { }
    protected override Type Prerequisite => typeof(SluaghbinderEpic20);
    protected override int RequiredLevel => 30;
}

public sealed class SluaghbinderEpic40 : SluaghbinderEpicQuest
{
    public SluaghbinderEpic40() : base() { }
    public SluaghbinderEpic40(GamePlayer player) : base(player) { }
    public SluaghbinderEpic40(GamePlayer player, int step) : base(player, step) { }
    public SluaghbinderEpic40(GamePlayer player, DbQuest quest) : base(player, quest) { }
    protected override Type Prerequisite => typeof(SluaghbinderEpic30);
    protected override int RequiredLevel => 40;
}

public sealed class SluaghbinderEpic50 : SluaghbinderEpicQuest
{
    public SluaghbinderEpic50() : base() { }
    public SluaghbinderEpic50(GamePlayer player) : base(player) { }
    public SluaghbinderEpic50(GamePlayer player, int step) : base(player, step) { }
    public SluaghbinderEpic50(GamePlayer player, DbQuest quest) : base(player, quest) { }
    protected override Type Prerequisite => typeof(SluaghbinderEpic40);
    protected override int RequiredLevel => 50;
}

public sealed class SluaghbinderEpicQuestDefinition
{
    public required Type QuestType { get; init; }
    public required string Title { get; init; }
    public required string TargetName { get; init; }
    public required string Clue { get; init; }
    public required int Region { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Z { get; init; }
    public required byte QuestLevel { get; init; }
    public required byte Level { get; init; }
    public required ushort Model { get; init; }
    public required int RewardSpellId { get; init; }
}

/// <summary>
/// Keeps one named quest target in the world for each step of the chain.  The
/// title-case names are deliberate: the autonomous grind catalog ignores
/// named/title-case NPCs, keeping these encounters quest-only.  The targets
/// use the normal NPC death/respawn path, so a player can find them before
/// accepting a quest and a later hunter is not left with a permanently empty
/// spawn.
/// </summary>
public static class SluaghbinderEpicQuestRuntime
{
    private static readonly object Sync = new();
    private static readonly Dictionary<Type, SluaghbinderEpicQuestDefinition> Definitions = new()
    {
        [typeof(SluaghbinderEpic10)] = new()
        {
            QuestType = typeof(SluaghbinderEpic10), Title = "The Rotten Thread", TargetName = "Gavin the Rotten",
            Clue = "Muirenn whispers of a corpse that should have stayed beneath the mist: travel to Lough Derg and seek the Parthanan farm. Follow the farm's southern edge into the quiet grass, where a weathered cairn marks the place the dead still answer their names.",
            Region = 200, X = 373600, Y = 510600, Z = 5100, QuestLevel = 10, Level = 15, Model = 110, RewardSpellId = 59080,
        },
        [typeof(SluaghbinderEpic20)] = new()
        {
            QuestType = typeof(SluaghbinderEpic20), Title = "Bones Beneath the Cairn", TargetName = "Mirebound Ossuary",
            Clue = "The rot has seeped deeper. Descend into Muire Tomb and seek Frang himself. The mire-bound ossuary has taken root directly beside the keeper, where the old stones meet his watch.",
            Region = 221, X = 32213, Y = 32765, Z = 15040, QuestLevel = 20, Level = 25, Model = 2213, RewardSpellId = 59081,
        },
        [typeof(SluaghbinderEpic30)] = new()
        {
            QuestType = typeof(SluaghbinderEpic30), Title = "The Fomor Gravewarden", TargetName = "Fomor Gravewarden",
            Clue = "A Fomor warden now keeps the stolen dead. Travel into the Vale of Balor and follow the lower Fomorian road, where the scattered sentries guard the warm green stone. There, a gravewarden has broken from the living patrol.",
            Region = 181, X = 350500, Y = 388800, Z = 5750, QuestLevel = 30, Level = 35, Model = 826, RewardSpellId = 59082,
        },
        [typeof(SluaghbinderEpic40)] = new()
        {
            QuestType = typeof(SluaghbinderEpic40), Title = "Ink of the Grave", TargetName = "Mirewood Death-Scribe",
            Clue = "The names of the dead are being written again. Enter the Cursed Forest and follow the blackwood trail to the black wraiths' standing stones; beneath the blue-tinged boughs, a death-scribe keeps the unquiet ledger.",
            // Move the scribe north off the blackwood trunk while keeping it
            // beside the standing stones and the black-wraith route.
            Region = 200, X = 479700, Y = 503630, Z = 5760, QuestLevel = 40, Level = 45, Model = 440, RewardSpellId = 59083,
        },
        [typeof(SluaghbinderEpic50)] = new()
        {
            QuestType = typeof(SluaghbinderEpic50), Title = "The Grave-Summoner", TargetName = "Mór-Ríoghain, the Grave-Summoner",
            Clue = "All five threads lead to one living hand. Enter the deepest galleries of Coruscating Mine and follow the Abysmals' chamber, where silver light dies. End the necromancer and the three dead that guard the last ritual.",
            Region = 220, X = 31820, Y = 25200, Z = 14174, QuestLevel = 50, Level = 55, Model = 8, RewardSpellId = 59084,
        },
    };

    private static readonly Dictionary<Type, GameNPC> ActiveTargets = new();
    private static readonly Dictionary<Type, List<GameNPC>> ActiveAdds = new();

    public static SluaghbinderEpicQuestDefinition GetDefinition(Type questType) => Definitions[questType];

    /// <summary>
    /// The generic quest-indicator code only understands data quests for the
    /// turn-in marker.  These five chained quests are scripted, so provide a
    /// small class-local indicator adapter for Muirenn without changing the
    /// global quest system.
    /// </summary>
    public static eQuestIndicator GetTrainerQuestIndicator(GamePlayer player, GameNPC trainer)
    {
        if (player == null || trainer == null || trainer != Muirenn)
            return eQuestIndicator.None;

        Type questType = GetNextQuest(player);
        if (questType == null)
            return eQuestIndicator.None;

        if (player.IsDoingQuest(questType) is SluaghbinderEpicQuest active)
            return active.Step == 2 ? eQuestIndicator.Finish : eQuestIndicator.None;

        return trainer.CanGiveQuest(questType, player) > 0
            ? eQuestIndicator.Available
            : eQuestIndicator.None;
    }

    public static void UpdateTrainerIndicator(GamePlayer player)
    {
        if (player != null && Muirenn != null)
            player.Out.SendNPCsQuestEffect(Muirenn, GetTrainerQuestIndicator(player, Muirenn));
    }

    public static void StartQuest(Type questType)
    {
        if (!Definitions.TryGetValue(questType, out SluaghbinderEpicQuestDefinition definition))
            return;

        lock (Sync)
        {
                if (ActiveTargets.TryGetValue(questType, out GameNPC existing))
                {
                    // A world target may be dead while its ordinary NPC respawn
                    // timer is running.  Do not create a duplicate on quest
                    // acceptance; the same object will return at its spawnpoint.
                    if (existing.ObjectState == GameObject.eObjectState.Active || existing.IsRespawning)
                        return;

                ActiveTargets.Remove(questType);
            }

            Region region = WorldMgr.GetRegion((ushort)definition.Region);
            if (region == null || region.IsDisabled)
                return;

            GameNPC target = CreateHostile(definition.TargetName, definition.Level, definition.Model,
                definition.Region, definition.X, definition.Y, definition.Z, 55,
                questType == typeof(SluaghbinderEpic30)
                    ? (ushort)NpcTemplateMgr.eBodyType.Humanoid
                    : (ushort)NpcTemplateMgr.eBodyType.Undead);
            ActiveTargets[questType] = target;
            RegisterTargetHandler(target);

            if (questType == typeof(SluaghbinderEpic50))
            {
                List<GameNPC> adds = new();
                (int X, int Y, int Z, string Name, ushort Model)[] addData =
                {
                    (31480, 25080, 14174, "Gravebound Acolyte", 24),
                    (31820, 25090, 14174, "Gravebound Wight", 2213),
                    (31590, 25320, 14174, "Gravebound Hexer", 25),
                };
                foreach (var add in addData)
                {
                    GameNPC epicAdd = CreateHostile(add.Name, 50, add.Model, definition.Region, add.X, add.Y, add.Z, 50);
                    // Epic-50 adds are encounter-only helpers.  They are
                    // removed with their quest target and must not create a
                    // second encounter by respawning on their own.
                    epicAdd.RespawnInterval = -1;
                    adds.Add(epicAdd);
                }
                ActiveAdds[questType] = adds;
            }
        }
    }

    /// <summary>
    /// Quest targets are deleted by the shared NPC death pipeline before their
    /// ordinary respawn timer fires.  GameObject.Delete removes per-object
    /// handlers, so use a tiny target type that reattaches this quest's Dying
    /// handler when the same NPC returns instead of spawning a duplicate.
    /// </summary>
    private sealed class SluaghbinderQuestTarget : GameNPC
    {
        protected override int RespawnTimerCallback(ECSGameTimer respawnTimer)
        {
            int result = base.RespawnTimerCallback(respawnTimer);
            if (ObjectState == eObjectState.Active)
                RegisterTargetHandler(this);
            return result;
        }
    }

    private static GameNPC CreateHostile(string name, byte level, ushort model, int regionId, int x, int y, int z, int aggroRange,
        ushort bodyType = (ushort)NpcTemplateMgr.eBodyType.Undead)
    {
        GameNPC npc = new SluaghbinderQuestTarget()
        {
            Name = name,
            // Quest targets are ordinary in-world NPCs; the quest marker and
            // dialogue identify their role, so do not expose an out-of-world
            // developer tag above their names.
            GuildName = string.Empty,
            Model = model,
            Size = 55,
            Level = level,
            Realm = eRealm.None,
            CurrentRegionID = (ushort)regionId,
            CurrentRegion = WorldMgr.GetRegion((ushort)regionId),
            X = x,
            Y = y,
            Z = z,
            Heading = 0,
            MaxSpeedBase = 200,
            // 0 uses the normal named-NPC respawn policy (including the
            // standard delay for title-case named mobs).  A negative interval
            // would make a quest kill permanently disappear until restart.
            RespawnInterval = 0,
            BodyType = bodyType,
            LoadedFromScript = true,
        };
        StandardMobBrain brain = new() { AggroLevel = 100, AggroRange = aggroRange };
        npc.SetOwnBrain(brain);
        if (name == "Mór-Ríoghain, the Grave-Summoner")
            ConfigureGraveSummonerAppearance(npc);
        npc.AddToWorld();
        return npc;
    }

    /// <summary>
    /// Gives the final quest target the same dark cloth silhouette as a
    /// necromancer, but uses the skeletal-hand staff model from the
    /// necromancer trainer's class-quest equipment.  This is presentation
    /// equipment only; it does not alter the target's combat statistics.
    /// </summary>
    private static void ConfigureGraveSummonerAppearance(GameNPC npc)
    {
        if (npc == null)
            return;

        GameNpcInventoryTemplate template = new();
        template.AddNPCEquipment(eInventorySlot.Cloak, 676, 0);
        template.AddNPCEquipment(eInventorySlot.TorsoArmor, 1266, 0);
        template.AddNPCEquipment(eInventorySlot.LegsArmor, 140, 0);
        template.AddNPCEquipment(eInventorySlot.ArmsArmor, 141, 0);
        template.AddNPCEquipment(eInventorySlot.HandsArmor, 142, 0);
        template.AddNPCEquipment(eInventorySlot.FeetArmor, 143, 0);
        template.AddNPCEquipment(eInventorySlot.TwoHandWeapon, 821, 0);

        npc.Inventory = template.CloseTemplate();
        npc.IsCloakHoodUp = true;
        npc.InitializeActiveWeaponFromInventory();
        npc.VisibleActiveWeaponSlots = 34;
    }

    private static void RegisterTargetHandler(GameNPC target)
    {
        if (target == null)
            return;

        // Script reloads can call this more than once.  Remove only this
        // script's handler before adding it back, preventing duplicate quest
        // completion notifications without touching unrelated handlers.
        GameEventMgr.RemoveHandler(target, GameLivingEvent.Dying, TargetDying);
        GameEventMgr.AddHandler(target, GameLivingEvent.Dying, TargetDying);
    }

    private static void EnsureWorldTargetsSpawned()
    {
        lock (Sync)
        {
            foreach ((Type questType, SluaghbinderEpicQuestDefinition definition) in Definitions)
            {
                if (ActiveTargets.TryGetValue(questType, out GameNPC current))
                {
                    if (current.ObjectState == GameObject.eObjectState.Active || current.IsRespawning)
                    {
                        // Definitions are authoritative for these scripted
                        // targets.  Apply a coordinate correction on reload
                        // as well as on a fresh spawn so a previously-created
                        // target cannot remain embedded in scenery after its
                        // location is repaired.
                        current.CurrentRegionID = (ushort)definition.Region;
                        current.CurrentRegion = WorldMgr.GetRegion((ushort)definition.Region);
                        current.X = definition.X;
                        current.Y = definition.Y;
                        current.Z = definition.Z;
                        current.Model = definition.Model;
                        current.Level = definition.Level;
                        current.RespawnInterval = 0;
                        if (current.Name == "Mór-Ríoghain, the Grave-Summoner")
                            ConfigureGraveSummonerAppearance(current);
                        RegisterTargetHandler(current);
                        continue;
                    }

                    ActiveTargets.Remove(questType);
                }

                // Avoid a duplicate when a script reload preserved an object
                // but reset the static map.
                GameNPC existing = WorldMgr.GetNPCsByName(definition.TargetName, eRealm.None)
                    .FirstOrDefault(npc => npc.CurrentRegionID == definition.Region &&
                                           Math.Abs(npc.X - definition.X) <= 300 &&
                                           Math.Abs(npc.Y - definition.Y) <= 300);

                if (existing != null)
                {
                    // Re-use the existing world object, but move it to the
                    // current definition.  This is what makes location fixes
                    // effective for targets that survived a server restart.
                    existing.CurrentRegionID = (ushort)definition.Region;
                    existing.CurrentRegion = WorldMgr.GetRegion((ushort)definition.Region);
                    existing.X = definition.X;
                    existing.Y = definition.Y;
                    existing.Z = definition.Z;
                    existing.Model = definition.Model;
                    existing.Level = definition.Level;
                    existing.RespawnInterval = 0;
                }

                GameNPC target = existing ?? CreateHostile(definition.TargetName, definition.Level, definition.Model,
                    definition.Region, definition.X, definition.Y, definition.Z, 55,
                    questType == typeof(SluaghbinderEpic30)
                        ? (ushort)NpcTemplateMgr.eBodyType.Humanoid
                        : (ushort)NpcTemplateMgr.eBodyType.Undead);

                if (target.Name == "Mór-Ríoghain, the Grave-Summoner")
                    ConfigureGraveSummonerAppearance(target);

                ActiveTargets[questType] = target;
                RegisterTargetHandler(target);
            }
        }
    }

    private static void TargetDying(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GameNPC target || args is not DyingEventArgs dying)
            return;

        Type questType;
        lock (Sync)
        {
            questType = ActiveTargets.FirstOrDefault(pair => ReferenceEquals(pair.Value, target)).Key;
            if (questType == null)
                return;

            if (ActiveAdds.TryGetValue(questType, out List<GameNPC> adds))
            {
                foreach (GameNPC add in adds)
                {
                    GameEventMgr.RemoveAllHandlersForObject(add);
                    if (add.ObjectState == GameObject.eObjectState.Active)
                        add.Delete();
                }
                ActiveAdds.Remove(questType);
            }
        }

        GamePlayer killer = dying.Killer as GamePlayer;
        if (killer == null && dying.Killer is GameSummonedPet pet)
            killer = pet.Owner as GamePlayer;
        if (killer == null)
            return;

        List<GamePlayer> participants = new() { killer };
        if (killer.Group != null)
            participants.AddRange(killer.Group.GetPlayersInTheGroup().Where(p => p != null && !participants.Contains(p)));

        foreach (GamePlayer player in participants)
        {
            if (player.IsDoingQuest(questType) is SluaghbinderEpicQuest quest && quest.Step == 1)
            {
                quest.Step = 2;
                player.Out.SendMessage($"{target.Name} is defeated. Return to Muirenn in Tir na Nog for your reward.",
                    eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);
            }
        }

        // GameNPC.ProcessDeath owns Delete() and StartRespawn().  Leaving the
        // target mapped and its handler attached lets the same object respawn
        // through the normal NPC timer and keeps later quest kills valid.
    }

    public static Type GetNextQuest(GamePlayer player)
    {
        foreach (Type questType in Definitions.Keys.OrderBy(type => Definitions[type].Level))
        {
            if (player.IsDoingQuest(questType) != null)
                return questType;

            if (player.HasFinishedQuest(questType) > 0)
                continue;

            SluaghbinderEpicQuestDefinition definition = Definitions[questType];
            if (player.Level >= definition.QuestLevel &&
                (questType == typeof(SluaghbinderEpic10) || player.HasFinishedQuest(GetPrerequisite(questType)) > 0))
                return questType;
        }
        return null;
    }

    private static Type GetPrerequisite(Type questType) => questType switch
    {
        var t when t == typeof(SluaghbinderEpic20) => typeof(SluaghbinderEpic10),
        var t when t == typeof(SluaghbinderEpic30) => typeof(SluaghbinderEpic20),
        var t when t == typeof(SluaghbinderEpic40) => typeof(SluaghbinderEpic30),
        var t when t == typeof(SluaghbinderEpic50) => typeof(SluaghbinderEpic40),
        _ => null,
    };

    [ScriptLoadedEvent]
    public static void ScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        if (!ServerProperties.Properties.LOAD_QUESTS)
            return;

        GameNPC[] npcs = WorldMgr.GetNPCsByName("Muirenn", eRealm.Hibernia);
        Muirenn = npcs.FirstOrDefault(npc => npc.CurrentRegionID == 201) ?? npcs.FirstOrDefault();

        // World targets are independent of the trainer lookup.  A transient
        // trainer load/order issue must never leave the five quest encounters
        // absent until the next server restart.
        EnsureWorldTargetsSpawned();

        if (Muirenn == null)
            return;

        foreach (Type questType in Definitions.Keys)
            Muirenn.AddQuestToGive(questType);

        GameEventMgr.AddHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
        GameEventMgr.AddHandler(Muirenn, GameObjectEvent.Interact, TalkToMuirenn);
        GameEventMgr.AddHandler(Muirenn, GameLivingEvent.WhisperReceive, TalkToMuirenn);
    }

    private static GameNPC Muirenn;

    [ScriptUnloadedEvent]
    public static void ScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        if (Muirenn == null)
            return;

        GameEventMgr.RemoveHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
        GameEventMgr.RemoveHandler(Muirenn, GameObjectEvent.Interact, TalkToMuirenn);
        GameEventMgr.RemoveHandler(Muirenn, GameLivingEvent.WhisperReceive, TalkToMuirenn);
        foreach (Type questType in Definitions.Keys)
            Muirenn.RemoveQuestToGive(questType);
        Muirenn = null;
    }

    private static void TalkToMuirenn(DOLEvent e, object sender, EventArgs args)
    {
        if (Muirenn == null || args is not SourceEventArgs sourceArgs || sourceArgs.Source is not GamePlayer player)
            return;

        Type questType = GetNextQuest(player);
        SluaghbinderEpicQuest active = questType == null ? null : player.IsDoingQuest(questType) as SluaghbinderEpicQuest;

        if (e == GameObjectEvent.Interact)
        {
            if (active != null)
            {
                Muirenn.SayTo(player, active.Step == 1
                    ? active.Description
                    : "The grave is quiet for now. Return to me and choose [reward].");
            }
            else if (questType != null && Muirenn.CanGiveQuest(questType, player) > 0)
            {
                SluaghbinderEpicQuestDefinition definition = Definitions[questType];
                Muirenn.SayTo(player, $"The next thread is ready: {definition.Title}. The dead must be faced before the service can be [begun].");
            }
            else
            {
                Muirenn.SayTo(player, "The six paths are quiet. Return when the next threshold has opened, or when your current hunt is complete.");
            }
        }
        else if (e == GameLivingEvent.WhisperReceive && args is WhisperReceiveEventArgs whisper)
        {
            string text = whisper.Text.Trim().ToLowerInvariant();
            if (active != null && active.Step == 2 && (text == "reward" || text == "claim reward"))
            {
                active.FinishQuest();
            }
            else if (active == null && questType != null && (text == "begun" || text == "begin" || text == "start"))
            {
                QuestMgr.ProposeQuestToPlayer(questType,
                    $"Will you undertake {Definitions[questType].Title}?", player, Muirenn);
            }
        }
    }

    private static void AcceptQuest(DOLEvent e, object sender, EventArgs args)
    {
        if (e != GamePlayerEvent.AcceptQuest || args is not QuestEventArgs questArgs || Muirenn == null)
            return;

        Type questType = QuestMgr.GetQuestTypeForID(questArgs.QuestID);
        if (questType == null || !Definitions.ContainsKey(questType) ||
            Muirenn.CanGiveQuest(questType, questArgs.Player) <= 0)
            return;

        Muirenn.GiveQuest(questType, questArgs.Player, 1);
    }
}
