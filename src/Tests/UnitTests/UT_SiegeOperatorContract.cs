using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_SiegeOperatorContract
{
    [Test]
    public void PersistentBot_RamControlUsesThePlayerLikeServerContract()
    {
        Assert.That(typeof(IGamePlayer).IsAssignableFrom(typeof(GameBot)), Is.True);
        Assert.That(typeof(GameSiegeWeapon).GetProperty(nameof(GameSiegeWeapon.Owner))?.PropertyType, Is.EqualTo(typeof(GameLiving)));
        Assert.That(typeof(GameSiegeWeapon).GetMethod(nameof(GameSiegeWeapon.TryTakeControl), [typeof(GameLiving)]), Is.Not.Null);
    }
}
