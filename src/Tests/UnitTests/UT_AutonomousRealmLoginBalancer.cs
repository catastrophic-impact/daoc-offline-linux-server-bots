using System.Collections.Generic;
using NUnit.Framework;

namespace DOL.GS.Tests;

[TestFixture]
public sealed class UT_AutonomousRealmLoginBalancer
{
    [Test]
    public void StartupSelectionBalancesAllThreeRealmsRegardlessOfBotIds()
    {
        var candidates = new List<AutonomousRealmLoginBalancer.Candidate>();
        for (int i = 1; i <= 60; i++) candidates.Add(new(i, eRealm.Hibernia));
        for (int i = 61; i <= 120; i++) candidates.Add(new(i, eRealm.Midgard));
        for (int i = 121; i <= 180; i++) candidates.Add(new(i, eRealm.Albion));
        var load = new Dictionary<eRealm, int>();

        for (int i = 0; i < 54; i++)
        {
            long selected = AutonomousRealmLoginBalancer.SelectNext(candidates, load)!.Value;
            AutonomousRealmLoginBalancer.Candidate candidate = candidates.Find(entry => entry.BotId == selected);
            candidates.Remove(candidate);
            load[candidate.Realm] = load.TryGetValue(candidate.Realm, out int count) ? count + 1 : 1;
        }

        Assert.That(load[eRealm.Albion], Is.EqualTo(18));
        Assert.That(load[eRealm.Midgard], Is.EqualTo(18));
        Assert.That(load[eRealm.Hibernia], Is.EqualTo(18));
    }

    [Test]
    public void SelectionFillsTheLeastPopulatedAvailableRealmFirst()
    {
        AutonomousRealmLoginBalancer.Candidate[] candidates =
        [
            new(1, eRealm.Hibernia),
            new(2, eRealm.Midgard),
            new(3, eRealm.Albion),
        ];
        var load = new Dictionary<eRealm, int>
        {
            [eRealm.Albion] = 0,
            [eRealm.Midgard] = 4,
            [eRealm.Hibernia] = 44,
        };

        Assert.That(AutonomousRealmLoginBalancer.SelectNext(candidates, load), Is.EqualTo(3));
    }
}
