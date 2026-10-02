using DOL.AI.Brain;
using DOL.Database;
using DOL.GS.Housing;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.Quests;
using System;
using System.Collections;
using System.Collections.Generic;

namespace DOL.GS
{
    public class BotDummyPacketLib : IPacketLib
    {
        public int BowPrepare => 0;

        public int BowShoot => 0;

        public int OneDualWeaponHit => 0;

        public int BothDualWeaponHit => 0;

        public void CheckLengthHybridSkillsPacket(ref GSTCPPacketOut pak, ref int maxSkills, ref int first)
        {
        }

        public byte GetPacketCode(eServerPackets packetCode)
        {
            return 0;
        }

        public void SendAddFriends(string[] friendNames)
        {
        }

        public void SendAttackMode(bool attackState)
        {
        }

        public void SendBadNameCheckReply(string name, bool bad)
        {
        }

        public void SendBlinkPanel(byte flag)
        {
        }

        public void SendChampionTrainerWindow(int type)
        {
        }

        public void SendChangeGroundTarget(Point3D newTarget)
        {
        }

        public void SendChangeTarget(GameObject newTarget)
        {
        }

        public void SendChangeGroundTarget(int x, int y, int z)
        {
            throw new NotImplementedException();
        }

        public void SendCharacterOverview(eRealm realm)
        {
        }

        public void SendCharCreateReply(string name)
        {
        }

        public void SendCharResistsUpdate()
        {
        }

        public void SendCharStatsUpdate()
        {
        }

        public void SendClearKeepComponentHookPoint(IGameKeepComponent component, int selectedHookPointIndex)
        {
        }

        public void SendCloseTimerWindow()
        {
        }

        public void SendCloseTradeWindow()
        {
        }

        public void SendCombatAnimation(GameObject attacker, GameObject defender, ushort weaponID, ushort shieldID, int style, byte stance, byte result, byte targetHealthPercent)
        {
        }

        public void SendConcentrationList()
        {
        }

        public void SendConsignmentMerchantMoney(long money)
        {
        }

        public void SendControlledHorse(GamePlayer player, bool flag)
        {
        }

        public void SendCrash(string str)
        {
        }

        public void SendCustomDialog(string msg, CustomDialogResponse callback)
        {
        }

        public bool SendLosCheckRequest(GameObject source, GameObject target, ILosCheckListener listener)
        {
            throw new NotImplementedException();
        }

        public void SendCustomTextWindow(string caption, IList<string> text)
        {
        }

        public void SendCustomTrainerWindow(int type, List<Tuple<Specialization, List<Tuple<Skill, byte>>>> tree)
        {
        }

        public void SendDebugMessage(string format, params object[] parameters)
        {
        }

        public void SendDebugMode(bool on)
        {
        }

        public void SendDebugPopupMessage(string format, params object[] parameters)
        {
        }

        public void SendDelveInfo(string info)
        {
        }

        public void SendDialogBox(eDialogCode code, ushort data1, ushort data2, ushort data3, ushort data4, eDialogType type, bool autoWrapText, string message)
        {
        }

        public void SendDisableSkill(ICollection<Tuple<Skill, int>> skills)
        {
        }

        public void SendDoorState(Region region, GameDoorBase door)
        {
        }

        public void SendDupNameCheckReply(string name, byte result)
        {
        }

        public void SendEmblemDialogue()
        {
        }

        public void SendEmoteAnimation(GameObject obj, eEmote emote)
        {
        }

        public void SendEncumberance()
        {
        }

        public void SendEncumbrance()
        {
        }

        public void SendEnterHouse(House house)
        {
        }

        public void SendExitHouse(House house, ushort unknown = 0)
        {
        }

        public void SendFindGroupWindowUpdate(GamePlayer[] list)
        {
        }

        public void SendFurniture(House house)
        {
        }

        public void SendFurniture(House house, int i)
        {
        }

        public void SendGameOpenReply()
        {
        }

        public void SendGarden(House house)
        {
        }

        public void SendGarden(House house, int i)
        {
        }

        public void SendGroupInviteCommand(GamePlayer invitingPlayer, string inviteMessage)
        {
        }

        public void SendGroupMembersUpdate(bool updateIcons, bool updateMap)
        {
        }

        public void SendGroupMemberUpdate(bool updateIcons, bool updateMap, GameLiving living)
        {
        }

        public void SendGroupWindowUpdate()
        {
        }

        public void SendGuildInviteCommand(GamePlayer invitingPlayer, string inviteMessage)
        {
        }

        public void SendGuildLeaveCommand(GamePlayer invitingPlayer, string inviteMessage)
        {
        }

        public void SendHexEffect(GamePlayer player, byte effect1, byte effect2, byte effect3, byte effect4, byte effect5)
        {
        }

        public void SendHookPointStore(GameKeepHookPoint hookPoint)
        {
        }

        public void SendHouse(House house)
        {
        }

        public void SendHouseOccupied(House house, bool flagHouseOccuped)
        {
        }

        public void SendHousePayRentDialog(string title)
        {
        }

        public void SendHouseUsersPermissions(House house)
        {
        }

        public void SendInterruptAnimation(GameLiving living)
        {
        }

        public void SendInventoryItemsUpdate(ICollection<DbInventoryItem> itemsToUpdate)
        {
        }

        public void SendInventoryItemsUpdate(eInventoryWindowType windowType, ICollection<DbInventoryItem> itemsToUpdate)
        {
        }

        public void SendInventoryItemsUpdate(IDictionary<int, DbInventoryItem> updateItems, eInventoryWindowType windowType)
        {
        }

        public void SendInventorySlotsUpdate(ICollection<int> slots)
        {
        }

        public void SendInventorySlotsUpdate(ICollection<eInventorySlot> slots)
        {
        }

        public void SendKeepClaim(IGameKeep keep, byte flag)
        {
        }

        public void SendKeepComponentDetailUpdate(IGameKeepComponent keepComponent)
        {
        }

        public void SendKeepComponentHookPoint(IGameKeepComponent component, int selectedHookPointIndex)
        {
        }

        public void SendKeepComponentInfo(IGameKeepComponent keepComponent)
        {
        }

        public void SendKeepComponentInteract(IGameKeepComponent component)
        {
        }

        public void SendKeepComponentRemove(IGameKeepComponent keepComponent)
        {
        }

        public void SendKeepComponentUpdate(IGameKeep keep, bool LevelUp)
        {
        }

        public void SendKeepInfo(IGameKeep keep)
        {
        }

        public void SendKeepRealmUpdate(IGameKeep keep)
        {
        }

        public void SendKeepRemove(IGameKeep keep)
        {
        }

        public void SendLevelUpSound()
        {
        }

        public void SendLivingDataUpdate(GameLiving living, bool updateStrings)
        {
        }

        public void SendLivingEquipmentUpdate(GameLiving living)
        {
        }

        public void SendLoginDenied(eLoginError et)
        {
        }

        public void SendLoginGranted()
        {
        }

        public void SendLoginGranted(byte color)
        {
        }

        public void SendMarketExplorerWindow(IList<DbInventoryItem> items, byte page, byte maxpage)
        {
        }

        public void SendMarketExplorerWindow()
        {
        }

        public void SendMasterLevelWindow(byte ml)
        {
        }

        public void SendMerchantWindow(MerchantTradeItems itemlist, eMerchantWindowType windowType)
        {
        }

        public void SendMessage(string msg, eChatType type, eChatLoc loc)
        {
        }

        public void SendRawMessage(string msg, eChatType type, eChatLoc loc)
        {
            throw new NotImplementedException();
        }

        public void SendMinotaurRelicBarUpdate(GamePlayer player, int xp)
        {
        }

        public void SendMinotaurRelicMapRemove(uint id)
        {
        }

        public void SendMinotaurRelicMapUpdate(uint id, ushort region, int x, int y, int z)
        {
        }

        public void SendMinotaurRelicWindow(GamePlayer player, int spell, bool flag)
        {
        }

        public void SendModelAndSizeChange(GameObject obj, ushort newModel, byte newSize)
        {
        }

        public void SendModelAndSizeChange(ushort objectId, ushort newModel, byte newSize)
        {
        }

        public void SendModelChange(GameObject obj, ushort newModel)
        {
        }

        public void SendMovingObjectCreate(GameMovingObject obj)
        {
        }

        public void SendNonHybridSpellLines()
        {
        }

        public void SendNPCCreate(GameNPC npc)
        {
        }

        public void SendNPCsQuestEffect(GameNPC npc, eQuestIndicator indicator)
        {
        }

        public void SendObjectCreate(GameObject obj)
        {
        }

        public void SendObjectDelete(GameObject obj)
        {
        }

        public void SendObjectDelete(ushort oid)
        {
        }

        public void SendObjectGuildID(GameObject obj, Guild guild)
        {
        }

        public void SendObjectRemove(GameObject obj)
        {
        }

        public void SendObjectUpdate(GameObject obj)
        {
        }

        public void SendObjectUpdate(GameObject obj, bool udp = true)
        {
        }

        public void SendPetWindow(GameLiving pet, ePetWindowAction windowAction, eAggressionState aggroState, eWalkState walkState)
        {
        }

        public void SendPingReply(ulong timestamp, ushort sequence)
        {
        }

        public void SendPlayerCreate(GamePlayer playerToCreate)
        {
        }

        public void SendPlayerDied(GamePlayer killedPlayer, GameObject killer)
        {
        }

        public void SendPlayerForgedPosition(GamePlayer player)
        {
        }

        public void SendPlayerFreeLevelUpdate()
        {
        }

        public void SendPlayerInitFinished(byte mobs)
        {
        }

        public void SendPlayerJump(bool headingOnly)
        {
        }

        public void SendPlayerModelTypeChange(GamePlayer player, byte modelType)
        {
        }

        public void SendPlayerPositionAndObjectID()
        {
        }

        public void SendPlayerQuit(bool totalOut)
        {
        }

        public void SendPlayerRevive(GamePlayer revivedPlayer)
        {
        }

        public void SendPlayerTitles()
        {
        }

        public void SendPlayerTitleUpdate(GamePlayer player)
        {
        }

        public void SendPlaySound(eSoundType soundType, ushort soundID)
        {
        }

        public void SendQuestAbortCommand(GameNPC abortingNPC, ushort questid, string abortMessage)
        {
        }

        public void SendQuestListUpdate()
        {
        }

        public void SendQuestOfferWindow(GameNPC questNPC, GamePlayer player, RewardQuest quest)
        {
        }

        public void SendQuestOfferWindow(GameNPC questNPC, GamePlayer player, DataQuest quest)
        {
        }

        public void SendQuestRemove(byte index)
        {
        }

        public void SendQuestRewardWindow(GameNPC questNPC, GamePlayer player, RewardQuest quest)
        {
        }

        public void SendQuestRewardWindow(GameNPC questNPC, GamePlayer player, DataQuest quest)
        {
        }

        public void SendQuestSubscribeCommand(GameNPC invitingNPC, ushort questid, string inviteMessage)
        {
        }

        public void SendQuestUpdate(AbstractQuest quest)
        {
        }

        public void SendRealm(eRealm realm)
        {
        }

        public void SendRegionChanged()
        {
        }

        public void SendRegionColorScheme()
        {
        }

        public void SendRegionColorScheme(byte color)
        {
        }

        public void SendRegionEnterSound(byte soundId)
        {
        }

        public void SendRegions(ushort region)
        {
        }

        public void SendRemoveFriends(string[] friendNames)
        {
        }

        public void SendRemoveHouse(House house)
        {
        }

        public void SendRentReminder(House house)
        {
        }

        public void SendRiding(GameObject rider, GameObject steed, bool dismount)
        {
        }

        public void SendRvRGuildBanner(GamePlayer player, bool show)
        {
        }

        public void SendSessionID()
        {
        }

        public void SendSetControlledHorse(GamePlayer player)
        {
        }

        public void SendSiegeWeaponAnimation(GameSiegeWeapon siegeWeapon)
        {
        }

        public void SendSiegeWeaponCloseInterface()
        {
        }

        public void SendSiegeWeaponFireAnimation(GameSiegeWeapon siegeWeapon, int timer)
        {
        }

        public void SendSiegeWeaponInterface(GameSiegeWeapon siegeWeapon, int time)
        {
        }

        public void SendSoundEffect(ushort soundId, ushort zoneId, ushort x, ushort y, ushort z, ushort radius)
        {
        }

        public void SendSpellCastAnimation(GameLiving spellCaster, ushort spellID, ushort castingTime)
        {
        }

        public void SendSpellEffectAnimation(GameObject spellCaster, GameObject spellTarget, ushort spellid, ushort boltTime, bool noSound, byte success)
        {
        }

        public void SendStarterHelp()
        {
        }

        public void SendStatusUpdate()
        {
        }

        public void SendStatusUpdate(byte sittingFlag)
        {
        }

        public void SendTCP(GSTCPPacketOut packet)
        {
        }

        public void SendTCP(byte[] buf)
        {
        }

        public void SendTCPRaw(GSTCPPacketOut packet)
        {
        }

        public void SendTime()
        {
        }

        public void SendTimerWindow(string title, int seconds)
        {
        }

        public void SendToggleHousePoints(House house)
        {
        }

        public void SendTradeWindow()
        {
        }

        public void SendTrainerWindow()
        {
        }

        public void SendUDP(GSUDPPacketOut packet)
        {
        }

        public void SendUDP(byte[] buf)
        {
        }

        public void SendUDPInitReply()
        {
        }

        public void SendUDPRaw(GSUDPPacketOut packet)
        {
        }

        public void SendUpdateCraftingSkills()
        {
        }

        public void SendUpdateIcons(IList changedEffects, ref int lastUpdateEffectsCount)
        {
        }

        public void SendUpdateMaxSpeed()
        {
        }

        public void SendUpdateMoney()
        {
        }

        public void SendUpdatePlayer()
        {
        }

        public void SendUpdatePlayerSkills()
        {
        }

        public void SendUpdatePlayerSkills(bool updateInternalCache)
        {
        }

        public void SendUpdatePoints()
        {
        }

        public void SendUpdateWeaponAndArmorStats()
        {
        }

        public void SendVampireEffect(GameLiving living, bool show)
        {
        }

        public void SendVersionAndCryptKey()
        {
        }

        public void SendWarlockChamberEffect(GamePlayer player)
        {
        }

        public void SendWarmapBonuses()
        {
        }

        public void SendWarmapDetailUpdate(List<List<byte>> fights, List<List<byte>> groups)
        {
        }

        public void SendWarmapUpdate(ICollection<IGameKeep> list)
        {
        }

        public void SendWeather(uint x, uint width, ushort speed, ushort fogdiffusion, ushort intensity)
        {
        }

        public void SendXFireInfo(byte flag)
        {
        }

    }
}
