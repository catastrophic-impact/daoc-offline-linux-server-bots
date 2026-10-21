using System;
using System.Numerics;
using System.Text;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    // Offline diagnostic only: reads installed navmeshes. PROBE="zone,offX,offY;fromX,fromY,fromZ;toX,toY,toZ|..."
    [TestFixture, NonParallelizable, Explicit("Reads installed navmeshes only")]
    public class UT_RouteSpotProbe
    {
        [Test]
        public void Probe()
        {
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var loaded = new System.Collections.Generic.Dictionary<ushort, Zone>();
            foreach (string spec in (Environment.GetEnvironmentVariable("PROBE") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = spec.Split(';');
                string[] z = p[0].Split(',');
                ushort id = ushort.Parse(z[0]);
                // One load per zone: unloading and reloading a mesh leaves a stale per-thread query behind (crash).
                if (!loaded.TryGetValue(id, out Zone zone))
                {
                    zone = new(null, id, "probe", int.Parse(z[1]) * 8192, int.Parse(z[2]) * 8192, 65536, 65536, id, false, 0, false, 0, 0, 0, 0, 0);
                    LocalPathfindingMgr.LoadNavMesh(zone);
                    loaded[id] = zone;
                }
                Vector3 V(string t) { var a = t.Split(','); return new(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); }
                {
                    Vector3 from = V(p[1]), to = V(p[2]);
                    Vector3? sf = nav.GetClosestPoint(zone, from, 64, 64, 400, nav.DefaultFilters);
                    Vector3? st = nav.GetClosestPoint(zone, to, 64, 64, 400, nav.DefaultFilters);
                    var nodes = new WrappedPathfindingNode[4096];
                    var r = nav.GetPathStraight(zone, from, to, nav.DefaultFilters, nodes);
                    Vector3 end = r.NodeCount > 0 ? nodes[r.NodeCount - 1].Position : from;
                    TestContext.WriteLine($"zone {id}: {from} -> {to}: {r.Status} nodes={r.NodeCount} end={end} endToTarget={Vector3.Distance(end, to):0} " +
                                          $"snapFrom={(sf.HasValue ? Vector3.Distance(sf.Value, from).ToString("0") : "none")} snapTo={(st.HasValue ? $"{st.Value} d={Vector3.Distance(st.Value, to):0}" : "none")}");
                }
            }
            foreach (Zone zone in loaded.Values) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
