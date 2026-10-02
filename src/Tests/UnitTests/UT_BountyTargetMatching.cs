using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_BountyTargetMatching
{
    [Test]
    public void OrdinaryBountyMatchesSpeciesAcrossAssignedLevelVariants()
    {
        // A Vigilant Rock strapper-vine camp has levels 28-33. Its assigned
        // level selects the bounty, but must not narrow which vines count.
        foreach (byte assignedLevel in new byte[] { 28, 29, 30, 31, 32, 33 })
        {
            var bounty = new BountyTargetCandidate
            {
                Name = "strapper vine",
                Level = assignedLevel,
                RegionId = 181,
                ZoneId = 186
            };

            Assert.That(bounty.MatchesOrdinaryMonster("Strapper Vine", eRealm.None, 181, 186),
                Is.True, $"Assigned level {assignedLevel} must not limit same-name kills.");
        }
    }

    [Test]
    public void OrdinaryBountyAcceptsSameNamedOutdoorMonsterAcrossHomeRealmZones()
    {
        var bounty = new BountyTargetCandidate
        {
            Name = "large frog",
            Level = 31,
            RegionId = 200,
            ZoneId = 207
        };

        Assert.Multiple(() =>
        {
            // Connacht and Lough Derg are different zones in the same region.
            Assert.That(bounty.MatchesOrdinaryMonster("Large Frog", eRealm.None, 200, 207), Is.True);
            Assert.That(bounty.MatchesOrdinaryMonster("large frog", eRealm.None, 200, 200), Is.True);
            // Classic and SI Hibernia also share one home-realm hunt pool.
            Assert.That(bounty.MatchesOrdinaryMonster("large frog", eRealm.None, 220, 220), Is.True);
        });
    }

    [Test]
    public void OrdinaryBountyStillRejectsOtherNamesRealmsDungeonMobsAndMissingZones()
    {
        var bounty = new BountyTargetCandidate
        {
            Name = "strapper vine",
            Level = 31,
            RegionId = 181,
            ZoneId = 186
        };

        Assert.Multiple(() =>
        {
            Assert.That(bounty.MatchesOrdinaryMonster("strangler vine", eRealm.None, 181, 186), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.None, 1, 186), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.None, 100, 186), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.None, 999, 186), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.Hibernia, 181, 186), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.None, 181, 185, monsterIsDungeon: true), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("strapper vine", eRealm.None, 181, null), Is.False);
        });
    }

    [Test]
    public void DungeonBountyRemainsBoundToAssignedDungeonZone()
    {
        var bounty = new BountyTargetCandidate
        {
            Name = "cave spider",
            RegionId = 221,
            ZoneId = 223,
            IsDungeon = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(bounty.MatchesOrdinaryMonster("cave spider", eRealm.None, 221, 223, true), Is.True);
            Assert.That(bounty.MatchesOrdinaryMonster("cave spider", eRealm.None, 221, 224, true), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("cave spider", eRealm.None, 220, 223, true), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("cave spider", eRealm.None, 221, 223, false), Is.False);
        });
    }

    [Test]
    public void CertifiedDarknessFallsBountyCountsOnlyVerifiedSpawnsInItsDungeonZone()
    {
        var bounty = new BountyTargetCandidate
        {
            Name = "demonic familiar",
            RegionId = AutonomousDarknessFallsPolicy.RegionId,
            ZoneId = 249,
            IsDungeon = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(bounty.MatchesOrdinaryMonster("Demonic Familiar", eRealm.None,
                249, 249, true, certifiedDarknessFallsSpawn: true), Is.True);
            Assert.That(bounty.MatchesOrdinaryMonster("demonic familiar", eRealm.None,
                249, 249, true), Is.False, "An unverified same-name DF mob cannot give credit.");
            Assert.That(bounty.MatchesOrdinaryMonster("demonic familiar", eRealm.None,
                249, 248, true, certifiedDarknessFallsSpawn: true), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("demonic familiar", eRealm.None,
                200, 249, true, certifiedDarknessFallsSpawn: true), Is.False);
            Assert.That(bounty.MatchesOrdinaryMonster("demonic familiar", eRealm.Hibernia,
                249, 249, true, certifiedDarknessFallsSpawn: true), Is.False);
        });
    }

    [Test]
    public void DarknessFallsBountyProofFilterKeepsOnlyOrdinaryReachableLevelOneToFortyNine()
    {
        var proof = new AutonomousDarknessFallsNavigation.SpawnProof
        {
            Id = "certified-ground-spawn", Name = "demonic familiar", Level = 25,
            Spawn = [35000, 30000, 20000], AttackableFromApproach = true,
            Routes =
            [
                new() { Realm = eRealm.Albion },
                new() { Realm = eRealm.Midgard },
                new() { Realm = eRealm.Hibernia }
            ]
        };

        Assert.Multiple(() =>
        {
            foreach (eRealm realm in new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia })
                Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(realm, proof), Is.True);
            Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.None, proof), Is.False);
        });
        proof.Level = 50;
        Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.Hibernia, proof), Is.False);
        proof.Level = 1;
        Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.Hibernia, proof), Is.True);
        proof.Flags = (uint)GameNPC.eFlags.FLYING;
        Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.Hibernia, proof), Is.False);
        proof.Flags = 0;
        proof.Id = "504d573f-deab-4cb2-9dbc-d9d053d7af2f";
        Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.Hibernia, proof), Is.False,
            "A frozen raid ID can never become an ordinary bounty.");
        proof.Id = "certified-ground-spawn";
        proof.Routes = [new() { Realm = eRealm.Albion }];
        Assert.That(BountyTargetCatalog.IsOrdinaryBountyDarknessFallsProof(eRealm.Hibernia, proof), Is.False);
    }

    [Test]
    public void EpicBountiesRemainExactSpawnOnly()
    {
        var bounty = new BountyTargetCandidate
        {
            Name = "King Tuscar",
            RegionId = 160,
            ZoneId = 160,
            IsEpic = true,
            RepresentativeMobId = "boss-id"
        };

        Assert.That(bounty.MatchesOrdinaryMonster("King Tuscar", eRealm.None, 160, 160), Is.False);
    }
}
