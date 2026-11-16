using System.Numerics;

namespace MetroCIM.Services;

public readonly record struct SweepFrame(Vector3 Start, Vector3 Axis, Vector3 Right, Vector3 Up, float Length);

/// <summary>
/// Carries a duct's width direction along an elbow so a riser keeps a full cross-section.
/// </summary>
public static class DuctFittingFrames
{
    public static IReadOnlyList<SweepFrame> Sweep(IReadOnlyList<Vector3> path, Vector3 widthDirection)
    {
        var frames = new List<SweepFrame>();
        Vector3 carried = widthDirection;
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 step = path[i] - path[i - 1];
            float length = step.Length();
            if (length < 1e-4f)
                continue;

            Vector3 axis = step / length;
            Vector3 right = Perpendicular(carried, axis);
            Vector3 up = Vector3.Cross(right, axis);
            if (up.LengthSquared() < 1e-8f)
                continue;

            up = Vector3.Normalize(up);
            frames.Add(new SweepFrame(path[i - 1], axis, right, up, length));
            carried = right;
        }

        return frames;
    }

    private static Vector3 Perpendicular(Vector3 preferred, Vector3 axis)
    {
        Vector3 projected = preferred - (axis * Vector3.Dot(preferred, axis));
        if (projected.LengthSquared() < 1e-8f)
        {
            Vector3 fallback = Math.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            projected = fallback - (axis * Vector3.Dot(fallback, axis));
        }

        return Vector3.Normalize(projected);
    }
}
