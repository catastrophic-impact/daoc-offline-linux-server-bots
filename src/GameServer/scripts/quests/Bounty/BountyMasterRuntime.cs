using System;
using System.Collections.Generic;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;

namespace DOL.GS
{
    /// <summary>Quest marker and appearance for one realm's stationary bounty giver.</summary>
    public sealed class BountyMasterNPC : GameNPC
    {
        public override eQuestIndicator GetQuestIndicator(GamePlayer player)
        {
            if (player == null || player.Realm != Realm)
                return eQuestIndicator.None;

            if (player.IsDoingQuest(typeof(BountyQuest)) is BountyQuest active)
                return active.IsReady ? eQuestIndicator.Finish : eQuestIndicator.None;

            return CanGiveQuest(typeof(BountyQuest), player) > 0
                ? eQuestIndicator.Available
                : eQuestIndicator.None;
        }
    }

    /// <summary>
    /// Three open-air, road-facing Bounty Masters. They are script-owned, not
    /// saved over existing village NPCs or monster spawns. Quest records, not
    /// NPC objects, own individual players' progress.
    /// </summary>
    public static class BountyMasterRuntime
    {
        private static readonly Dictionary<eRealm, BountyMasterNPC> Masters = new();

        public static BountyMasterNPC GetMaster(eRealm realm) => Masters.TryGetValue(realm, out BountyMasterNPC npc) ? npc : null;

        [ScriptLoadedEvent]
        public static void ScriptLoaded(DOLEvent e, object sender, EventArgs args)
        {
            if (!ServerProperties.Properties.LOAD_QUESTS)
                return;

            // Deliberately offset from the stable/healer clusters and doorway
            // lines, while remaining on their ground-level village approaches.
            AddMaster(eRealm.Hibernia, "Maelin Greenmantle", 200, 346750, 491480, 5200, 3416, 342, eGender.Female);
            AddMaster(eRealm.Albion, "Dame Elowen Vale", 1, 560800, 511550, 2280, 761, 38, eGender.Female);
            AddMaster(eRealm.Midgard, "Yrsa Wolfmark", 100, 804150, 724550, 4680, 2393, 218, eGender.Female);

            GameEventMgr.AddHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
            GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, PlayerEntered);
            GameEventMgr.AddHandler(GamePlayerEvent.Quit, PlayerQuit);
        }

        [ScriptUnloadedEvent]
        public static void ScriptUnloaded(DOLEvent e, object sender, EventArgs args)
        {
            GameEventMgr.RemoveHandler(GamePlayerEvent.AcceptQuest, AcceptQuest);
            GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, PlayerEntered);
            GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, PlayerQuit);

            foreach (BountyMasterNPC master in Masters.Values)
            {
                GameEventMgr.RemoveHandler(master, GameObjectEvent.Interact, TalkToMaster);
                GameEventMgr.RemoveHandler(master, GameLivingEvent.WhisperReceive, TalkToMaster);
                master.RemoveQuestToGive(typeof(BountyQuest));
                if (master.ObjectState == GameObject.eObjectState.Active)
                    master.Delete();
            }

            Masters.Clear();
        }

        public static void UpdateIndicator(GamePlayer player)
        {
            BountyMasterNPC master = player == null ? null : GetMaster(player.Realm);
            if (master != null)
                player.Out.SendNPCsQuestEffect(master, master.GetQuestIndicator(player));
        }

        private static void AddMaster(eRealm realm, string name, ushort regionId, int x, int y, int z,
            ushort heading, ushort model, eGender gender)
        {
            Region region = WorldMgr.GetRegion(regionId);
            if (region == null || region.IsDisabled)
                return;

            BountyMasterNPC master = new()
            {
                Name = name,
                GuildName = "Bounty Master",
                Realm = realm,
                Gender = gender,
                Model = model,
                Size = 52,
                Level = 50,
                CurrentRegionID = regionId,
                CurrentRegion = region,
                X = x,
                Y = y,
                Z = z,
                Heading = heading,
                LoadedFromScript = true,
                MaxSpeedBase = 0,
                BodyType = (ushort)NpcTemplateMgr.eBodyType.Humanoid,
            };

            master.Flags |= GameNPC.eFlags.PEACE;
            DressMaster(master);
            if (!master.AddToWorld())
                return;

            Masters[realm] = master;
            master.AddQuestToGive(typeof(BountyQuest));
            GameEventMgr.AddHandler(master, GameObjectEvent.Interact, TalkToMaster);
            GameEventMgr.AddHandler(master, GameLivingEvent.WhisperReceive, TalkToMaster);
        }

        private static void DressMaster(BountyMasterNPC master)
        {
            GameNpcInventoryTemplate outfit = new();
            switch (master.Realm)
            {
                case eRealm.Hibernia:
                    // Celtic woodland hunter: moss-green reinforced pieces,
                    // dark cloak, and an existing in-game ranger bow.
                    outfit.AddNPCEquipment(eInventorySlot.TorsoArmor, 403, 69);
                    outfit.AddNPCEquipment(eInventorySlot.ArmsArmor, 405, 62);
                    outfit.AddNPCEquipment(eInventorySlot.LegsArmor, 404, 69);
                    outfit.AddNPCEquipment(eInventorySlot.HandsArmor, 406, 62);
                    outfit.AddNPCEquipment(eInventorySlot.FeetArmor, 407, 62);
                    outfit.AddNPCEquipment(eInventorySlot.Cloak, 57, 43);
                    outfit.AddNPCEquipment(eInventorySlot.DistanceWeapon, 471, 0);
                    master.IsCloakHoodUp = true;
                    master.VisibleActiveWeaponSlots = (byte)eInventorySlot.DistanceWeapon;
                    break;

                case eRealm.Albion:
                    // Weathered silver plate and a crimson field cloak.
                    outfit.AddNPCEquipment(eInventorySlot.TorsoArmor, 713, 0);
                    outfit.AddNPCEquipment(eInventorySlot.ArmsArmor, 715, 0);
                    outfit.AddNPCEquipment(eInventorySlot.LegsArmor, 714, 0);
                    outfit.AddNPCEquipment(eInventorySlot.HandsArmor, 716, 0);
                    outfit.AddNPCEquipment(eInventorySlot.FeetArmor, 717, 0);
                    outfit.AddNPCEquipment(eInventorySlot.Cloak, 4105, 27);
                    outfit.AddNPCEquipment(eInventorySlot.RightHandWeapon, 67, 0);
                    master.VisibleActiveWeaponSlots = 16;
                    break;

                case eRealm.Midgard:
                    // Fur-trimmed dark studded gear and a northern hunter's bow.
                    outfit.AddNPCEquipment(eInventorySlot.TorsoArmor, 250, 19);
                    outfit.AddNPCEquipment(eInventorySlot.ArmsArmor, 252, 19);
                    outfit.AddNPCEquipment(eInventorySlot.LegsArmor, 251, 19);
                    outfit.AddNPCEquipment(eInventorySlot.HandsArmor, 253, 19);
                    outfit.AddNPCEquipment(eInventorySlot.FeetArmor, 254, 19);
                    outfit.AddNPCEquipment(eInventorySlot.Cloak, 326, 32);
                    outfit.AddNPCEquipment(eInventorySlot.DistanceWeapon, 564, 0);
                    master.VisibleActiveWeaponSlots = (byte)eInventorySlot.DistanceWeapon;
                    break;
            }

            master.Inventory = outfit.CloseTemplate();
            master.InitializeActiveWeaponFromInventory();
        }

        private static void TalkToMaster(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is not BountyMasterNPC master || args is not SourceEventArgs source ||
                source.Source is not GamePlayer player || player.Realm != master.Realm ||
                !master.IsWithinRadius(player, 600))
                return;

            BountyQuest active = player.IsDoingQuest(typeof(BountyQuest)) as BountyQuest;
            if (e == GameObjectEvent.Interact)
            {
                if (active == null)
                {
                    master.SayTo(player, "The roads have grown hungry for blood. I keep a ledger of those who threaten our folk. " +
                        "Take [a bounty] when you are ready, or ask [how bounties work].");
                }
                else if (active.IsReady)
                {
                    string refresh = player.Level > active.AssignedLevel
                        ? " Or [refresh] this outleveled contract for free."
                        : string.Empty;
                    string reroll = active.AssignedLevel == 50
                        ? "You may also [show location] or [reroll] for another foe."
                        : "You may also [show location] or [reroll] at half XP.";
                    master.SayTo(player, $"You have felled {active.Target?.Name}. Choose [claim reward] to close the ledger. " +
                        "Your target remains marked on its local map until you do. " + reroll + refresh);
                }
                else
                {
                    string refresh = player.Level > active.AssignedLevel
                        ? " You have grown beyond this contract; [refresh] it for free at your current level."
                        : string.Empty;
                    string reroll = active.AssignedLevel == 50
                        ? "for another great foe"
                        : "for another target at half XP";
                    master.SayTo(player, $"Your mark is {active.Target?.Name} in {active.Target?.ZoneName}: " +
                        $"{active.Progress}/{active.RequiredKills} slain. The red dot appears only on that zone's map. " +
                        "Ask to [show location], or [reroll] " +
                        $"{reroll}.{refresh}");
                }
                return;
            }

            if (e != GameLivingEvent.WhisperReceive || args is not WhisperReceiveEventArgs whisper)
                return;

            switch (whisper.Text.Trim().ToLowerInvariant())
            {
                case "a bounty":
                case "take a bounty":
                case "bounty":
                    if (active == null && master.CanGiveQuest(typeof(BountyQuest), player) > 0)
                    {
                        string offer = player.Level == 50
                            ? "Accept a great-foe bounty? Defeat one named boss and return for 100 gold and 1-3 exceptional class items. The red dot appears on the target's local map."
                            : "Accept a hunt at your level? The journal tracks kills. Return for two XP bulbs at the assigned level and 1-3 class items. The red dot appears on the target's local map.";
                        player.Out.SendQuestSubscribeCommand(master,
                            QuestMgr.GetIDForQuestType(typeof(BountyQuest)), offer);
                    }
                    break;

                case "how bounties work":
                    master.SayTo(player, "The journal counts kills. Enter the target's zone or dungeon, then press BOUNTY MAP " +
                        "to open its local map and see the red dot; it cannot show another zone. " +
                        "Reroll freely, but XP is halved until you finish. Outleveled contracts refresh free.");
                    master.SayTo(player, "For an outdoor hunt, the marked camp is a lead: the same-named hostile monster " +
                        "elsewhere in your realm counts too. Dungeon contracts still require their assigned dungeon.");
                    break;

                case "show location":
                    if (active != null)
                    {
                        active.ShowMarker();
                        master.SayTo(player, $"Seek {active.Target?.Name} in {active.Target?.ZoneName}. " +
                            "Enter that zone or dungeon, then open its local map to see the red bounty dot. " +
                            "The journal's BOUNTY MAP button opens your current map; /bountylocation refreshes the marker.");
                        if (active.Target?.IsDungeon == false && active.Target.IsEpic == false)
                            master.SayTo(player, "This mark is one likely camp. The same-named hostile monster " +
                                "in another outdoor part of your realm counts as well.");
                    }
                    break;

                case "reroll":
                    if (active != null)
                        player.Out.SendCustomDialog(active.AssignedLevel == 50
                                ? "Choose a different great foe? Level-50 gold and equipment rewards are unchanged."
                                : "Choose a different monster? This bounty will pay only one XP bulb at its assigned level, even after further rerolls.",
                            ConfirmReroll);
                    break;

                case "refresh":
                    if (active != null && player.Level > active.AssignedLevel)
                        player.Out.SendCustomDialog("Replace this outleveled bounty with one at your current level, free of penalty? Your current kill progress will be lost.",
                            ConfirmRefresh);
                    break;

                case "claim reward":
                case "reward":
                    if (active?.IsReady == true)
                    {
                        active.QuestGiver = master;
                        active.Claim();
                    }
                    break;
            }
        }

        private static void ConfirmReroll(GamePlayer player, byte response)
        {
            if (response != 0x01 || player?.IsDoingQuest(typeof(BountyQuest)) is not BountyQuest quest ||
                !NearOwnMaster(player, out _))
                return;
            quest.Reroll();
        }

        private static void ConfirmRefresh(GamePlayer player, byte response)
        {
            if (response != 0x01 || player?.IsDoingQuest(typeof(BountyQuest)) is not BountyQuest quest ||
                !NearOwnMaster(player, out _))
                return;
            quest.RefreshOutleveled();
        }

        private static bool NearOwnMaster(GamePlayer player, out BountyMasterNPC master)
        {
            master = player == null ? null : GetMaster(player.Realm);
            return master != null && master.CurrentRegionID == player.CurrentRegionID &&
                   master.IsWithinRadius(player, 600);
        }

        private static void AcceptQuest(DOLEvent e, object sender, EventArgs args)
        {
            if (e != GamePlayerEvent.AcceptQuest || args is not QuestEventArgs accepted ||
                QuestMgr.GetQuestTypeForID(accepted.QuestID) != typeof(BountyQuest) ||
                !NearOwnMaster(accepted.Player, out BountyMasterNPC master) ||
                accepted.Player.IsDoingQuest(typeof(BountyQuest)) != null ||
                master.CanGiveQuest(typeof(BountyQuest), accepted.Player) <= 0)
                return;

            master.GiveQuest(typeof(BountyQuest), accepted.Player, 1);
        }

        private static void PlayerEntered(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player && player.IsDoingQuest(typeof(BountyQuest)) is BountyQuest quest)
            {
                quest.QuestGiver = GetMaster(player.Realm);
                quest.ShowMarker();
                UpdateIndicator(player);
            }
        }

        private static void PlayerQuit(DOLEvent e, object sender, EventArgs args)
        {
            if (sender is GamePlayer player)
                BountyMapMarkers.Clear(player);
        }
    }
}
