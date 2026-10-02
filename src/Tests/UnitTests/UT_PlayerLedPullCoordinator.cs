using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_PlayerLedPullCoordinator
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previous;
        private readonly List<GameLiving> _actors = new();
        private sealed class Rules : NormalServerRules
        {
            public override bool IsAllowedToAttack(GameLiving attacker, GameLiving defender, bool quiet) =>
                attacker.Realm != defender.Realm && !BotPvpCrowdControl.Protected(attacker, defender);
        }
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
            protected override IServerRules ServerRulesImpl => new Rules();
        }
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public bool Attacking;
            public override byte Level { get => 50; set { } }
            public override bool IsAlive => true;
            public override bool IsAttacking => Attacking;
            public override bool IsCasting => false;
            public override bool IsMoving => false;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override GameObject TargetObject { get; set; }
            public override IControlledBrain ControlledBrain { get; set; }
            public override IPacketLib Out => new BotDummyPacketLib();
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass TestClass;
            public int TestX;
            public byte TestLevel = 1;
            public bool Alive = true;
            public eRealm TestRealm = eRealm.Albion;
            public int Stops;
            public int MeleeStarts;
            public int FollowRange;
            public bool Casting;
            public bool TestWeaponAllowed;
            public override bool HasAbilityToUseItem(DbItemTemplate item) => TestWeaponAllowed || base.HasAbilityToUseItem(item);
            public override bool IsAlive => Alive;
            public override bool IsAttacking => false;
            public override bool IsCasting => Casting;
            public override bool IsMoving => false;
            public override bool IsCrowdControlled => false;
            public override bool InCombat => false;
            public override short MaxSpeed => 200;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => TestRealm; set => TestRealm = value; }
            public override byte Level { get => TestLevel; set => TestLevel = value; }
            public override int EffectiveLevel => TestLevel;
            public override int MeleeAttackRange => 200;
            public override ICharacterClass CharacterClass => TestClass;
            public override IControlledBrain ControlledBrain { get; set; }
            public override void WalkTo(Vector3 target, short speed) { }
            public override void StopAttack() { Stops++; }
            public override void StartAttack(GameObject target) { MeleeStarts++; }
            public override void Follow(GameObject target, int minDistance, int maxDistance) { FollowRange = minDistance; }
            public override int Mana { get; set; }
            public override byte EndurancePercent => 0;
            public override int GetModified(eProperty property) => property == eProperty.SpellRange ? 100 : 0;
        }
        private sealed class Enemy : GameNPC
        {
            public int TestX;
            public byte TestLevel = 1;
            public override bool IsAlive => true;
            public override bool IsCasting => false;
            public override bool IsAttacking => false;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Midgard; set { } }
            public override GameObject TargetObject { get; set; }
            public override byte Level { get => TestLevel; set => TestLevel = value; }
            public override int EffectiveLevel => TestLevel;
        }
        private sealed class Brain : BotBrain
        {
            public bool RealPull;
            public override void AttackMostWanted() { if (!RealPull) base.AttackMostWanted(); }
            public bool? SpellResult;
            public override bool CheckSpells(eCheckSpellType type) => SpellResult ?? base.CheckSpells(type);
            public int Pulls;
            public int Cancellations;
            public GameLiving LastTarget;
            public override bool OrderPull(GameLiving target) { Pulls++; LastTarget = target; return !RealPull || base.OrderPull(target); }
            public override void CancelOrderedPull(GameLiving target) { Cancellations++; }
        }
        private sealed class PetBrain : ControlledMobBrain
        {
            public int Attacks;
            public int Stops;
            public PetBrain(GameLiving owner) : base(owner) { }
            public override void Attack(GameObject target) { Attacks++; OrderedAttackTarget = target as GameLiving; }
            public override void Disengage() { Stops++; OrderedAttackTarget = null; }
            public override void Follow(GameObject target) { }
        }
        [SetUp] public void Setup()
        {
            _previous = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void Cleanup()
        {
            foreach (GameLiving actor in _actors) ServiceObjectStore.Remove(actor.effectListComponent);
            _actors.Clear();
            GameServer.LoadTestDouble(_previous);
        }

        [TestCase(eCharacterClass.Sorcerer)]
        [TestCase(eCharacterClass.Mentalist)]
        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Enchanter)]
        [TestCase(eCharacterClass.Bonedancer)]
        [TestCase(eCharacterClass.Spiritmaster)]
        [TestCase(eCharacterClass.Necromancer)]
        [TestCase(eCharacterClass.Hunter)]
        [TestCase(eCharacterClass.Friar)]
        [TestCase(eCharacterClass.Bard)]
        [TestCase(eCharacterClass.Minstrel)]
        [TestCase(eCharacterClass.Skald)]
        public void DamageClassesAreNotSuppressedForKnowingBuffs(eCharacterClass characterClass) =>
            Assert.That(BotPartyRoles.For(characterClass), Is.EqualTo(BotPartyRole.Damage));

        [TestCase(eCharacterClass.Cleric)]
        [TestCase(eCharacterClass.Druid)]
        [TestCase(eCharacterClass.Healer)]
        public void GroupSupportRosterIsExplicit(eCharacterClass characterClass) =>
            Assert.That(BotPartyRoles.For(characterClass), Is.EqualTo(BotPartyRole.Support));

        [Test] public void SupportOnlyRestrictionDoesNotApplyToSoloBots()
        {
            Bot bot = MakeBot(new ClassDruid());
            Assert.That(BotPartyRoles.IsSupport(bot), Is.False);
        }

        [TestCase(1, 5, true)]
        [TestCase(10, 30, true)]
        [TestCase(20, 28, true)]
        [TestCase(50, 65, true)]
        [TestCase(20, 1, false)]
        public void SharedPullGateUsesBotsViewOfMonster_NotMonstersViewOfBot(byte level, byte targetLevel, bool allowed)
        {
            Bot bot = MakeBot(new ClassPaladin());
            bot.Level = level;
            Enemy target = Actor<Enemy>();
            target.Level = targetLevel;
            Assert.That(B(bot).CanAggroTarget(target), Is.EqualTo(allowed));
        }

        [Test] public void HigherTargetStillMustPassNativeHostilityRules()
        {
            Bot bot = MakeBot(new ClassPaladin());
            bot.Level = 10;
            Bot ally = MakeBot(new ClassPaladin());
            ally.Level = 30;
            Assert.That(B(bot).CanAggroTarget(ally), Is.False);
        }

        [Test] public void OldHitDoesNotForceMeleeOrCancelCastWhenInterruptHasExpired()
        {
            Bot caster = MakeBot(new ClassCabalist());
            Enemy target = Actor<Enemy>();
            Field(typeof(GameLiving), caster, "<LastInterrupter>k__BackingField", target);
            Field(typeof(GameLiving), caster, "<InterruptTime>k__BackingField", GameLoop.GameLoopTime - 1);
            var method = typeof(BotBrain).GetMethod("TryEngageUnderMeleePressure", Hidden);
            Assert.That(method.Invoke(B(caster), new object[] { target }), Is.False);
        }

        [TestCase(typeof(ClassRunemaster))] [TestCase(typeof(ClassWizard))]
        [TestCase(typeof(ClassSpiritmaster))] [TestCase(typeof(ClassBonedancer))]
        [TestCase(typeof(ClassCabalist))] [TestCase(typeof(ClassEnchanter))]
        [TestCase(typeof(ClassSorcerer))] [TestCase(typeof(ClassTheurgist))]
        [TestCase(typeof(ClassNecromancer))] [TestCase(typeof(ClassAnimist))]
        [TestCase(typeof(ClassEldritch))] [TestCase(typeof(ClassMentalist))]
        public void FailedCastNeverStartsStaffChargeForEitherBotKind(Type classType)
        {
            foreach (bool temporary in new[] { false, true })
            {
                Bot caster = MakeBot((ICharacterClass)Activator.CreateInstance(classType));
                Enemy target = Actor<Enemy>(); target.TestX = 1000;
                caster.Spells = new(); caster.Mana = 0;
                caster.attackComponent = new AttackComponent(caster);
                Field(typeof(GameBot), caster, "<IsAutonomousWorldBot>k__BackingField", true);
                Field(typeof(GameBot), caster, "<IsTemporaryGroupHelper>k__BackingField", temporary);
                B(caster).SpellResult = false;
                B(caster).AddToAggroList(target, 10);
                Assert.That(BotBrain.PrefersSpellRange(caster.CharacterClass), Is.True);
                for (int tick = 0; tick < 4; tick++) B(caster).AttackMostWanted();
                Assert.That(caster.MeleeStarts, Is.Zero, $"{classType.Name}, temporary={temporary}");
                Assert.That(caster.FollowRange, Is.Zero, "No staff-distance pursuit when power/spells are unavailable");
            }
        }

        [TestCase(1)]
        [TestCase(10)]
        [TestCase(50)]
        public void AutonomousMinstrelWithoutAvailableSpellsStartsMelee(int level)
        {
            Bot bot = MakeBot(new ClassMinstrel());
            bot.Level = (byte)level;
            bot.TestWeaponAllowed = true;
            Field(typeof(GameBot), bot, "m_specialization", new Dictionary<string, Specialization>());
            bot.Inventory = new BotInventory();
            var sword = GameInventoryItem.Create(new DbItemTemplate { Name = "training sword",
                Object_Type = (int)eObjectType.SlashingWeapon, Item_Type = Slot.RIGHTHAND,
                DPS_AF = 12, SPD_ABS = 30, Quality = 100, Condition = 50000, MaxCondition = 50000 });
            bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, sword);
            Field(typeof(GameLiving), bot, "_activeWeapon", sword);
            Field(typeof(GameLiving), bot, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), bot, "_abilitiesLock", new System.Threading.Lock());
            Field(typeof(GameBot), bot, "<IsAutonomousWorldBot>k__BackingField", true);
            bot.attackComponent = new AttackComponent(bot);
            bot.styleComponent = StyleComponent.Create(bot);
            bot.Spells = new();
            bot.Mana = 0;
            Enemy target = Actor<Enemy>(); target.TestX = 1000;
            B(bot).AddToAggroList(target, 10);
            B(bot).AttackMostWanted();
            Assert.That(bot.MeleeStarts, Is.EqualTo(1), "No incoming attack is needed to initiate melee.");
        }

        [Test] public void ArchersAndMeleeHybridsKeepTheirCombatPolicy()
        {
            Assert.That(BotBrain.PrefersSpellRange(new ClassHunter()), Is.False);
            Assert.That(BotBrain.PrefersSpellRange(new ClassThane()), Is.False);
            Assert.That(BotBrain.PrefersSpellRange(new ClassValewalker()), Is.False);
            Assert.That(BotBrain.PrefersSpellRange(new ClassPaladin()), Is.False);
        }

        [Test] public void ActualCloseIncomingAttackAllowsCasterMeleeFallback()
        {
            Bot caster = MakeBot(new ClassBonedancer());
            Enemy target = Actor<Enemy>(); target.TestX = 100;
            caster.attackComponent = new AttackComponent(caster);
            caster.styleComponent = StyleComponent.Create(caster);
            Field(typeof(GameLiving), caster, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), caster, "_abilitiesLock", new System.Threading.Lock());
            Field(typeof(GameBot), caster, "<IsAutonomousWorldBot>k__BackingField", true);
            Field(typeof(GameLiving), caster, "<InterruptTime>k__BackingField", GameLoop.GameLoopTime + 1000);
            B(caster).SpellResult = false;
            B(caster).AddToAggroList(target, 10);
            B(caster).AttackMostWanted();
            Assert.That(caster.MeleeStarts, Is.EqualTo(1));
        }

        [Test] public void BoltOnlyCasterApproachesSpellRangeNotStaffRange()
        {
            Bot caster = MakeBot(new ClassRunemaster());
            Enemy target = Actor<Enemy>(); target.TestX = 2500;
            caster.attackComponent = new AttackComponent(caster);
            caster.castingComponent = (NpcCastingComponent)CastingComponent.Create(caster);
            ((GameLiving)caster).castingComponent = caster.castingComponent;
            caster.Spells = new() { new Spell(new DbSpell { Type = "Bolt", Target = "Enemy", CastTime = 2.5, Range = 1800, Damage = 20 }, 1) };
            caster.Mana = 100;
            Field(typeof(GameBot), caster, "<IsAutonomousWorldBot>k__BackingField", true);
            B(caster).SpellResult = false;
            B(caster).AddToAggroList(target, 10);
            B(caster).AttackMostWanted();
            Assert.That(caster.MeleeStarts, Is.Zero);
            Assert.That(caster.FollowRange, Is.EqualTo(1700));
        }

        private Bot OutOfRangeBoltCaster(out Enemy target)
        {
            Bot caster = MakeBot(new ClassRunemaster());
            target = Actor<Enemy>(); target.TestX = 2500;
            caster.attackComponent = new AttackComponent(caster);
            caster.castingComponent = (NpcCastingComponent)CastingComponent.Create(caster);
            ((GameLiving)caster).castingComponent = caster.castingComponent;
            caster.Spells = new() { new Spell(new DbSpell { Type = "Bolt", Target = "Enemy", CastTime = 2.5, Range = 1800, Damage = 20 }, 1) };
            caster.Mana = 100;
            Field(typeof(GameBot), caster, "<IsAutonomousWorldBot>k__BackingField", true);
            B(caster).SpellResult = false;
            B(caster).AddToAggroList(target, 10);
            return caster;
        }

        [Test] public void RestingCasterWakesToWalkIntoSpellRange()
        {
            Bot caster = OutOfRangeBoltCaster(out _);
            Field(typeof(GameBot), caster, "_recoveryRestLocked", true);
            Assert.That(caster.IsRecoveryResting, Is.True);
            B(caster).AttackMostWanted();
            Assert.That(caster.IsRecoveryResting, Is.False,
                "a resting bot ignores follow orders, so the approach must wake it");
            Assert.That(caster.FollowRange, Is.EqualTo(1700));
        }

        [Test] public void UnconnectedPullMovesTheCasterWellInsideSpellRange()
        {
            Bot caster = OutOfRangeBoltCaster(out _);
            B(caster).RequestCloserSpellApproach(GameLoop.GameLoopTime + 30_000);
            B(caster).AttackMostWanted();
            Assert.That(caster.FollowRange, Is.EqualTo(1800 * 6 / 10 - 100));
        }

        [TestCase(true)] [TestCase(false)]
        public void AcceptedCastKeepsItsTargetAndDoesNotReenterMeleeDecisions(bool temporary)
        {
            Bot caster = MakeBot(new ClassWizard());
            Enemy target = Actor<Enemy>();
            caster.Casting = true;
            caster.TargetObject = target;
            Field(typeof(GameBot), caster, "<IsAutonomousWorldBot>k__BackingField", true);
            Field(typeof(GameBot), caster, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Field(typeof(GameLiving), caster, "<InterruptTime>k__BackingField", GameLoop.GameLoopTime + 1000);
            Assert.That(B(caster).IsActive, Is.True);
            B(caster).AttackMostWanted();
            Assert.That(caster.TargetObject, Is.SameAs(target));
            Assert.That(caster.Stops, Is.Zero);
        }

        private sealed class ScanningShroom : TurretFNFBrain
        {
            public int PlayerScans, NpcScans;
            public ScanningShroom(GameLiving owner) : base(owner) { }
            protected override void CheckPlayerAggro() { PlayerScans++; }
            protected override void CheckNpcAggro() { NpcScans++; }
        }

        [Test] public void AnimistFnfUsesNativeScansWithoutAnOwnerAttackOrder()
        {
            Bot animist = MakeBot(new ClassAnimist());
            Enemy target = Actor<Enemy>();
            animist.TargetObject = target;
            var shroom = new ScanningShroom(animist) { Body = Actor<Enemy>() };
            shroom.CheckProximityAggro();
            Assert.That(shroom.PlayerScans, Is.EqualTo(1));
            Assert.That(shroom.NpcScans, Is.EqualTo(1));
            Assert.That(shroom.OrderedAttackTarget, Is.Null);
            Assert.That(shroom.HasAggro, Is.False, "The wrapper must not copy the owner's target.");
        }

        [TestCase(eCharacterClass.Paladin)]
        public void HybridTanksRetainHealingAndAreNotExclusiveSupport(eCharacterClass cls)
        {
            Assert.That(BotPartyRoles.For(cls), Is.EqualTo(BotPartyRole.Tank));
            Assert.That(BotPartyRoles.IsHealingClass(cls), Is.True);
            Assert.That(BotPartyRoles.Label(cls), Is.EqualTo("Tank/Healer/Buffer"));
        }


        [Test]
        public void WardenIsAttackerHealerBufferAndNotTank()
        {
            Assert.That(BotPartyRoles.For(eCharacterClass.Warden), Is.EqualTo(BotPartyRole.Damage));
            Assert.That(BotPartyRoles.IsHealingClass(eCharacterClass.Warden), Is.True);
            Assert.That(BotPartyRoles.IsHybridSupport(eCharacterClass.Warden), Is.True);
            Assert.That(BotPartyRoles.Label(eCharacterClass.Warden), Is.EqualTo("Attacker/Healer/Buffer"));
        }

        [Test]
        public void ShamanIsAttackerHealerBufferAndNotTank()
        {
            Assert.That(BotPartyRoles.For(eCharacterClass.Shaman), Is.EqualTo(BotPartyRole.Damage));
            Assert.That(BotPartyRoles.IsTank(MakeBot(new ClassShaman())), Is.False);
            Assert.That(BotPartyRoles.IsHybridSupport(eCharacterClass.Shaman), Is.True);
            Assert.That(BotPartyRoles.IsHealingClass(eCharacterClass.Shaman), Is.True);
            Assert.That(BotPartyRoles.Label(eCharacterClass.Shaman), Is.EqualTo("Attacker/Healer/Buffer"));
        }

        [TestCase(false)] [TestCase(true)]
        public void FriarRetainsHealingWithoutExclusiveSupportRestriction(bool temporary)
        {
            Bot friar = MakeBot(new ClassFriar());
            Field(typeof(GameBot), friar, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Bot ally = MakeBot(new ClassPaladin());
            var group = new Group(ally);
            Add(group, ally);
            Add(group, friar);
            Assert.That(BotPartyRoles.IsSupport(friar), Is.False);
            Assert.That(BotPartyRoles.IsTank(friar), Is.False);
            Assert.That(BotPartyRoles.IsHybridSupport(eCharacterClass.Friar), Is.True);
            Assert.That(BotPartyRoles.IsHealingClass(eCharacterClass.Friar), Is.True);
            Assert.That(BotPartyRoles.Label(eCharacterClass.Friar), Is.EqualTo("Attacker/Healer/Buffer"));
            Assert.That(BotBrain.PrefersSpellRange(friar.CharacterClass), Is.False);
        }

        [Test] public void FriarJoinsAttackAfterTankContactInsteadOfRemainingSupportOnly()
        {
            var (player, tank, damage, healer, enemy) = Party();
            damage.TestClass = new ClassFriar();
            PlayerLedPullCoordinator.Begin(player, enemy);
            Assert.That(B(damage).Pulls, Is.Zero);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
            PlayerLedPullCoordinator.OnAttack(tank, Hit(tank, enemy));
            Assert.That(B(damage).Pulls, Is.EqualTo(1));
            Assert.That(B(healer).Pulls, Is.Zero);
        }

        [Test] public void PlantingDoesNotStarveAnimistsOwnSpellTurn()
        {
            Bot animist = MakeBot(new ClassAnimist());
            Spell summon = new(new DbSpell { SpellID = 11350, Type = eSpellType.SummonAnimistFnF.ToString(), Target = eSpellTarget.SELF.ToString(), CastTime = 5 }, 5);
            Spell damage = new(new DbSpell { SpellID = 11317, Type = eSpellType.Bomber.ToString(), Target = eSpellTarget.ENEMY.ToString() }, 1);
            AnimistSingleTargetPolicy.PlantedTurret(animist, summon);
            Assert.That(AnimistSingleTargetPolicy.OwnerSpellPending(animist, true), Is.True);
            AnimistSingleTargetPolicy.CastOwnerSpell(animist, damage);
            Assert.That(AnimistSingleTargetPolicy.OwnerSpellPending(animist, true), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void HunterSendsPetOnceWithoutInterruptingOwnersRangedAction(bool temporary)
        {
            Bot hunter = MakeBot(new ClassHunter());
            Field(typeof(GameBot), hunter, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Enemy target = Actor<Enemy>(); target.TestX = 1000;
            PetBrain pet = AttachPet(hunter);
            hunter.TargetObject = target;
            hunter.Casting = true;
            for (int tick = 0; tick < 5; tick++)
                AutonomousPetSupport.SynchronizeIndependentPet(hunter, pet, target);
            Assert.That(pet.Attacks, Is.EqualTo(1), "Do not restart pet chasing on every AI tick.");
            Assert.That(pet.OrderedAttackTarget, Is.SameAs(target));
            Assert.That(pet.AggroLevel, Is.Zero, "No unrestricted pet camp-pulling.");
            Assert.That(hunter.TargetObject, Is.SameAs(target));
            Assert.That(hunter.Casting, Is.True);
            Assert.That(hunter.Stops, Is.Zero);
            Assert.That(hunter.MeleeStarts, Is.Zero);
        }

        [Test] public void TankAloneGoesFirst_ContactReleasesDamageAndHumanPetButNotHealer()
        {
            var (player, tank, damage, healer, enemy) = Party();
            PetBrain pet = AttachPet(player);
            Assert.That(PlayerLedPullCoordinator.Begin(player, enemy), Does.Contain("leads"));
            Assert.That(B(tank).Pulls, Is.EqualTo(1));
            Assert.That(B(damage).Pulls, Is.Zero);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
            Assert.That(pet.Attacks, Is.Zero);
            PlayerLedPullCoordinator.OnAttack(tank, Hit(tank, enemy));
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            Assert.That(B(damage).Pulls, Is.EqualTo(1));
            Assert.That(B(healer).Pulls, Is.Zero);
            Assert.That(pet.Attacks, Is.EqualTo(1));
        }

        [Test] public void NoTank_DamageEngagesImmediately()
        {
            var (player, tank, damage, healer, enemy) = Party();
            Remove(tank);
            PlayerLedPullCoordinator.Begin(player, enemy);
            Assert.That(B(damage).Pulls, Is.EqualTo(1));
            Assert.That(B(healer).Pulls, Is.Zero);
        }

        [Test] public void SoloPullStillOrdersThePlayersPet()
        {
            Player player = Actor<Player>();
            Enemy enemy = Actor<Enemy>();
            PetBrain pet = AttachPet(player);
            Assert.That(PlayerLedPullCoordinator.Begin(player, enemy), Is.EqualTo("Pet pull ordered."));
            Assert.That(pet.Attacks, Is.EqualTo(1));
        }

        [Test] public void HumanAttackOverridesTankWaitingWithoutSendingSupport()
        {
            var (player, _, damage, healer, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.LeaderEngaged(player, enemy);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            Assert.That(B(damage).Pulls, Is.EqualTo(1));
            Assert.That(B(healer).Pulls, Is.Zero);
        }

        [Test] public void UnrelatedHumanCannotBreakAnotherLeadersTankGate()
        {
            var (player, _, damage, _, enemy) = Party();
            Player other = Actor<Player>();
            Add(player.Group, other);
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.LeaderEngaged(other, enemy);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
            Assert.That(B(damage).Pulls, Is.Zero);
        }

        [Test] public void OutOfRangeSwingIsNotTankContact()
        {
            var (player, tank, damage, _, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            AttackData attack = Hit(tank, enemy);
            attack.AttackResult = eAttackResult.OutOfRange;
            PlayerLedPullCoordinator.OnAttack(tank, attack);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
        }

        [Test] public void LostTankCancelsWithoutSendingDamageIn()
        {
            var (player, tank, damage, _, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            tank.Alive = false;
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            Assert.That(B(damage).Pulls, Is.Zero);
            Assert.That(B(tank).Cancellations, Is.EqualTo(1));
        }

        [Test] public void ContactTimeoutCancelsOnceInsteadOfReleasingDamage()
        {
            var (player, tank, damage, _, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            object states = typeof(PlayerLedPullCoordinator).GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object[] args = { player.Group, null };
            Assert.That(states.GetType().GetMethod("TryGetValue").Invoke(states, args), Is.True);
            object order = args[1].GetType().GetField("Pending").GetValue(args[1]);
            order.GetType().GetField("Deadline").SetValue(order, -1L);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            Assert.That(B(damage).Pulls, Is.Zero);
            Assert.That(B(tank).Cancellations, Is.EqualTo(1));
        }

        [Test] public void RemovedCompanionCannotReceiveDelayedContactOrder()
        {
            var (player, tank, damage, _, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            Remove(damage);
            PlayerLedPullCoordinator.OnAttack(tank, Hit(tank, enemy));
            Assert.That(B(damage).Pulls, Is.Zero);
        }

        [Test] public void CompanionPetCannotMasqueradeAsHumanOverride()
        {
            var (player, _, damage, _, enemy) = Party();
            PetBrain pet = AttachPet(damage);
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.OnAttack(pet.Body, Hit(pet.Body, enemy));
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
            Assert.That(B(damage).Pulls, Is.Zero);
        }

        [Test] public void ReleasedPetCannotOverrideTankWaiting()
        {
            var (player, _, damage, _, enemy) = Party();
            PetBrain oldPet = AttachPet(player);
            AttachPet(player);
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.OnAttack(oldPet.Body, Hit(oldPet.Body, enemy));
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
            Assert.That(B(damage).Pulls, Is.Zero);
        }

        [Test] public void OldTankCannotReleaseANewGroupAfterLeaderLeaves()
        {
            var (player, tank, damage, _, enemy) = Party();
            PlayerLedPullCoordinator.Begin(player, enemy);
            Remove(player);
            var nextGroup = new Group(player);
            Add(nextGroup, player);
            Bot nextDamage = MakeBot(new ClassWizard());
            Add(nextGroup, nextDamage);
            nextDamage.EnterPlayerLedGroup(player);
            PlayerLedPullCoordinator.OnAttack(tank, Hit(tank, enemy));
            Assert.That(B(damage).Pulls, Is.Zero);
            Assert.That(B(nextDamage).Pulls, Is.Zero);
        }

        [Test] public void SupportAttackIsStoppedImmediatelyWhenHumanEngages()
        {
            var (player, _, _, healer, enemy) = Party();
            int before = healer.Stops;
            PlayerLedPullCoordinator.LeaderEngaged(player, enemy);
            Assert.That(healer.Stops, Is.GreaterThan(before));
            Assert.That(B(healer).Pulls, Is.Zero);
        }

        [Test] public void ManualMainPetOrderIsObservedWithoutOwnerCasting()
        {
            var (player, _, _, _, enemy) = Party();
            PetBrain pet = AttachPet(player);
            pet.OrderedAttackTarget = enemy;
            Assert.That(PlayerLedPullCoordinator.FindLeaderTarget(player), Is.SameAs(enemy));
        }

        [Test] public void DistantDamageMemberDoesNotChaseAcrossTheMap()
        {
            var (player, tank, damage, _, enemy) = Party();
            damage.TestX = -1500; enemy.TestX = 1500;
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.OnAttack(tank, Hit(tank, enemy));
            Assert.That(B(damage).Pulls, Is.Zero);
        }

        [Test] public void ExistingCompanionPetOrderIsStoppedWhileWaiting()
        {
            var (player, _, damage, _, enemy) = Party();
            PetBrain pet = AttachPet(damage);
            pet.OrderedAttackTarget = enemy;
            PlayerLedPullCoordinator.Begin(player, enemy);
            Assert.That(pet.OrderedAttackTarget, Is.Null);
            Assert.That(pet.Stops, Is.GreaterThan(0));
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.True);
        }

        [Test] public void HumanZombieAttackWakesAttackersAndNotSupport()
        {
            var (player, _, damage, healer, enemy) = Party();
            PetBrain pet = AttachPet(player);
            PlayerLedPullCoordinator.Begin(player, enemy);
            PlayerLedPullCoordinator.OnAttack(pet.Body, Hit(pet.Body, enemy));
            Assert.That(B(damage).Pulls, Is.EqualTo(1));
            Assert.That(B(healer).Pulls, Is.Zero);
        }

        [Test] public void PetVictimResolvesToActualOwnerNotHumanOwnerOfCompanion()
        {
            var (_, _, damage, _, _) = Party();
            PetBrain pet = AttachPet(damage);
            Assert.That(BotBrain.GroupMemberForCombat(pet.Body), Is.SameAs(damage));
        }

        [Test] public void PetHitWakesExactGroupOnlyAndRespectsActualPetDistance()
        {
            var (player, _, damage, _, enemy) = Party();
            PetBrain pet = AttachPet(player);
            damage.BeginRecoveryRest();
            ((Enemy)pet.Body).TestX = 2100;
            BotBrain.NotifyNearbyGroupBots(pet.Body, Hit(enemy, pet.Body));
            Assert.That(B(damage).HasAggro, Is.False);
            ((Enemy)pet.Body).TestX = 100;
            BotBrain.NotifyNearbyGroupBots(pet.Body, Hit(enemy, pet.Body));
            Assert.That(B(damage).HasAggro, Is.True);
            Assert.That(damage.IsRecoveryResting, Is.False);
            Assert.That(damage.IsSitting, Is.False);
        }

        private (Player player, Bot tank, Bot damage, Bot healer, Bot enemy) PvpParty(bool defensive)
        {
            var (player, tank, damage, healer, _) = Party();
            foreach (Bot bot in new[] { tank, damage, healer })
            {
                Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", true);
                Field(typeof(GameBot), bot, "<Owner>k__BackingField", player);
                B(bot).RealPull = true;
            }
            CompanionEngagementMode.Set(player, defensive);
            Bot enemy = MakeBot(new ClassWarrior());
            enemy.Realm = eRealm.Midgard;
            enemy.TestX = 100;
            Field(typeof(GameBot), enemy, "<IsAutonomousWorldBot>k__BackingField", true);
            return (player, tank, damage, healer, enemy);
        }

        [TestCase(40)] [TestCase(80)]
        public void PvpPullWakesEntireRaidWithoutRestOrTankContactGate(int size)
        {
            var (player, tank, damage, healer, enemy) = PvpParty(false);
            Assert.That(player.Group.EnableCompanionRaid(player, size), Is.True);
            while (Members(player.Group).Count < size)
            {
                Bot companion = MakeBot(new ClassMercenary());
                Add(player.Group, companion);
                companion.EnterPlayerLedGroup(player);
                Field(typeof(GameBot), companion, "<IsTemporaryGroupHelper>k__BackingField", true);
                Field(typeof(GameBot), companion, "<Owner>k__BackingField", player);
                B(companion).RealPull = true;
            }
            foreach (GameLiving member in Members(player.Group))
                if (member is Bot bot) bot.BeginRecoveryRest();
            Assert.That(PlayerLedPullCoordinator.Begin(player, enemy), Does.Contain("PvP"));
            foreach (GameLiving member in Members(player.Group))
                if (member is Bot bot)
                {
                    Assert.That(bot.IsTemporaryCompanionRestLocked, Is.False, bot.Name);
                    Assert.That(PlayerLedPullCoordinator.IsWaiting(bot), Is.False);
                    if (bot != healer) Assert.That(B(bot).HasAggro, Is.True, bot.Name);
                }
            Assert.That(B(healer).Pulls, Is.Zero, "Healers keep their support role.");
        }

        [TestCase(false)] [TestCase(true)]
        public void ExplicitPvpPullBreaksCompanionMezReservationOnly(bool petTarget)
        {
            var (player, tank, damage, _, enemy) = PvpParty(false);
            GameLiving target = petTarget ? AttachPet(enemy).Body : enemy;
            Assert.That(BotPvpCrowdControl.Reserve(tank, target, 3000), Is.True);
            Assert.That(BotPvpCrowdControl.Protected(damage, target), Is.True);
            PlayerLedPullCoordinator.Begin(player, target);
            Assert.That(BotPvpCrowdControl.Protected(damage, target), Is.False);
            Assert.That(B(damage).HasAggro, Is.True);
            Bot autonomous = MakeBot(new ClassMercenary());
            Field(typeof(GameBot), autonomous, "<IsAutonomousWorldBot>k__BackingField", true);
            Assert.That(BotPvpCrowdControl.Protected(autonomous, target), Is.True, "Autonomous CC guard is unchanged.");
        }

        [Test] public void HumanPetOrderOverridesPvpReservationWithoutHumanSwing()
        {
            var (player, tank, damage, _, enemy) = PvpParty(false);
            var pet = AttachPet(player);
            BotPvpCrowdControl.Reserve(tank, enemy, 3000);
            pet.OrderedAttackTarget = enemy;
            Assert.That(CompanionPvpEngagement.Focused(damage, enemy), Is.True);
            CompanionPvpEngagement.Observe(damage);
            Assert.That(B(damage).HasAggro, Is.True);
            Assert.That(player.IsAttacking, Is.False);
        }

        [Test] public void HumanMeleeOrderWakesAttackersWithoutChangingAutonomousPolicy()
        {
            var (player, _, damage, _, enemy) = PvpParty(false);
            player.Attacking = true;
            player.TargetObject = enemy;
            CompanionPvpEngagement.Observe(damage);
            Assert.That(B(damage).HasAggro, Is.True);
            Assert.That(CompanionPvpEngagement.Focused(damage, enemy), Is.True);
            Field(typeof(GameBot), damage, "<IsAutonomousWorldBot>k__BackingField", true);
            Assert.That(CompanionPvpEngagement.Focused(damage, enemy), Is.False);
        }

        [Test] public void CrowdControlMemoryOutlastsMezWithoutChangingItsDuration()
        {
            var (player, _, damage, _, enemy) = PvpParty(true);
            enemy.TestX = 1000;
            var spell = new Spell(new DbSpell { Name = "Test mez", Type = "Mesmerize", Target = "Enemy", Duration = 60 }, 50);
            var attack = Hit(enemy, player);
            attack.AttackType = AttackData.eAttackType.Spell;
            attack.Damage = 0;
            attack.SpellHandler = new DOL.GS.Spells.SpellHandler(enemy, spell, new SpellLine("test", "test", "test", true));
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, player, attack), Is.True);
            object states = typeof(CompanionPvpEngagement).GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object[] args = { player, null };
            states.GetType().GetMethod("TryGetValue").Invoke(states, args);
            var threats = (Dictionary<GameLiving, long>)args[1].GetType().GetField("Threats").GetValue(args[1]);
            long until = threats[enemy];
            Assert.That(until - GameLoop.GameLoopTime, Is.EqualTo(132_000));
            Assert.That(spell.Duration, Is.EqualTo(60_000));
            CompanionPvpEngagement.RecordThreat(damage, player, Hit(enemy, player));
            Assert.That(threats[enemy], Is.EqualTo(until), "A subsequent normal hit must not erase the longer CC memory.");
        }

        [Test] public void SwitchingToPveTargetClearsOldExplicitPvpFocus()
        {
            var (player, _, damage, _, enemy) = PvpParty(false);
            CompanionPvpEngagement.Order(player, enemy);
            CompanionPvpEngagement.Order(player, Actor<Enemy>());
            Assert.That(CompanionPvpEngagement.Focused(damage, enemy), Is.False);
        }

        [Test] public void DefensivePullWaitsForApproachButDoesNotCreateTankWaitingGate()
        {
            var (player, _, damage, _, enemy) = PvpParty(true);
            enemy.TestX = 1000;
            Assert.That(PlayerLedPullCoordinator.Begin(player, enemy), Does.Contain("Defensive mode"));
            Assert.That(B(damage).HasAggro, Is.False);
            Assert.That(PlayerLedPullCoordinator.IsWaiting(damage), Is.False);
            enemy.TestX = 300;
            Assert.That(CompanionEngagementMode.NearbyPull(damage), Is.SameAs(enemy));
            B(damage).AssistPlayerAttack(enemy);
            Assert.That(B(damage).HasAggro, Is.True);
        }

        [Test] public void DefensiveAcquiresNearbyEnemyAndEnemyPet_NotOrdinaryMobsFriendsOrOccludedTargets()
        {
            var (player, tank, damage, _, enemy) = PvpParty(true);
            Enemy ordinary = Actor<Enemy>();
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new GameNPC[] { tank, ordinary, enemy }, (_, _) => true), Is.SameAs(enemy));
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new GameNPC[] { enemy }, (_, _) => false), Is.Null);
            enemy.TestX = 351;
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new GameNPC[] { enemy }, (_, _) => true), Is.Null);
            var enemyPet = AttachPet(enemy);
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new[] { enemyPet.Body }, (_, _) => true), Is.SameAs(enemyPet.Body));
            CompanionEngagementMode.Set(player, false);
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new[] { enemyPet.Body }, (_, _) => true), Is.Null, "Aggressive is assist, not automatic nearby acquisition.");
        }

        [TestCase(false, 0)] [TestCase(true, 0)]
        [TestCase(false, 1)] [TestCase(true, 1)]
        [TestCase(false, 2)] [TestCase(true, 2)]
        [TestCase(false, 3)] [TestCase(true, 3)]
        [TestCase(false, 4)] [TestCase(true, 4)]
        public void EnemyBotOrPetZeroDamageCrowdControlWakesRaidInEitherMode(bool defensive, int victimKind)
        {
            var (player, tank, damage, _, enemy) = PvpParty(defensive);
            enemy.TestX = 1000; // Outside defensive acquisition, but actually attacking the raid.
            GameLiving victim = victimKind switch
            {
                0 => player,
                1 => tank,
                2 => AttachPet(player).Body,
                _ => AttachPet(tank).Body
            };
            if (victimKind == 4) victim = AttachPet(victim).Body;
            var pet = AttachPet(enemy);
            ((Enemy)pet.Body).TestX = 1000;
            GameLiving attacker = victimKind % 2 == 0 ? enemy : pet.Body;
            BotPvpCrowdControl.Reserve(tank, attacker, 3000);
            damage.BeginRecoveryRest();
            AttackData cc = Hit(attacker, victim);
            cc.AttackType = AttackData.eAttackType.Spell;
            cc.Damage = 0; // SpellHandler delivers mez/stun/root/debuffs this way.
            BotBrain.NotifyNearbyGroupBots(victim, cc);
            Assert.That(B(damage).HasAggro, Is.True);
            Assert.That(damage.IsRecoveryResting, Is.False);
            Assert.That(CompanionEngagementMode.Allows(damage, attacker), Is.True);
            Assert.That(CompanionPvpEngagement.Defending(damage, attacker), Is.True);
            Assert.That(BotPvpCrowdControl.Protected(damage, attacker), Is.False);
        }

        [Test] public void DefensiveThreatExpiresAndDoesNotAllowUnboundedPursuit()
        {
            var (player, _, damage, _, enemy) = PvpParty(true);
            enemy.TestX = 1000;
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, player, Hit(enemy, player)), Is.True);
            enemy.TestX = 2001;
            Assert.That(CompanionEngagementMode.Allows(damage, enemy), Is.False);
            enemy.TestX = 1000;
            object states = typeof(CompanionPvpEngagement).GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object[] args = { player, null };
            states.GetType().GetMethod("TryGetValue").Invoke(states, args);
            var threats = (Dictionary<GameLiving, long>)args[1].GetType().GetField("Threats").GetValue(args[1]);
            threats[enemy] = -1;
            Assert.That(CompanionEngagementMode.Allows(damage, enemy), Is.False);
        }

        [Test] public void PveAndUnrelatedGroupsDoNotReceiveNewPvpExceptions()
        {
            var (player, _, damage, _, enemy) = PvpParty(true);
            Enemy mob = Actor<Enemy>();
            mob.TestX = 1000;
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, player, Hit(mob, player)), Is.False);
            Assert.That(CompanionEngagementMode.Allows(damage, mob), Is.False);
            CompanionPvpEngagement.Order(player, mob);
            Assert.That(CompanionPvpEngagement.Focused(damage, mob), Is.False);
            var (other, _, _, _, _) = Party();
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, other, Hit(enemy, other)), Is.False);
            Field(typeof(GameBot), damage, "<IsAutonomousWorldBot>k__BackingField", true);
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, player, Hit(enemy, player)), Is.False);
            Assert.That(CompanionPvpEngagement.Leader(damage), Is.Null);
        }

        [Test] public void ReleasedPetCannotEnlistRaidAndDeadEnemyCannotBeAcquired()
        {
            var (player, _, damage, _, enemy) = PvpParty(true);
            var old = AttachPet(player);
            AttachPet(player);
            Assert.That(CompanionPvpEngagement.RecordThreat(damage, old.Body, Hit(enemy, old.Body)), Is.False);
            enemy.Alive = false;
            Assert.That(CompanionPvpEngagement.SelectNearby(damage, new GameNPC[] { enemy }, (_, _) => true), Is.Null);
        }

        private (Player, Bot, Bot, Bot, Enemy) Party()
        {
            Player player = Actor<Player>();
            var group = new Group(player);
            Add(group, player);
            Bot tank = MakeBot(new ClassPaladin()), damage = MakeBot(new ClassCabalist()), healer = MakeBot(new ClassCleric());
            foreach (Bot bot in new[] { tank, damage, healer }) { Add(group, bot); bot.EnterPlayerLedGroup(player); }
            return (player, tank, damage, healer, Actor<Enemy>());
        }
        private Bot MakeBot(ICharacterClass characterClass)
        {
            Bot bot = Actor<Bot>();
            bot.Alive = true;
            bot.TestRealm = eRealm.Albion;
            bot.Level = 1;
            bot.Name = characterClass.Name;
            bot.TestClass = characterClass;
            var brain = new Brain { Body = bot };
            Field(typeof(GameNPC), bot, "m_ownBrain", brain);
            return bot;
        }
        private PetBrain AttachPet(GameLiving owner)
        {
            Enemy pet = Actor<Enemy>();
            PetBrain brain = new(owner) { Body = pet };
            if (owner is Enemy) Field(typeof(GameLiving), owner, "m_controlledBrain", new IControlledBrain[] { brain });
            else owner.ControlledBrain = brain;
            Field(typeof(GameNPC), pet, "m_ownBrain", brain);
            return brain;
        }
        private T Actor<T>() where T : GameLiving
        {
            T actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            if (actor is Enemy enemy) enemy.Level = 1;
            actor.ObjectState = GameObject.eObjectState.Active;
            Field(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
            if (actor is GameNPC npc)
            {
                Field(typeof(GameNPC), actor, "m_brains", new ArrayList());
                Field(typeof(GameNPC), actor, "m_spells", new List<Spell>());
                npc.movementComponent = new NpcMovementComponent(npc);
            }
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }
        private static Brain B(Bot bot) => (Brain)bot.Brain;
        private static List<GameLiving> Members(Group group) => (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
        private static void Add(Group group, GameLiving member) { Members(group).Add(member); member.Group = group; }
        private static void Remove(GameLiving member) { Members(member.Group).Remove(member); member.Group = null; }
        private static void Field(Type type, object owner, string name, object value) => type.GetField(name, Hidden).SetValue(owner, value);
        private static AttackData Hit(GameLiving attacker, GameLiving target) => new()
        { Attacker = attacker, Target = target, AttackType = AttackData.eAttackType.MeleeOneHand, AttackResult = eAttackResult.HitUnstyled, Damage = 1 };
    }
}
