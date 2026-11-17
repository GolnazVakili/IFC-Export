using Autodesk.Revit.DB;
using System.Numerics;

namespace MetroCIM.Services;

internal static class FittingSolids
{
    public static Solid? LoftSection(
        IReadOnlyList<Vector3> path,
        Vector3 widthDirection,
        Func<double, IReadOnlyList<(double Across, double Rise)>> section)
    {
        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(path, widthDirection);
        if (frames.Count == 0)
            return null;

        var profiles = new List<CurveLoop>();
        Vector3 previousUp = frames[0].Up;
        XYZ? previousOrigin = null;
        for (int i = 0; i < frames.Count; i++)
        {
            SweepFrame frame = frames[i];
            double t = frames.Count == 1 ? 0 : i / (double)frames.Count;
            if (!TryAdd(profiles, frame.Start, frame.Axis, frame.Right, frame.Up, t, section, ref previousUp, ref previousOrigin))
                return null;
        }

        SweepFrame last = frames[^1];
        if (!TryAdd(
                profiles,
                last.Start + (last.Axis * last.Length),
                last.Axis,
                last.Right,
                last.Up,
                1,
                section,
                ref previousUp,
                ref previousOrigin))
        {
            return null;
        }

        return Create(profiles);
    }

    private static bool TryAdd(
        List<CurveLoop> profiles,
        Vector3 origin,
        Vector3 axis,
        Vector3 right,
        Vector3 up,
        double t,
        Func<double, IReadOnlyList<(double Across, double Rise)>> section,
        ref Vector3 previousUp,
        ref XYZ? previousOrigin)
    {
        if (Vector3.Dot(up, previousUp) < 0)
        {
            up = -up;
            right = -right;
        }

        previousUp = up;
        XYZ point = ToXyz(origin);
        if (previousOrigin is not null && point.DistanceTo(previousOrigin) < 0.02)
            return true;

        CurveLoop? loop = Polygon(point, ToXyz(axis), ToXyz(right), ToXyz(up), section(t));
        if (loop is null)
            return false;

        profiles.Add(loop);
        previousOrigin = point;
        return true;
    }

    private static CurveLoop? Polygon(
        XYZ origin,
        XYZ axis,
        XYZ right,
        XYZ up,
        IReadOnlyList<(double Across, double Rise)> section)
    {
        if (section.Count < 3)
            return null;

        var corners = new List<XYZ>(section.Count);
        foreach ((double across, double rise) in section)
            corners.Add(origin + (across * right) + (rise * up));

        var lines = new List<Curve>();
        for (int i = 0; i < corners.Count; i++)
        {
            XYZ start = corners[i];
            XYZ end = corners[(i + 1) % corners.Count];
            if (start.DistanceTo(end) < 1e-6)
                return null;

            lines.Add(Line.CreateBound(start, end));
        }

        try
        {
            CurveLoop loop = CurveLoop.Create(lines);
            XYZ normal = (corners[1] - corners[0]).CrossProduct(corners[2] - corners[1]);
            if (normal.DotProduct(axis) < 0)
                loop.Flip();

            return loop;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static Solid? Create(IList<CurveLoop> profiles)
    {
        if (profiles.Count < 2)
            return null;

        try
        {
            Solid solid = GeometryCreationUtilities.CreateLoftGeometry(
                profiles,
                new SolidOptions(ElementId.InvalidElementId, ElementId.InvalidElementId));
            return solid.Volume > 1e-9 ? solid : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static XYZ ToXyz(Vector3 point) => new(point.X, point.Y, point.Z);
}
