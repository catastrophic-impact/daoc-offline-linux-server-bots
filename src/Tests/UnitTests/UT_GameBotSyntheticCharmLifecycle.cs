using System.Reflection;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[TestFixture]
public sealed class UT_GameBotSyntheticCharmLifecycle
{
    [Test]
    public void GameBotCandidatesCannotInheritWorldMobRespawn()
    {
        Assert.That(RespawnInterval(80_000, true), Is.EqualTo(-1));
    }

    [Test]
    public void PlayerCandidatesKeepExistingRespawnBehavior()
    {
        Assert.That(RespawnInterval(80_000, false), Is.EqualTo(80_000));
    }

    [Test]
    public void UnboundGameBotCandidateGetsTwoSecondCharmHandoffGrace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BindingExpired(10_000, 0), Is.False);
            Assert.That(BindingExpired(11_999, 10_000), Is.False);
            Assert.That(BindingExpired(12_000, 10_000), Is.True);
        });
    }

    [Test]
    public void EndingOldGeneratedCharmCannotReleaseReplacementPet()
    {
        object oldBrain = new();
        object replacementBrain = new();

        Assert.Multiple(() =>
        {
            Assert.That(IsCurrentOwnerBrain(oldBrain, oldBrain), Is.True);
            Assert.That(IsCurrentOwnerBrain(replacementBrain, oldBrain), Is.False);
            Assert.That(IsCurrentOwnerBrain(replacementBrain, null), Is.False);
        });
    }

    private static int RespawnInterval(int inheritedInterval, bool gameBotOwner) =>
        (int)typeof(AutonomousPetSupport).GetMethod("SyntheticCharmRespawnInterval",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [inheritedInterval, gameBotOwner])!;

    private static bool BindingExpired(long now, long unboundSinceTick) =>
        (bool)typeof(AutonomousPetSupport).GetMethod("IsExpiredSyntheticCharmBinding",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [now, unboundSinceTick])!;

    private static bool IsCurrentOwnerBrain(object currentBrain, object endingBrain) =>
        (bool)typeof(CharmECSGameEffect).GetMethod("IsCurrentBotSyntheticCharmBrain",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [currentBrain, endingBrain])!;
}
