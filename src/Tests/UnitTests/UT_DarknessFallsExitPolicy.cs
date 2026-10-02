using DOL.Database;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public class UT_DarknessFallsExitPolicy
{
    [Test]
    public void EachExitId_UsesCharactersOwnRealmRow_EvenWhenAlbionIsFirst()
    {
        foreach (ushort id in new ushort[] { 70, 72, 73, 74, 75, 76 })
        {
            DbZonePoint[] rows =
            [
                new() { Id = id, Realm = 1, SourceRegion = 249, TargetRegion = 1 },
                new() { Id = id, Realm = 2, SourceRegion = 249, TargetRegion = 100 },
                new() { Id = id, Realm = 3, SourceRegion = 249, TargetRegion = 200 },
            ];

            Assert.Multiple(() =>
            {
                Assert.That(DarknessFallsExitPolicy.SelectRealmExit(rows, eRealm.Albion, id), Is.SameAs(rows[0]));
                Assert.That(DarknessFallsExitPolicy.SelectRealmExit(rows, eRealm.Midgard, id), Is.SameAs(rows[1]));
                Assert.That(DarknessFallsExitPolicy.SelectRealmExit(rows, eRealm.Hibernia, id), Is.SameAs(rows[2]));
                Assert.That(DarknessFallsExitPolicy.SelectRealmExit(rows, eRealm.None, id), Is.Null);
            });
        }
    }

    [Test]
    public void WrongRealmOrSource_CannotBeSelected_AndOtherPortalsAreUnaffected()
    {
        DbZonePoint[] badRows =
        [
            new() { Id = 72, Realm = 1, SourceRegion = 249, TargetRegion = 1 },
            new() { Id = 72, Realm = 3, SourceRegion = 249, TargetRegion = 1 },
            new() { Id = 72, Realm = 3, SourceRegion = 100, TargetRegion = 200 },
        ];

        Assert.Multiple(() =>
        {
            Assert.That(DarknessFallsExitPolicy.SelectRealmExit(badRows, eRealm.Hibernia, 72), Is.Null);
            Assert.That(DarknessFallsExitPolicy.SelectRealmExit(badRows, eRealm.Albion, 74), Is.Null,
                "A different portal ID cannot be selected from an unexpected DB result.");
            Assert.That(DarknessFallsExitPolicy.IsExitRequest(249, 78), Is.False,
                "The internal Purgatory portal is not a realm exit.");
            Assert.That(DarknessFallsExitPolicy.IsExitRequest(200, 72), Is.False,
                "Other regions keep their existing portal rules.");
        });
    }
}
