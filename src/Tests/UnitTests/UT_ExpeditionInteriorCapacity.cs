using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable, Explicit("Read-only installed epic interior formation check")]
public class UT_ExpeditionInteriorCapacity
{
    [Test]
    public void InteriorPosts()
    {
        string root=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        string previous=Environment.CurrentDirectory;
        var loaded=new List<Zone>();
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,
                (name,assembly,search)=>name=="lib/Detour"?NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")):IntPtr.Zero);
            Environment.CurrentDirectory=root;
            using var db=new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");
            db.Open();
            foreach(int id in new[]{60,160,191})
            {
                var region=(Region)typeof(UT_AuditedDungeonInstalledMesh).GetMethod("BuildRegion",BindingFlags.NonPublic|BindingFlags.Static)
                    .Invoke(null,new object[]{db,id,loaded});
                using var cmd=db.CreateCommand();
                cmd.CommandText=$"SELECT TargetX,TargetY,TargetZ FROM ZonePoint WHERE TargetRegion={id} AND SourceRegion<>{id} LIMIT 1";
                using var reader=cmd.ExecuteReader();
                Assert.That(reader.Read(),Is.True);
                Vector3 raw=new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2));
                var zone=region.GetZone((int)raw.X,(int)raw.Y);
                var nav=PathfindingProvider.LocalPathfindingMgr;
                Vector3 center=nav.GetClosestPoint(zone,raw,48,48,128,nav.DefaultFilters).Value;
                var posts=new List<Vector3>();
                for(int i=0;i<RealmRaidRecruitmentPolicy.MaximumParties;i++)
                {
                    bool valid=RealmRaidFormation.TryResolve(nav,zone,center,i,posts,out var p);
                    TestContext.Progress.WriteLine($"INTERIOR {id} slot={i} valid={valid} point={p}");
                    if(valid)posts.Add(p);
                }
                TestContext.Progress.WriteLine($"CAPACITY {id}: {posts.Count}/38");
                Assert.That(posts.Count,Is.EqualTo(RealmRaidRecruitmentPolicy.MaximumParties),$"Interior {id} must accommodate every expedition party");
            }
        }
        finally { foreach(var zone in loaded)LocalPathfindingMgr.UnloadNavMesh(zone);Environment.CurrentDirectory=previous; }
    }
}
