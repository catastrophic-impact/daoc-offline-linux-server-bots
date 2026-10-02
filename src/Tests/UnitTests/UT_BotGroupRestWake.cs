using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_BotGroupRestWake
    {
        private const long IdleTick = 999_999;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameServer _previousServer;
        private readonly List<GameLiving> _actors = new();
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();

        // These actors deliberately bypass bot login, persistence and world
        // registration. The tests exercise only BotBrain's group-defense gate.
        private sealed class RestingBot : GameBot
        {
            private RestingBot() : base((OfflineWorldBotRecord)null) { }
            public int TestX;
            public ushort TestRegion = 1;
            public override bool IsAlive => true;
            public override byte Level { get; set; } = 1;
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; } = eRealm.Hibernia;
            public override int X => TestX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => TestRegion; set => TestRegion = value; }
            public override ICharacterClass CharacterClass => new ClassEnchanter();
        }

        private sealed class Attacker : GameNPC
        {
            public override bool IsAlive => true;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override int X => 0;
            public override int Y => 0;
            public override int Z => 0;
            public override byte Level { get; set; } = 1;
            public override int EffectiveLevel => Level;
            public override eRealm Realm { get; set; } = eRealm.Midgard;
        }

        private sealed class TestServer : GameServer
        {
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        }

        [SetUp]
        public void SetUp()
        {
            _previousServer = GameServer.Instance;
            GameServer.LoadTestDouble((TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer)));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameLiving actor in _actors)
                ServiceObjectStore.Remove(actor.effectListComponent);
            _actors.Clear();
            GameServer.LoadTestDouble(_previousServer);
        }

        [TestCase(1)] [TestCase(6)] [TestCase(50)] [TestCase(255)]
        public void NearbyExactGroupHit_WakesRestingBotAndSchedulesImmediateThink(int attackerLevel)
        {
            var (helper, brain, victim) = GroupedBots();
            SetRecovery(helper);
            brain.NextThinkTick = IdleTick;

            AttackData attack = Attack(victim, eAttackResult.HitUnstyled);
            attack.Attacker.Level = (byte)attackerLevel;
            attack.Attacker.Realm = eRealm.None;
            brain.OnGroupMemberAttacked(victim, attack);

            Assert.That(brain.HasAggro, Is.True);
            Assert.That(helper.IsRecoveryResting, Is.False);
            Assert.That(helper.IsSitting, Is.False);
            Assert.That(brain.NextThinkTick, Is.EqualTo(GameLoop.GameLoopTime));
        }

        [TestCase(1)] [TestCase(6)] [TestCase(50)] [TestCase(255)]
        public void SoloRetaliationWakesAndRetainsAttackersOfAnyLevel(int attackerLevel)
        {
            RestingBot bot = Bot();
            bot.Level = 10;
            BotBrain brain = Brain(bot);
            SetRecovery(bot);
            brain.NextThinkTick = IdleTick;
            AttackData attack = Attack(bot, eAttackResult.HitUnstyled);
            attack.Attacker.Level = (byte)attackerLevel;
            attack.Attacker.Realm = eRealm.None;

            brain.OnAttackedByEnemy(attack);

            Assert.That(brain.GetBaseAggroAmount(attack.Attacker), Is.GreaterThan(0));
            Assert.That(bot.IsRecoveryResting, Is.False);
            Assert.That(brain.NextThinkTick, Is.EqualTo(GameLoop.GameLoopTime));
            bool removed = (bool)typeof(BotBrain).GetMethod("ShouldBeRemovedFromAggroList", PrivateInstance)
                .Invoke(brain, new object[] {attack.Attacker});
            Assert.That(removed, Is.False, "A grey or extremely high-level attacker must remain a defense target");
        }

        [Test]
        public void OutsiderHit_DoesNotWakeOrScheduleRestingBot()
        {
            var (helper, brain, _) = GroupedBots();
            RestingBot outsider = Bot();
            outsider.Group = new Group(outsider);
            SetRecovery(helper);
            brain.NextThinkTick = IdleTick;

            brain.OnGroupMemberAttacked(outsider, Attack(outsider, eAttackResult.HitUnstyled));

            AssertUnchanged(helper, brain);
        }

        [Test]
        public void OutOfRangeGroupHit_DoesNotWakeOrScheduleRestingBot()
        {
            var (helper, brain, victim) = GroupedBots();
            victim.TestX = 2001;
            SetRecovery(helper);
            brain.NextThinkTick = IdleTick;

            brain.OnGroupMemberAttacked(victim, Attack(victim, eAttackResult.HitUnstyled));

            AssertUnchanged(helper, brain);
        }

        [Test]
        public void NoGroupHit_DoesNotWakeOrScheduleRestingBot()
        {
            RestingBot helper = Bot();
            BotBrain brain = Brain(helper);
            RestingBot victim = Bot();
            SetRecovery(helper);
            brain.NextThinkTick = IdleTick;

            brain.OnGroupMemberAttacked(victim, Attack(victim, eAttackResult.HitUnstyled));

            AssertUnchanged(helper, brain);
        }

        [Test]
        public void InvalidAttackResult_DoesNotWakeOrScheduleRestingBot()
        {
            var (helper, brain, victim) = GroupedBots();
            SetRecovery(helper);
            brain.NextThinkTick = IdleTick;

            brain.OnGroupMemberAttacked(victim, Attack(victim, eAttackResult.Any));

            AssertUnchanged(helper, brain);
        }

        private (RestingBot Helper, BotBrain Brain, RestingBot Victim) GroupedBots()
        {
            RestingBot helper = Bot();
            RestingBot victim = Bot();
            Group group = new(helper);
            Members(group).Add(helper);
            Members(group).Add(victim);
            helper.Group = group;
            victim.Group = group;
            return (helper, Brain(helper), victim);
        }

        private RestingBot Bot()
        {
            RestingBot bot = (RestingBot)RuntimeHelpers.GetUninitializedObject(typeof(RestingBot));
            bot.ObjectState = GameObject.eObjectState.Active;
            bot.TestRegion = 1;
            bot.Level = 1;
            bot.Realm = eRealm.Hibernia;
            Field(typeof(GameLiving), bot, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameNPC), bot, "m_brains", new ArrayList());
            bot.effectListComponent = EffectListComponent.Create(bot);
            _actors.Add(bot);
            return bot;
        }

        private static BotBrain Brain(RestingBot bot)
        {
            BotBrain brain = new() { Body = bot };
            Field(typeof(GameNPC), bot, "m_ownBrain", brain);
            return brain;
        }

        private Attacker Enemy()
        {
            Attacker attacker = (Attacker)RuntimeHelpers.GetUninitializedObject(typeof(Attacker));
            attacker.ObjectState = GameObject.eObjectState.Active;
            attacker.Level = 1;
            attacker.Realm = eRealm.Midgard;
            Field(typeof(GameLiving), attacker, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameNPC), attacker, "m_brains", new ArrayList());
            attacker.effectListComponent = EffectListComponent.Create(attacker);
            _actors.Add(attacker);
            return attacker;
        }

        private AttackData Attack(GameLiving target, eAttackResult result) => new()
        {
            Attacker = Enemy(),
            Target = target,
            AttackResult = result,
            Damage = 1
        };

        private static List<GameLiving> Members(Group group) => (List<GameLiving>)typeof(Group)
            .GetField("_groupMembers", PrivateInstance).GetValue(group);

        private static void Field(Type type, object target, string name, object value) => type
            .GetField(name, PrivateInstance).SetValue(target, value);

        private static void SetRecovery(GameBot bot) =>
            Field(typeof(GameBot), bot, "_recoveryRestLocked", true);

        private static void AssertUnchanged(RestingBot helper, BotBrain brain)
        {
            Assert.That(brain.HasAggro, Is.False);
            Assert.That(helper.IsRecoveryResting, Is.True);
            Assert.That(helper.IsSitting, Is.False);
            Assert.That(brain.NextThinkTick, Is.EqualTo(IdleTick));
        }
    }
}
