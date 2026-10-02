using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_AutonomousDungeonNavigation
    {
        [TestCase(246)] [TestCase(248)] [TestCase(276)] [TestCase(277)]
        public void OldFrontierBranchesAreSharedCombatAreas(int region)
        {
            Assert.That(AutonomousDungeonPolicy.IsSharedFrontierDungeon((ushort)region), Is.True);
            Assert.That(AutonomousDungeonPolicy.CanEngageLocalOpponent(eRealm.Albion, eRealm.Hibernia,
                (ushort)region, (ushort)region, true, true), Is.True);
        }

        [TestCase(244)] [TestCase(245)] [TestCase(247)] [TestCase(220)]
        public void NoModernPassageOrHomeDungeonCrossRealmAggression(int region)
        {
            Assert.That(AutonomousDungeonPolicy.IsSharedCombatDungeon((ushort)region), Is.False);
        }

        [Test]
        public void OpponentsMustBeLocalAliveLegalAndEnemyRealm()
        {
            Assert.That(AutonomousDungeonPolicy.CanEngageLocalOpponent(eRealm.Albion,eRealm.Albion,248,248,true,true),Is.False);
            Assert.That(AutonomousDungeonPolicy.CanEngageLocalOpponent(eRealm.Albion,eRealm.Midgard,248,246,true,true),Is.False);
            Assert.That(AutonomousDungeonPolicy.CanEngageLocalOpponent(eRealm.Albion,eRealm.Midgard,248,248,false,true),Is.False);
            Assert.That(AutonomousDungeonPolicy.CanEngageLocalOpponent(eRealm.Albion,eRealm.Midgard,248,248,true,false),Is.False);
        }

        [Test]
        public void CorridorFollowsCornerNotDiagonalThroughWall()
        {
            Vector3[] path = [new(0,0,0),new(0,800,0),new(800,800,0)];
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor(path,new(400,400,0),100,1600,out _),Is.False);
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor(path,new(30,500,0),100,1600,out float along),Is.True);
            Assert.That(along,Is.EqualTo(500));
        }

        [Test]
        public void CorridorDoesNotPullAnotherFloorOrDistantRoom()
        {
            Vector3[] path = [new(0,0,0),new(2000,0,0)];
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor(path,new(500,0,400),600,1000,out _),Is.False);
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor(path,new(1800,0,0),100,1000,out _),Is.False);
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor(path,new(700,0,0),100,1000,out _),Is.True);
        }

        [Test]
        public void CorridorHandlesEmptyAndRepeatedNodes()
        {
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor([],Vector3.Zero,100,1000,out _),Is.False);
            Assert.That(AutonomousDungeonPolicy.IntersectsCorridor([Vector3.Zero,Vector3.Zero,new(500,0,0)],new(300,0,0),20,1000,out _),Is.True);
        }

        [Test]
        public void AuditedSpawnCannotSilentlyFollowMovedOrRenamedDatabaseRow()
        {
            var point = new AutonomousDungeonGoalCatalog.Point { Id="test",Zone=276,Region=276,Name="monster",Spawn=[100,200,300] };
            Assert.That(AutonomousDungeonGoalCatalog.MatchesSpawn(point,"test",276,276,"monster",new(100,200,300)),Is.True);
            Assert.That(AutonomousDungeonGoalCatalog.MatchesSpawn(point,"test",276,276,"monster",new(300,200,300)),Is.False);
            Assert.That(AutonomousDungeonGoalCatalog.MatchesSpawn(point,"test",277,277,"monster",new(100,200,300)),Is.False);
            Assert.That(AutonomousDungeonGoalCatalog.MatchesSpawn(point,"other",276,276,"monster",new(100,200,300)),Is.False);
        }

        [Test]
        public void EmbeddedNativeProofLookupLoads()
        {
            Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCount,Is.GreaterThan(5000));
        }

        [Test]
        public void RouteBlockerMustProjectToTheSameReachableDungeonComponent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.CanSelectRouteBlocker(true, true, true), Is.True);
                Assert.That(AutonomousDungeonPolicy.CanSelectRouteBlocker(false, true, true), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanSelectRouteBlocker(true, false, true), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanSelectRouteBlocker(true, true, false), Is.False);
            });
        }

        [Test]
        public void CorridorBlockerHandoffRequiresViableCurrentPveRoster()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, true, true, true, true, false), Is.True);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Grinding", 8, true, true, true, true, false), Is.True);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 7, true, true, true, true, false), Is.True);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 6, true, true, true, true, false), Is.True);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    false, "Traveling", 8, true, true, true, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Meeting up", 8, true, true, true, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 2, true, true, true, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, false, true, true, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, true, false, true, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, true, true, false, true, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, true, true, true, false, false), Is.False);
                Assert.That(AutonomousDungeonPolicy.CanHandoffRouteBlocker(
                    true, "Traveling", 8, true, true, true, true, true), Is.False);
            });
        }

        [Test]
        public void StarterDungeonsExposeEveryAuditedRoomSpawn()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(21), Is.GreaterThanOrEqualTo(168));
                Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(129), Is.EqualTo(355));
                Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(221), Is.EqualTo(71));
            });
        }

        [Test]
        public void KeltoiAndSpraggonRetainInstalledEntranceRouteProofs()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(22),
                    Is.GreaterThanOrEqualTo(124),
                    "Keltoi must include the 118 positive-level live spawns missing from its six legacy level-zero rows");
                Assert.That(AutonomousDungeonGoalCatalog.VerifiedSpawnCountForRegion(222),
                    Is.EqualTo(291), "Spraggon Den's installed two-way route proofs remain available");
            });
        }

        [Test]
        public void UnreliableNisseHauntIsNotAnAutonomousGoal()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(129, "haunt"), Is.False);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(129, "lair guard"), Is.True);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(221, "haunt"), Is.True,
                    "The exclusion is restricted to the audited Nisse objective");
            });
        }

        [Test]
        public void CursedTombCaveSpiderBehindHigherLevelEntrancePackIsNotAssigned()
        {
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(128, "cave spider"), Is.False);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(128, "tomb sentry"), Is.True);
                Assert.That(AutonomousDungeonPolicy.IsReliableAutonomousGoal(129, "cave spider"), Is.True);
            });
        }

        [Test]
        public void FirstCursedTombGroupCampPrefersEntryWithoutClosingLaterOrOtherGoals()
        {
            var entry = (Region: (ushort)128, Point: new Vector3(32882, 33644, 16008));
            var deep = (Region: (ushort)128, Point: new Vector3(37473, 34612, 15393));
            var upperFloor = (Region: (ushort)128, Point: new Vector3(31900, 33000, 16600));
            var otherDungeon = (Region: (ushort)129, Point: new Vector3(33000, 33000, 16000));
            var cells = new[] { entry, deep, upperFloor, otherDungeon };
            var first = AutonomousDungeonPolicy.PreferFirstCursedTombGroupCamps(cells,
                formedGroup: true, hasInteriorProgress: false, camp => camp.Region, camp => camp.Point);
            var later = AutonomousDungeonPolicy.PreferFirstCursedTombGroupCamps(cells,
                formedGroup: true, hasInteriorProgress: true, camp => camp.Region, camp => camp.Point);
            var solo = AutonomousDungeonPolicy.PreferFirstCursedTombGroupCamps(cells,
                formedGroup: false, hasInteriorProgress: false, camp => camp.Region, camp => camp.Point);
            var noEntry = AutonomousDungeonPolicy.PreferFirstCursedTombGroupCamps(
                new[] { deep, otherDungeon }, formedGroup: true, hasInteriorProgress: false,
                camp => camp.Region, camp => camp.Point);
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.IsCursedTombEntryStage(151, new(300000, 330000, 5000)), Is.True);
                Assert.That(AutonomousDungeonPolicy.IsCursedTombEntryStage(128, new(31700, 33000, 16000)), Is.True);
                Assert.That(AutonomousDungeonPolicy.IsCursedTombEntryStage(128, new(34500, 33000, 16000)), Is.False);
                Assert.That(first, Is.EquivalentTo(new[] { entry, otherDungeon }));
                Assert.That(later, Is.EquivalentTo(cells));
                Assert.That(solo, Is.EquivalentTo(cells));
                Assert.That(noEntry, Is.EquivalentTo(new[] { deep, otherDungeon }));
            });
        }

        [Test]
        public void CursedTombHasVerifiedEntryRoomGoalSpawns()
        {
            var entry = AutonomousDungeonGoalCatalog.VerifiedPointsForRegion(128)
                .Where(point => AutonomousDungeonPolicy.IsCursedTombEntryStage(128, point.Position))
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(entry.Length, Is.GreaterThanOrEqualTo(5));
                Assert.That(entry.Any(point => point.Name == "roaming corpse"), Is.True);
            });
        }

        [Test]
        public void DungeonRoomsNeverCombineDifferentLiveMonsterLevels()
        {
            Vector3 koalinthRoom = new(28576, 32270, 17026);
            var low = AutonomousWorldBotController.LiveRoomKey(koalinthRoom, 18);
            var high = AutonomousWorldBotController.LiveRoomKey(koalinthRoom, 27);
            Assert.That(low, Is.Not.EqualTo(high));
            Assert.That(low, Is.EqualTo(AutonomousWorldBotController.LiveRoomKey(
                koalinthRoom + new Vector3(20, 20, 5), 18)));
        }

        [Test]
        public void DungeonPartyWaitsForEveryMemberBeforeFirstCrossing()
        {
            Vector3 entrance = new(1000, 2000, 100);
            var staged = new[]
            {
                new AutonomousDungeonPolicy.GroupTransitMember(1, new(1000, 2000, 100), false),
                new AutonomousDungeonPolicy.GroupTransitMember(1, new(1400, 2000, 100), false),
            };
            Assert.That(AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(1, 21, entrance, staged), Is.True);
            Assert.That(AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(1, 21, entrance,
                [staged[0], new(1, new(1800, 2000, 100), false)]), Is.False);
            Assert.That(AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(1, 21, entrance,
                [staged[0], new(2, new(1000, 2000, 100), false)]), Is.False);
            Assert.That(AutonomousDungeonPolicy.GroupReadyForDungeonEntrance(1, 21, entrance,
                [new(21, new(33150, 32732, 16480), false), staged[1]]), Is.True);
        }

        [Test]
        public void DungeonPartyWaitsForEverySurvivorAtProtectedInteriorStaging()
        {
            Vector3 staging = new(10_000, 20_000, 500);
            var ready = new[]
            {
                new AutonomousDungeonPolicy.GroupTransitMember(221, staging, false),
                new AutonomousDungeonPolicy.GroupTransitMember(221, staging + new Vector3(240, 0, 0), false),
            };
            Assert.Multiple(() =>
            {
                Assert.That(AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(221, staging, ready), Is.True);
                Assert.That(AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(221, staging,
                    [ready[0], ready[1] with { Region = 200 }]), Is.False);
                Assert.That(AutonomousDungeonPolicy.GroupReadyAtInteriorStaging(221, staging,
                    [ready[0], ready[1] with { Position = staging + new Vector3(500, 0, 0) }]), Is.False);
            });
        }

        [Test]
        public void OnlySeparatedDungeonFollowerReturningToLeaderMayClearRejoinBlocker()
        {
            Vector3 leader = new(1000, 1000, 100);
            Assert.That(AutonomousDungeonPolicy.IsFollowerRejoinDestination(true, true, 129, 129,
                new(2000, 1000, 100), new(1100, 1000, 100), leader), Is.True);
            Assert.That(AutonomousDungeonPolicy.IsFollowerRejoinDestination(true, false, 129, 129,
                new(2000, 1000, 100), new(1100, 1000, 100), leader), Is.False);
            Assert.That(AutonomousDungeonPolicy.IsFollowerRejoinDestination(true, true, 129, 129,
                new(2000, 1000, 100), new(1400, 1000, 100), leader), Is.False);
            Assert.That(AutonomousDungeonPolicy.IsFollowerRejoinDestination(true, true, 129, 21,
                new(2000, 1000, 100), new(1100, 1000, 100), leader), Is.False);
            Assert.That(AutonomousDungeonPolicy.IsFollowerRejoinDestination(true, true, 129, 129,
                new(1400, 1000, 100), new(1100, 1000, 100), leader), Is.False);
        }

    }
}
