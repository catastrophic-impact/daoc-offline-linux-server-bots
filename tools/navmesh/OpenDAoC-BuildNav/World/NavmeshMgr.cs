using System.Diagnostics;
using CEM.Client.ZoneExporter;
using CEM.Utils;

namespace CEM.World
{
    internal static class NavmeshMgr
    {
        public const float CONVERSION_FACTOR = 1.0f / 32f;

        /// <summary>
        /// Must match MAX_OFFMESH_CONNECTIONS in RecastDemo Include/InputGeom.h (stock value was 256).
        /// </summary>
        public const int RecastOffMeshConnectionLimit = 4096;

        public static void BuildNavMesh(Zone2 z)
        {
            if (z.Name == "ArtOutside" || z.Name == "ArtInside")
            {
                Log.Normal($"Skipping zone {z} because it has name {z.Name}");
                return;
            }
            if (z.ProxyZone != 0)
            {
                Log.Normal($"Skipping zone {z} because it has a proxy zone id {z.ProxyZone}");
                return;
            }

            string obj = z.ObjFile;
            string gset = obj.Replace(".obj", ".gset");
            string nav = z.NavFile.Replace(".gz", "");
            string laddersPath = obj.Replace(".obj", ".ladders.json");

            // Pass 0: export geometry + ladder definitions (no ladder off-mesh links yet).
            DateTime start = DateTime.Now;
            Log.Normal($"Building navmesh for zone {z}...");
            if (File.Exists(obj))
                File.Delete(obj);
            if (File.Exists(gset))
                File.Delete(gset);
            if (File.Exists(laddersPath))
                File.Delete(laddersPath);

            using (Zone2Obj exp = new(z))
                exp.Export();

            VerifiedClassicMeshRepairs.Apply(z, gset);

            if (Program.Arguments.ExportObjOnly)
                return;

            // Pass 1: walkable mesh without ladder off-mesh links.
            RunRecast(gset, nav, z.Name, pass: 1);

            LadderDefinitionFile? ladderFile = LadderDefinitionWriter.TryRead(laddersPath);
            List<LadderDefinition> ladders = ladderFile?.Definitions.ToList() ?? [];

            if (ladders.Count > 0 && File.Exists(nav))
            {
                Log.Normal($"Placing ladder off-mesh links for zone {z} ({ladders.Count} ladder(s))...");
                LadderLinkPlacer.PlacementResult placement = LadderLinkPlacer.Place(nav, ladders);
                List<LadderLinkPlacer.OffMeshLink> links = placement.Links;

                if (links.Count > 0)
                {
                    if (links.Count > RecastOffMeshConnectionLimit)
                    {
                        // Keep complete 3-link hops only (entry + climb + exit).
                        int keep = RecastOffMeshConnectionLimit - (RecastOffMeshConnectionLimit % 3);
                        int hopsKept = keep / 3;
                        Log.Error($"Zone {z} produced {links.Count} ladder off-mesh links but limit is {RecastOffMeshConnectionLimit}; truncating to {keep} ({hopsKept} hop(s)).");
                        links = links.Take(keep).ToList();
                        // Pads are 1:1 with floors; without hop→pad mapping, keep all pads (harmless extras).
                    }

                    // Inject virtual horizontal pads so intermediate OMC ends land on real polys.
                    if (placement.Pads.Count > 0 && File.Exists(obj))
                    {
                        LadderPadObjWriter.AppendPads(obj, placement.Pads, CONVERSION_FACTOR);
                        LadderPadObjWriter.AppendLadderPadVolumes(gset, placement.Pads);
                    }
                    else if (placement.Pads.Count > 0)
                        Log.Warn($"Zone {z}: ladder pads ready but obj missing; pass-2 may fail to chain links.");

                    int written = GeomSetWriter.AppendOffMeshConnections(
                        gset,
                        links.Select(l => (l.Start, l.End)));

                    Log.Normal($"Appended {written} ladder off-mesh connection(s) to {gset}");

                    // Pass 2: rebuild navmesh with pads + ladder links baked in.
                    RunRecast(gset, nav, z.Name, pass: 2);
                }
                else
                {
                    Log.Warn($"Zone {z}: no valid ladder off-mesh links after navmesh snap.");
                }
            }
            else if (ladders.Count > 0)
            {
                Log.Warn($"Zone {z}: ladders found but pass-1 navmesh missing; skipping ladder links.");
            }

            if (!File.Exists(nav))
            {
                Log.Error($"Did not generate navmesh for file {nav} for unknown reasons");
            }
            else if (new FileInfo(nav).Length < 2048)
            {
                Log.Warn($"{nav} was empty :(");
                File.Delete(nav);
            }

            Log.Normal($"Zone {z} finished in {DateTime.Now - start}");
        }

        private static void RunRecast(string gset, string nav, string zoneName, int pass)
        {
            Log.Normal($"Running RecastDemo.exe for {zoneName} (pass={pass})");

            if (File.Exists(nav))
                File.Delete(nav);

            Process buildnav = Process.Start("RecastDemo.exe", [gset, nav]);
            buildnav.PriorityClass = ProcessPriorityClass.BelowNormal;
            buildnav.WaitForExit();
            if (buildnav.ExitCode > 0)
                throw new InvalidOperationException("RecastDemo.exe failed with " + buildnav.ExitCode);
        }
    }
}
