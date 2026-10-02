using DOL.AI.Brain;
using DOL.Database;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousNaturalMobAggro
{
    [Test]
    public void GameBotIsPlayerLikeForNaturalMobAggro()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StandardMobBrain.IsPlayerLikeNaturalAggroType(typeof(GameBot)), Is.True);
            Assert.That(StandardMobBrain.IsPlayerLikeNaturalAggroType(typeof(GamePlayer)), Is.True);
            Assert.That(StandardMobBrain.IsPlayerLikeNaturalAggroType(typeof(GameNPC)), Is.False);
        });
    }

    [TestCase(100, Faction.Standing.AGGRESIVE)]
    [TestCase(75, Faction.Standing.HOSTILE)]
    [TestCase(50, Faction.Standing.NEUTRAL)]
    [TestCase(25, Faction.Standing.FRIENDLY)]
    public void PersistentBotUsesSameInitialFactionStandingAsNewPlayer(int baseAggro, Faction.Standing expected)
    {
        var faction = new Faction();
        faction.LoadFromDatabase(new DbFaction { BaseAggroLevel = baseAggro, Name = "test" });

        Assert.That(faction.GetDefaultStanding(), Is.EqualTo(expected));
    }

    [Test]
    public void DirectHitTransitionsBotBeforeItsFirstWokenCombatPulse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotBrain.ShouldEnterAggroStateAfterDirectAttack(true, false), Is.True);
            Assert.That(BotBrain.ShouldEnterAggroStateAfterDirectAttack(true, true), Is.False);
            Assert.That(BotBrain.ShouldEnterAggroStateAfterDirectAttack(false, false), Is.False);
        });
    }
}
