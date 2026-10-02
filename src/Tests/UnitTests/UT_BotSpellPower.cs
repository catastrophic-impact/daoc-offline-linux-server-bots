using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Effects;
using DOL.GS.PlayerClass;
using DOL.GS.PropertyCalc;
using DOL.GS.Spells;
using DOL.GS.Styles;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public sealed class UT_BotSpellPower
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase Empty = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        private readonly List<GameLiving> _actors = new();
        private GameServer _previous;
        private PetTestLanguageScope _language;
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => Empty;
            protected override GS.ServerRules.IServerRules ServerRulesImpl => new GS.ServerRules.NormalServerRules();
        }
        private sealed class Bot : GameBot
        {
            private Bot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass Class;
            public int Stat = 100;
            public int Focus;
            public override byte Level { get; set; }
            public override ICharacterClass CharacterClass => Class;
            public override int Mana { get; set; }
            public override int Endurance { get; set; }
            public override double Effectiveness => 1;
            public override SpellLine GetSpellLine(string key) => null;
            public override bool IsAlive => true;
            public override bool IsMoving => false;
            public override bool IsCrowdControlled => false;
            public override bool InCombat => false;
            public override bool IsBeingInterrupted => false;
            public override int GetBaseStat(eStat stat) => 100;
            public override int GetModifiedSpecLevel(string keyName) => Level;
            public override int GetModified(eProperty p) => p == eProperty.MaxMana
                ? new MaxManaCalculator().CalcValue(this, p)
                : p == (eProperty)Class.ManaStat ? Stat
                : p == eProperty.MaxConcentration ? new MaxConcentrationCalculator().CalcValue(this, p)
                : p is eProperty.SpellRange or eProperty.FatigueConsumption ? 100
                : p >= eProperty.Focus_Darkness && p <= eProperty.Focus_Summoning ? Focus : 0;
        }
        private sealed class Player : GamePlayer
        {
            private Player() : base(null, null) { }
            public ICharacterClass Class;
            public int Stat = 100;
            public int Focus;
            public override byte Level { get; set; }
            public override ICharacterClass CharacterClass => Class;
            public override int Endurance { get; set; }
            public override double Effectiveness => 1;
            public override SpellLine GetSpellLine(string key) => null;
            public override int GetBaseStat(eStat stat) => 100;
            public override int GetModifiedSpecLevel(string keyName) => Level;
            public override int GetModified(eProperty p) => p == eProperty.MaxMana
                ? new MaxManaCalculator().CalcValue(this, p)
                : p == eProperty.MaxConcentration ? new MaxConcentrationCalculator().CalcValue(this, p)
                : p == eProperty.FatigueConsumption ? 100
                : p == (eProperty)Class.ManaStat ? Stat
                : p >= eProperty.Focus_Darkness && p <= eProperty.Focus_Summoning ? Focus : 0;
        }
        private sealed class Brain : BotBrain
        {
            public int DefensiveTargetChecks;
            protected override GameLiving FindTargetForDefensiveSpell(Spell spell)
            {
                DefensiveTargetChecks++;
                return base.FindTargetForDefensiveSpell(spell);
            }
            public int InstantOffense;
            public int InstantDefense;
            public int Normal;
            public bool DefensiveAllowed(Spell spell) => CanCastDefensiveSpell(spell);
            public bool InstantDefensiveAllowed(Spell spell) => base.CheckInstantDefensiveSpells(spell);
            protected override bool CheckInstantOffensiveSpells(Spell spell) { InstantOffense++; return true; }
            protected override bool CheckInstantDefensiveSpells(Spell spell) { InstantDefense++; return true; }
            protected override bool CheckOffensiveSpells(Spell spell) { Normal++; return true; }
        }
        private sealed class DebitOnlyDamage : DirectDamageSpellHandler
        {
            public DebitOnlyDamage(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) { }
            public override bool StartSpell(GameLiving target) => true;
            public override int CalculateEnduranceCost() => 0;
            public override bool CheckDuringCast(GameLiving target) => true;
        }

        private sealed class RestBot : GameBot
        {
            private RestBot() : base((OfflineWorldBotRecord)null) { }
            public ICharacterClass Class;
            public bool Moving;
            public bool Attacking;
            public bool Casting;
            public int TestX;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public IControlledBrain Pet;
            public override ICharacterClass CharacterClass => Class;
            public override byte Level => 7;
            public override bool IsAlive => Health > 0;
            public override bool IsCrowdControlled => false;
            public override bool IsMoving => Moving;
            public override bool IsAttacking => Attacking;
            public override bool IsCasting => Casting;
            public override IControlledBrain ControlledBrain => Pet;
            public override int MaxHealth => 100;
            public override int MaxMana => 100;
            public override int MaxEndurance => 100;
            public override int Health { get; set; }
            public override int Mana { get; set; }
            public override int Endurance { get; set; }
            public override int GetModified(eProperty p) => p == eProperty.PowerRegenerationAmount
                ? new PowerRegenerationAmountCalculator().CalcValue(this, p) : p == eProperty.HealthRegenerationAmount ? 3 : 0;
            public int PowerTick() => PowerRegenerationTimerCallback(null);
            public int HealthTick() => HealthRegenerationTimerCallback(null);
            public int EnduranceTick() => EnduranceRegenerationTimerCallback(null);
        }

        [TestCase(typeof(ClassSpiritmaster))] [TestCase(typeof(ClassBonedancer))]
        [TestCase(typeof(ClassRunemaster))] [TestCase(typeof(ClassCabalist))]
        [TestCase(typeof(ClassWizard))] [TestCase(typeof(ClassEnchanter))]
        [TestCase(typeof(ClassEldritch))] [TestCase(typeof(ClassAnimist))]
        [TestCase(typeof(ClassSorcerer))] [TestCase(typeof(ClassTheurgist))]
        [TestCase(typeof(ClassMentalist))] [TestCase(typeof(ClassNecromancer))]
        public void BotLowPowerDoesNotHalveRegen_AndFastRestUsesSameRateAcrossRealms(Type type)
        {
            bool previousPenalty = GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_HALVED_BELOW_50_PERCENT;
            double previousModifier = GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_MODIFIER;
            try
            {
                GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_HALVED_BELOW_50_PERCENT = true;
                GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_MODIFIER = 1;
                foreach (bool temporary in new[] { false, true })
                {
                    var bot = NewRestBot(type, temporary);
                    bot.Mana = 20;
                    int low = new PowerRegenerationAmountCalculator().CalcValue(bot, eProperty.PowerRegenerationAmount);
                    bot.Mana = 75;
                    Assert.That(new PowerRegenerationAmountCalculator().CalcValue(bot, eProperty.PowerRegenerationAmount), Is.EqualTo(low));
                    Assert.That(low, Is.EqualTo(3));
                    foreach (int start in new[] { 20, 75 })
                    {
                        bot.Mana = start; SetRecovery(bot, true);
                        Assert.That(bot.PowerTick(), Is.EqualTo(1000));
                        Assert.That(bot.Mana, Is.EqualTo(start + 10));
                    }
                    bot.Health = 30; bot.Endurance = 20;
                    bot.HealthTick(); bot.EnduranceTick();
                    Assert.That(bot.Health, Is.EqualTo(40));
                    Assert.That(bot.Endurance, Is.EqualTo(30));
                }
                Player human = Actor<Player>(); human.Class = (ICharacterClass)Activator.CreateInstance(type); human.Level = 7; human.Stat = 100;
                Field(typeof(GameLiving), human, "m_mana", 10);
                int humanLow = new PowerRegenerationAmountCalculator().CalcValue(human, eProperty.PowerRegenerationAmount);
                Field(typeof(GameLiving), human, "m_mana", human.MaxMana);
                Assert.That(new PowerRegenerationAmountCalculator().CalcValue(human, eProperty.PowerRegenerationAmount), Is.GreaterThan(humanLow));
            }
            finally
            {
                GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_HALVED_BELOW_50_PERCENT = previousPenalty;
                GS.ServerProperties.Properties.MANA_REGEN_AMOUNT_MODIFIER = previousModifier;
            }
        }

        private RestBot NewRestBot(Type type, bool temporary = false)
        {
            var bot = Actor<RestBot>(); bot.Class = (ICharacterClass)Activator.CreateInstance(type);
            bot.Health = 100; bot.Mana = 20; bot.Endurance = 100;
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Field(typeof(GameNPC), bot, "m_ownBrain", new Brain { Body = bot });
            return bot;
        }

        [TestCase("moving")] [TestCase("attack")] [TestCase("cast")] [TestCase("aggro")] [TestCase("pet")]
        public void CombatMovementAndPetCombatCannotReceiveFastRestTicks(string blocker)
        {
            var bot = NewRestBot(typeof(ClassSpiritmaster)); SetRecovery(bot, true);
            if (blocker == "moving") bot.Moving = true;
            if (blocker == "attack") bot.Attacking = true;
            if (blocker == "cast") bot.Casting = true;
            if (blocker == "aggro") ((Brain)bot.Brain).AddToAggroList(NewRestBot(typeof(ClassWizard)), 10);
            if (blocker == "pet")
            {
                var pet = NewRestBot(typeof(ClassSpiritmaster)); pet.Attacking = true;
                bot.Pet = new ControlledMobBrain(bot) { Body = pet };
            }
            Assert.That(bot.IsEnhancedResting, Is.False);
            bot.PowerTick();
            Assert.That(bot.Mana, Is.LessThan(30), "No 10% boost while active combat or movement owns the bot.");
        }

        [Test]
        public void RecoveringCasterDefersOptionalUpkeepButTravelHealerAndActiveCombatDoNot()
        {
            var caster = NewRestBot(typeof(ClassSpiritmaster));
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(caster), Is.False, "A stationary non-resting caster still needs its pet and buffs");
            SetRecovery(caster, true);
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(caster), Is.True);
            caster.Moving = true;
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(caster), Is.False, "Do not suppress travel upkeep");
            caster.Moving = false;
            SetRecovery(caster, false);
            caster.Mana = 100;
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(caster), Is.False);
            caster.Mana = 20; caster.Attacking = true;
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(caster), Is.False);
            Assert.That(BotRestRecovery.DeferOptionalCasterUpkeep(NewRestBot(typeof(ClassHealer))), Is.False);
        }

        [TestCase("attackPve")] [TestCase("attackPvp")]
        [TestCase("hitPve")] [TestCase("hitPvp")]
        public void PetCombatTimestampImmediatelyWakesOwnerWithoutDiscardingAggro(string combatEvent)
        {
            var bot = NewRestBot(typeof(ClassSpiritmaster));
            var pet = NewRestBot(typeof(ClassSpiritmaster));
            Field(typeof(GameNPC), pet, "m_ownBrain", new ControlledMobBrain(bot) { Body = pet });
            var brain = (Brain)bot.Brain;
            brain.AddToAggroList(NewRestBot(typeof(ClassWizard)), 10);
            brain.NextThinkTick = GameLoop.GameLoopTime + 1800;
            SetRecovery(bot, true);
            long tick = Math.Max(1, GameLoop.GameLoopTime);
            switch (combatEvent)
            {
                case "attackPve": pet.LastAttackTickPvE = tick; break;
                case "attackPvp": pet.LastAttackTickPvP = tick; break;
                case "hitPve": pet.LastAttackedByEnemyTickPvE = tick; break;
                case "hitPvp": pet.LastAttackedByEnemyTickPvP = tick; break;
            }
            Assert.That(bot.IsRecoveryResting, Is.False);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(brain.NextThinkTick, Is.EqualTo(GameLoop.GameLoopTime));
            Assert.That(brain.HasAggro, Is.True);
            Assert.That(Math.Max(bot.LastAttackTick, bot.LastAttackedByEnemyTick), Is.EqualTo(tick));
        }

        [TestCase(500, true)] [TestCase(2001, false)]
        public void OnlyLocalGroupCombatBlocksRest(int distance, bool expected)
        {
            var bot = NewRestBot(typeof(ClassSpiritmaster));
            var member = NewRestBot(typeof(ClassWizard));
            member.Attacking = true; member.TestX = distance;
            Group group = new(bot);
            var members = (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
            members.Add(bot); members.Add(member);
            bot.Group = group; member.Group = group;
            Assert.That(BotRestRecovery.BlocksRest(bot), Is.EqualTo(expected));
            SetRecovery(bot, true);
            Assert.That(bot.WakeRecoveryRestIfCombatBlocked(), Is.EqualTo(expected));
            Assert.That(bot.IsRecoveryResting, Is.EqualTo(!expected));
        }

        [Test]
        public void EnteringRestReschedulesOldCombatTimersOnce()
        {
            var bot = NewRestBot(typeof(ClassSpiritmaster));
            bot.Health = 30; bot.Endurance = 20; SetRecovery(bot, true);
            var timers = new[] { new ECSGameTimer(bot, _ => 0), new ECSGameTimer(bot, _ => 0), new ECSGameTimer(bot, _ => 0) };
            string[] names = { "m_healthRegenerationTimer", "m_powerRegenerationTimer", "m_enduRegenerationTimer" };
            try
            {
                for (int i = 0; i < timers.Length; i++) { Field(typeof(GameLiving), bot, names[i], timers[i]); timers[i].Start(14000); }
                typeof(GameBot).GetMethod("StartEnhancedRecoveryTimers", Hidden).Invoke(bot, null);
                foreach (var timer in timers) Assert.That(timer.TimeUntilElapsed, Is.LessThanOrEqualTo(1));
            }
            finally { foreach (var timer in timers) timer.Stop(); }
        }

        [SetUp] public void Setup()
        {
            _previous = GameServer.Instance;
            _language = new PetTestLanguageScope();
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void Cleanup()
        {
            foreach (GameLiving actor in _actors)
            {
                ServiceObjectStore.Remove(actor.castingComponent);
                ServiceObjectStore.Remove(actor.effectListComponent);
            }
            _actors.Clear();
            GameServer.LoadTestDouble(_previous);
            _language.Dispose();
        }

        [TestCase(typeof(ClassBonedancer))] [TestCase(typeof(ClassSpiritmaster))]
        [TestCase(typeof(ClassRunemaster))] [TestCase(typeof(ClassWizard))]
        [TestCase(typeof(ClassCabalist))] [TestCase(typeof(ClassSorcerer))]
        [TestCase(typeof(ClassTheurgist))] [TestCase(typeof(ClassNecromancer))]
        [TestCase(typeof(ClassEnchanter))] [TestCase(typeof(ClassEldritch))]
        [TestCase(typeof(ClassMentalist))] [TestCase(typeof(ClassAnimist))]
        [TestCase(typeof(ClassCleric))] [TestCase(typeof(ClassDruid))] [TestCase(typeof(ClassHealer))]
        [TestCase(typeof(ClassValewalker))]
        public void PoolsAndNativeCostsMatchPlayerForEveryCasterAndBotKind(Type cls)
        {
            foreach (byte level in new byte[] { 1, 10, 50 })
            foreach (bool temporary in new[] { false, true })
            {
                Bot bot = MakeBot(cls, level, temporary);
                Player player = Actor<Player>(); player.Class = bot.Class; player.Level = level;
                bot.Stat = player.Stat = 140;
                bot.Focus = player.Focus = level;
                bot.ItemBonus[eProperty.MaxMana] = player.ItemBonus[eProperty.MaxMana] = 20;
                bot.ItemBonus[eProperty.PowerPool] = player.ItemBonus[eProperty.PowerPool] = 30;
                bot.AbilityBonus[eProperty.PowerPool] = player.AbilityBonus[eProperty.PowerPool] = 5;
                Assert.That(bot.MaxMana, Is.EqualTo(player.MaxMana));
                Assert.That(bot.MaxMana, Is.GreaterThan(bot.CalculateMaxMana(level, 100)));
                foreach (int power in new[] { 2, 30, -20 })
                {
                    Spell spell = S(power, 2, level);
                    var line = new SpellLine("test", "test", "Darkness", true);
                    int nativePlayerCost = new SpellHandler(player, spell, line).PowerCost(player);
                    int expected = nativePlayerCost;
                    Assert.That(new SpellHandler(bot, spell, line).PowerCost(bot), Is.EqualTo(expected));
                    Assert.That(BotSpellPower.Cost(bot, spell, line), Is.EqualTo(expected));
                    bot.Mana = 1;
                    Assert.That(BotSpellPower.Cost(bot, spell, line), Is.EqualTo(expected), "full pool, not remaining power");
                }
            }
        }

        [Test] public void LowRankNukeCostsItsNativeAbsolutePower()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 1);
            bot.Mana = bot.MaxMana;
            Spell spell = S(2, 2);
            int cost = new SpellHandler(bot, spell, new SpellLine("test", "test", "", true)).PowerCost(bot);
            Assert.That(bot.MaxMana, Is.EqualTo(55));
            Assert.That(cost, Is.EqualTo(2));
            Assert.That(bot.Mana - cost, Is.EqualTo(53));
        }

        [TestCase(typeof(ClassShaman))] [TestCase(typeof(ClassWarden))]
        [TestCase(typeof(ClassSpiritmaster))] [TestCase(typeof(ClassCabalist))]
        public void ConcentrationCapacityMatchesPlayerRatherThanNpcInfinity(Type cls)
        {
            foreach(byte level in new byte[]{1,10,50})
            {
                Bot bot=MakeBot(cls,level);
                Player player=Actor<Player>(); player.Class=bot.Class; player.Level=level;
                bot.Stat=player.Stat=90;
                Assert.That(bot.MaxConcentration,Is.EqualTo(player.MaxConcentration));
                Assert.That(bot.MaxConcentration,Is.LessThan(1000000));
                Assert.That(bot.Concentration,Is.EqualTo(bot.MaxConcentration));
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void BotConcentrationSpellCountIsUncappedButHumanLimitRemains(bool temporary)
        {
            Bot bot = MakeBot(typeof(ClassShaman), 50, temporary);
            Player human = Actor<Player>(); human.Class = bot.Class; human.Level = 50;
            Spell buff = new(new DbSpell { SpellID = 99881, Name = "Concentration test",
                Type = "StrengthBuff", Target = "Realm", Concentration = 1, Value = 10 }, 1);
            foreach (GameLiving caster in new GameLiving[] { bot, human })
            {
                var effects = (List<ECSGameSpellEffect>)typeof(EffectListComponent)
                    .GetField("_concentrationEffects", Hidden).GetValue(caster.effectListComponent);
                var handler = new SpellHandler(caster, buff, new SpellLine("test", "test", "", true));
                foreach (int count in new[] { 19, 20, 40 })
                {
                    effects.Clear();
                    // Only count is read by the cap; do not start real effects or timers.
                    effects.AddRange(new ECSGameSpellEffect[count]);
                    Assert.That(handler.CheckConcentrationCost(true), Is.EqualTo(caster is GameBot || count < 20));
                }
                Field(typeof(EffectListComponent), caster.effectListComponent, "_usedConcentration", caster.MaxConcentration);
                Assert.That(handler.CheckConcentrationCost(true), Is.False, "Point budget remains authoritative.");
                effects.Clear();
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ExhaustedConcentrationSkipsAllDefensiveSelectorsAndDoesNotWakeRest(bool temporary)
        {
            Bot bot = MakeBot(typeof(ClassShaman), 50, temporary);
            bot.Mana = bot.MaxMana;
            SetRecovery(bot, true);
            Spell buff = new(new DbSpell { SpellID = 99882, Name = "Concentration test",
                Type = "StrengthBuff", Target = "Realm", Concentration = 1, Power = 1, CastTime = 2, Value = 10 }, 1);
            Brain brain = (Brain)bot.Brain;
            Field(typeof(EffectListComponent), bot.effectListComponent, "_usedConcentration", bot.MaxConcentration);
            var selector = typeof(BotBrain).GetMethod("CheckDefensiveSpells", Hidden, null, new[] { typeof(List<Spell>) }, null);
            for (int tick = 0; tick < 10; tick++)
            {
                Assert.That(bot.CanAffordConcentration(buff), Is.False);
                Assert.That(brain.DefensiveAllowed(buff), Is.False);
                Assert.That(brain.InstantDefensiveAllowed(buff), Is.False);
                Assert.That(selector.Invoke(brain, new object[] { new List<Spell> { buff } }), Is.False);
                Assert.That(bot.CastSpell(buff, new SpellLine("test", "test", "", true)), Is.False);
            }
            Assert.That(brain.DefensiveTargetChecks, Is.Zero);
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.Mana, Is.EqualTo(bot.MaxMana));
            Assert.That(bot.castingComponent.HasPendingSkillRequests, Is.False);
            Field(typeof(EffectListComponent), bot.effectListComponent, "_usedConcentration", 0);
            Assert.That(bot.CanAffordConcentration(buff), Is.True);
            Assert.That(brain.DefensiveAllowed(buff), Is.True);
        }

        [TestCase(30,false)] [TestCase(50,false)] [TestCase(50,true)]
        public void BotStyleEnduranceMatchesNativePlayerDebit(int speed,bool missed)
        {
            Bot bot=MakeBot(typeof(ClassChampion),10);
            Player player=Actor<Player>(); player.Class=bot.Class; player.Level=10;
            var weapon=new DbInventoryItem{SPD_ABS=speed};
            var style=new Style(new DbStyle{EnduranceCost=12},null);
            bot.Endurance=player.Endurance=100;
            Assert.That(StyleProcessor.CheckEnduranceCost(bot,weapon,style),Is.True);
            StyleProcessor.ApplyEnduranceCost(bot,weapon,style,missed);
            StyleProcessor.ApplyEnduranceCost(player,weapon,style,missed);
            Assert.That(bot.Endurance,Is.EqualTo(player.Endurance));
            Assert.That(bot.Endurance,Is.LessThan(100));
            bot.Endurance=0;
            Assert.That(StyleProcessor.CheckEnduranceCost(bot,weapon,style),Is.False);
        }

        [Test] public void RangeOverridePercentageUsesBaseStatPoolNotItemBonuses()
        {
            Bot bot=MakeBot(typeof(ClassWarlock),50);
            Player player=Actor<Player>(); player.Class=bot.Class; player.Level=50;
            bot.ItemBonus[eProperty.PowerPool]=player.ItemBonus[eProperty.PowerPool]=50;
            var spell=S(-20,2,50,"Range","Self");
            var line=new SpellLine("test","test","test",false);
            Assert.That(new RangeSpellHandler(bot,spell,line).PowerCost(bot),
                Is.EqualTo(new RangeSpellHandler(player,spell,line).PowerCost(player)));
        }

        [Test] public void NativeCompletionDebitsOnce_AnotherTickDoesNotDebitAgain()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 1);
            bot.Mana = 55;
            var handler = new DebitOnlyDamage(bot, S(2, 2), new SpellLine("test", "test", "", true));
            handler.Target = bot;
            Field(typeof(SpellHandler), handler, "<CastState>k__BackingField", eCastState.Finished);
            handler.Tick();
            Assert.That(bot.Mana, Is.EqualTo(53));
            handler.Tick();
            Assert.That(bot.Mana, Is.EqualTo(53));
        }

        [Test] public void GenericNpcDispatchAndAffordabilityUseNativeLearnedFocusLine()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 50);
            bot.Focus = 50;
            Spell spell = S(30, 2, 50);
            var learned = new SpellLine("Darkness", "Darkness", "Darkness", true);
            Field(typeof(GameBot), bot, "_powerSpellLines", new Dictionary<int, SpellLine> { [spell.ID] = learned });
            var generic = new SpellLine(GlobalSpellsLines.Mob_Spells, GlobalSpellsLines.Mob_Spells, GlobalSpellsLines.Mob_Spells, true);
            Player player = Actor<Player>(); player.Class = bot.Class; player.Level = 50; player.Focus = 50;
            Assert.That(new SpellHandler(player, spell, learned).PowerCost(player), Is.EqualTo(23), "human focus cost is unchanged");
            int expected = 23;
            Assert.That(bot.PowerCost(spell), Is.EqualTo(expected));
            var handler = new DebitOnlyDamage(bot, spell, generic) { Target = bot };
            Field(typeof(SpellHandler), handler, "<CastState>k__BackingField", eCastState.Finished);
            bot.Mana = 100;
            handler.Tick();
            Assert.That(bot.Mana, Is.EqualTo(100 - expected));
        }

        [Test] public void PendingBotRequestsCannotStackCostsBeforeNativeValidation()
        {
            Bot bot = MakeBot(typeof(ClassBonedancer), 50);
            bot.Mana = 100;
            var line = new SpellLine("test", "test", "", true);
            Assert.That(bot.CastSpell(S(30, 0), line, false), Is.True);
            for (int i = 0; i < 10; i++)
                Assert.That(bot.CastSpell(S(33, 2), line, false), Is.False);
            Assert.That(bot.castingComponent.HasPendingSkillRequests, Is.True);
            Assert.That(bot.Mana, Is.EqualTo(100), "requesting or rejecting a spell does not spend power");
            bot.castingComponent.ClearSpellHandlers();
            Assert.That(bot.CastSpell(S(33, 2), line, false), Is.True, "clearing a request must not latch the bot idle");
        }

        [TestCase(typeof(ClassBonedancer))] [TestCase(typeof(ClassSpiritmaster))]
        [TestCase(typeof(ClassRunemaster))] [TestCase(typeof(ClassWizard))]
        [TestCase(typeof(ClassCabalist))] [TestCase(typeof(ClassSorcerer))]
        [TestCase(typeof(ClassTheurgist))] [TestCase(typeof(ClassNecromancer))]
        [TestCase(typeof(ClassEnchanter))] [TestCase(typeof(ClassEldritch))]
        [TestCase(typeof(ClassMentalist))] [TestCase(typeof(ClassAnimist))]
        public void AttackerCannotOverwriteRunningCastAndCanCastAgainAfterEveryCompletion(Type cls)
        {
            foreach (bool companion in new[] { false, true })
            {
                Bot bot = MakeBot(cls, 50, companion);
                bot.Mana = bot.MaxMana;
                var line = new SpellLine("test", "test", "", true);
                Spell spell = S(30, 3, 50);
                int cost = BotSpellPower.Cost(bot, spell, line);
                for (int cast = 0; cast < 6; cast++)
                {
                    var handler = new DebitOnlyDamage(bot, spell, line) { Target = bot };
                    Field(typeof(CastingComponent), bot.castingComponent, "<SpellHandler>k__BackingField", handler);
                    Field(typeof(SpellHandler), handler, "<CastState>k__BackingField", eCastState.Casting);
                    Field(typeof(SpellHandler), handler, "_castEndTick", long.MaxValue);
                    int before = bot.Mana;
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        Assert.That(bot.CastSpell(spell, line, false), Is.False);
                        Assert.That(bot.castingComponent.RequestCastSpell(spell, line, target: bot, checkLos: false), Is.False);
                        handler.Tick();
                        Assert.That(bot.Mana, Is.EqualTo(before));
                        Assert.That(bot.castingComponent.SpellHandler, Is.SameAs(handler));
                        Assert.That(handler.CastState, Is.EqualTo(eCastState.Casting));
                    }
                    Field(typeof(SpellHandler), handler, "<CastState>k__BackingField", eCastState.Finished);
                    handler.Tick(); handler.Tick();
                    Assert.That(bot.Mana, Is.EqualTo(before - cost));
                    Assert.That(bot.castingComponent.IsCasting, Is.False);
                    Assert.That(bot.CastSpell(spell, line, false), Is.True, "next blast must not be latched out");
                    bot.castingComponent.ClearSpellHandlers();
                }
                Assert.That(bot.Mana, Is.EqualTo(bot.MaxMana - 6 * cost));
            }
        }

        [Test] public void CasterSummonsBuffsAndDotsUseNativePercentageCost_FreeSpellsStayFree()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 50);
            foreach (string type in new[] { "SummonSpiritFighter", "StrengthBuff", "DamageOverTime", "DirectDamage" })
            {
                Assert.That(BotSpellPower.Cost(bot, S(-80, 3, 50, type), null),
                    Is.EqualTo((int)(bot.CalculateMaxMana(bot.Level, bot.GetBaseStat(bot.CharacterClass.ManaStat)) * 0.8)));
                Assert.That(BotSpellPower.Cost(bot, S(0, 3, 50, type), null), Is.Zero);
            }
        }

        [Test] public void NativePulseChargesOncePerDuePulse_NotEveryServiceOrAiTick()
        {
            Bot bot = MakeBot(typeof(ClassBonedancer), 50);
            bot.Mana = bot.MaxMana;
            Spell spell = new(new DbSpell { SpellID = 99982, Name = "Pulse test", Type = "SpeedDecrease",
                Target = "Enemy", Pulse = 1, Frequency = 50, Duration = 5, Power = 9, PulsePower = 77 }, 50);
            var handler = new DebitOnlyDamage(bot, spell, new SpellLine("test", "test", "", true));
            var pulse = new ECSPulseEffect(new(bot, 5000, 1, handler), 5000) { NextTick = GameLoop.GameLoopTime - 1 };
            bot.ActivePulseSpells[spell.SpellType] = spell;
            var tick = typeof(EffectService).GetMethod("TickPulsingEffect", BindingFlags.NonPublic | BindingFlags.Static);
            tick.Invoke(null, new object[] { pulse, spell, handler, bot });
            Assert.That(bot.Mana, Is.EqualTo(bot.MaxMana - 77));
            tick.Invoke(null, new object[] { pulse, spell, handler, bot });
            Assert.That(bot.Mana, Is.EqualTo(bot.MaxMana - 77));
            Player player = Actor<Player>(); player.Class = bot.Class; player.Level = 50;
            Assert.That(BotSpellPower.PulseCost(player, spell), Is.EqualTo(77));
            Assert.That(BotSpellPower.PulseCost(MakeBot(typeof(ClassHealer), 50), spell), Is.EqualTo(77));
        }

        [Test] public void FocusRootCannotLatchAttackerIntoChanneling_OrdinaryRootAndNukesRemainAllowed()
        {
            Bot bot = MakeBot(typeof(ClassBonedancer), 50);
            Spell focusRoot = new(new DbSpell { SpellID = 10082, Name = "Shroud of Doubt", Type = "SpeedDecrease",
                Target = "Enemy", Pulse = 1, Frequency = 50, Duration = 5, IsFocus = true, Power = 9, PulsePower = 5 }, 14);
            Assert.That(BotSpellPower.BlocksAttackerRotation(bot, focusRoot), Is.True);
            Assert.That(bot.CastSpell(focusRoot, null), Is.False);
            Assert.That(BotSpellPower.BlocksAttackerRotation(bot, S(9, 3, 14, "SpeedDecrease")), Is.False);
            Assert.That(BotSpellPower.BlocksAttackerRotation(bot, S(9, 3)), Is.False);
        }

        [Test] public void ActiveDotDoesNotSuppressRepeatedNukes()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 50);
            Spell dot = new(new DbSpell { SpellID = 99983, Name = "DoT", Type = "DamageOverTime",
                Target = "Enemy", Duration = 20, Damage = 10, Power = 2, CastTime = 3 }, 1);
            var effect = new ECSGameSpellEffect(new(bot, 20000, 1,
                new SpellHandler(bot, dot, new SpellLine("test", "test", "", true))));
            var effects = (Dictionary<eEffect, List<ECSGameEffect>>)typeof(EffectListComponent)
                .GetField("_effects", Hidden).GetValue(bot.effectListComponent);
            effects[EffectHelper.GetEffectFromSpell(dot)] = new() { effect };
            var needs = typeof(BotBrain).GetMethod("NeedsOffensiveSpellApplication", Hidden);
            Assert.That(needs.Invoke(bot.Brain, new object[] { bot, dot }), Is.EqualTo(false));
            for (int i = 0; i < 6; i++)
                Assert.That(needs.Invoke(bot.Brain, new object[] { bot, S(2, 3) }), Is.EqualTo(true));
        }

        [TestCase(typeof(ClassSkald))] [TestCase(typeof(ClassBard))] [TestCase(typeof(ClassMinstrel))]
        public void AllGenericDefensivePathsLeaveSongRotationToItsScheduler(Type cls)
        {
            Bot bot = MakeBot(cls, 50);
            bot.Mana = 100;
            var song = new Spell(new DbSpell { SpellID = 99984, Name = "Rest song", Type = "HealthRegenBuff",
                Target = "Group", Pulse = 1, Frequency = 50, Duration = 5, Value = 4 }, 1);
            Brain brain = (Brain)bot.Brain;
            Assert.That(brain.DefensiveAllowed(song), Is.False);
            Assert.That(brain.InstantDefensiveAllowed(song), Is.False);
            var listCheck = typeof(BotBrain).GetMethod("CheckDefensiveSpells", Hidden, null,
                new[] { typeof(List<Spell>) }, null);
            Assert.That(listCheck.Invoke(brain, new object[] { new List<Spell> { song } }), Is.EqualTo(false));
            Assert.That(bot.castingComponent.HasPendingSkillRequests, Is.False);
        }

        [Test] public void StoppingSongParentPreservesAlreadyAppliedChildBuff()
        {
            Bot bot = MakeBot(typeof(ClassSkald), 50);
            var spell = new Spell(new DbSpell { SpellID = 99985, Name = "Speed song", Type = "SpeedEnhancement",
                Target = "Group", Pulse = 1, Frequency = 50, Duration = 5, Value = 144 }, 3);
            var handler = new SpellHandler(bot, spell, new SpellLine("test", "test", "", true));
            var parent = new ECSPulseEffect(new(bot, 5000, 1, handler), 5000);
            var child = new ECSGameSpellEffect(new(bot, 5000, 1, handler));
            child.FinalizeState(EffectListComponent.AddEffectResult.Added);
            parent.ChildEffects[bot] = child;
            long expiry = child.ExpireTick;
            parent.OnStartEffect();
            parent.OnStopEffect();
            Assert.That(bot.ActivePulseSpells.ContainsKey(spell.SpellType), Is.False);
            Assert.That(child.IsActive, Is.True);
            Assert.That(child.IsEnding, Is.False);
            Assert.That(child.ExpireTick, Is.EqualTo(expiry), "no artificial extension of the old song");
        }

        [Test] public void UnaffordableBuffsDoNotQueueOrWakeRestingBot()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 50);
            bot.Mana = 1; SetRecovery(bot, true);
            Spell buff = S(30, 2, 1, "BaseArmorFactorBuff", "Self");
            var brain = (Brain)bot.Brain;
            for (int i = 0; i < 10; i++)
            {
                Assert.That(brain.DefensiveAllowed(buff), Is.False);
                Assert.That(brain.InstantDefensiveAllowed(S(30, 0, 1, "Bladeturn", "Self")), Is.False);
                Assert.That(bot.CastSpell(buff, new SpellLine("test", "test", "", true)), Is.False);
            }
            Assert.That(bot.Mana, Is.EqualTo(1));
            Assert.That(bot.IsRecoveryResting, Is.True);
            Assert.That(bot.IsSitting, Is.False);
            Assert.That(bot.castingComponent.HasPendingSkillRequests, Is.False);
        }

        [Test] public void OffensiveDecisionStopsAfterOneAcceptedInstant()
        {
            Bot bot = MakeBot(typeof(ClassBonedancer), 50);
            bot.Mana = 100;
            bot.Spells = new List<Spell> { S(30, 0) };
            bot.InstantHarmfulSpells = new List<Spell> { S(30, 0) };
            bot.InstantMiscSpells = new List<Spell> { S(30, 0, 1, "Bladeturn", "Self") };
            var brain = (Brain)bot.Brain;
            Assert.That(brain.CheckSpells(BotBrain.eCheckSpellType.Offensive), Is.True);
            Assert.That(brain.InstantOffense, Is.EqualTo(1));
            Assert.That(brain.InstantDefense, Is.Zero);
            Assert.That(brain.Normal, Is.Zero);
        }

        [Test] public void QuickcastIsIgnoredByBotsButStillWorksForHumans()
        {
            Bot bot = MakeBot(typeof(ClassSpiritmaster), 50);
            Player player = Actor<Player>(); player.Class = bot.Class; player.Level = 50;
            var spell = S(30, 3);
            var line = new SpellLine("test", "test", "", true);
            foreach (GameLiving actor in new GameLiving[] { bot, player })
            {
                var handler = new SpellHandler(actor, spell, line);
                Field(typeof(SpellHandler), handler, "_quickcast", new QuickCastECSGameEffect(new(actor, 3000, 1)));
                Assert.That(handler.IsQuickCasting, Is.EqualTo(actor is GamePlayer));
                Assert.That(handler.PowerCost(actor), Is.EqualTo(actor is GamePlayer ? 60 : 30));
            }
        }

        private Bot MakeBot(Type cls, byte level, bool temporary = false)
        {
            Bot bot = Actor<Bot>(); bot.Class = (ICharacterClass)Activator.CreateInstance(cls); bot.Level = level;
            bot.Stat = 100;
            Field(typeof(GameBot), bot, "<IsTemporaryGroupHelper>k__BackingField", temporary);
            Field(typeof(GameNPC), bot, "m_ownBrain", new Brain { Body = bot });
            return bot;
        }
        private T Actor<T>() where T : GameLiving
        {
            var actor = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            actor.ObjectState = GameObject.eObjectState.Active;
            foreach (string mutex in new[] { "_changeHealthLock", "_changeManaLock", "_changeEnduranceLock", "_disabledSkillsLock" })
                Field(typeof(GameLiving), actor, mutex, new System.Threading.Lock());
            foreach (string p in new[] { "ItemBonus", "AbilityBonus", "BaseBuffBonusCategory", "SpecBuffBonusCategory", "OtherBonus", "DebuffCategory", "SpecDebuffCategory" })
                Field(typeof(GameLiving), actor, "<" + p + ">k__BackingField", new PropertyIndexer());
            Field(typeof(GameLiving), actor, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameLiving), actor, "m_effects", new GameEffectList(actor));
            Field(typeof(GameLiving), actor, "m_abilities", new Dictionary<string, Ability>());
            Field(typeof(GameLiving), actor, "m_disabledSkills", new Dictionary<KeyValuePair<int, Type>, KeyValuePair<long, Skill>>());
            Field(typeof(GameLiving), actor, "<ActivePulseSpells>k__BackingField", new ConcurrentDictionary<eSpellType, Spell>());
            if (actor is GameNPC npc)
            {
                Field(typeof(GameNPC), npc, "m_brains", new ArrayList());
                Field(typeof(GameNPC), npc, "m_spells", new List<Spell>());
            }
            actor.castingComponent = CastingComponent.Create(actor);
            if (actor is GameNPC npcActor)
                npcActor.castingComponent = (NpcCastingComponent)actor.castingComponent;
            actor.effectListComponent = EffectListComponent.Create(actor);
            _actors.Add(actor);
            return actor;
        }
        private static Spell S(int power, double cast, int level = 1, string type = "DirectDamage", string target = "Enemy") =>
            new(new DbSpell { SpellID = 99981, Name = "Power test", Type = type, Target = target,
                Power = power, CastTime = cast, Damage = 10, Range = 1500 }, level);
        private static void Field(Type type, object owner, string name, object value) => type.GetField(name, Hidden).SetValue(owner, value);
        private static void SetRecovery(GameBot bot, bool value) => Field(typeof(GameBot), bot, "_recoveryRestLocked", value);
    }
}
