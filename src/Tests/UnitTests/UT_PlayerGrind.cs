using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.AI.Brain;
using DOL.Database;
using DOL.GS;
using DOL.GS.Commands;
using DOL.GS.PacketHandler;
using DOL.GS.PlayerClass;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture, NonParallelizable]
    public class UT_PlayerGrind
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly IObjectDatabase EmptyDatabase = DispatchProxy.Create<IObjectDatabase, UT_AutonomousLootFlow.EmptyReadDatabase>();
        private GameServer _previous;
        private readonly List<Human> _players = new();
        private sealed class Server : GameServer
        {
            protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
            protected override IServerRules ServerRulesImpl => new NormalServerRules();
        }
        public class Packets : DispatchProxy
        {
            public readonly List<string> Messages = new();
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SendMessage") Messages.Add((string)args[0]);
                return method.ReturnType != typeof(void) && method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }
        private sealed class Human : GamePlayer
        {
            private Human() : base(null, null) { }
            public GameClient Connection;
            public bool Moving, Casting, Combat;
            public int PositionX;
            public override GameClient Client => Connection;
            public override bool IsAlive => true;
            public override bool IsMoving => Moving;
            public override bool IsCasting => Casting;
            public override bool IsAttacking => false;
            public override bool InCombat => Combat;
            public override bool IsCrowdControlled => false;
            public override bool IsOnHorse { get; set; }
            public override bool IsSitting { get; set; }
            public override short CurrentSpeed { get; set; }
            public override int X { get => PositionX; set => PositionX = value; }
            public override int Y { get => 0; set { } }
            public override int Z { get => 0; set { } }
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override byte Level { get => 10; set { } }
            public override int Health { get; set; }
            public override int MaxHealth => 100;
            public override int Mana { get; set; }
            public override int MaxMana => 100;
            public override int Endurance { get; set; }
            public override int MaxEndurance => 100;
            public override IControlledBrain ControlledBrain { get; set; }
            public override void Sit(bool sit) => IsSitting = sit;
        }
        private sealed class Companion : GameBot
        {
            private Companion() : base((OfflineWorldBotRecord)null) { }
            public bool Alive, Casting, Combat;
            public int PositionX;
            public int PowerMax;
            public ICharacterClass Class;
            public override bool IsAlive => Alive;
            public override bool IsMoving => false;
            public override bool IsCasting => Casting;
            public override bool IsAttacking => false;
            public override bool InCombat => Combat;
            public override bool IsCrowdControlled => false;
            public override int X => PositionX;
            public override int Y => 0;
            public override int Z => 0;
            public override ushort CurrentRegionID { get => 1; set { } }
            public override eRealm Realm { get => eRealm.Albion; set { } }
            public override byte Level { get => 10; set { } }
            public override ICharacterClass CharacterClass => Class;
            public override int Health { get; set; }
            public override int MaxHealth => 100;
            public override int Mana { get; set; }
            public override int MaxMana => PowerMax;
            public override int Endurance { get; set; }
            public override int MaxEndurance => 100;
            public override IControlledBrain ControlledBrain { get; set; }
        }
        private sealed class Brain : BotBrain
        {
            public int Cancellations;
            public override void CancelOrderedPull(GameLiving target) => Cancellations++;
        }
        private sealed class Enemy : GameNPC
        {
            public bool Combat;
            public override bool InCombat => Combat;
            public override bool IsAlive => true;
        }

        [SetUp] public void SetUp()
        {
            _previous = GameServer.Instance;
            GameServer.LoadTestDouble((Server)RuntimeHelpers.GetUninitializedObject(typeof(Server)));
        }
        [TearDown] public void TearDown()
        {
            foreach (Human player in _players) PlayerCompanionGrind.Stop(player, "test cleanup", false);
            _players.Clear(); GameServer.LoadTestDouble(_previous);
        }
        private static void Field(Type type, object instance, string name, object value) => type.GetField(name, Hidden).SetValue(instance, value);
        private Human Player()
        {
            var player = (Human)RuntimeHelpers.GetUninitializedObject(typeof(Human));
            player.Connection = (GameClient)RuntimeHelpers.GetUninitializedObject(typeof(GameClient));
            Field(typeof(GameClient), player.Connection, "<ClientState>k__BackingField", GameClient.eClientState.Playing);
            player.Connection.Out = DispatchProxy.Create<IPacketLib, Packets>();
            Field(typeof(GamePlayer), player, "m_steed", new WeakReference(null));
            Field(typeof(GameLiving), player, "<TempProperties>k__BackingField", new PropertyCollection());
            player.ObjectState = GameObject.eObjectState.Active;
            player.Health = player.Mana = player.Endurance = 100;
            _players.Add(player); return player;
        }
        private Companion Helper(Human owner, ICharacterClass role, bool persistent = false)
        {
            var bot = (Companion)RuntimeHelpers.GetUninitializedObject(typeof(Companion));
            bot.Alive = true; bot.Class = role; bot.PowerMax = 100;
            bot.Health = bot.Mana = bot.Endurance = 100;
            bot.ObjectState = GameObject.eObjectState.Active;
            Field(typeof(GameLiving), bot, "<TempProperties>k__BackingField", new PropertyCollection());
            Field(typeof(GameNPC), bot, "m_brains", new ArrayList());
            Field(typeof(GameNPC), bot, "m_ownBrain", new Brain { Body = bot });
            bot.movementComponent = new NpcMovementComponent(bot);
            typeof(GameBot).GetProperty(nameof(GameBot.Owner)).SetValue(bot, persistent ? null : owner);
            typeof(GameBot).GetProperty(nameof(GameBot.IsTemporaryGroupHelper)).SetValue(bot, !persistent);
            typeof(GameBot).GetProperty(nameof(GameBot.IsAutonomousWorldBot)).SetValue(bot, persistent);
            return bot;
        }
        private static List<GameLiving> Members(Group group) => (List<GameLiving>)typeof(Group).GetField("_groupMembers", Hidden).GetValue(group);
        private static void Add(Group group, GameLiving living) { living.Group = group; Members(group).Add(living); }
        private (Human Player, Companion Tank, Companion Healer) Party()
        {
            Human player = Player(); var group = new Group(player); Add(group, player);
            Companion tank = Helper(player, new ClassPaladin()), healer = Helper(player, new ClassCleric());
            Add(group, tank); Add(group, healer); return (player, tank, healer);
        }
        private static object Session(Human player) => ((IDictionary)typeof(PlayerCompanionGrind)
            .GetField("Sessions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))[player];
        private static void Tick(Human player) => typeof(PlayerCompanionGrind)
            .GetMethod("Tick", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { Session(player) });
        private static string Detail(Human player) => (string)Session(player).GetType().GetField("Detail").GetValue(Session(player));

        [TestCase("1", true)] [TestCase("2", true)] [TestCase("50", true)] [TestCase("65", true)] [TestCase("255", true)]
        [TestCase("0", false)] [TestCase("-1", false)] [TestCase("256", false)] [TestCase("frog", false)]
        public void MobLevelArgumentIsExplicitAndValidated(string value, bool valid) =>
            Assert.That(MobsCommandHandler.TryReadLevel(new[] { "mobs", value }, 25, out _), Is.EqualTo(valid));

        [Test] public void MobsWithoutNumberUsesOnlyCurrentLevelAndRejectsExtraArguments()
        {
            Assert.That(MobsCommandHandler.TryReadLevel(new[] { "mobs" }, 25, out byte level), Is.True);
            Assert.That(level, Is.EqualTo(25));
            Assert.That(MobsCommandHandler.TryReadLevel(new[] { "mobs", "1", "0" }, 25, out _), Is.False);
            Assert.That(MobsCommandHandler.TryReadLevel(new[] { "mobs", "1", "2" }, 25, out _), Is.True);
            Assert.That(MobsCommandHandler.TryReadLevel(new[] { "mobs", "1", "2", "3" }, 25, out _), Is.False);
        }

        [Test] public void ExactLevelNamesDoNotIncludeAdjacentLevelsAndDeduplicateSpawns()
        {
            var names = PlayerMobNavigator.ExactLevelNames(new (string, byte)[]
            { ("large frog", 1), ("large frog", 1), ("Large Frog", 1), ("wolf", 2), ("beetle", 1), ("rat", 3) }, 1);
            Assert.That(names.Select(entry => entry.Name), Is.EqualTo(new[] { "beetle", "large frog" }));
            Assert.That(names.Select(entry => entry.Level), Is.All.EqualTo(1));
        }

        [Test] public void LongMonsterListsArePagedWithoutSilentNativeWindowTruncation()
        {
            var entries = Enumerable.Range(0, 450).Select(i => (Name: new string('x', 150) + i, Level: (byte)50)).ToArray();
            var pages = MobsCommandHandler.BuildPages(entries);
            Assert.That(pages.Count, Is.GreaterThan(1));
            Assert.That(pages.SelectMany(page => page), Is.EqualTo(entries.Select(entry => $"{entry.Name} ({entry.Level})")));
            Assert.That(pages.All(page => page.Sum(line => line.Length + 2) <= 1200 && page.Length <= 40), Is.True);
        }

        [Test] public void TankPreferredAndAttackersUsedWhenNoTankSupportNeverPulls()
        {
            var (player, tank, healer) = Party(); var attacker = Helper(player, new ClassWizard());
            Assert.That(PlayerCompanionGrind.SelectPuller(new[] { healer, attacker, tank }), Is.SameAs(tank));
            Assert.That(PlayerCompanionGrind.SelectPuller(new[] { healer, attacker }), Is.SameAs(attacker));
            Assert.That(PlayerCompanionGrind.SelectPuller(new[] { healer }), Is.Null);
            tank.Alive = false;
            Assert.That(PlayerCompanionGrind.SelectPuller(new[] { tank, attacker }), Is.SameAs(attacker));
        }

        [TestCase(1, 1, 0, 1, true)] [TestCase(10, 9, -1, 7, true)]
        [TestCase(10, 1, -3, 7, false)] [TestCase(10, 30, 3, 7, true)]
        [TestCase(10, 13, 2, 7, true)] [TestCase(10, 13, 2, 1, true)]
        [TestCase(1, 0, 0, 7, false)] [TestCase(50, 65, 3, 7, true)]
        public void CompanionPullsAllowPurpleButNeverGreyOrInvalidLevels(int level, int mob, int con, int helpers, bool expected) =>
            Assert.That(PlayerCompanionGrind.IsSuitableLevel(level, mob, con, helpers), Is.EqualTo(expected));

        [Test] public void MixedPersistentPartyIsRejectedWithoutTouchingItsBrain()
        {
            var (player, _, _) = Party(); var persistent = Helper(player, new ClassWizard(), true); Add(player.Group, persistent);
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.False);
            Assert.That(((Brain)persistent.Brain).Cancellations, Is.Zero);
        }

        [Test] public void OtherHumansAndOtherOwnersCompanionsAreRejected()
        {
            var (player, _, _) = Party(); Human other = Player(); Add(player.Group, other);
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.False);
            Members(player.Group).Remove(other);
            Add(player.Group, Helper(other, new ClassWizard()));
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.False);
        }

        [Test] public void AddingPersistentBotToActiveModeStopsItWithoutMutatingThatBot()
        {
            var (player, _, _) = Party(); Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True);
            var persistent = Helper(player, new ClassWizard(), true); Add(player.Group, persistent); Tick(player);
            Assert.That(PlayerCompanionGrind.IsActive(player), Is.False);
            Assert.That(((Brain)persistent.Brain).Cancellations, Is.Zero);
            Assert.That(persistent.IsSitting, Is.False);
        }

        [Test] public void RestUsesRealDeficitsAndDoesNotRefillOrMoveActors()
        {
            var (player, tank, healer) = Party(); tank.Health = 60; healer.Mana = 40;
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True); Tick(player);
            Assert.That(tank.IsRecoveryResting, Is.True); Assert.That(healer.IsRecoveryResting, Is.True);
            Assert.That(tank.IsSitting, Is.False); Assert.That(healer.IsSitting, Is.False);
            Assert.That(tank.Health, Is.EqualTo(60)); Assert.That(healer.Mana, Is.EqualTo(40));
            Assert.That(player.PositionX, Is.Zero); Assert.That(Detail(player), Does.Contain("Resting"));
            Assert.That(((Packets)player.Connection.Out).Messages.Any(message => message.Contains("GRIND MODE ACTIVE")), Is.True);
        }

        [Test] public void BeneficialCastingIsNotInterruptedByForcedSitting()
        {
            var (player, _, healer) = Party(); healer.Mana = 40; healer.Casting = true;
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True); Tick(player);
            Assert.That(healer.IsRecoveryResting, Is.False); Assert.That(healer.IsSitting, Is.False);
            Assert.That(healer.Casting, Is.True);
            Assert.That(Detail(player), Does.Contain("buffing"));
        }

        [Test] public void IncomingCombatWakesModeRestAndPreventsAnotherPull()
        {
            var (player, tank, healer) = Party(); tank.Health = 60; healer.Mana = 40;
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True); Tick(player);
            tank.Combat = true; Tick(player);
            Assert.That(tank.IsRecoveryResting, Is.False); Assert.That(healer.IsRecoveryResting, Is.False);
            Assert.That(tank.IsSitting, Is.False); Assert.That(healer.IsSitting, Is.False);
            Assert.That(Detail(player), Does.Contain("no new pulls"));
        }

        [Test] public void DeadOrDistantCompanionBlocksNewPullUntilReturn()
        {
            var (player, tank, _) = Party(); tank.Alive = false;
            // Another attacker is present so start is allowed, but nobody pulls short-handed.
            Add(player.Group, Helper(player, new ClassWizard()));
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True); Tick(player);
            Assert.That(Detail(player), Does.Contain("revive/return"));
            tank.Alive = true; tank.PositionX = 351; Tick(player);
            Assert.That(Detail(player), Does.Contain("revive/return"));
        }

        [Test] public void ManualMovementStopsWithoutTeleportingPlayerBack()
        {
            var (player, _, _) = Party(); Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True);
            player.PositionX = 70; Tick(player);
            Assert.That(PlayerCompanionGrind.IsActive(player), Is.False);
            Assert.That(player.PositionX, Is.EqualTo(70));
        }

        [Test] public void StopRestoresOnlyModeRequestedSeatsAndLeavesGroupIntact()
        {
            var (player, tank, healer) = Party(); var group = player.Group;
            healer.BeginRecoveryRest(); tank.Health = 60;
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True); Tick(player);
            PlayerCompanionGrind.Stop(player, "test");
            Assert.That(tank.IsRecoveryResting, Is.False); Assert.That(healer.IsRecoveryResting, Is.True);
            Assert.That(tank.IsSitting, Is.False); Assert.That(healer.IsSitting, Is.False);
            Assert.That(player.Group, Is.SameAs(group)); Assert.That(group.MemberCount, Is.EqualTo(3));
        }

        [Test] public void RecoveryRequiresAllUsedResourcesButNotNonexistentPower()
        {
            var (player, tank, _) = Party(); tank.Mana = 0; tank.PowerMax = 0;
            Assert.That(PlayerCompanionGrind.NeedsRecovery(tank), Is.False);
            tank.Endurance = 99; Assert.That(PlayerCompanionGrind.NeedsRecovery(tank), Is.True);
            tank.Endurance = 100; tank.Health = 99; Assert.That(PlayerCompanionGrind.NeedsRecovery(tank), Is.True);
            player.Mana = 99; Assert.That(PlayerCompanionGrind.NeedsRecovery(player), Is.True);
        }

        [TestCase(true)] [TestCase(false)]
        public void StopCancelsOnlyUnengagedPullsNotActualBattles(bool fighting)
        {
            var (player, tank, _) = Party();
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True);
            var enemy = (Enemy)RuntimeHelpers.GetUninitializedObject(typeof(Enemy)); enemy.Combat = fighting;
            object session = Session(player); session.GetType().GetField("Target").SetValue(session, enemy);
            PlayerCompanionGrind.Stop(player, "test");
            Assert.That(((Brain)tank.Brain).Cancellations, Is.EqualTo(fighting ? 0 : 1));
        }

        [Test] public void TimedOutUnreachablePullIsBlacklistedInsteadOfSpammed()
        {
            var (player, tank, _) = Party();
            Assert.That(PlayerCompanionGrind.TryStart(player, out _), Is.True);
            var enemy = (Enemy)RuntimeHelpers.GetUninitializedObject(typeof(Enemy));
            enemy.ObjectState = GameObject.eObjectState.Active; enemy.Name = "blocked monster";
            object session = Session(player);
            session.GetType().GetField("Target").SetValue(session, enemy);
            session.GetType().GetField("ContactDeadline").SetValue(session, -1L);
            Tick(player);
            Assert.That(((Brain)tank.Brain).Cancellations, Is.EqualTo(1));
            Assert.That(session.GetType().GetField("Target").GetValue(session), Is.Null);
            var failed = (Dictionary<GameNPC, long>)session.GetType().GetField("FailedTargets").GetValue(session);
            Assert.That(failed[enemy], Is.GreaterThan(GameLoop.GameLoopTime));
        }

        [TestCase(1, 0, eRealm.Albion)] [TestCase(100, 100, eRealm.Midgard)] [TestCase(200, 207, eRealm.Hibernia)]
        [TestCase(1, 11, eRealm.None)] [TestCase(100, 111, eRealm.None)] [TestCase(200, 210, eRealm.None)]
        [TestCase(245, 245, eRealm.None)] [TestCase(246, 246, eRealm.None)] [TestCase(247, 247, eRealm.None)]
        [TestCase(248, 248, eRealm.None)] [TestCase(249, 249, eRealm.None)]
        public void MobTerritoryFilterKeepsHomelandsPrivateAndFrontierDungeonsShared(int region, int zone, eRealm expected)
        {
            var filter = typeof(PlayerMobNavigator).GetMethod("ProtectedRealm", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(filter.Invoke(null, new object[] { (ushort)region, (ushort)zone }), Is.EqualTo(expected));
        }
    }
}
