using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Reflection;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_ParallelCasterPetSpells
    {
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass TestClass;
            public override ICharacterClass CharacterClass => TestClass;
            public override byte Level => 50;
        }
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private GameServer _previousServer;
        private PetTestLanguageScope _language;
        [SetUp]
        public void Setup()
        {
            _language = new PetTestLanguageScope();
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown]
        public void Teardown()
        {
            GameServer.LoadTestDouble(_previousServer);
            _language.Dispose();
        }

        [TestCase(eCharacterClass.Cabalist, false)]
        [TestCase(eCharacterClass.Cabalist, true)]
        [TestCase(eCharacterClass.Enchanter, false)]
        [TestCase(eCharacterClass.Enchanter, true)]
        public void EveryShieldRankIsRejectedAndDamageSpellRemainsSelectable(eCharacterClass characterClass, bool companion)
        {
            Bot bot = Create(characterClass);
            typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, companion);
            Spell damage = new(new DbSpell { Type = "DirectDamage", Target = "Enemy", CastTime = 2.5, Damage = 20, Range = 1500 }, 1);
            var known = new List<Spell> { damage };
            // Reject by spell type, not names or today's database IDs.
            for (int rank = 1; rank <= 50; rank++)
            {
                Spell shield = Shield(rank, rank % 2 == 0);
                known.Add(shield);
                Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(shield, true, bot), Is.False);
                Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(shield, false, bot), Is.False);
                Assert.That(bot.CastSpell(shield, null), Is.False, "Final cast guard must not start a channel or spend power");
            }
            bot.Spells = known;
            bot.SortSpells();
            Assert.That(bot.MiscSpells, Is.Null.Or.Empty);
            Assert.That(bot.HarmfulSpells, Does.Contain(damage));
        }

        [TestCase(eCharacterClass.Spiritmaster)]
        [TestCase(eCharacterClass.Bonedancer)]
        public void OtherPetClassesKeepExistingSpellPolicy(eCharacterClass characterClass)
        {
            Bot bot = Create(characterClass);
            Spell shield = Shield(1, true);
            Assert.That(AutonomousPetSupport.IsDisabledBotDamageShield(bot, shield), Is.False);
            Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(shield, true, bot), Is.True);
        }

        [TestCase(eCharacterClass.Cabalist)]
        [TestCase(eCharacterClass.Enchanter)]
        public void OrdinaryPetBuffsStillWork(eCharacterClass characterClass)
        {
            Spell stats = new(new DbSpell { Type = "StrengthBuff", Target = "Pet", Duration = 1200, Value = 10 }, 1);
            Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(stats, false, Create(characterClass)), Is.True);
        }

        [Test]
        public void ActualPlayerHotbarPolicyIsUnchanged()
        {
            GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
            Assert.That(AutonomousPetSupport.ShouldMaintainRoutinePetBuff(Shield(1, true), true, player), Is.True);
        }

        private static Spell Shield(int rank, bool focus) => new(new DbSpell
        {
            SpellID = 900000 + rank, Type = "DamageShield", Target = "Pet", Duration = 8,
            IsFocus = focus, Pulse = focus ? 1 : 0, Damage = 5, CastTime = 2.5
        }, rank);
        private static Bot Create(eCharacterClass characterClass)
        {
            Bot bot = (Bot)RuntimeHelpers.GetUninitializedObject(typeof(Bot));
            bot.TestClass = characterClass switch
            {
                eCharacterClass.Cabalist => new ClassCabalist(),
                eCharacterClass.Enchanter => new ClassEnchanter(),
                eCharacterClass.Spiritmaster => new ClassSpiritmaster(),
                _ => new ClassBonedancer()
            };
            return bot;
        }
    }
}
