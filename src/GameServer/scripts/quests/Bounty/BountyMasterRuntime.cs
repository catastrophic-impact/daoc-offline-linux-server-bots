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

        private const string OfferedDifficultyKey = "BountyOfferedDifficulty";
        private const string RerollDifficultyKey = "BountyRerollDifficulty";
        private const string UpdateDifficultyKey = "BountyUpdateDifficulty";

        /// <summary>The difficulty last offered by the Bounty Master; read once when the quest is accepted.</summary>
        internal static BountyDifficulty TakeOfferedDifficulty(GamePlayer player) => TakeDifficulty(player, OfferedDifficultyKey);

        private static BountyDifficulty TakeDifficulty(GamePlayer player, string key)
        {
            if (player == null)
                return BountyDifficulty.Normal;
            BountyDifficulty difficulty = player.TempProperties.GetProperty(key, BountyDifficulty.Normal);
            player.TempProperties.RemoveProperty(key);
            return difficulty;
        }

        private static string Lower(BountyDifficulty difficulty) =>
            BountyDifficultyRules.DisplayName(difficulty).ToLowerInvariant();

        private static string Bulbs(BountyDifficulty difficulty, bool rerolled)
        {
            int bulbs = BountyDifficultyRules.Bulbs(difficulty, rerolled);
            return $"{bulbs} XP bulb{(bulbs == 1 ? "" : "s")}";
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
                string update = active?.CanUpdateLegacy == true
                    ? " This contract is from an older version: [update bounty] to swap it once, free, for a new hunt."
                    : string.Empty;
                if (active == null)
                {
                    master.SayTo(player, player.Level >= 50
                        ? "The roads have grown hungry for blood. I keep a ledger of those who threaten our folk. " +
                          "Take [a bounty] when you are ready, or ask [how bounties work]."
                        : "The roads have grown hungry for blood. Take a [normal bounty] at your level, a [hard bounty] " +
                          "about 6 levels higher, or a [very hard bounty] about 12 higher. Or ask [how bounties work].");
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
                        "Your target remains marked on its local map until you do. " + reroll + refresh + update);
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
                        $"{reroll}.{refresh}{update}");
                }
                return;
            }

            if (e != GameLivingEvent.WhisperReceive || args is not WhisperReceiveEventArgs whisper)
                return;

            string text = whisper.Text.Trim().ToLowerInvariant();
            switch (text)
            {
                case "a bounty":
                case "take a bounty":
                case "bounty":
                    OfferBounty(master, player, active, BountyDifficulty.Normal);
                    return;

                case "how bounties work":
                    master.SayTo(player, "A normal hunt is at your level, a hard hunt about 6 levels higher and a very hard hunt " +
                        "about 12 higher. Where too few kinds of monster live at that exact level, the mark may be one or two levels lower " +
                        "(never below 4 higher on hard or 10 higher on very hard). " +
                        "They pay 2, 4 or 8 XP bulbs at your level and 1-3 class items 1, 3 or 5 levels above you. " +
                        "Kills needed: 5 below level 20, 10 in your 20s, 15 in your 30s and 20 in your 40s.");
                    master.SayTo(player, "The journal counts kills. Enter the target's zone or dungeon, then press BOUNTY MAP " +
                        "to open its local map and see the red dot; it cannot show another zone. " +
                        "Reroll freely, but XP is halved until you finish; a reroll keeps the difficulty or picks an easier one. " +
                        "Outleveled contracts refresh free.");
                    master.SayTo(player, "For an outdoor hunt, the marked camp is a lead: the same-named hostile monster " +
                        "elsewhere in your realm counts too (on hard and very hard, only if it is at most one level below the mark). " +
                        "Dungeon contracts still require their assigned dungeon.");
                    return;

                case "show location":
                    if (active != null)
                    {
                        active.ShowMarker();
                        master.SayTo(player, $"Seek {active.Target?.Name} in {active.Target?.ZoneName}. " +
                            "Enter that zone or dungeon, then open its local map to see the red bounty dot. " +
                            "The journal's BOUNTY MAP button opens your current map; /bountylocation refreshes the marker.");
                        if (active.Target?.IsDungeon == false && active.Target.IsEpic == false)
                            master.SayTo(player, "This mark is one likely camp. The same-named hostile monster " +
                                "in another outdoor part of your realm counts as well" +
                                (active.Difficulty == BountyDifficulty.Normal ? "." : ", if it is at most one level below the mark."));
                    }
                    return;

                case "reroll":
                    if (active == null)
                        return;
                    if (active.AssignedLevel == 50 || active.Difficulty == BountyDifficulty.Normal)
                    {
                        AskReroll(player, active, BountyDifficulty.Normal);
                        return;
                    }
                    master.SayTo(player, active.Difficulty == BountyDifficulty.VeryHard
                        ? "Reroll to which hunt? [reroll very hard], [reroll hard] or [reroll normal]. It pays half the XP of the one you choose."
                        : "Reroll to which hunt? [reroll hard] or [reroll normal]. It pays half the XP of the one you choose.");
                    return;

                case "update bounty":
                    if (active?.CanUpdateLegacy == true)
                        master.SayTo(player, "Your contract was written under the old ledger. Choose a new hunt at your level, free: " +
                            "[update normal], [update hard] or [update very hard]. Your current kill progress is replaced.");
                    return;

                case "refresh":
                    if (active != null && player.Level > active.AssignedLevel)
                        player.Out.SendCustomDialog("Replace this outleveled bounty with one at your current level, free of penalty? Your current kill progress will be lost.",
                            ConfirmRefresh);
                    return;

                case "claim reward":
                case "reward":
                    if (active?.IsReady == true)
                    {
                        active.QuestGiver = master;
                        active.Claim();
                    }
                    return;
            }

            BountyDifficulty choice;
            if (text.EndsWith(" bounty") && BountyDifficultyRules.TryParseChoice(text[..^" bounty".Length], out choice))
                OfferBounty(master, player, active, choice);
            else if (text.StartsWith("reroll ") && BountyDifficultyRules.TryParseChoice(text["reroll ".Length..], out choice))
            {
                if (active != null && active.AssignedLevel < 50 && choice <= active.Difficulty)
                    AskReroll(player, active, choice);
            }
            else if (text.StartsWith("update ") && BountyDifficultyRules.TryParseChoice(text["update ".Length..], out choice))
            {
                if (active?.CanUpdateLegacy == true)
                {
                    player.TempProperties.SetProperty(UpdateDifficultyKey, choice);
                    player.Out.SendCustomDialog($"Replace your old bounty with a new {Lower(choice)} hunt at your level, free of penalty? " +
                        "Your current kill progress will be lost.", ConfirmUpdate);
                }
            }
        }

        private static void OfferBounty(BountyMasterNPC master, GamePlayer player, BountyQuest active, BountyDifficulty difficulty)
        {
            if (active != null || master.CanGiveQuest(typeof(BountyQuest), player) <= 0)
                return;

            if (player.Level >= 50)
                difficulty = BountyDifficulty.Normal;
            player.TempProperties.SetProperty(OfferedDifficultyKey, difficulty);

            int kills = BountyQuest.RequiredKillsForLevel(player.Level);
            string where = difficulty switch
            {
                BountyDifficulty.Hard => "about 6 levels above you",
                BountyDifficulty.VeryHard => "about 12 levels above you",
                _ => "at your level"
            };
            string offer = player.Level >= 50
                ? "Accept a great-foe bounty? Defeat one named boss and return for 100 gold and 1-3 exceptional class items. The red dot appears on the target's local map."
                : $"Accept a {Lower(difficulty)} hunt? Slay {kills} monsters {where}; the journal tracks kills. " +
                  $"Return for {Bulbs(difficulty, false)} at your level and 1-3 level " +
                  $"{BountyDifficultyRules.GearLevel((byte)player.Level, difficulty)} class items. The red dot appears on the target's local map.";
            player.Out.SendQuestSubscribeCommand(master, QuestMgr.GetIDForQuestType(typeof(BountyQuest)), offer);
        }

        private static void AskReroll(GamePlayer player, BountyQuest active, BountyDifficulty difficulty)
        {
            player.TempProperties.SetProperty(RerollDifficultyKey, difficulty);
            player.Out.SendCustomDialog(active.AssignedLevel == 50
                    ? "Choose a different great foe? Level-50 gold and equipment rewards are unchanged."
                    : $"Choose a different {Lower(difficulty)} monster? This bounty will pay only {Bulbs(difficulty, true)} " +
                      "at its assigned level, even after further rerolls.",
                ConfirmReroll);
        }

        private static void ConfirmReroll(GamePlayer player, byte response)
        {
            BountyDifficulty difficulty = TakeDifficulty(player, RerollDifficultyKey);
            if (response != 0x01 || player?.IsDoingQuest(typeof(BountyQuest)) is not BountyQuest quest ||
                !NearOwnMaster(player, out _))
                return;
            quest.Reroll(difficulty);
        }

        private static void ConfirmUpdate(GamePlayer player, byte response)
        {
            BountyDifficulty difficulty = TakeDifficulty(player, UpdateDifficultyKey);
            if (response != 0x01 || player?.IsDoingQuest(typeof(BountyQuest)) is not BountyQuest quest ||
                !NearOwnMaster(player, out _))
                return;
            quest.UpdateLegacy(difficulty);
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
                quest.NormalizeLegacyOnLogin();
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
