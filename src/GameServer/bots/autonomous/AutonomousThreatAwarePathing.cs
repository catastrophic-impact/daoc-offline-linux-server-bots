using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DOL.GS;

/// <summary>
/// Adds dynamic danger steering on top of the normal static navmesh. High-level
/// hostile creatures are treated as moving avoidance circles; doors, walls and
/// terrain remain the navmesh's responsibility.
/// </summary>
public static class AutonomousThreatAwarePathing
{
    private const int ThreatScanRadius = 2300;
    private static readonly int[] CandidateAngles = [0, -25, 25, -50, 50, -80, 80, -115, 115, 180];

    public readonly record struct SafeStep(Vector3 Position, bool Detouring, string Reason);

    public static SafeStep ChooseStep(
        GameLiving actor,
        Vector3 current,
        Vector3 desired,
        float maximumStep,
        GameLiving intendedTarget = null)
    {
        if (actor?.CurrentZone == null)
            return new(desired, false, string.Empty);

        // Bot return/resurrection callers may supply a destination across an
        // entire zone. A local surface query is not a long-distance pathfinder.
        if (actor is GameBot)
            desired = BoundStep(current, desired, maximumStep);

        List<GameNPC> threats = actor.GetNPCsInRadius(ThreatScanRadius)
            .Where(npc => IsRouteThreat(actor, npc, intendedTarget))
            .ToList();
        if (threats.Count == 0)
            return new(Snap(actor, current, desired), false, string.Empty);

        Vector2 goalVector = new(desired.X - current.X, desired.Y - current.Y);
        float goalDistance = goalVector.Length();
        if (goalDistance < 1f)
            return new(current, false, string.Empty);

        float step = Math.Min(maximumStep, goalDistance);
        double baseAngle = Math.Atan2(goalVector.Y, goalVector.X);
        Vector3 best = current;
        double bestScore = double.MaxValue;
        bool detouring = false;

        foreach (int offsetDegrees in CandidateAngles)
        {
            double angle = baseAngle + offsetDegrees * Math.PI / 180d;
            Vector3 raw = new(
                current.X + (float)Math.Cos(angle) * step,
                current.Y + (float)Math.Sin(angle) * step,
                desired.Z);
            Vector3 candidate = Snap(actor, current, raw);

            double danger = RouteDanger(current, candidate, threats, actor);
            double remaining = Vector2.Distance(new(candidate.X, candidate.Y), new(desired.X, desired.Y));
            double turnPenalty = Math.Abs(offsetDegrees) * 1.8;
            double score = danger + remaining * 0.16 + turnPenalty;
            if (score >= bestScore)
                continue;

            best = candidate;
            bestScore = score;
            detouring = offsetDegrees != 0 || danger > 0;
        }

        GameNPC nearest = threats.OrderBy(threat => DistanceToSegment2D(current, best, new(threat.X, threat.Y, threat.Z))).FirstOrDefault();
        string reason = detouring && nearest != null
            ? $"Avoiding level {nearest.EffectiveLevel} {nearest.Name}"
            : string.Empty;
        return new(best, detouring, reason);
    }

    public static bool IsRouteThreat(GameLiving actor, GameNPC npc, GameLiving intendedTarget = null)
    {
        if (actor == null || npc == null || npc == intendedTarget || !npc.IsAlive ||
            npc.ObjectState is not GameObject.eObjectState.Active || npc.CurrentRegion != actor.CurrentRegion ||
            (npc.Flags & (GameNPC.eFlags.PEACE | GameNPC.eFlags.CANTTARGET)) != 0 ||
            !GameServer.ServerRules.IsAllowedToAttack(actor, npc, true))
            return false;

        ConColor con = ConLevels.GetConColor(actor.GetConLevel(npc));
        return con >= ConColor.RED;
    }

    public static float AvoidanceRadius(GameLiving actor, GameNPC threat)
    {
        int levelDifference = Math.Max(0, threat.EffectiveLevel - actor.EffectiveLevel);
        return Math.Clamp(700 + levelDifference * 55, 700, 1650);
    }

    private static Vector3 Snap(GameLiving actor, Vector3 current, Vector3 candidate)
    {
        if (actor is GameBot && PathfindingProvider.Instance is LocalPathfindingMgr local &&
            local.IsAvailable && local.HasNavmesh(actor.CurrentZone))
            return AutonomousNavigationSurface.MoveAlongGround(PathfindingProvider.Instance,
                actor.CurrentZone, current, candidate) ?? current;
        return PathfindingProvider.Instance.GetMoveAlongSurface(
            actor.CurrentZone,
            current,
            candidate,
            PathfindingProvider.Instance.DefaultFilters) ?? candidate;
    }

    public static Vector3 BoundStep(Vector3 current, Vector3 desired, float maximumStep)
    {
        float distance = Vector2.Distance(new(current.X, current.Y), new(desired.X, desired.Y));
        return distance > Math.Max(0, maximumStep) && distance > 0
            ? Vector3.Lerp(current, desired, Math.Max(0, maximumStep) / distance) : desired;
    }

    private static double RouteDanger(Vector3 start, Vector3 end, IEnumerable<GameNPC> threats, GameLiving actor)
    {
        double score = 0;
        foreach (GameNPC threat in threats)
        {
            float clearance = DistanceToSegment2D(start, end, new(threat.X, threat.Y, threat.Z));
            float safeRadius = AvoidanceRadius(actor, threat);
            if (clearance >= safeRadius)
                continue;

            double intrusion = safeRadius - clearance;
            score += 100000 + intrusion * intrusion * 3;
        }
        return score;
    }

    public static float DistanceToSegment2D(Vector3 start, Vector3 end, Vector3 point)
    {
        Vector2 a = new(start.X, start.Y);
        Vector2 b = new(end.X, end.Y);
        Vector2 p = new(point.X, point.Y);
        Vector2 ab = b - a;
        float denominator = ab.LengthSquared();
        if (denominator <= float.Epsilon)
            return Vector2.Distance(a, p);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / denominator, 0f, 1f);
        return Vector2.Distance(a + ab * t, p);
    }
}
