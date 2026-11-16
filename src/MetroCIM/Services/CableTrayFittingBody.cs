using System.Numerics;
using MetroCIM.Models;

namespace MetroCIM.Services;

public readonly record struct FittingLeg(Vector3 Origin, Vector3 WidthDirection, float Width, float Height);

public static class CableTrayFittingBody
{
    public static TriangleMesh Build(IReadOnlyList<FittingLeg> legs, Vector3 bend)
    {
        var mesh = new TriangleMesh();
        foreach (FittingLeg leg in legs)
            AddLeg(mesh, leg, bend);
        return mesh;
    }

    public static IReadOnlyList<Vector3> Centerline(
        Vector3 start,
        Vector3 startTangent,
        Vector3 end,
        Vector3 endTangent,
        int steps = 16)
    {
        Vector3 leave = SafeNormalize(startTangent);
        Vector3 arrive = SafeNormalize(endTangent);
        if (Vector3.Dot(leave, arrive) > 0.985f)
            return [start, end];

        float handle = Vector3.Distance(start, end) * 0.45f;
        Vector3 controlA = start + (leave * handle);
        Vector3 controlB = end - (arrive * handle);
        var points = new List<Vector3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float u = 1f - t;
            points.Add(
                (u * u * u * start) +
                (3f * u * u * t * controlA) +
                (3f * u * t * t * controlB) +
                (t * t * t * end));
        }

        return points;
    }

    public static IReadOnlyList<Vector3> ExtendIntoRuns(
        IReadOnlyList<Vector3> path,
        Vector3 startOutward,
        Vector3 endOutward,
        float reach)
    {
        if (path.Count == 0 || reach <= 0f)
            return path;

        var extended = new List<Vector3>(path.Count + 2)
        {
            path[0] + (SafeNormalize(startOutward) * reach)
        };
        extended.AddRange(path);
        extended.Add(path[^1] + (SafeNormalize(endOutward) * reach));
        return extended;
    }

    public static Vector3 BendOf(Vector3 originA, Vector3 inwardA, Vector3 originB, Vector3 inwardB)
    {
        Vector3 directionA = SafeNormalize(inwardA);
        Vector3 directionB = SafeNormalize(inwardB);
        Vector3 between = originB - originA;
        float a = Vector3.Dot(directionA, directionA);
        float b = Vector3.Dot(directionA, directionB);
        float c = Vector3.Dot(directionB, directionB);
        float d = Vector3.Dot(directionA, between);
        float e = Vector3.Dot(directionB, between);
        float denom = (a * c) - (b * b);
        if (Math.Abs(denom) < 1e-6f)
            return (originA + originB) * 0.5f;

        float distance = (d * c - b * e) / denom;
        float span = between.Length();
        if (distance < 0 || distance > span * 4f)
            return (originA + originB) * 0.5f;

        return originA + (directionA * distance);
    }

    private static void AddLeg(TriangleMesh mesh, FittingLeg leg, Vector3 bend)
    {
        if (leg.Width <= 0 || leg.Height <= 0)
            return;

        Vector3 toward = bend - leg.Origin;
        float reach = toward.Length();
        if (reach < 1e-4f)
            return;

        Vector3 axis = toward / reach;
        float overlap = Math.Max(leg.Width, leg.Height) * 0.5f;
        Vector3 end = leg.Origin + (axis * (reach + overlap));
        Vector3 width = Perpendicular(leg.WidthDirection, axis);
        Vector3 height = Vector3.Normalize(Vector3.Cross(axis, width));
        Vector3 across = width * (leg.Width * 0.5f);
        Vector3 up = height * (leg.Height * 0.5f);

        Vector3 a = leg.Origin - across - up;
        Vector3 b = leg.Origin + across - up;
        Vector3 c = leg.Origin + across + up;
        Vector3 d = leg.Origin - across + up;
        Vector3 e = end - across - up;
        Vector3 f = end + across - up;
        Vector3 g = end + across + up;
        Vector3 h = end - across + up;

        Quad(mesh, a, d, c, b);
        Quad(mesh, e, f, g, h);
        Quad(mesh, a, b, f, e);
        Quad(mesh, d, h, g, c);
        Quad(mesh, a, e, h, d);
        Quad(mesh, b, c, g, f);
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

    private static Vector3 SafeNormalize(Vector3 value)
    {
        float length = value.Length();
        return length < 1e-8f ? Vector3.UnitX : value / length;
    }

    private static void Quad(TriangleMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        mesh.AddTriangle(a, b, c);
        mesh.AddTriangle(a, c, d);
    }
}
