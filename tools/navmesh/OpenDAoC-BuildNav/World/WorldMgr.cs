using CEM.Utils;

namespace CEM.World
{
    /// <summary>
    /// Static world class containing all world-relevant information
    /// </summary>
    internal static class WorldMgr
    {
        private static readonly Dictionary<ushort, Region> _regions = new Dictionary<ushort, Region>();
        private static readonly Dictionary<ushort, Zone2> _zones = new Dictionary<ushort, Zone2>();

        /// <summary>
        /// Retrieves a region, or returns null if not found
        /// </summary>
        public static Region GetRegion(int id)
        {
            return _regions.ContainsKey((ushort) id) ? _regions[(ushort) id] : null;
        }

        /// <summary>
        /// Looks up a specific zone by id, or returns null if not found
        /// </summary>
        public static Zone2 GetZone(int id)
        {
            return _zones.ContainsKey((ushort) id) ? _zones[(ushort) id] : null;
        }

        /// <summary>
        /// Returns all loaded zones
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<Zone2> GetAllZones()
        {
            return _zones.Values;
        }

        /// <summary>
        /// Returns all loaded regions
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<Region> GetAllRegions()
        {
            return _regions.Values;
        }

        /// <summary>
        /// Looks up a specific zone by name, or returns null if not found
        /// </summary>
        public static Zone2 FindZoneByName(string name)
        {
            return
              _regions.SelectMany(x => x.Value)
                      .Select(x => x.Value)
                      .FirstOrDefault(x => x.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// Loads all world related data
        /// </summary>
        public static void Init()
        {
            Log.Normal("Loading World...");

            // Regions
            foreach (var reg in Region.LoadRegions())
                _regions.Add(reg.ID, reg);
            // Zones
            foreach (var zone in Zone2.LoadZones(_regions))
                _zones.Add(zone.ID, zone);
        }
    }
}
