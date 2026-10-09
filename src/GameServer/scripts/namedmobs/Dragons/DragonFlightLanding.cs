namespace DOL.GS
{
    public static class DragonFlightLanding
    {
        // Use all three dimensions. A client showing a grounded animation is
        // not evidence that the authoritative airborne position has descended.
        public static bool HasArrived(IPoint3D position, IPoint3D home) =>
            position != null && home != null && new Point3D(position.X, position.Y, position.Z).IsWithinRadius(home, 32);
    }

    /// <summary>
    /// Guarantees a dragon's flight ends in a landing. Cuuldurach landed at 19:17 on
    /// 2026-10-02 and never logged another landing while the other two dragons landed
    /// hourly, and its raid sat on "Waiting for dragon landing". A flight that stops
    /// advancing along its route, or runs far longer than a normal route, flies home
    /// now; a landing approach that cannot arrive is completed in place.
    /// </summary>
    public sealed class DragonFlightWatch
    {
        public const long MaximumRouteMilliseconds = 20 * 60_000;
        public const long WaypointStallMilliseconds = 90_000;
        public const long LandingApproachMilliseconds = 3 * 60_000;
        private long _started, _progress, _landingStarted;
        private bool _landing;
        private int _index = -1;

        public bool Active { get; private set; }

        public void Begin(long now)
        {
            _started = _progress = now;
            _index = 0;
            _landing = false;
            Active = true;
        }

        public void End()
        {
            Active = _landing = false;
            _index = -1;
        }

        /// <summary>True when the route should be abandoned in favour of flying home.</summary>
        public bool ShouldCutShort(long now, int routeIndex)
        {
            if (!Active) Begin(now);
            if (routeIndex != _index) { _index = routeIndex; _progress = now; }
            return now - _started >= MaximumRouteMilliseconds || now - _progress >= WaypointStallMilliseconds;
        }

        /// <summary>True once the final approach home has taken too long to arrive.</summary>
        public bool ShouldForceLanding(long now)
        {
            if (!_landing) { _landing = true; _landingStarted = now; }
            return now - _landingStarted >= LandingApproachMilliseconds;
        }
    }
}
