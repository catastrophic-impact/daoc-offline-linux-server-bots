namespace DOL.GS
{
    public static class DragonFlightLanding
    {
        // Use all three dimensions. A client showing a grounded animation is
        // not evidence that the authoritative airborne position has descended.
        public static bool HasArrived(IPoint3D position, IPoint3D home) =>
            position != null && home != null && new Point3D(position.X, position.Y, position.Z).IsWithinRadius(home, 32);
    }
}
