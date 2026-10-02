using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.AI.Brain;
using DOL.Database;
using DOL.Database.Handlers;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using DOL.GS.ServerProperties;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_AutonomousLootFlow
    {
        // Inert actors avoid bot login, database constructors, timers and world
        // registration. Only the input state used by loot is initialized here.
        // The generator, world-item pickup, inventory and upgrade evaluator are real.
        private sealed class LootBot : GameBot
        {
            private LootBot() : base((OfflineWorldBotRecord)null) { }
            public override byte Level { get; set; }
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; }
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public ICharacterClass TestClass;
            public override ICharacterClass CharacterClass => TestClass ?? new ClassEnchanter();
            public int BonusRefreshes;
            public override void RefreshItemBonuses() { BonusRefreshes++; }
        }

        private sealed class LootMob : GameNPC
        {
            public override byte Level { get; set; }
            public override int EffectiveLevel => Level;
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
        }

        private sealed class LootPlayer : GamePlayer
        {
            private LootPlayer() : base(null, null) { }
            public override byte Level { get; set; }
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; }
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public ICharacterClass TestClass;
            public override ICharacterClass CharacterClass => TestClass ?? new ClassEnchanter();
        }

        private sealed class LootServer : GameServer
        {
            public IObjectDatabase TestDatabase;
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
            protected override IObjectDatabase DataBaseImpl => TestDatabase ?? EmptyDatabase;
        }

        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, EmptyReadDatabase>();

        public class EmptyReadDatabase : DispatchProxy
        {
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (!method.Name.StartsWith("Select") && !method.Name.StartsWith("Find"))
                    throw new InvalidOperationException("Loot unit test attempted a database mutation: " + method.Name);
                Type result = method.ReturnType;
                if (result.IsGenericType && typeof(IEnumerable).IsAssignableFrom(result))
                    return Activator.CreateInstance(typeof(List<>).MakeGenericType(result.GetGenericArguments()[0]));
                return null;
            }
        }

        private sealed class FixedDropGenerator : ROGMobGenerator
        {
            public bool Drop = true;
            public int Rolls;
            public readonly List<eCharacterClass> Classes = new();
            protected override bool RollDropChance(int chance) { Rolls++; return Drop; }
            protected override DbItemTemplate GenerateItemTemplate(GameLiving owner, eCharacterClass characterClass,
                byte level, int con, bool adjustQuality = true)
            {
                Classes.Add(characterClass);
                return Pants(level);
            }
        }

        private sealed class RealItemGenerator : ROGMobGenerator
        {
            // Force only the drop roll. Item stats, quality, class, realm and
            // unique-template creation still use the production item factory.
            protected override bool RollDropChance(int chance) { return true; }
        }

        private GameServer _previousServer;
        private static long _nextId = 900000;
        private readonly Dictionary<FieldInfo, object> _rogDefaults = new();

        [OneTimeSetUp]
        public void InitializeItemFactory()
        {
            foreach (FieldInfo field in typeof(Properties).GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(field => field.Name.StartsWith("ROG_")))
            {
                _rogDefaults[field] = field.GetValue(null);
                field.SetValue(null, field.GetCustomAttribute<ServerPropertyAttribute>().DefaultValue);
            }
            var prefixes = (IDictionary)typeof(GeneratedUniqueItem).GetField("hPropertyToMagicPrefix",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            if (prefixes.Count == 0) GeneratedUniqueItem.InitializeHashtables();
        }

        [OneTimeTearDown]
        public void RestoreItemDefaults()
        {
            foreach (var entry in _rogDefaults) entry.Key.SetValue(null, entry.Value);
        }

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((LootServer)RuntimeHelpers.GetUninitializedObject(typeof(LootServer)));
        }

        [TearDown]
        public void TearDown() { GameServer.LoadTestDouble(_previousServer); }

        private static void Field(Type type, object target, string name, object value)
        {
            type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static void Property(GameBot bot, string name, object value)
        {
            typeof(GameBot).GetProperty(name).SetValue(bot, value);
        }

        private static T Inert<T>() where T : GameLiving
        {
            T living = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            Field(typeof(GameLiving), living, "<TempProperties>k__BackingField", new PropertyCollection());
            if (living is GameNPC)
                Field(typeof(GameNPC), living, "m_brains", new ArrayList());
            return living;
        }

        private static LootBot Bot(bool helper = false)
        {
            LootBot bot = Inert<LootBot>();
            bot.Level = 10;
            bot.Realm = eRealm.Hibernia;
            bot.Name = "Loot test bot";
            bot.DatabaseID = Interlocked.Increment(ref _nextId);
            bot.InternalID = AutonomousBotEconomy.GetOwnerId(bot.DatabaseID);
            bot.Inventory = new BotInventory(bot.InternalID);
            Property(bot, nameof(GameBot.IsAutonomousWorldBot), !helper);
            Property(bot, nameof(GameBot.IsTemporaryGroupHelper), helper);
            Field(typeof(GameNPC), bot, "m_ownBrain", new BotBrain { Body = bot });
            Field(typeof(GameLiving), bot, "_abilitiesLock", new Lock());
            Field(typeof(GameLiving), bot, "m_abilities", new Dictionary<string, Ability>
            {
                [Abilities.HibArmor] = new Ability(new DbAbility { KeyName = Abilities.HibArmor, Name = "Cloth" }, 1)
            });
            return bot;
        }

        private static LootMob Mob(byte level = 10)
        {
            LootMob mob = Inert<LootMob>();
            mob.Level = level;
            mob.Name = "large frog";
            return mob;
        }

        private static LootMob Pet(GameLiving owner)
        {
            LootMob pet = Mob();
            Field(typeof(GameNPC), pet, "m_ownBrain", new ControlledMobBrain(owner) { Body = pet });
            return pet;
        }

        private static DbItemTemplate Pants(int level = 1)
        {
            return new DbItemTemplate { Id_nb = Guid.NewGuid().ToString(), Name = "cloth pants",
                Realm = (int)eRealm.Hibernia, Level = level, Item_Type = (int)eInventorySlot.LegsArmor,
                Object_Type = (int)eObjectType.Cloth, Quality = 95, MaxCount = 1, PackSize = 1,
                MaxCondition = 10000, MaxDurability = 10000, DPS_AF = level, Price = 100 };
        }

        [Test]
        public void DirectBotKillGeneratesLootDespiteBotBrainNotBeingControlledMobBrain()
        {
            LootBot bot = Bot();
            Assert.That(bot.Brain, Is.TypeOf<BotBrain>());
            Assert.That(new FixedDropGenerator().GenerateLoot(Mob(), bot).GetLoot(), Has.Length.EqualTo(1));
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void BotOwnedPetAndSubPetKillsResolvePersistentOwner(int depth)
        {
            LootBot owner = Bot();
            GameLiving killer = owner;
            for (int i = 0; i < depth; i++) killer = Pet(killer);
            Assert.That(ROGMobGenerator.ResolveEquipmentLootOwner(killer), Is.SameAs(owner));
            Assert.That(new FixedDropGenerator().GenerateLoot(Mob(), killer).GetLoot(), Has.Length.EqualTo(1));
        }

        [Test]
        public void FailedDropRollAndGreyMobsDoNotInventEquipment()
        {
            LootBot bot = Bot();
            Assert.That(new FixedDropGenerator { Drop = false }.GenerateLoot(Mob(), bot).GetLoot(), Is.Empty);
            Assert.That(new FixedDropGenerator().GenerateLoot(Mob(1), bot).GetLoot(), Is.Empty);
        }

        [TestCase(2)] [TestCase(4)] [TestCase(8)]
        public void BotOnlyGroupRollsForRealMembersWithoutPlayerCasts(int count)
        {
            LootBot leader = Bot();
            Group group = new(leader);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            for (int i = 0; i < count; i++)
            {
                LootBot member = i == 0 ? leader : Bot();
                member.Group = group;
                members.Add(member);
            }
            FixedDropGenerator generator = new();
            Assert.That(generator.GenerateLoot(Mob(), leader).GetLoot(), Has.Length.EqualTo(Math.Max(1, (int)Math.Round(count / 3m))));
            Assert.That(generator.Rolls, Is.EqualTo(count));
            Assert.That(generator.Classes, Is.All.EqualTo(eCharacterClass.Enchanter));
        }

        [Test]
        public void HumanAndHelperPetRetainHumanLootOwner()
        {
            LootPlayer player = Inert<LootPlayer>();
            player.Level = 10;
            player.Realm = eRealm.Hibernia;
            LootBot helper = Bot(true);
            Property(helper, nameof(GameBot.Owner), player);
            Assert.That(ROGMobGenerator.ResolveEquipmentLootOwner(helper), Is.SameAs(player));
            Assert.That(ROGMobGenerator.ResolveEquipmentLootOwner(Pet(helper)), Is.SameAs(player));
            Assert.That(new FixedDropGenerator().GenerateLoot(Mob(), player).GetLoot(), Has.Length.EqualTo(1));
            WorldInventoryItem drop = new(GameInventoryItem.Create(Pants()));
            Assert.That(drop.TryAutoPickUp(helper), Is.EqualTo(TryPickUpResult.DoesNotWant));
            Assert.That(helper.Inventory.AllItems, Is.Empty);
        }

        [Test]
        public void CompanionGroupLootAlwaysUsesCurrentPlayerClass()
        {
            LootPlayer player = Inert<LootPlayer>();
            player.Level = 3;
            player.Realm = eRealm.Albion;
            player.TestClass = new ClassFighter();
            LootBot helper = Bot(true);
            helper.TestClass = new ClassEnchanter();
            Property(helper, nameof(GameBot.Owner), player);

            Group group = new(player);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            foreach (GameLiving member in new GameLiving[] { player, helper })
            {
                members.Add(member);
                member.Group = group;
            }

            FixedDropGenerator generator = new();
            Assert.That(generator.GenerateLoot(Mob(3), Pet(helper)).GetLoot(), Has.Length.EqualTo(1));
            Assert.That(generator.Rolls, Is.EqualTo(1), "The companion receives no independent loot roll.");
            Assert.That(generator.Classes, Is.All.EqualTo(eCharacterClass.Fighter),
                "A companion's class must never replace the current player's smart-loot class.");
        }

        [Test]
        public void EveryClassicStarterClassHasUsableSmartLoot()
        {
            var cases = new[]
            {
                (eRealm.Albion, eCharacterClass.Fighter, eObjectType.Studded, new[] { eObjectType.SlashingWeapon, eObjectType.Shield }),
                (eRealm.Albion, eCharacterClass.Acolyte, eObjectType.Leather, new[] { eObjectType.CrushingWeapon, eObjectType.Shield }),
                (eRealm.Albion, eCharacterClass.AlbionRogue, eObjectType.Leather, new[] { eObjectType.ThrustWeapon }),
                (eRealm.Albion, eCharacterClass.Mage, eObjectType.Cloth, new[] { eObjectType.Staff }),
                (eRealm.Albion, eCharacterClass.Elementalist, eObjectType.Cloth, new[] { eObjectType.Staff }),
                (eRealm.Midgard, eCharacterClass.Viking, eObjectType.Studded, new[] { eObjectType.Axe }),
                (eRealm.Midgard, eCharacterClass.Seer, eObjectType.Leather, new[] { eObjectType.Hammer, eObjectType.Shield }),
                (eRealm.Midgard, eCharacterClass.MidgardRogue, eObjectType.Leather, new[] { eObjectType.Sword }),
                (eRealm.Midgard, eCharacterClass.Mystic, eObjectType.Cloth, new[] { eObjectType.Staff }),
                (eRealm.Hibernia, eCharacterClass.Guardian, eObjectType.Reinforced, new[] { eObjectType.Blades, eObjectType.Shield }),
                (eRealm.Hibernia, eCharacterClass.Naturalist, eObjectType.Leather, new[] { eObjectType.Blunt, eObjectType.Shield }),
                (eRealm.Hibernia, eCharacterClass.Stalker, eObjectType.Leather, new[] { eObjectType.Piercing }),
                (eRealm.Hibernia, eCharacterClass.Magician, eObjectType.Cloth, new[] { eObjectType.Staff }),
                (eRealm.Hibernia, eCharacterClass.Forester, eObjectType.Cloth, new[] { eObjectType.Staff }),
                (eRealm.Hibernia, eCharacterClass.Sluaghbinder, eObjectType.Reinforced,
                    new[] { eObjectType.Blunt, eObjectType.Scythe, eObjectType.Shield })
            };

            foreach (var entry in cases)
            {
                var (realm, classId, expectedArmor, validWeapons) = entry;
                eObjectType armor = realm switch
                {
                    eRealm.Albion => GeneratedUniqueItem.GetAlbionArmorType(classId, 3),
                    eRealm.Midgard => GeneratedUniqueItem.GetMidgardArmorType(classId, 3),
                    _ => GeneratedUniqueItem.GetHiberniaArmorType(classId, 3)
                };
                Assert.That(armor, Is.EqualTo(expectedArmor), $"{classId} starter armor");

                for (int i = 0; i < 128; i++)
                {
                    eObjectType weapon = realm switch
                    {
                        eRealm.Albion => GeneratedUniqueItem.GetAlbionWeapon(classId),
                        eRealm.Midgard => GeneratedUniqueItem.GetMidgardWeapon(classId),
                        _ => GeneratedUniqueItem.GetHiberniaWeapon(classId)
                    };
                    Assert.That(validWeapons, Does.Contain(weapon), $"{classId} generated {weapon}");
                }
            }
        }

        [Test]
        public void SluaghbinderRogArmorProgressesAndAllowsAllShieldSizes()
        {
            Assert.That(GeneratedUniqueItem.GetHiberniaArmorType(eCharacterClass.Sluaghbinder, 19),
                Is.EqualTo(eObjectType.Reinforced));
            Assert.That(GeneratedUniqueItem.GetHiberniaArmorType(eCharacterClass.Sluaghbinder, 20),
                Is.EqualTo(eObjectType.Scale));

            MethodInfo shieldSize = typeof(GeneratedUniqueItem).GetMethod("GetMaxShieldSizeFromClass",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(shieldSize, Is.Not.Null);
            Assert.That(shieldSize.Invoke(null, new object[] { eCharacterClass.Sluaghbinder }),
                Is.EqualTo(3));
        }

        [Test]
        public void SluaghbinderRandomRogsNeverFallBackToClothOrUnsupportedWeapons()
        {
            var valid = new[] { eObjectType.Blunt, eObjectType.Scythe, eObjectType.Shield,
                eObjectType.Reinforced, eObjectType.Scale, eObjectType.Magical };

            for (int i = 0; i < 256; i++)
            {
                var item = new GeneratedUniqueItem(eRealm.Hibernia, eCharacterClass.Sluaghbinder, 50);
                Assert.That(valid, Does.Contain((eObjectType)item.Object_Type),
                    $"Unexpected Sluaghbinder ROG type {(eObjectType)item.Object_Type}");
                if ((eObjectType)item.Object_Type == eObjectType.Shield)
                    Assert.That(item.Type_Damage, Is.InRange(1, 3), "Sluaghbinder shield size");
            }
        }

        // Invoke the existing kill-attribution stage, without changing production
        // reward rules or starting a world. Its compiler-generated local method
        // keeps the test pointed at the actual code used by OnNpcKilled.
        private static object[] ProcessKillCredit(LootMob mob)
        {
            MethodInfo process = typeof(AbstractServerRules).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name.Contains("g__ProcessXpGainers|") && method.GetParameters().Length == 10);
            var args = new object[10]; args[0] = mob;
            Assert.That(process.Invoke(null, args), Is.True);
            return args;
        }

        private static object CreditField(object value, string name) =>
            value.GetType().GetProperty(name).GetValue(value);

        [TestCase(1, 0, 10)] [TestCase(7, 0, 14)]
        [TestCase(7, 1, 18)] [TestCase(7, 2, 18)]
        public void CompanionOnlyDamageGivesHumanFullCreditWithoutHelperXpSplit(int count, int petDepth, int mobLevel)
        {
            LootPlayer player = Inert<LootPlayer>(); player.Level = 10;
            player.ObjectState = GameObject.eObjectState.Active;
            Group group = new(player); player.Group = group;
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            members.Add(player);
            LootMob mob = Mob((byte)mobLevel);
            var gainers = new Dictionary<GameLiving, double> { [player] = 0 }; // Never attacked.
            for (int i = 0; i < count; i++)
            {
                LootBot helper = Bot(true); Property(helper, nameof(GameBot.Owner), player);
                helper.Group = group; members.Add(helper);
                GameLiving attacker = helper;
                for (int depth = 0; depth < petDepth; depth++) attacker = Pet(attacker);
                gainers[attacker] = 100d / count;
            }
            Field(typeof(GameLiving), mob, "m_xpGainers", gainers);
            object[] result = ProcessKillCredit(mob);
            Assert.That((double)result[1], Is.EqualTo(100).Within(0.00001));
            IDictionary humans = (IDictionary)result[2], bots = (IDictionary)result[3], groups = (IDictionary)result[6];
            Assert.That(humans.Count, Is.EqualTo(1)); Assert.That(humans.Contains(player), Is.True);
            Assert.That(bots.Count, Is.Zero);
            Assert.That(CreditField(groups[group], "Count"), Is.EqualTo(1));
            Assert.That((double)CreditField(groups[group], "Damage"), Is.EqualTo(100).Within(0.00001));
            Assert.That(CreditField(result[4], "Owner"), Is.SameAs(player), "Human also owns the loot; helper never takes it.");
            Assert.That(group.GetPlayersInTheGroup(), Is.EquivalentTo(new[] { player }), "Native coin/item splitting sees one real player, not eight helpers.");
        }

        [TestCase(2)] [TestCase(8)]
        public void PersistentBotGroupStillHasOneXpSharePerRealMember(int count)
        {
            LootMob mob = Mob(); var gainers = new Dictionary<GameLiving, double>();
            Group group = null;
            for (int i = 0; i < count; i++)
            {
                LootBot bot = Bot(); bot.ObjectState = GameObject.eObjectState.Active;
                group ??= new(bot); bot.Group = group;
                gainers[bot] = 100d / count;
            }
            Field(typeof(GameLiving), mob, "m_xpGainers", gainers);
            object[] result = ProcessKillCredit(mob);
            Assert.That(((IDictionary)result[2]).Count, Is.Zero);
            Assert.That(((IDictionary)result[3]).Count, Is.EqualTo(count));
            Assert.That(CreditField(((IDictionary)result[6])[group], "Count"), Is.EqualTo(count));
        }

        [Test]
        public void AllSummonFamiliesCreditTheirActualOwnerForXpAndEquipment(
            [Values("player", "companion", "persistent")] string ownerKind,
            [Values("shroom", "fnf", "earth", "ice", "air", "necro", "commander", "subpet", "ordinary")] string summonKind)
        {
            LootPlayer human = Inert<LootPlayer>(); human.Level = 10; human.Realm = eRealm.Hibernia;
            human.ObjectState = GameObject.eObjectState.Active;
            GameLiving owner = human;
            if (ownerKind != "player")
            {
                LootBot bot = Bot(ownerKind == "companion");
                bot.ObjectState = GameObject.eObjectState.Active;
                if (ownerKind == "companion") Property(bot, nameof(GameBot.Owner), human);
                owner = bot;
            }
            GameLiving credited = ownerKind == "persistent" ? owner : human;
            // Real class-specific brains, including the extra commander ownership
            // link. No substitute reward algorithm or fake generated items.
            GameLiving parent = owner;
            if (summonKind == "subpet")
            {
                LootMob commander = Mob();
                Field(typeof(GameNPC), commander, "m_ownBrain", new CommanderBrain(owner) { Body = commander });
                parent = commander;
            }
            ControlledMobBrain brain = summonKind switch
            {
                "shroom" => new TurretBrain(parent),
                "fnf" => new TurretFNFBrain(parent),
                "earth" => new TheurgistEarthPetBrain(parent),
                "ice" => new TheurgistIcePetBrain(parent),
                "air" => new TheurgistAirPetBrain(parent),
                "necro" => new NecromancerPetBrain(parent),
                "commander" => new CommanderBrain(parent),
                "subpet" => new BdCasterBrain(parent),
                _ => new ControlledMobBrain(parent)
            };
            LootMob summon = Mob(); brain.Body = summon;
            Field(typeof(GameNPC), summon, "m_ownBrain", brain);
            Assert.That(brain.GetLivingOwner(), Is.SameAs(credited), "Native damage enrollment resolves the same root before applying group XP credit.");
            LootMob victim = Mob(14);
            Field(typeof(GameLiving), victim, "m_xpGainers", new Dictionary<GameLiving, double> { [summon] = 100 });
            object[] credit = ProcessKillCredit(victim);
            IDictionary awards = (IDictionary)credit[ownerKind == "persistent" ? 3 : 2];
            Assert.That(awards.Count, Is.EqualTo(1));
            Assert.That(CreditField(awards[credited], "Count"), Is.EqualTo(1));
            Assert.That(CreditField(awards[credited], "Damage"), Is.EqualTo(100));
            Assert.That(ROGMobGenerator.ResolveEquipmentLootOwner(summon), Is.SameAs(credited));
        }

        [TestCase(false)] [TestCase(true)]
        public void HumanAndMixedGroupsKeepLootWithoutGrantingHelpersExtraRolls(bool mixed)
        {
            LootPlayer player = Inert<LootPlayer>();
            player.Level = 10;
            player.Realm = eRealm.Hibernia;
            Group group = new(player);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            GameLiving second = mixed ? Bot() : Inert<LootPlayer>();
            second.Level = 10;
            second.Realm = eRealm.Hibernia;
            foreach (GameLiving member in new GameLiving[] { player, second, Bot(true) })
            {
                members.Add(member);
                member.Group = group;
            }
            FixedDropGenerator generator = new();
            Assert.That(generator.GenerateLoot(Mob(), player).GetLoot(), Has.Length.EqualTo(1));
            Assert.That(generator.Rolls, Is.EqualTo(2));
        }

        [TestCase(1)] [TestCase(10)] [TestCase(50)]
        public void RealGeneratedUniqueEquipmentCanEnterBotInventory(int level)
        {
            LootBot bot = Bot();
            bot.Level = (byte)level;
            DbItemTemplate template = new RealItemGenerator().GenerateLoot(Mob((byte)level), Pet(bot)).GetLoot().Single();
            Assert.That(template, Is.TypeOf<GeneratedUniqueItem>());
            Assert.That(template.AllowAdd, Is.True);
            Assert.That(template.Realm, Is.EqualTo((int)bot.Realm));
            Assert.That(template.Quality, Is.InRange(95, 99));
            WorldInventoryItem drop = new(GameInventoryItem.Create(template));
            drop.Item.IsROG = true;
            Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Success));
            Assert.That(bot.Inventory.AllItems.Single(), Is.SameAs(drop.Item));
            Assert.That(drop.Item.OwnerID, Is.EqualTo(bot.InternalID));
        }

        [Test]
        public void LootedUniqueItemAndEquipmentSlotSurviveActualSqliteSaveReload()
        {
            // A new, isolated database: never the user's world or account data.
            string path = Path.Combine(Path.GetTempPath(), "daoc-loot-test-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            LootServer server = (LootServer)GameServer.Instance;
            try
            {
                LootBot bot = Bot();
                DbItemTemplate template = new RealItemGenerator().GenerateLoot(Mob(), bot).GetLoot().Single();
                WorldInventoryItem drop = new(GameInventoryItem.Create(template));
                drop.Item.IsROG = true;
                Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Success));
                int slot = drop.Item.SlotPosition;
                var database = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=False;");
                database.RegisterDataObject(typeof(DbItemTemplate));
                database.RegisterDataObject(typeof(DbItemUnique));
                database.RegisterDataObject(typeof(DbInventoryItem));
                server.TestDatabase = database;
                Assert.That(bot.Inventory.SaveIntoDatabase(bot.InternalID), Is.True);
                var rows = database.SelectAllObjects<DbInventoryItem>();
                Assert.That(rows, Has.Count.EqualTo(1));
                Assert.That(rows[0].OwnerID, Is.EqualTo(bot.InternalID));
                Assert.That(rows[0].UTemplate_Id, Is.EqualTo(template.Id_nb));
                Assert.That(rows[0].Name, Is.EqualTo(template.Name));
                Assert.That(rows[0].Quality, Is.EqualTo(template.Quality));
                Assert.That(rows[0].SlotPosition, Is.EqualTo(slot));
                BotInventory reloaded = new(bot.InternalID);
                // The staggered-login preloader uses this exact async query;
                // applying its result must retain the real unique item and slot.
                var prepared = reloaded.StartLoadFromDatabaseTask(bot.InternalID).GetAwaiter().GetResult();
                Assert.That(reloaded.LoadInventory(bot.InternalID, prepared), Is.True);
                Assert.That(reloaded.GetItem((eInventorySlot)slot).ObjectId, Is.EqualTo(drop.Item.ObjectId));
            }
            finally
            {
                server.TestDatabase = null;
                // Only the exact GUID-named file created by this test is removed.
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void BulkInventoryPersistenceKeepsEveryOwnerSlotAndDurableDeletion()
        {
            string path = Path.Combine(Path.GetTempPath(), "daoc-bulk-inventory-test-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            LootServer server = (LootServer)GameServer.Instance;
            try
            {
                LootBot first = Bot();
                LootBot second = Bot();
                foreach (LootBot bot in new[] { first, second })
                {
                    DbItemTemplate template = new RealItemGenerator().GenerateLoot(Mob(), bot).GetLoot().Single();
                    WorldInventoryItem drop = new(GameInventoryItem.Create(template));
                    drop.Item.IsROG = true;
                    Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Success));
                }
                var expectedSlots = new Dictionary<string, int>
                {
                    [first.InternalID] = first.Inventory.AllItems.Single().SlotPosition,
                    [second.InternalID] = second.Inventory.AllItems.Single().SlotPosition,
                };

                var database = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=False;");
                database.RegisterDataObject(typeof(DbItemTemplate));
                database.RegisterDataObject(typeof(DbItemUnique));
                database.RegisterDataObject(typeof(DbInventoryItem));
                server.TestDatabase = database;

                Assert.That(BotInventory.SaveManyIntoDatabase(new[]
                {
                    ((BotInventory)first.Inventory, first.InternalID),
                    ((BotInventory)second.Inventory, second.InternalID),
                }), Is.True);
                DbInventoryItem[] saved = database.SelectAllObjects<DbInventoryItem>().ToArray();
                Assert.That(saved.Select(item => item.OwnerID), Is.EquivalentTo(new[] { first.InternalID, second.InternalID }));
                Assert.That(saved.All(item => item.SlotPosition == expectedSlots[item.OwnerID]), Is.True);

                DbInventoryItem soldOrVendored = first.Inventory.AllItems.Single();
                Assert.That(first.Inventory.RemoveItem(soldOrVendored), Is.True);
                Assert.That(BotInventory.SaveManyIntoDatabase(new[]
                {
                    ((BotInventory)first.Inventory, first.InternalID),
                    ((BotInventory)second.Inventory, second.InternalID),
                }), Is.True);
                saved = database.SelectAllObjects<DbInventoryItem>().ToArray();
                Assert.That(saved, Has.Length.EqualTo(1));
                Assert.That(saved[0].OwnerID, Is.EqualTo(second.InternalID));
            }
            finally
            {
                server.TestDatabase = null;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private sealed class InterleavedInventoryDatabase : SqliteObjectDatabase
        {
            public Action DuringInventoryInsert;
            public InterleavedInventoryDatabase(string path) : base($"Data Source={path};Version=3;Pooling=False;") { }
            protected override IEnumerable<bool> AddObjectImpl(DataTableHandler tableHandler, IEnumerable<DataObject> dataObjects)
            {
                var rows = dataObjects.ToArray();
                if (rows.Any(row => row is DbInventoryItem) && DuringInventoryInsert is { } action)
                {
                    DuringInventoryInsert = null;
                    Assert.That(System.Threading.Tasks.Task.Run(action).Wait(TimeSpan.FromSeconds(3)), Is.True,
                        "Actor inventory changes must not wait for the database writer");
                }
                return base.AddObjectImpl(tableHandler, rows).ToArray();
            }
        }

        [TestCase(true)] [TestCase(false)]
        public void ConcurrentRemovalOrEquipmentMoveCannotEraseSaveOwnerOrLoseLaterMutation(bool remove)
        {
            string path = Path.Combine(Path.GetTempPath(), "daoc-inventory-race-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            LootServer server = (LootServer)GameServer.Instance;
            try
            {
                var database = new InterleavedInventoryDatabase(path);
                database.RegisterDataObject(typeof(DbItemTemplate));
                database.RegisterDataObject(typeof(DbItemUnique));
                database.RegisterDataObject(typeof(DbInventoryItem));
                server.TestDatabase = database;
                var inventory = new BotInventory("offlinebot:race-test");
                var item = GameInventoryItem.Create(new DbItemUnique(Pants()));
                Assert.That(inventory.AddItem(eInventorySlot.FirstBackpack, item), Is.True);
                database.DuringInventoryInsert = () =>
                {
                    Assert.That(inventory.RemoveItem(item), Is.True);
                    if (!remove) Assert.That(inventory.AddItem(eInventorySlot.LegsArmor, item), Is.True);
                };
                Assert.That(inventory.SaveIntoDatabase("offlinebot:race-test"), Is.True);
                var saved = database.SelectAllObjects<DbInventoryItem>().Single();
                Assert.That(saved.OwnerID, Is.EqualTo("offlinebot:race-test"));
                Assert.That(saved.ObjectId, Is.EqualTo(item.ObjectId));
                Assert.That(saved.SlotPosition, Is.EqualTo((int)eInventorySlot.FirstBackpack));
                Assert.That(inventory.SaveIntoDatabase("offlinebot:race-test"), Is.True);
                var final = database.SelectAllObjects<DbInventoryItem>();
                if (remove) Assert.That(final, Is.Empty, "Concurrent sale/removal must be deleted on the next flush");
                else
                {
                    Assert.That(final, Has.Count.EqualTo(1));
                    Assert.That(final.Single().SlotPosition, Is.EqualTo((int)eInventorySlot.LegsArmor));
                    Assert.That(final.Single().Name, Is.EqualTo(item.Name));
                }
            }
            finally { server.TestDatabase = null; if (File.Exists(path)) File.Delete(path); }
        }

        [Test]
        public void FailedUniqueDefinitionCannotLeaveAnOrphanInventoryRow()
        {
            string path = Path.Combine(Path.GetTempPath(), "daoc-definition-guard-" + Guid.NewGuid().ToString("N") + ".sqlite3");
            LootServer server = (LootServer)GameServer.Instance;
            try
            {
                var database = new SqliteObjectDatabase($"Data Source={path};Version=3;Pooling=False;");
                database.RegisterDataObject(typeof(DbItemTemplate));
                database.RegisterDataObject(typeof(DbItemUnique));
                database.RegisterDataObject(typeof(DbInventoryItem));
                server.TestDatabase = database;
                var template = new DbItemUnique(Pants()) { AllowAdd = false };
                var inventory = new BotInventory("offlinebot:definition-test");
                var item = GameInventoryItem.Create(template);
                Assert.That(inventory.AddItem(eInventorySlot.FirstBackpack, item), Is.True);
                Assert.That(inventory.SaveIntoDatabase("offlinebot:definition-test"), Is.False);
                Assert.That(database.SelectAllObjects<DbInventoryItem>(), Is.Empty);
                Assert.That(inventory.AllItems, Does.Contain(item));
                template.AllowAdd = true;
                Assert.That(inventory.SaveIntoDatabase("offlinebot:definition-test"), Is.True);
                Assert.That(database.SelectAllObjects<DbInventoryItem>().Single().Name, Is.EqualTo(template.Name));
            }
            finally
            {
                server.TestDatabase = null;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void BotGroupAutoPickupTransfersExactlyOneItemToExactlyOneRealBot()
        {
            LootBot first = Bot();
            LootBot second = Bot();
            LootBot helper = Bot(true);
            Group group = new(first);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(group);
            foreach (LootBot member in new[] { first, second, helper })
            {
                member.ObjectState = GameObject.eObjectState.Active;
                member.Group = group;
                members.Add(member);
            }
            WorldInventoryItem drop = new(GameInventoryItem.Create(Pants()));
            Assert.That(drop.TryAutoPickUp(group), Is.EqualTo(TryPickUpResult.Success));
            Assert.That(first.Inventory.AllItems.Concat(second.Inventory.AllItems).ToArray(), Has.Length.EqualTo(1));
            Assert.That(helper.Inventory.AllItems, Is.Empty);
        }

        [Test]
        public void RealmExpeditionSharesRealItemsAcrossPartiesWithoutAwardingCompanions()
        {
            LootBot[] bots = Enumerable.Range(0, 16).Select(_ => Bot()).ToArray();
            LootBot helper = Bot(true);
            try
            {
                foreach (LootBot bot in bots)
                {
                    bot.ObjectState = GameObject.eObjectState.Active;
                    AutonomousBotRegistry.Register(bot);
                }
                helper.ObjectState = GameObject.eObjectState.Active;
                var owner = new RealmRaidLootOwner(bots.Concat(new[] { bots[0], helper }).ToArray(), new());
                for (int i = 0; i < 32; i++)
                {
                    var drop = new WorldInventoryItem(GameInventoryItem.Create(Pants()));
                    Assert.That(drop.TryAutoPickUp(owner), Is.EqualTo(TryPickUpResult.Success));
                }
                Assert.That(bots.Select(b => b.Inventory.AllItems.Count), Is.All.EqualTo(2));
                Assert.That(helper.Inventory.AllItems, Is.Empty);
            }
            finally
            {
                foreach (LootBot bot in bots) AutonomousBotRegistry.Unregister(bot);
            }
        }

        [TestCase(1L)] [TestCase(197L)] [TestCase(1000000L)] [TestCase(long.MaxValue)]
        public void RealmExpeditionCurrencyConservesEveryCopper(long copper)
        {
            var records = Enumerable.Range(0, 240).Select(_ => new OfflineWorldBotRecord()).ToArray();
            MethodInfo allocate = typeof(RealmRaidLootOwner).GetMethod("TryAllocateMoney", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(allocate.Invoke(null, new object[] { records, copper }), Is.True);
            Assert.That(records.Sum(r => (decimal)r.MoneyCopper), Is.EqualTo((decimal)copper));
            Assert.That(records.Max(r => r.MoneyCopper) - records.Min(r => r.MoneyCopper), Is.LessThanOrEqualTo(1));
            var paid = records.Select(r => r.MoneyCopper).ToArray();
            Assert.That(allocate.Invoke(null, new object[] { records, 0L }), Is.True);
            Assert.That(records.Select(r => r.MoneyCopper), Is.EqualTo(paid), "A depleted bag cannot pay twice.");
        }

        [Test]
        public void RealmExpeditionCurrencyRejectsOverflowWithoutPartialPayment()
        {
            var records = new[] { new OfflineWorldBotRecord { MoneyCopper = 7 }, new OfflineWorldBotRecord { MoneyCopper = long.MaxValue } };
            MethodInfo allocate = typeof(RealmRaidLootOwner).GetMethod("TryAllocateMoney", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(allocate.Invoke(null, new object[] { records, 100L }), Is.False);
            Assert.That(records[0].MoneyCopper, Is.EqualTo(7));
            Assert.That(records[1].MoneyCopper, Is.EqualTo(long.MaxValue));
            Assert.That(allocate.Invoke(null, new object[] { new[] { records[0], records[0] }, 10L }), Is.False);
            Assert.That(records[0].MoneyCopper, Is.EqualTo(7));
        }

        [Test]
        public void ActualPickupStoresThenEquipsUsableUpgradeAndRetainsReplacedItem()
        {
            LootBot bot = Bot();
            DbInventoryItem old = GameInventoryItem.Create(Pants(1));
            Assert.That(bot.Inventory.AddItem(eInventorySlot.LegsArmor, old), Is.True);
            DbItemTemplate template = new FixedDropGenerator().GenerateLoot(Mob(), bot).GetLoot().Single();
            WorldInventoryItem drop = new(GameInventoryItem.Create(template));
            Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Success));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.LegsArmor), Is.SameAs(drop.Item));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.SameAs(old));
            Assert.That(drop.Item.OwnerID, Is.EqualTo(bot.InternalID));
            Assert.That(bot.BonusRefreshes, Is.EqualTo(1));
            Assert.That(bot.Inventory.AllItems.Count, Is.EqualTo(2));
        }

        [TestCase("level")] [TestCase("class")] [TestCase("realm")] [TestCase("armor")]
        public void UnusableLootStaysInBackpackInsteadOfBeingDiscarded(string reason)
        {
            LootBot bot = Bot();
            DbItemTemplate template = Pants();
            if (reason == "level") template.LevelRequirement = 50;
            if (reason == "class") template.AllowedClasses = ((int)eCharacterClass.Hero).ToString();
            if (reason == "realm") template.Realm = (int)eRealm.Albion;
            if (reason == "armor") template.Object_Type = (int)eObjectType.Scale;
            WorldInventoryItem drop = new(GameInventoryItem.Create(template));
            Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Success));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.FirstBackpack), Is.SameAs(drop.Item));
            Assert.That(bot.Inventory.GetItem(eInventorySlot.LegsArmor), Is.Null);
            Assert.That(bot.BonusRefreshes, Is.Zero);
        }

        [Test]
        public void FullFortySlotBackpackRejectsDropWithoutDeletingAnything()
        {
            LootBot bot = Bot();
            for (int i = 0; i < 40; i++)
                Assert.That(bot.Inventory.AddItem(eInventorySlot.FirstEmptyBackpack, GameInventoryItem.Create(Pants())), Is.True);
            WorldInventoryItem drop = new(GameInventoryItem.Create(Pants(10)));
            Assert.That(drop.TryAutoPickUp(bot), Is.EqualTo(TryPickUpResult.Blocked));
            Assert.That(bot.Inventory.AllItems.Count, Is.EqualTo(40));
            Assert.That(bot.Inventory.AllItems.Contains(drop.Item), Is.False);
            Assert.That(drop.Item.OwnerID, Is.Null);
        }
    }
}
