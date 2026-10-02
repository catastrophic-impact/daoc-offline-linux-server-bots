using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using DOL.GS.ServerRules;
using DOL.GS.PlayerClass;
using System.Collections.Generic;
using DOL.GS.Styles;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_BotCombatRefinement
    {
        private sealed class GalladoriaLiveTeleporter : DOL.GS.Scripts.LiveTeleporter
        {
            public override eRealm Realm { get; set; }
            public bool DestinationSelected { get; private set; }
            public bool Select(GamePlayer player) => base.GetTeleportLocation(player, "epic dungeon");
            protected override bool GetTeleportLocation(GamePlayer player, string text)
            {
                if (text == "Galladoria")
                {
                    DestinationSelected = true;
                    return false;
                }

                return base.GetTeleportLocation(player, text);
            }
        }

        private sealed class GalladoriaInlandTeleporter : DOL.GS.Scripts.InlandTeleporter
        {
            public override eRealm Realm { get; set; }
            public bool DestinationSelected { get; private set; }
            public bool Select(GamePlayer player) => base.GetTeleportLocation(player, "epic dungeon");
            protected override bool GetTeleportLocation(GamePlayer player, string text)
            {
                if (text == "Galladoria")
                {
                    DestinationSelected = true;
                    return false;
                }

                return base.GetTeleportLocation(player, text);
            }
        }

        private sealed class Server : GameServer
        {
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
            protected override IObjectDatabase DataBaseImpl => Empty;
        }
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previous;
        private PetTestLanguageScope _language;
        [SetUp] public void Setup()
        {
            _previous = GameServer.Instance;
            _language = new PetTestLanguageScope();
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void Cleanup()
        {
            GameServer.LoadTestDouble(_previous);
            _language.Dispose();
        }

        private static Style Style(int weapon, string spec = "Sword", int opening = 0, double growth = .5, int level = 10) =>
            new(new DbStyle { Name = "test", ID = level, SpecKeyName = spec, SpecLevelRequirement = level,
                WeaponTypeRequirement = weapon, OpeningRequirementType = opening, GrowthRate = growth }, null);
        private static DbInventoryItem Weapon(eObjectType type, int slot = Slot.RIGHTHAND) =>
            GameInventoryItem.Create(new DbItemTemplate { Name = "test", Object_Type = (int)type, Item_Type = slot });

        [TestCase(eObjectType.Sword)] [TestCase(eObjectType.Axe)] [TestCase(eObjectType.Hammer)]
        [TestCase(eObjectType.SlashingWeapon)] [TestCase(eObjectType.ThrustWeapon)] [TestCase(eObjectType.CrushingWeapon)]
        [TestCase(eObjectType.Blades)] [TestCase(eObjectType.Blunt)] [TestCase(eObjectType.Piercing)]
        [TestCase(eObjectType.Spear)] [TestCase(eObjectType.CelticSpear)] [TestCase(eObjectType.LargeWeapons)]
        [TestCase(eObjectType.PolearmWeapon)] [TestCase(eObjectType.TwoHandedWeapon)]
        [TestCase(eObjectType.Staff)] [TestCase(eObjectType.Flexible)] [TestCase(eObjectType.Scythe)]
        public void StylesRequireTheirActualWeapon(eObjectType type)
        {
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(Style((int)type), Weapon(type), null, eActiveWeaponSlot.Standard), Is.True);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(Style((int)type), Weapon(eObjectType.CompositeBow), null, eActiveWeaponSlot.Distance), Is.False);
        }

        [Test]
        public void ShieldAndDualWieldCannotUseMissingOrWrongOffhand()
        {
            var sword = Weapon(eObjectType.Sword);
            var shield = Weapon(eObjectType.Shield, Slot.LEFTHAND);
            var h2h = Weapon(eObjectType.HandToHand);
            var dual = Style(StyleTypeDual, Specs.HandToHand);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(dual, h2h, h2h, eActiveWeaponSlot.Standard), Is.True);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(dual, h2h, sword, eActiveWeaponSlot.Standard), Is.False);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(dual, h2h, shield, eActiveWeaponSlot.Standard), Is.False);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(dual, h2h, null, eActiveWeaponSlot.Standard), Is.False);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(Style(42), sword, shield, eActiveWeaponSlot.Standard), Is.True);
            Assert.That(BotMeleeStylePolicy.MatchesWeapon(Style(42), sword, shield, eActiveWeaponSlot.TwoHanded), Is.False);
        }
        private const int StyleTypeDual = 1000;

        private sealed class Assassin : GameBot
        {
            private Assassin() : base((OfflineWorldBotRecord)null) { }
            public bool Fighting;
            public override bool IsAlive => true;
            public override bool InCombat => Fighting;
            public override bool IsAttacking => false;
            public override bool IsCasting => false;
            public override bool IsCrowdControlled => false;
            public override byte Level { get; set; }
            public override eRealm Realm { get; set; }
            public override ushort CurrentRegionID { get; set; }
            public override int Endurance { get; set; }
            public override int GetModified(eProperty property) => property == eProperty.FatigueConsumption ? 100 : 0;
            public override ICharacterClass CharacterClass => new ClassShadowblade();
            public override int GetModifiedSpecLevel(string name) => 20;
            public override bool HasAbilityToUseItem(DbItemTemplate item) => item.Object_Type == (int)eObjectType.Sword;
            public override void RefreshItemBonuses() { }
        }

        [Test]
        public void UnlimitedPoisonCoatsUseNoBackpackSlotsAndNeverRefillDuringCombat()
        {
            var spell = new Spell(new DbSpell { SpellID = 30019, Name = "poison", Type = "DamageOverTime", Target = "Enemy" }, 20);
            var supply = new DbItemTemplate { Level = 20, PoisonCharges = 1, PoisonSpellID = 30019 };
            BotPoisonSupply.Load(new[] { supply }, new[] { spell });
            var bot = (Assassin)RuntimeHelpers.GetUninitializedObject(typeof(Assassin));
            bot.Level = 50;
            bot.Inventory = new BotInventory();
            var blade = GameInventoryItem.Create(new DbItemTemplate { Name = "earned sword", Object_Type = (int)eObjectType.Sword,
                Item_Type = Slot.RIGHTHAND, DPS_AF = 150, SPD_ABS = 35, Quality = 99, MaxCondition = 50000, Condition = 50000 });
            bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, blade);
            for (int i = (int)eInventorySlot.FirstBackpack; i <= (int)eInventorySlot.LastBackpack; i++)
                bot.Inventory.AddItem((eInventorySlot)i, GameInventoryItem.Create(new DbItemTemplate { Name = "earned loot", MaxCount = 1 }));
            int count = bot.Inventory.AllItems.Count;
            for (int i = 0; i < 1000; i++)
            {
                Assert.That(BotPoisonSupply.Maintain(bot), Is.True);
                Assert.That(blade.PoisonSpellID, Is.EqualTo(30019));
                Assert.That(blade.PoisonCharges, Is.EqualTo(1));
                Assert.That(BotPoisonSupply.Maintain(bot), Is.False);
                blade.PoisonCharges = 0; blade.PoisonSpellID = 0;
                bot.Fighting = true;
                Assert.That(BotPoisonSupply.Maintain(bot), Is.False);
                bot.Fighting = false;
            }
            Assert.That(bot.Inventory.AllItems.Count, Is.EqualTo(count));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.RightHandWeapon), Is.SameAs(blade));
            Assert.That(blade.Quality, Is.EqualTo(99));
            BotPoisonSupply.Load(Array.Empty<DbItemTemplate>(), Array.Empty<Spell>());
        }

        [Test]
        public void BuildSurvivesRepeatedRecreationWithoutRerollingWeaponOrTrainingPlan()
        {
            var original = new ShadowbladeBotSpec(eSpecType.LeftAxe);
            original.WeaponOneType = eObjectType.Sword;
            original.SpecLines = new List<BotSpecLine> { new(Specs.Sword, 34, .6f), new(Specs.Left_Axe, 39, .8f) };
            string saved = BotLifetimeBuild.Encode(original);
            for (int restart = 0; restart < 100; restart++)
            {
                var fresh = new ShadowbladeBotSpec(eSpecType.TwoHanded);
                Assert.That(BotLifetimeBuild.Restore(fresh, saved), Is.True);
                Assert.That(BotLifetimeBuild.Encode(fresh), Is.EqualTo(saved));
            }
            Assert.That(BotLifetimeBuild.Restore(original, "broken"), Is.False);
        }

        [Test]
        public void MigrationFollowsExistingInvestmentWithoutChangingSpentLevels()
        {
            var plan = new BotSpec { WeaponOneType = eObjectType.Axe,
                SpecLines = new List<BotSpecLine> { new(Specs.Axe, 50, 1) } };
            var points = new Dictionary<string, int> { [Specs.Sword] = 39, [Specs.Axe] = 1 };
            BotLifetimeBuild.AlignWithInvestedWeapons(plan, key => points.GetValueOrDefault(key));
            Assert.That(plan.WeaponOneType, Is.EqualTo(eObjectType.Sword));
            Assert.That(plan.SpecLines[0].Spec, Is.EqualTo(Specs.Sword));
            Assert.That(points[Specs.Sword], Is.EqualTo(39));
            Assert.That(points[Specs.Axe], Is.EqualTo(1));
            Assert.That(BotWeaponStats.PrimaryType(eObjectType.Sword, eObjectType.Axe, true), Is.EqualTo(eObjectType.Sword));
        }

        [Test]
        public void GeneratedFistSavageAndUntrainedSecondaryWeaponStayBuildCorrect()
        {
            var savage = new SavageBotSpec(eSpecType.DualWield, eObjectType.HandToHand);
            Assert.That(BotWeaponStats.MatchesBuild(savage, eObjectType.HandToHand), Is.True);
            Assert.That(BotWeaponStats.MatchesBuild(savage, eObjectType.Sword), Is.False);
            var armsman = new BotSpec { WeaponOneType = eObjectType.SlashingWeapon, WeaponTwoType = eObjectType.PolearmWeapon };
            Assert.That(BotWeaponStats.MatchesBuild(armsman, eObjectType.PolearmWeapon), Is.False);
            armsman.SpecLines.Add(new BotSpecLine(Specs.Polearms, 50, 1));
            Assert.That(BotWeaponStats.MatchesBuild(armsman, eObjectType.PolearmWeapon), Is.True);
        }

        [Test]
        public void RealStyleSelectorChecksChainsDefensiveOpeningsEnduranceAndStaleQueue()
        {
            var bot = (Assassin)RuntimeHelpers.GetUninitializedObject(typeof(Assassin));
            bot.Level = 50; bot.Endurance = 100; bot.Inventory = new BotInventory();
            var sword = GameInventoryItem.Create(new DbItemTemplate { Name = "sword", Object_Type = (int)eObjectType.Sword,
                Item_Type = Slot.RIGHTHAND, DPS_AF = 150, SPD_ABS = 35, Quality = 99, Condition = 50000, MaxCondition = 50000 });
            bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, sword);
            typeof(GameLiving).GetField("_activeWeapon", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, sword);
            bot.styleComponent = new NpcStyleComponent(bot);
            typeof(GameLiving).GetField("m_effects", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(bot, new DOL.GS.Effects.GameEffectList(bot));
            var enemy = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            enemy.attackComponent = new AttackComponent(enemy);
            bot.TargetObject = enemy;
            var anytime = Style(11, growth: .5);
            var chain = new Style(new DbStyle { Name = "chain", ID = 90, SpecKeyName = Specs.Sword,
                SpecLevelRequirement = 20, WeaponTypeRequirement = 11, OpeningRequirementValue = anytime.ID,
                AttackResultRequirement = (int)DOL.GS.Styles.Style.eAttackResultRequirement.Style, GrowthRate = .9 }, null);
            var reactive = new Style(new DbStyle { Name = "evade", ID = 91, SpecKeyName = Specs.Sword,
                SpecLevelRequirement = 25, WeaponTypeRequirement = 11, OpeningRequirementType = 1,
                AttackResultRequirement = (int)DOL.GS.Styles.Style.eAttackResultRequirement.Evade, GrowthRate = 1 }, null);
            bot.Styles = new List<Style> { anytime, chain, reactive };
            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.SameAs(anytime));
            Assert.That(BotMeleeStylePolicy.Select(bot, new AttackData { AttackResult = eAttackResult.HitStyle, Style = anytime }), Is.SameAs(chain));
            enemy.attackComponent.attackAction.LastAttackData = new AttackData { Target = bot, AttackResult = eAttackResult.Evaded };
            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.SameAs(reactive));
            enemy.attackComponent.attackAction.LastAttackData = null;
            bot.styleComponent.NextCombatStyle = chain;
            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.SameAs(anytime));
            bot.Endurance = 1;
            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.SameAs(anytime));
            bot.Endurance = 0;
            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.Null);
        }

        [Test]
        public void NonPositionalSelectionKeepsLowLevelUsefulStylesAndPrefersGrowth()
        {
            var low = Style(11, level: 2, growth: .7);
            var high = Style(11, level: 40, growth: .2);
            Assert.That(BotMeleeStylePolicy.IsEligible(low), Is.True);
            Assert.That(BotMeleeStylePolicy.IsEligible(Style(11, opening: 2)), Is.False);
            Assert.That(BotMeleeStylePolicy.IsEligible(Style(11, opening: 1)), Is.True);
            Assert.That(BotMeleeStylePolicy.Better(low, high), Is.True);
        }

        [Test]
        public void PlayerLikeBotsResolveTheirActualClassForStyleCatalogs()
        {
            var bot = (GameBot)RuntimeHelpers.GetUninitializedObject(typeof(GameBot));
            typeof(GameBot).GetField("m_characterClass", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(bot, new ClassArmsman());
            MethodInfo resolver = typeof(Specialization).GetMethod("ResolveClassId",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(resolver, Is.Not.Null);
            Assert.That(resolver.Invoke(null, new object[] { bot }), Is.EqualTo((int)eCharacterClass.Armsman));
        }

        [Test]
        public void AnytimeFallbackCannotBeDisplacedByUnusableOrPositionalStyles()
        {
            var bot = (Assassin)RuntimeHelpers.GetUninitializedObject(typeof(Assassin));
            bot.Level = 50; bot.Endurance = 100; bot.Inventory = new BotInventory();
            var sword = GameInventoryItem.Create(new DbItemTemplate { Name = "sword", Object_Type = (int)eObjectType.Sword,
                Item_Type = Slot.RIGHTHAND, DPS_AF = 30, SPD_ABS = 35, Quality = 99, Condition = 50000, MaxCondition = 50000 });
            bot.Inventory.AddItem(eInventorySlot.RightHandWeapon, sword);
            typeof(GameLiving).GetField("_activeWeapon", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(bot, sword);
            bot.styleComponent = new NpcStyleComponent(bot);
            typeof(GameLiving).GetField("m_effects", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(bot, new DOL.GS.Effects.GameEffectList(bot));
            var enemy = (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC));
            enemy.attackComponent = new AttackComponent(enemy);
            bot.TargetObject = enemy;

            var anytime = Style((int)eObjectType.Sword, level: 5, growth: .4);
            var positional = Style((int)eObjectType.Sword,
                opening: (int)DOL.GS.Styles.Style.eOpening.Positional, level: 5, growth: 5);
            var tooHigh = Style((int)eObjectType.Sword, level: 51, growth: 6);
            var wrongWeapon = Style((int)eObjectType.Axe, level: 5, growth: 7);
            var stealth = new Style(new DbStyle { Name = "stealth", ID = 92, SpecKeyName = Specs.Sword,
                SpecLevelRequirement = 5, WeaponTypeRequirement = (int)eObjectType.Sword,
                StealthRequirement = true, GrowthRate = 8 }, null);
            bot.Styles = new List<Style> { positional, tooHigh, wrongWeapon, stealth, anytime };

            Assert.That(BotMeleeStylePolicy.Select(bot, null), Is.SameAs(anytime));
            Assert.That(bot.styleComponent.NextCombatBackupStyle, Is.SameAs(anytime));
        }

        [Test]
        public void GalladoriaEpicDungeonSelectionIsHandledByBothClassicTeleporters()
        {
            var player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
            player.Realm = eRealm.Hibernia;
            var live = (GalladoriaLiveTeleporter)RuntimeHelpers.GetUninitializedObject(typeof(GalladoriaLiveTeleporter));
            var inland = (GalladoriaInlandTeleporter)RuntimeHelpers.GetUninitializedObject(typeof(GalladoriaInlandTeleporter));
            live.Realm = eRealm.Hibernia;
            inland.Realm = eRealm.Hibernia;

            Assert.Multiple(() =>
            {
                Assert.That(live.Select(player), Is.True);
                Assert.That(live.DestinationSelected, Is.True);
                Assert.That(inland.Select(player), Is.True);
                Assert.That(inland.DestinationSelected, Is.True);
            });
        }

        [TestCase("DexterityDebuff", false)] [TestCase("StrengthConstitutionDebuff", false)]
        [TestCase("Disease", false)] [TestCase("DirectDamage", true)] [TestCase("Lifedrain", true)]
        public void CasterInstantsCannotStarveRangedDamage(string type, bool allowedAtRange)
        {
            var spell = new Spell(new DbSpell { Name = "test", Type = type, Target = "Enemy" }, 1);
            Assert.That(BotCasterPriority.AllowInstant(spell, true, false), Is.EqualTo(allowedAtRange));
            Assert.That(BotCasterPriority.AllowInstant(spell, true, true), Is.True);
            Assert.That(BotCasterPriority.AllowInstant(spell, false, false), Is.True);
        }

        [TestCase(50, 35, 35)] [TestCase(10, 20, 10)] [TestCase(50, 0, 0)] [TestCase(50, 61, 50)]
        public void PoisonsRespectBothLevelAndEnvenom(int level, int spec, int expected) =>
            Assert.That(BotPoisonSupply.EligibleLevel(level, spec), Is.EqualTo(expected));

        [Test]
        public void OnlyThreeAssassinsHavePoisonSupply()
        {
            Assert.That(Enum.GetValues<eCharacterClass>().Where(BotPoisonSupply.IsAssassin), Is.EquivalentTo(new[]
                { eCharacterClass.Shadowblade, eCharacterClass.Infiltrator, eCharacterClass.Nightshade }));
            Assert.That(BotRvrAmbush.IsStealthClass(eCharacterClass.Wizard), Is.False);
            Assert.That(BotRvrAmbush.IsStealthClass(eCharacterClass.Hunter), Is.True);
        }

        [Test]
        public void AmbushRequiresOpposingPlayerLikeActorNotFriendlyBotOrMonster()
        {
            var bot = (Assassin)RuntimeHelpers.GetUninitializedObject(typeof(Assassin));
            var opponent = (Assassin)RuntimeHelpers.GetUninitializedObject(typeof(Assassin));
            bot.Realm = eRealm.Midgard; opponent.Realm = eRealm.Albion;
            bot.CurrentRegionID = opponent.CurrentRegionID = 100;
            Assert.That(BotRvrAmbush.IsEnemyCombatant(bot, opponent), Is.True);
            opponent.Realm = eRealm.Midgard;
            Assert.That(BotRvrAmbush.IsEnemyCombatant(bot, opponent), Is.False);
            Assert.That(BotRvrAmbush.IsEnemyCombatant(bot, (GameNPC)RuntimeHelpers.GetUninitializedObject(typeof(GameNPC))), Is.False);
            opponent.Realm = eRealm.Albion; opponent.CurrentRegionID = 200;
            Assert.That(BotRvrAmbush.IsEnemyCombatant(bot, opponent), Is.False);
        }

        [Test]
        public void AllSupportedClassBuildVariantsRoundTripTheirEntirePlan()
        {
            int count = 0;
            foreach (eCharacterClass characterClass in Enum.GetValues<eCharacterClass>())
            foreach (eSpecType choice in BotSpec.GetSpecializationChoices(characterClass))
            {
                BotSpec plan = BotSpec.GetSpec(characterClass, choice);
                if (plan == null) continue;
                string saved = BotLifetimeBuild.Encode(plan);
                var restored = new BotSpec();
                Assert.That(BotLifetimeBuild.Restore(restored, saved), Is.True, $"{characterClass} {choice}");
                Assert.That(BotLifetimeBuild.Encode(restored), Is.EqualTo(saved));
                count++;
            }
            Assert.That(count, Is.GreaterThan(60));
            TestContext.WriteLine($"Lifetime plan round-trip: {count} supported class/build choices.");
        }

        [Test]
        public void PartialCorridorContinuationRequiresProgressAndAnUnreachedGoal()
        {
            var start = System.Numerics.Vector3.Zero;
            var position = new System.Numerics.Vector3(200, 0, 0);
            var target = new System.Numerics.Vector3(400, 0, 0);
            Assert.That(BotTravelHandoff.CanContinuePartial(PathfindingStatus.PartialPathFound, start, position, target), Is.True);
            Assert.That(BotTravelHandoff.CanContinuePartial(PathfindingStatus.PartialPathFound, position, position, target), Is.False);
            Assert.That(BotTravelHandoff.CanContinuePartial(PathfindingStatus.PartialPathFound, start, target, target), Is.False);
            Assert.That(BotTravelHandoff.CanContinuePartial(PathfindingStatus.NoPathFound, start, position, target), Is.False);
            Assert.That(BotTravelHandoff.CanContinuePartial(PathfindingStatus.PathFound, start, position, target), Is.False);
        }

        [Test]
        public void MillionHandoffDecisionsAllocateNothing()
        {
            long sum = 0;
            var origin = System.Numerics.Vector3.Zero;
            var position = new System.Numerics.Vector3(200, 0, 0);
            var target = new System.Numerics.Vector3(400, 0, 0);
            for (int i = 0; i < 10000; i++) sum += BotTravelHandoff.CanContinuePartial(PathfindingStatus.PartialPathFound, origin, position, target) ? 1 : 0;
            var clock = Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000000; i++) sum += BotTravelHandoff.CanContinuePartial(PathfindingStatus.PartialPathFound, origin, position, target) ? 1 : 0;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            clock.Stop();
            Assert.That(allocated, Is.Zero);
            TestContext.WriteLine($"1,000,000 arrival scheduling decisions: {clock.Elapsed.TotalMilliseconds:F3} ms; allocated {allocated} bytes; checksum {sum}");
        }
    }
}
