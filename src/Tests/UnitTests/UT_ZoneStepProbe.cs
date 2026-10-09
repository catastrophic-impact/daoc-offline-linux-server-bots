using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// Tool: the server's zone-step planning (AutonomousZoneItinerary) on the real meshes, with a trace
    /// of every route and crossing tried. Read only.
    /// Env: OFFLINE_DAOC_NAV_ROOT (folder with navmesh/, navmesh/seams.json and lib/Detour.dll),
    /// OFFLINE_DAOC_PROOF_OUT, OFFLINE_DAOC_ZONES = "id:offsetX:offsetY:width:height;..." (the region's zones),
    /// OFFLINE_DAOC_REGION = region id, OFFLINE_DAOC_STEPS = "x,y,z>x,y,z;..." (start and goal, world units).
    /// </summary>
    [TestFixture, Explicit("Zone step probe tool"), NonParallelizable]
    public class UT_ZoneStepProbe
    {
        [Test]
        public void ProbeZoneSteps()
        {
            string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
            string output = Environment.GetEnvironmentVariable("OFFLINE_DAOC_PROOF_OUT");
            string zoneSpec = Environment.GetEnvironmentVariable("OFFLINE_DAOC_ZONES");
            string stepSpec = Environment.GetEnvironmentVariable("OFFLINE_DAOC_STEPS");
            Assume.That(root != null && output != null && zoneSpec != null && stepSpec != null);
            ushort region = ushort.Parse(Environment.GetEnvironmentVariable("OFFLINE_DAOC_REGION") ?? "0");
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = root;
            var nav = PathfindingProvider.LocalPathfindingMgr;
            var lines = new List<string>();
            var zones = zoneSpec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(item =>
            {
                int[] v = item.Split(':').Select(int.Parse).ToArray();
                return new Zone(null, (ushort)v[0], $"zone{v[0]}", v[1], v[2], v[3], v[4], (ushort)v[0], false, 0, false, 0, 0, 0, 0, 0);
            }).ToList();
            try
            {
                foreach (Zone zone in zones) LocalPathfindingMgr.LoadNavMesh(zone);
                Zone At(Vector3 p) => zones.FirstOrDefault(z => p.X >= z.XOffset && p.X < z.XOffset + z.Width && p.Y >= z.YOffset && p.Y < z.YOffset + z.Height);
                foreach (string item in stepSpec.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    Vector3[] ends = item.Split('>').Select(p => p.Split(',').Select(float.Parse).ToArray())
                        .Select(v => new Vector3(v[0], v[1], v[2])).ToArray();
                    var trace = new List<string>();
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = AutonomousZoneItinerary.ProbeNextStep(zones, region, At(ends[0]), At(ends[1]), ends[0], ends[1], nav, out var step, trace);
                    lines.Add($"{item} => {(ok ? $"OK {step.Inside} > {step.Outside}" : "FAILED")} {watch.ElapsedMilliseconds}ms");
                    lines.AddRange(trace.Select(t => "    " + t));
                }
            }
            finally
            {
                foreach (Zone zone in zones) LocalPathfindingMgr.UnloadNavMesh(zone);
                Environment.CurrentDirectory = previous;
            }
            File.WriteAllLines(output, lines);
            TestContext.Progress.WriteLine(lines.FirstOrDefault() ?? "no steps");
        }
    }
}
