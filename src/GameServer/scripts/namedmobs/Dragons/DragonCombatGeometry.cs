using System;
using System.Numerics;

namespace DOL.GS
{
    /// <summary>Only the three mainland dragons: model reach and encounter throws.</summary>
    public static class DragonCombatGeometry
    {
        private const string ThrowActive = "dragon_throw_active";
        private const string ThrowUntil = "dragon_throw_until";
        public static bool IsDragon(GameObject target) => target is AlbGolestandt or MidGjalpinulva or HibCuuldurach;
        // A conservative body radius, not the wingspan. Never extends bow/spell range.
        public static int TargetReach(GameObject target) => IsDragon(target) &&
            target is GameNPC npc && (npc.Flags & GameNPC.eFlags.FLYING) == 0 ? 320 : 0;

        public static bool IsDisplacing(GamePlayer player) => player?.TempProperties.GetProperty<bool>(ThrowActive) == true;
        public static bool IsRecoveringFromThrow(GamePlayer player) => player != null &&
            player.TempProperties.GetProperty<long>(ThrowUntil) > GameLoop.GameLoopTime;

        // Half the former Albion lair throw (1815 - 391). No horizontal relocation.
        public const int AlbionThrowHeight = 712;

        public static bool LiftPlayer(GamePlayer player)
        {
            if (player == null || IsRecoveringFromThrow(player)) return false;
            return ThrowPlayer(player, player.CurrentRegionID, player.X, player.Y,
                player.Z + AlbionThrowHeight, player.Heading);
        }

        public static bool TeleportGrounded(GamePlayer player, ushort region, int x, int y, int oldAirZ, ushort heading)
        {
            if (player?.CurrentRegion == null || player.CurrentRegionID != region || IsRecoveringFromThrow(player)) return false;
            var zone = player.CurrentRegion.GetZone(x, y);
            var nav = PathfindingProvider.Instance;
            if (zone == null || zone != player.CurrentZone || !nav.IsAvailable || !nav.HasNavmesh(zone)) return false;
            // The old coordinates describe an airborne destination. Resolve its floor,
            // and reject isolated surfaces instead of depositing players on props.
            Vector3? floor = nav.GetClosestPoint(zone, new Vector3(x, y, oldAirZ), 64, 64, 4096, nav.DefaultFilters);
            Vector3? origin = nav.GetClosestPoint(zone, new Vector3(player.X, player.Y, player.Z), 64, 64, 128, nav.DefaultFilters);
            if (!floor.HasValue || !origin.HasValue ||
                !AutonomousZoneItinerary.HasCompleteCorridor(nav, zone, origin.Value, floor.Value)) return false;
            return ThrowPlayer(player, region, (int)Math.Round(floor.Value.X), (int)Math.Round(floor.Value.Y),
                (int)Math.Round(floor.Value.Z), heading);
        }

        public static bool ThrowPlayer(GamePlayer player, ushort region, int x, int y, int z, ushort heading)
        {
            // Encounter displacement is NOT a party portal. Keep helpers and pets fighting
            // on the ground; the real client handles its player's fall normally.
            player.TempProperties.SetProperty(ThrowActive, true);
            player.TempProperties.SetProperty(ThrowUntil, GameLoop.GameLoopTime + 15000);
            try { return player.MoveTo(region, x, y, z, heading); }
            finally { player.TempProperties.RemoveProperty(ThrowActive); }
        }
    }
}
