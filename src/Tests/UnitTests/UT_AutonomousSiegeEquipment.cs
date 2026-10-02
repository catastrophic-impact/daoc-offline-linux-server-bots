using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_AutonomousSiegeEquipment
    {
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private sealed class Server : GameServer { protected override IObjectDatabase DataBaseImpl => Empty; }
        private GameServer _previous;
        [SetUp] public void Setup()
        { _previous=GameServer.Instance; GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server))); }
        [TearDown] public void Cleanup() => GameServer.LoadTestDouble(_previous);

        private sealed class Target : GameNPC
        {
            public override int MaxHealth => 1000;
            public override int X { get; set; }
            public override int Y { get; set; }
            public override int Z { get; set; }
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public override bool IsAlive => true;
            public override eRealm Realm { get; set; }
            public override ICharacterClass CharacterClass => new DOL.GS.PlayerClass.ClassArmsman();
        }

        [TestCase(true, 6)]
        [TestCase(false, 4)]
        public void RespondersShareDestinationCapacityAndRetainTheirReservedEquipment(bool attacking, int capacity)
        {
            var bots = new Bot[capacity + 1];
            string keep = "test-siege-" + Guid.NewGuid();
            try
            {
                for (int i = 0; i < bots.Length; i++)
                {
                    var bot = bots[i] = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
                    bot.Realm = eRealm.Albion;
                    bot.ObjectState = GameObject.eObjectState.Active;
                    typeof(GameBot).GetField("<IsAutonomousWorldBot>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, true);
                    typeof(GameLiving).GetField("<TempProperties>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, new PropertyCollection());
                    bool acquired = AutonomousSiegeJobs.TryAcquire(bot, keep, 64, attacking, true, out var kind, out var slot, 163);
                    Assert.That(acquired, Is.EqualTo(i < capacity));
                    if (!acquired) continue;
                    Assert.That(slot, Is.EqualTo(i));
                    Assert.That(AutonomousSiegeJobs.TryAcquire(bot, keep, 8, attacking, false, out var retained, out var retainedSlot, 163), Is.True);
                    Assert.That(retained, Is.EqualTo(kind));
                    Assert.That(retainedSlot, Is.EqualTo(slot));
                }
            }
            finally { foreach (var bot in bots) if (bot != null) AutonomousSiegeJobs.Release(bot); }
        }

        [TestCase(true, 180_000L)]
        [TestCase(false, 600_000L)]
        public void SupplyTime_IsBoundedAndPlayerResponsesAreFaster(bool playerResponse, long budget)
        {
            Assert.That(AutonomousWorldBotController.SiegeSupplyBudget(playerResponse), Is.EqualTo(budget));
        }

        [TestCase(BotSiegeKind.Ram, 400)]
        [TestCase(BotSiegeKind.Catapult, 3000)]
        [TestCase(BotSiegeKind.Trebuchet, 5000)]
        [TestCase(BotSiegeKind.Ballista, 4000)]
        public void FactoryRetainsNativeCombatRanges(BotSiegeKind kind,int maximum)
        {
            var weapon=AutonomousWorldBotController.CreateSiegeWeapon(kind);
            Assert.That(BotSiegeRuntime.Kind(weapon),Is.EqualTo(kind));
            Assert.That(weapon is GameSiegeRam ? weapon.attackComponent.AttackRange : weapon.MaxAttackRange,Is.EqualTo(maximum));
        }
        [TestCase(199,false)] [TestCase(200,true)] [TestCase(310,true)] [TestCase(375,true)] [TestCase(401,false)] [TestCase(500,false)]
        public void RamPlacementCannotExceedTheActualLightRamReach(int distance,bool valid)
        {
            var weapon=AutonomousWorldBotController.CreateSiegeWeapon(BotSiegeKind.Ram);
            var target=new Target { X=distance };
            Assert.That(AutonomousWorldBotController.InSiegeRange(weapon,target),Is.EqualTo(valid));
        }
        [TestCase(0,0)] [TestCase(7,0)] [TestCase(8,2)] [TestCase(23,2)] [TestCase(24,4)] [TestCase(63,4)] [TestCase(64,6)] [TestCase(300,6)]
        public void EquipmentJobsAreBoundedByArrivedTroops(int present,int slots) => Assert.That(AutonomousSiegeJobs.Slots(present),Is.EqualTo(slots));

        [TestCase(eCharacterClass.Armsman,true)] [TestCase(eCharacterClass.Warrior,true)] [TestCase(eCharacterClass.Hero,true)]
        [TestCase(eCharacterClass.Cleric,false)] [TestCase(eCharacterClass.Healer,false)] [TestCase(eCharacterClass.Druid,false)]
        [TestCase(eCharacterClass.Minstrel,false)] [TestCase(eCharacterClass.Bard,false)] [TestCase(eCharacterClass.Skald,false)]
        [TestCase(eCharacterClass.Sorcerer,false)] [TestCase(eCharacterClass.Necromancer,false)] [TestCase(eCharacterClass.Shaman,false)]
        public void SiegeAssignmentsPreserveHealersPerformersAndPrimaryPetJobs(eCharacterClass characterClass,bool eligible)
            => Assert.That(AutonomousSiegeJobs.CanOperate(characterClass),Is.EqualTo(eligible));

        [TestCase(eRealm.Albion,"deploy_siege_ballista")]
        [TestCase(eRealm.Midgard,"deploy_siege_ballista2")]
        [TestCase(eRealm.Hibernia,"deploy_siege_ballista3")]
        public void PurchasesUseActualRealmSpecificMerchantKitIds(eRealm realm,string id) => Assert.That(BotSiegeRuntime.Kit(realm,BotSiegeKind.Ballista),Is.EqualTo(id));

        [Test]
        public void NativeRepairRemainsFiniteAndCannotGiveInfiniteHealth()
        {
            var weapon=new GameSiegeRam { ObjectState=GameObject.eObjectState.Active };
            weapon.Health=3000;
            for(int n=0;n<4;n++) Assert.That(weapon.Repair(1500),Is.True);
            Assert.That(weapon.Health,Is.EqualTo(9000));
            Assert.That(weapon.Repair(1500),Is.False);
            Assert.That(weapon.Health,Is.EqualTo(9000));
        }
        [Test]
        public void RepairNeverChargesForDeadFullOrExhaustedEquipment()
        {
            var weapon=new GameSiegeRam { ObjectState=GameObject.eObjectState.Active };
            int charges=0;
            var paid=typeof(GameSiegeWeapon).GetMethod("Repair",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(int),typeof(Func<bool>)},null);
            bool Attempt() => (bool)paid.Invoke(weapon,new object[]{1500,(Func<bool>)(()=>{charges++;return true;})});
            weapon.Health=weapon.MaxHealth;
            Assert.That(Attempt(),Is.False);
            weapon.Health=0;
            Assert.That(Attempt(),Is.False);
            Assert.That(weapon.Health,Is.Zero);
            Assert.That(charges,Is.Zero);
            weapon.Health=3000;
            for (int n=0;n<4;n++) Assert.That(Attempt(),Is.True);
            Assert.That(Attempt(),Is.False);
            Assert.That(charges,Is.EqualTo(4));
        }
        [Test]
        public void FailedKitPaymentCannotRestoreHealthOrConsumeRepairAllowance()
        {
            var weapon=new GameSiegeRam { Health=3000,ObjectState=GameObject.eObjectState.Active };
            var paid=typeof(GameSiegeWeapon).GetMethod("Repair",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(int),typeof(Func<bool>)},null);
            Assert.That(paid.Invoke(weapon,new object[]{1500,(Func<bool>)(()=>false)}),Is.False);
            Assert.That(weapon.Health,Is.EqualTo(3000));
            Assert.That(weapon.TimesRepaired,Is.Zero);
        }
        [TestCase(0,250000,5)] [TestCase(3,250000,2)] [TestCase(5,250000,0)]
        [TestCase(99,250000,0)] [TestCase(0,100000,2)] [TestCase(0,49999,0)]
        public void RepairRestockIsOnlyTheMissingAffordableAmount(int held,long money,int expected)
            => Assert.That(BotSiegeRuntime.RepairRestockAmount(held,money,50000),Is.EqualTo(expected));

        [Test]
        public void RepairStacksFitNativePacketAndCanBeUsedAllTheWayToEmpty()
        {
            var inventory=new BotInventory();
            var item=GameInventoryItem.Create(new DbItemTemplate { Id_nb=BotSiegeRuntime.RepairKit,MaxCount=BotSiegeRuntime.RepairStackLimit,Name="field siege repair kit" });
            item.Count=1;
            Assert.That(inventory.AddItem(eInventorySlot.FirstBackpack,item),Is.True);
            Assert.That(inventory.AddCountToStack(item,254),Is.True);
            Assert.That(item.Count,Is.EqualTo(255));
            Assert.That((byte)item.Count,Is.EqualTo(item.Count));
            Assert.That(inventory.AddCountToStack(item,1),Is.False);
            for (int n=0;n<255;n++) Assert.That(inventory.RemoveCountFromStack(item,1),Is.True);
            Assert.That(inventory.AllItems,Is.Empty);
        }
        [TestCase("offline_siege_field_repair_kit",true)]
        [TestCase("deploy_siege_ram",true)] [TestCase("deploy_siege_ballista2",true)]
        [TestCase("deploy_siege_trebuchet3",true)] [TestCase("ordinary_sword",false)] [TestCase(null,false)]
        public void OnlyExactSiegeSuppliesAreReservedFromAutomaticSales(string id,bool reserved)
            => Assert.That(BotSiegeRuntime.IsSupply(id),Is.EqualTo(reserved));
        [Test]
        public void EmptyDeadOrMissingEquipmentCannotBeRepairedOrFired()
        {
            Assert.That(BotSiegeRuntime.CanRepair(null,null),Is.False);
            Assert.That(BotSiegeRuntime.CanDamage(null,null),Is.False);
            var weapon=new GameSiegeRam();
            Assert.That(BotSiegeRuntime.CanDamage(weapon,new Target()),Is.False);
            Assert.DoesNotThrow(()=>weapon.Aim());
            Assert.DoesNotThrow(()=>weapon.ReleaseControl());
        }
        [Test]
        public void NativeMovementCapabilityIsPreservedNotGrantedToStationaryRamsOrTrebuchets()
        {
            Assert.That(AutonomousWorldBotController.CreateSiegeWeapon(BotSiegeKind.Ram).EnableToMove,Is.False);
            Assert.That(AutonomousWorldBotController.CreateSiegeWeapon(BotSiegeKind.Trebuchet).EnableToMove,Is.False);
            Assert.That(AutonomousWorldBotController.CreateSiegeWeapon(BotSiegeKind.Catapult).EnableToMove,Is.True);
            Assert.That(AutonomousWorldBotController.CreateSiegeWeapon(BotSiegeKind.Ballista).EnableToMove,Is.True);
        }
        [Test]
        public void PveMonstersAreNotIncludedInPvpCrowdControlReservations()
        {
            Assert.That(BotPvpCrowdControl.PlayerLike(new Target()),Is.False);
            Assert.That(BotPvpCrowdControl.Protected(new Target(),new Target()),Is.False);
        }
        [TestCase(BotSiegeKind.Ram)] [TestCase(BotSiegeKind.Ballista)]
        [TestCase(BotSiegeKind.Catapult)] [TestCase(BotSiegeKind.Trebuchet)]
        public void EveryEngineTracksOwnershipTransferAndFiltersRemovedObjects(BotSiegeKind kind)
        {
            var first=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            var second=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            var engine=AutonomousWorldBotController.CreateSiegeWeapon(kind);
            engine.ObjectState=GameObject.eObjectState.Active;
            engine.Owner=first;
            Assert.That(AutonomousSiegeOwnership.All(first),Does.Contain(engine));
            engine.Owner=second;
            Assert.That(AutonomousSiegeOwnership.All(first),Is.Empty);
            Assert.That(AutonomousSiegeOwnership.All(second),Does.Contain(engine));
            engine.ObjectState=GameObject.eObjectState.Inactive;
            Assert.That(AutonomousSiegeOwnership.All(second),Is.Empty);
            engine.Owner=null;
        }
        [Test]
        public void CrowdControlClaimPreventsDuplicateCastersAndReleasesAfterFailedCast()
        {
            var first=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            var second=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            var enemy=(Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            first.Realm=second.Realm=eRealm.Albion; enemy.Realm=eRealm.Midgard;
            Assert.That(BotPvpCrowdControl.Reserve(first,enemy,3000),Is.True);
            Assert.That(BotPvpCrowdControl.Reserve(second,enemy,3000),Is.False);
            BotPvpCrowdControl.Release(first,enemy);
            Assert.That(BotPvpCrowdControl.Reserve(second,enemy,3000),Is.True);
            BotPvpCrowdControl.Release(second,enemy);
        }
    }
}
