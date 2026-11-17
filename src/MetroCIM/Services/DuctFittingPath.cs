using System.Numerics;

namespace MetroCIM.Services;

public readonly record struct OrientedEnd(
    Vector3 Origin,
    Vector3 Outward,
    Vector3 Inward,
    Vector3 WidthDirection,
    float Width,
    float Height,
    bool Round);

/// <summary>
/// Builds a duct, pipe, or conduit fitting centerline from its connectors.
/// The family insertion point is not used: it often sits off the bend and
/// leaves a straight box beside the run.
/// </summary>
public static class DuctFittingPath
{
    public static Vector3 Outward(Vector3 origin, Vector3 axis, Vector3 fittingCenter)
    {
        Vector3 direction = Normalize(axis);
        Vector3 away = origin - fittingCenter;
        if (away.LengthSquared() < 1e-8f)
            return direction;

        return Vector3.Dot(direction, away) >= 0 ? direction : -direction;
    }

    public static Vector3 Toward(Vector3 origin, Vector3 axis, Vector3 target)
    {
        Vector3 direction = Normalize(axis);
        Vector3 toTarget = target - origin;
        if (toTarget.LengthSquared() < 1e-8f)
            return direction;

        return Vector3.Dot(direction, toTarget) >= 0 ? direction : -direction;
    }

    public static Vector3 ProfileRight(Vector3 widthDirection, Vector3 outward)
    {
        Vector3 projected = widthDirection - (outward * Vector3.Dot(widthDirection, outward));
        if (projected.LengthSquared() < 1e-8f)
        {
            Vector3 fallback = Math.Abs(outward.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            projected = fallback - (outward * Vector3.Dot(fallback, outward));
        }

        return Vector3.Normalize(projected);
    }

    public static float Reach(float width, float height, float distanceToHostEnd)
    {
        float minor = Math.Max(Math.Min(width, height), 1e-4f);
        float major = Math.Max(width, height);
        float reach = Math.Clamp(minor * 0.45f, 0.06f, 0.4f);
        if (distanceToHostEnd > 0.01f && distanceToHostEnd < major * 3f)
            reach = Math.Max(reach, distanceToHostEnd + (minor * 0.2f));

        return reach;
    }

    public static Vector3 PointToward(Vector3 origin, Vector3 direction, Vector3 target)
    {
        Vector3 axis = Normalize(direction);
        Vector3 toTarget = target - origin;
        if (toTarget.LengthSquared() < 1e-8f)
            return axis;

        return Vector3.Dot(axis, toTarget) >= 0 ? axis : -axis;
    }

    public readonly record struct BendCurve(
        Vector3 Start,
        Vector3 ArcStart,
        Vector3 ArcPoint,
        Vector3 ArcEnd,
        Vector3 End);

    public static IReadOnlyList<Vector3> Elbow(OrientedEnd start, OrientedEnd end, float startReach, float endReach)
    {
        Vector3 travelStart = PointToward(start.Origin, start.Inward, end.Origin);
        Vector3 travelEnd = -PointToward(end.Origin, end.Outward, start.Origin);
        IReadOnlyList<Vector3> center = Fillet(start.Origin, travelStart, end.Origin, travelEnd)
            ?? CableTrayFittingBody.Centerline(start.Origin, travelStart, end.Origin, travelEnd);
        if (center.Count == 0)
            return center;

        var extended = new List<Vector3>(center.Count + 2)
        {
            center[0] - (Normalize(travelStart) * startReach)
        };
        extended.AddRange(center);
        extended.Add(center[^1] + (Normalize(travelEnd) * endReach));
        return extended;
    }

    public static bool TryBend(OrientedEnd start, OrientedEnd end, float startReach, float endReach, out BendCurve curve)
    {
        curve = default;
        Vector3 travelStart = PointToward(start.Origin, start.Inward, end.Origin);
        Vector3 travelEnd = -PointToward(end.Origin, end.Outward, start.Origin);
        if (!TryArc(start.Origin, travelStart, end.Origin, travelEnd, out ArcTurn arc))
            return false;

        startReach = Math.Max(startReach, 0f);
        endReach = Math.Max(endReach, 0f);
        curve = new BendCurve(
            arc.Start - (Normalize(travelStart) * startReach),
            arc.Start,
            arc.Mid,
            arc.End,
            arc.End + (Normalize(travelEnd) * endReach));
        return true;
    }

    public static IReadOnlyList<Vector3>? Fillet(
        Vector3 originA,
        Vector3 travelA,
        Vector3 originB,
        Vector3 travelB,
        int steps = 8)
    {
        travelA = Normalize(travelA);
        travelB = Normalize(travelB);
        if (Vector3.Dot(travelA, travelB) > 0.985f)
            return [originA, originB];

        Vector3 turn = Vector3.Cross(travelA, travelB);
        if (turn.LengthSquared() < 1e-6f)
            return null;

        IReadOnlyList<Vector3>? forward = TryFillet(originA, travelA, originB, travelB, Normalize(turn), steps);
        if (forward is not null && StartsAlong(forward, travelA))
            return forward;

        IReadOnlyList<Vector3>? reverse = TryFillet(originA, travelA, originB, travelB, -Normalize(turn), steps);
        if (reverse is not null && StartsAlong(reverse, travelA))
            return reverse;

        return forward ?? reverse;
    }

    private readonly record struct ArcTurn(Vector3 Start, Vector3 End, Vector3 Mid);

    private static bool TryArc(
        Vector3 originA,
        Vector3 travelA,
        Vector3 originB,
        Vector3 travelB,
        out ArcTurn arc)
    {
        arc = default;
        travelA = Normalize(travelA);
        travelB = Normalize(travelB);
        if (Vector3.Dot(travelA, travelB) > 0.985f)
            return false;

        Vector3 turn = Vector3.Cross(travelA, travelB);
        if (turn.LengthSquared() < 1e-6f)
            return false;

        if (TryOneArc(originA, travelA, originB, travelB, Normalize(turn), out arc))
            return true;

        return TryOneArc(originA, travelA, originB, travelB, -Normalize(turn), out arc);
    }

    public static Vector3 Junction(IReadOnlyList<OrientedEnd> ends)
    {
        Vector3 best = (ends[0].Origin + ends[1].Origin) * 0.5f;
        float bestScore = float.MaxValue;
        for (int i = 0; i < ends.Count; i++)
        {
            for (int j = i + 1; j < ends.Count; j++)
            {
                Vector3 hit = CableTrayFittingBody.BendOf(
                    ends[i].Origin,
                    ends[i].Inward,
                    ends[j].Origin,
                    ends[j].Inward);
                float score = 0;
                foreach (OrientedEnd end in ends)
                    score += DistanceToRay(hit, end.Origin, end.Inward);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = hit;
                }
            }
        }

        return best;
    }

    public static float Span(IReadOnlyList<OrientedEnd> ends)
    {
        float span = 0;
        for (int i = 0; i < ends.Count; i++)
        {
            for (int j = i + 1; j < ends.Count; j++)
                span = Math.Max(span, Vector3.Distance(ends[i].Origin, ends[j].Origin));
        }

        return span;
    }

    public static bool Meets(OrientedEnd end, Vector3 junction, float span)
    {
        Vector3 toward = junction - end.Origin;
        if (toward.LengthSquared() < 1e-8f || span < 1e-4f)
            return false;

        if (Vector3.Dot(Vector3.Normalize(toward), end.Inward) <= 0.15f)
            return false;

        return toward.Length() <= span * 1.35f;
    }

    public static IReadOnlyList<Vector3> Branch(OrientedEnd end, Vector3 junction, float reach) =>
    [
        end.Origin + (Normalize(end.Outward) * reach),
        end.Origin,
        junction
    ];

    private static IReadOnlyList<Vector3>? TryFillet(
        Vector3 originA,
        Vector3 travelA,
        Vector3 originB,
        Vector3 travelB,
        Vector3 normal,
        int steps)
    {
        Vector3 sideA = Vector3.Cross(normal, travelA);
        Vector3 sideB = Vector3.Cross(normal, travelB);
        if (sideA.LengthSquared() < 1e-8f || sideB.LengthSquared() < 1e-8f)
            return null;

        if (!TryIntersect(originA, Normalize(sideA), originB, Normalize(sideB), out Vector3 center))
            return null;

        Vector3 from = originA - center;
        Vector3 to = originB - center;
        float radius = from.Length();
        float other = to.Length();
        if (radius < 1e-4f || Math.Abs(radius - other) > radius * 0.2f)
            return null;

        float cosine = Math.Clamp(Vector3.Dot(from, to) / (radius * other), -1f, 1f);
        float angle = MathF.Acos(cosine);
        if (angle < 0.05f)
            return [originA, originB];

        var points = new List<Vector3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = angle * i / steps;
            points.Add(center + Rotate(from, normal, t));
        }

        points[0] = originA;
        points[^1] = originB;
        return points;
    }

    private static bool TryOneArc(
        Vector3 originA,
        Vector3 travelA,
        Vector3 originB,
        Vector3 travelB,
        Vector3 normal,
        out ArcTurn arc)
    {
        arc = default;
        Vector3 sideA = Vector3.Cross(normal, travelA);
        Vector3 sideB = Vector3.Cross(normal, travelB);
        if (sideA.LengthSquared() < 1e-8f || sideB.LengthSquared() < 1e-8f)
            return false;

        if (!TryIntersect(originA, Normalize(sideA), originB, Normalize(sideB), out Vector3 center))
            return false;

        Vector3 from = originA - center;
        Vector3 to = originB - center;
        float radius = from.Length();
        float other = to.Length();
        if (radius < 1e-4f || Math.Abs(radius - other) > radius * 0.2f)
            return false;

        float cosine = Math.Clamp(Vector3.Dot(from, to) / (radius * other), -1f, 1f);
        float angle = MathF.Acos(cosine);
        if (angle < 0.05f)
            return false;

        Vector3 step = Rotate(from, normal, Math.Min(angle * 0.25f, 0.2f)) - from;
        if (step.LengthSquared() < 1e-8f || Vector3.Dot(Normalize(step), travelA) <= 0.5f)
            return false;

        arc = new ArcTurn(originA, originB, center + Rotate(from, normal, angle * 0.5f));
        return true;
    }

    private static bool StartsAlong(IReadOnlyList<Vector3> path, Vector3 travel)
    {
        if (path.Count < 2)
            return false;

        Vector3 step = path[1] - path[0];
        return step.LengthSquared() > 1e-8f && Vector3.Dot(Normalize(step), Normalize(travel)) > 0.5f;
    }

    private static bool TryIntersect(
        Vector3 originA,
        Vector3 directionA,
        Vector3 originB,
        Vector3 directionB,
        out Vector3 hit)
    {
        Vector3 between = originB - originA;
        float a = Vector3.Dot(directionA, directionA);
        float b = Vector3.Dot(directionA, directionB);
        float c = Vector3.Dot(directionB, directionB);
        float d = Vector3.Dot(directionA, between);
        float e = Vector3.Dot(directionB, between);
        float denom = (a * c) - (b * b);
        if (Math.Abs(denom) < 1e-6f)
        {
            hit = default;
            return false;
        }

        float distance = ((d * c) - (b * e)) / denom;
        hit = originA + (directionA * distance);
        return true;
    }

    private static Vector3 Rotate(Vector3 vector, Vector3 axis, float radians)
    {
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        return (vector * cosine)
            + (Vector3.Cross(axis, vector) * sine)
            + (axis * Vector3.Dot(axis, vector) * (1f - cosine));
    }

    private static float DistanceToRay(Vector3 point, Vector3 origin, Vector3 direction)
    {
        float along = Vector3.Dot(point - origin, direction);
        Vector3 closest = origin + (direction * Math.Max(along, 0));
        return Vector3.Distance(point, closest);
    }

    private static Vector3 Normalize(Vector3 value)
    {
        float length = value.Length();
        return length < 1e-8f ? Vector3.UnitX : value / length;
    }
}
