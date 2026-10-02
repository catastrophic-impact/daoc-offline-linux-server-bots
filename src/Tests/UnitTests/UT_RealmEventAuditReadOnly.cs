using System;
using System.IO;
using System.Data.SQLite;
using System.Numerics;
using System.Runtime.InteropServices;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests;

[NonParallelizable,Explicit("Read-only captured route diagnostics, never edits meshes or game state")]
public class UT_RealmEventAuditReadOnly
{
    [Test]public void InspectCapturedHibernianExpeditionPile()
    {
        string previous=Environment.CurrentDirectory;
        string root=Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        var region=new Region(new RegionData{Id=200,Name="Read-only probe",Description="Read-only probe",Mobs=[]});
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LocalPathfindingMgr).Assembly,(name,asm,search)=>name=="lib/Detour"?NativeLibrary.Load(Path.Combine(root,"lib","Detour.dll")):IntPtr.Zero);
            Environment.CurrentDirectory=root;
            using var db=new SQLiteConnection($"Data Source={Path.GetFullPath("../data/opendaoc.sqlite3.db")};Read Only=True;Pooling=False;");db.Open();
            using var command=db.CreateCommand();command.CommandText="SELECT ZoneID,Name,OffsetX,OffsetY,Width,Height FROM Zones WHERE RegionID=200";
            using(var rows=command.ExecuteReader())while(rows.Read())
            {
                ushort id=Convert.ToUInt16(rows[0]);var z=new Zone(region,id,rows.GetString(1),Convert.ToInt32(rows[2])*8192,Convert.ToInt32(rows[3])*8192,Convert.ToInt32(rows[4])*8192,Convert.ToInt32(rows[5])*8192,id,false,0,false,0,0,0,0,0){IsPathfindingEnabled=true};
                region.Zones.Add(z);LocalPathfindingMgr.LoadNavMesh(z);
            }
            var nav=PathfindingProvider.LocalPathfindingMgr;var h=DragonLairPlacement.Home(eRealm.Hibernia);var goal=new Vector3(h.X,h.Y,h.Z);
            foreach(var p in new[]{new Vector3(352160,680000,7224),new Vector3(352160,679872,7315),new Vector3(334820,719979,4296)})
            {
                var zone=region.GetZone((int)p.X,(int)p.Y);var floor=nav.GetClosestPoint(zone,p,64,64,128,nav.DefaultFilters);
                bool direct=RealmRaidMuster.TryRoute(region,nav,eRealm.Hibernia,p,goal,out var directSteps);
                Assert.That(direct, Is.True, "Ordinary Sheeroe travel must find the road without an expedition-only via hint");
                bool via=RealmRaidMuster.TryRoute(region,nav,eRealm.Hibernia,p,goal,out var viaSteps,new Vector3(361354,750434,4944));
                TestContext.Progress.WriteLine($"Captured {p} zone={zone.Description} floor={floor}: direct={direct} [{string.Join(";",directSteps)}] validated-via={via} [{string.Join(";",viaSteps)}]");
            }
        }
        finally{foreach(var z in region.Zones)LocalPathfindingMgr.UnloadNavMesh(z);Environment.CurrentDirectory=previous;}
    }
}
