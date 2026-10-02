namespace DOL.GS
{
    /// <summary>Lair anchors verified against the client mound geometry (not terrain beneath it).
    /// Flight coordinates are transient, never a spawn/home location.</summary>
    public static class DragonLairPlacement
    {
        public static Point3D Home(eRealm realm) => realm switch
        {
            eRealm.Albion => new Point3D(391326, 755351, 391),
            eRealm.Midgard => new Point3D(708811, 1021459, 3014),
            eRealm.Hibernia => new Point3D(408646, 706432, 2965),
            _ => throw new System.ArgumentOutOfRangeException(nameof(realm))
        };

        public static void Restore(GameNPC dragon, eRealm realm)
        {
            Point3D home = Home(realm);
            dragon.X = home.X;
            dragon.Y = home.Y;
            dragon.Z = home.Z;
            dragon.Flags = GroundFlags(dragon.Flags);
        }

        public static GameNPC.eFlags GroundFlags(GameNPC.eFlags flags) => flags & ~GameNPC.eFlags.FLYING;
        public static bool CanReceiveDamageFrom(GameObject source) => source != null && CanReceiveDamageType(source.GetType());
        public static bool CanReceiveDamageType(System.Type type) => type != null && typeof(GameLiving).IsAssignableFrom(type);
    }
}
