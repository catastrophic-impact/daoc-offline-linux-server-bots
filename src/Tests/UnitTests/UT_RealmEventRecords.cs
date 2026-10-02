using System;
using System.Collections.Generic;
using System.Reflection;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable]
public class UT_RealmEventRecords
{
    private static Dictionary<string,RealmEventRecord> Field(string name)=>(Dictionary<string,RealmEventRecord>)typeof(RealmEventRecords).GetField(name,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
    [SetUp]public void Setup(){Field("Active").Clear();Field("Pending").Clear();}
    [TearDown]public void Cleanup(){Field("Active").Clear();Field("Pending").Clear();}
    [TestCase("Dragon")] [TestCase("Epic dungeon")] [TestCase("Keep")] [TestCase("Relic keep")] [TestCase("Relic")]
    public void LifecycleIsOneRecordPerAttemptAndQueuesOnlyMemory(string kind)
    {
        RealmEventRecords.Begin("test","Encounter",kind,"Albion","Automatic rally");
        string first=Field("Active")["test"].Id;
        RealmEventRecords.Begin("test","Encounter",kind,"Albion","Duplicate notification");
        Assert.That(Field("Pending").Count,Is.EqualTo(1));
        RealmEventRecords.Progress("test","Battle","Advancing",300,200);
        Assert.That(Field("Pending")[first].Present,Is.EqualTo(200));
        RealmEventRecords.Finish("test","Timed out","Four-hour battle ended");
        Assert.That(Field("Active"),Is.Empty);
        Assert.That(Field("Pending")[first].Outcome,Is.EqualTo("Timed out"));
        Assert.That(Field("Pending")[first].EndedUtc,Is.Not.Empty);
        RealmEventRecords.Finish("test","Boss defeated","Stale duplicate");
        Assert.That(Field("Pending")[first].Outcome,Is.EqualTo("Timed out"));
        RealmEventRecords.Begin("test","Encounter",kind,"Albion","A new rally");
        Assert.That(Field("Active")["test"].Id,Is.Not.EqualTo(first));
        Assert.That(Field("Pending").Count,Is.EqualTo(2));
    }
}
