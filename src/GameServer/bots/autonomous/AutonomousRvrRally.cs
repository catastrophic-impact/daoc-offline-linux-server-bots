using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using DOL.GS.Keeps;

namespace DOL.GS;

/// <summary>Physical, separated siege camps. Failed projections never count as attendance.</summary>
public static class AutonomousRvrRally
{
    public const int ArrivalRadius = 100;
    public const int CampRadius = 9000;
    public const int MinimumKeepDistance = 6500;
    public const int PostVariations = 24;
    private static readonly ConcurrentDictionary<(int Keep,eRealm Attacker,eRealm Defender), int> Orientations = new();

    public static Vector3 AttackerPost(Vector3 center, int keepId, bool primaryAttacker, int slot, int variation = 0)
    {
        double angle = (keepId % 8) * Math.PI / 4 + (primaryAttacker ? 0 : Math.PI);
        Vector3 outward = new((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
        Vector3 sideways = new(-outward.Y, outward.X, 0);
        // Compact eight-person blocks with clear space between warbands.
        int block = slot / 8;
        float across = (block % 4 - 1.5f) * 650 + (slot % 2 - 0.5f) * 100;
        // A cliff can block every point on the same outward ray. Try modest
        // lateral offsets within this army's wing, never the opposing camp.
        across += (variation / 6) switch { 1 => 300, 2 => -300, _ => 0 };
        float depthOffset = variation < 18 ? variation % 6 * 400 : -(variation - 17) * 100;
        float depth = (block / 4 - 1.5f) * 650 + (slot % 8 / 2 - 1.5f) * 100 + depthOffset;
        return center + outward * (CampRadius + depth) + sideways * across;
    }

    public static bool TryPost(GameBot bot, AbstractGameKeep keep, AutonomousRvrEventLayer.RallyOrder order,
        int memberIndex, out Vector3 point)
    {
        int slot = order.Slots[memberIndex];
        if (bot.Realm == order.Defender)
            return TryDefenderPost(bot, keep, slot, out point);
        point = default;
        var nav = PathfindingProvider.Instance;
        Vector3 center = new(keep.X, keep.Y, keep.Z);
        // Choose a shared pair of separated wings; both armies agree on it.
        int orientation = Orientations.GetOrAdd((keep.KeepID,order.Attacker,order.Defender), _ => ChooseOrientation(keep,order.Attacker));
        for (int variation = 0; variation < PostVariations; variation++)
        {
            bool primary = bot.Realm == order.Attacker;
            Vector3 raw = AttackerPost(center, OrientationFor(orientation, primary), primary, slot, variation);
            Zone zone = WorldMgr.GetRegion(keep.Region)?.GetZone((int)raw.X, (int)raw.Y);
            if (zone == null || !AutonomousRealmBoundary.Allows(bot.Realm, keep.Region, zone.ID) ||
                !nav.IsAvailable || !nav.HasNavmesh(zone)) continue;
            Vector3? floor = nav.GetClosestPoint(zone, raw, 72, 72, 4096, nav.DefaultFilters);
            if (!floor.HasValue || Vector2.Distance(new(floor.Value.X, floor.Value.Y), new(center.X, center.Y)) < MinimumKeepDistance)
                continue;
            if (!AutonomousRendezvousNavigation.HasLocalExit(nav, zone, floor.Value)) continue;
            if (bot.CurrentRegionID == keep.Region && !HasRoute(bot.CurrentRegion, nav, bot.Realm,
                    new(bot.X, bot.Y, bot.Z), floor.Value)) continue;
            point = floor.Value;
            return true;
        }
        return false;
    }

    public static bool HasRoute(Region region, IPathfindingMgr nav, eRealm realm, Vector3 start, Vector3 goal)
    {
        Zone from = region?.GetZone((int)start.X, (int)start.Y);
        Zone to = region?.GetZone((int)goal.X, (int)goal.Y);
        if (from == null || to == null || !AutonomousRealmBoundary.Allows(realm, region.ID, to.ID)) return false;
        for (int leg = 0; leg < 10; leg++)
        {
            if (from == to) return AutonomousZoneItinerary.HasCompleteCorridor(nav, from, start, goal);
            if (!AutonomousZoneItinerary.TryNextStep(region, from, to, start, goal, nav, out var next,
                    zone => AutonomousRealmBoundary.Allows(realm, region.ID, zone.ID))) return false;
            start = next.Outside;
            from = region.GetZone((int)start.X, (int)start.Y);
            if (from == null) return false;
        }
        return false;
    }

    private static int ChooseOrientation(AbstractGameKeep keep,eRealm attacker)
    {
        return ChooseOrientation(keep.KeepID, keep.Region, keep.Realm, keep.IsRelic,
            new(keep.X, keep.Y, keep.Z), WorldMgr.GetRegion(keep.Region), PathfindingProvider.Instance, attacker);
    }

    public static int ChooseOrientation(int keepId, ushort regionId, eRealm defender, bool relic,
        Vector3 center, Region region, IPathfindingMgr nav, eRealm attacker)
    {
        // These two classic keeps sit against constrained map terrain. A
        // diametrically opposed camp puts one army on an isolated hill or
        // beyond the usable frontier. Installed-mesh validation covers every
        // slot on these perpendicular, still widely separated wings.
        if (regionId == 1 && keepId == 50) return 4 | (2 << 3); // west / south
        if (regionId == 1 && keepId == 58) return 0 | (6 << 3); // east / north
        int cap = relic ? 192 : 128;
        eRealm other=(eRealm)(6-(int)attacker-(int)defender);
        var a=AutonomousFrontierTransport.Destination(attacker,regionId)?.Location;
        var b=AutonomousFrontierTransport.Destination(other,regionId)?.Location;
        int preferred=a==null || b==null ? keepId%8 :
            ((int)Math.Round(Math.Atan2(a.Y-b.Y,a.X-b.X)/(Math.PI/4))+8)%8;
        int best = preferred, bestScore = -1;
        for (int offset = 0; offset < 8; offset++)
        {
            int orientation = (preferred + (offset%2==0 ? offset/2 : -(offset+1)/2)+8) % 8;
            int[] scores = new int[2];
            for (int side = 0; side < 2; side++)
            for (int slot = 0; slot < cap; slot += 8)
            {
                Vector3 raw = AttackerPost(center, orientation, side == 0, slot);
                Zone zone = region?.GetZone((int)raw.X, (int)raw.Y);
                if (zone != null && AutonomousRealmBoundary.Allows(side == 0 ? attacker : other, regionId, zone.ID) &&
                    nav.IsAvailable && nav.HasNavmesh(zone) &&
                    nav.GetClosestPoint(zone, raw, 72, 72, 4096, nav.DefaultFilters).HasValue) scores[side]++;
            }
            int score = Math.Min(scores[0], scores[1]);
            if (score > bestScore) { bestScore = score; best = orientation; }
            if (score == cap / 8) break;
        }
        return best | (best << 3);
    }

    public static int OrientationFor(int pair, bool primary) => primary ? pair & 7 : (pair >> 3) & 7;

    public static bool TryDefenderPost(GameBot bot, AbstractGameKeep keep, int slot, out Vector3 point)
        => TryDefenderPost(bot, keep, slot, PathfindingProvider.Instance, null, out point);

    public static bool TryDefenderPost(GameBot bot, AbstractGameKeep keep, int slot, IPathfindingMgr nav,
        Vector3? routeStart, out Vector3 point)
    {
        point = default;
        GameKeepGuard lord = keep.Guards.Values.OfType<GuardLord>().FirstOrDefault();
        // Classic relic keeps have commanders and a shrine, not a keep lord.
        lord ??= keep.IsRelic ? keep.Guards.Values.OfType<GuardCommander>()
            .OrderBy(guard=>Vector2.DistanceSquared(new(guard.X,guard.Y),new(keep.X,keep.Y))).FirstOrDefault() : null;
        if (lord == null || !nav.IsAvailable) return false;
        bool protectObjective = routeStart.HasValue && AutonomousRvrDefense.ObjectiveExposed(keep);
        bool ranged = AutonomousRvrDefense.IsRangedDefender(bot);
        bool support = bot.CharacterClass != null &&
            (BotPartyRoles.For((eCharacterClass)bot.CharacterClass.ID) == BotPartyRole.Support ||
             (eCharacterClass)bot.CharacterClass.ID == eCharacterClass.Shaman);
        int gateHeight = keep.Doors.Values.Where(door => door.IsAttackableDoor).Select(door => door.Z).DefaultIfEmpty(keep.Z).Min();
        var entry = keep.Doors.Values.Where(door => door.IsAttackableDoor).OrderByDescending(door => door.GetDistanceTo(lord)).FirstOrDefault();
        int insideRadius = entry == null ? 1800 : entry.GetDistanceTo(lord);
        var guards = keep.Guards.Values.Where(guard => guard.Realm == bot.Realm &&
            guard.GetDistanceTo(lord) < 2400 && (protectObjective ? guard == lord : ranged ? guard is GuardArcher && guard.Z > gateHeight + 100 :
                support ? guard == lord : guard == lord ||
                    guard is GuardFighter or GuardFighterRK && guard.GetDistanceTo(lord) < insideRadius - 100))
            .OrderBy(guard => !ranged && !support && entry != null ? guard.GetDistanceTo(entry) : 0)
            .ThenBy(guard => guard.ObjectID).ToArray();
        if (guards.Length == 0) return false;
        // Use the authored wall-archer floors for ranged defenders. Other
        // defenders spread through the interior around the lord; no doorway
        // fallback can be mistaken for being inside and ready.
        for (int attempt = 0; attempt < guards.Length; attempt++)
        {
            var guard = guards[(slot + attempt) % guards.Length];
            int local = slot / guards.Length;
            for (int turn = 0; turn < 8; turn++)
            {
                // Slots are global across the realm, not a count of healers
                // at this anchor. A late support slot must not spiral 1,500
                // units away from the lord's small floor and become invalid.
                double angle = (local % 8 + turn) * Math.PI / 4 + local / 24 * 0.137;
                int radius = 48 + local / 8 % 3 * 24;
                Vector3 raw = new(guard.X + (float)Math.Cos(angle) * radius,
                    guard.Y + (float)Math.Sin(angle) * radius, guard.Z);
                if (!nav.HasNavmesh(guard.CurrentZone)) continue;
                Vector3? floor = nav.GetClosestPoint(guard.CurrentZone, raw, 24, 24, 64, nav.DefaultFilters);
                // For active defenders the real actor-to-post route is the
                // proof. Some authored guard coordinates sit just outside the
                // mesh despite their adjacent floor being fully reachable.
                if (!floor.HasValue || Math.Abs(floor.Value.Z - guard.Z) > 64 ||
                    !(routeStart.HasValue ? HasRoute(bot.CurrentRegion, nav, bot.Realm, routeStart.Value, floor.Value) :
                        AutonomousZoneItinerary.HasCompleteCorridor(nav, guard.CurrentZone,
                            new(guard.X, guard.Y, guard.Z), floor.Value))) continue;
                point = floor.Value;
                return true;
            }
            Vector3 anchor = new(guard.X,guard.Y,guard.Z);
            Vector3? anchorFloor = nav.GetClosestPoint(guard.CurrentZone,anchor,24,24,64,nav.DefaultFilters);
            if(anchorFloor.HasValue && Math.Abs(anchorFloor.Value.Z-guard.Z)<=64 &&
                (routeStart.HasValue ? HasRoute(bot.CurrentRegion,nav,bot.Realm,routeStart.Value,anchorFloor.Value) :
                    AutonomousZoneItinerary.HasCompleteCorridor(nav,guard.CurrentZone,anchor,anchorFloor.Value)))
            {
                point=anchorFloor.Value;
                return true;
            }
        }
        return false;
    }

}
