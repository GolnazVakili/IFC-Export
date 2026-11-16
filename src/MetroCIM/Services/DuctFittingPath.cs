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

    public static IReadOnlyList<Vector3> Elbow(OrientedEnd start, OrientedEnd end, float startReach, float endReach)
    {
        IReadOnlyList<Vector3> center = CableTrayFittingBody.Centerline(
            start.Origin,
            start.Inward,
            end.Origin,
            end.Outward);
        if (center.Count == 0)
            return center;

        var extended = new List<Vector3>(center.Count + 2)
        {
            center[0] + (Normalize(start.Outward) * startReach)
        };
        extended.AddRange(center);
        extended.Add(center[^1] + (Normalize(end.Outward) * endReach));
        return extended;
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
