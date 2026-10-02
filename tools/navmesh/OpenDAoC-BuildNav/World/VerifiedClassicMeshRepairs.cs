using OpenTK;

namespace CEM.World
{
    /// <summary>
    /// Two client-geometry verified repairs, reproduced after each export.
    /// See Offline DAOC extra files/route-repair-20260830 for geometry profiles
    /// and native forward/reverse corridor checks. No other zone is altered.
    /// </summary>
    internal static class VerifiedClassicMeshRepairs
    {
        public static void Apply(Zone2 zone, string gset)
        {
            if (zone.ID == 2)
            {
                // 50-unit horizontal step, 45-unit drop from a low prop onto
                // open ground. Small endpoint radius avoids a nearby wrong floor.
                using var writer = new GeomSetWriter(gset, true);
                writer.WriteLine("c 16113.0000 106.1641 15446.0625 16113.0000 104.7619 15444.5000 0.4 1 5 8");
            }
            else if (zone.ID == 181)
            {
                // RC_NULL_AREA (0) removes only the buried ground beneath the
                // Domnann platform. Its upper walkable surface is above this
                // volume and is retained. Do not connect through the solid prop.
                using var writer = new GeomSetWriter(gset, true);
                writer.WriteConvexVolume(4, 5940, 5980, (GeomSetWriter.eAreas)0);
                writer.WriteConvexVolumeVertex(new Vector3(423030, 444295, 5940));
                writer.WriteConvexVolumeVertex(new Vector3(423260, 444295, 5940));
                writer.WriteConvexVolumeVertex(new Vector3(423260, 444525, 5940));
                writer.WriteConvexVolumeVertex(new Vector3(423030, 444525, 5940));
            }
        }
    }
}
