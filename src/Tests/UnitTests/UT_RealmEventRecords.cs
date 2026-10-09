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

    [Test]
    public void DeleteAllEmptiesOnlyTheRecordLedger()
    {
        string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"realm-records-"+Guid.NewGuid().ToString("N"));
        string path=System.IO.Path.Combine(folder,"realm-event-records.sqlite3");
        string neighbour=System.IO.Path.Combine(folder,"opendaoc.sqlite3.db");
        try
        {
            Assert.That(RealmEventRecordStore.DeleteAll(path),Is.EqualTo(0),"a missing ledger is not created");
            Assert.That(System.IO.File.Exists(path),Is.False);
            RealmEventRecordStore.Save(path,new[]{
                new RealmEventRecord("a","dragon-albion","Golestandt","Dragon","Albion","2026-10-01T00:00:00Z","2026-10-01T01:00:00Z","Ended","Boss defeated","",300,210),
                new RealmEventRecord("b","keep","Caer Benowyc","Keep","Midgard","2026-10-02T00:00:00Z","","Battle","In progress","",50,40)});
            System.IO.File.WriteAllText(neighbour,"game data");
            Assert.That(RealmEventRecordStore.DeleteAll(path),Is.EqualTo(2));
            Assert.That(RealmEventRecordStore.Read(path,"","","","",0).Total,Is.EqualTo(0));
            Assert.That(System.IO.File.ReadAllText(neighbour),Is.EqualTo("game data"));
            // The server can keep saving into the emptied ledger.
            RealmEventRecordStore.Save(path,new[]{new RealmEventRecord("c","keep","Caer Benowyc","Keep","Midgard","2026-10-03T00:00:00Z","","Battle","In progress","",50,40)});
            Assert.That(RealmEventRecordStore.Read(path,"","","","",0).Total,Is.EqualTo(1));
        }
        finally{System.Data.SQLite.SQLiteConnection.ClearAllPools();if(System.IO.Directory.Exists(folder))System.IO.Directory.Delete(folder,true);}
    }
}
