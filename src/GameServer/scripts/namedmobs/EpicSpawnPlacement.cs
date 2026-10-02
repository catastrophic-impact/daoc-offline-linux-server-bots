using System.Numerics;

namespace DOL.GS
{
    /// <summary>Exact legacy placements proven to be in rock/on an unclimbable wall.</summary>
    public static class EpicSpawnPlacement
    {
        public static Vector3 Correct(ushort region, string name, Vector3 original)
        {
            if (region != 160) return original;
            // Original collision OBJ + native entry/return paths checked. These
            // are existing mobs moved onto their adjacent corridor floors, not
            // extra spawns or a general snap-to-ground rule for flying actors.
            if (name == "icebound skeleton" && original == new Vector3(44673, 34675, 14483))
                return new(44676, 34300, 14480);
            if (name == "hrimthursa berg" && original == new Vector3(29194, 33054, 15198))
                return new(29194, 33310, 14412);
            return original;
        }
    }
}
