using Autodesk.Revit.DB;
using System.Numerics;

namespace MetroCIM.Services;

internal static class FittingSolids
{
    public static Solid? LoftSection(
        IReadOnlyList<Vector3> path,
        Vector3 widthDirection,
        Func<double, IReadOnlyList<(double Across, double Rise)>> section,
        double minimumLength)
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
            if (!TryAdd(profiles, frame.Start, frame.Axis, frame.Right, frame.Up, t, section, minimumLength, ref previousUp, ref previousOrigin))
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
                minimumLength,
                ref previousUp,
                ref previousOrigin))
        {
            return null;
        }

        return Create(profiles);
    }

    public static IReadOnlyList<Solid> SweepProfiles(
        DuctFittingPath.BendCurve bend,
        Vector3 riseDirection,
        IReadOnlyList<IReadOnlyList<(double Across, double Rise)>> profiles,
        double minimumLength)
    {
        try
        {
            if (!TryPath(bend, minimumLength, out CurveLoop path, out XYZ origin, out XYZ tangent))
                return [];

            XYZ rise = ToXyz(riseDirection);
            XYZ up = rise - (tangent * rise.DotProduct(tangent));
            if (up.GetLength() < 1e-8)
            {
                XYZ fallback = Math.Abs(tangent.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
                up = fallback - (tangent * fallback.DotProduct(tangent));
            }

            up = up.Normalize();
            if (rise.GetLength() > 1e-8 && up.DotProduct(rise) < 0)
                up = -up;

            XYZ right = tangent.CrossProduct(up);
            if (right.GetLength() < 1e-8)
                return [];

            right = right.Normalize();
            up = right.CrossProduct(tangent).Normalize();
            var solids = new List<Solid>(profiles.Count);
            foreach (IReadOnlyList<(double Across, double Rise)> section in profiles)
            {
                CurveLoop? profile = Polygon(origin, tangent, right, up, section, minimumLength);
                if (profile is null)
                    return [];

                Solid solid = GeometryCreationUtilities.CreateSweptGeometry(path, 0, 0, [profile]);
                if (solid.Volume <= 1e-9)
                    return [];

                solids.Add(solid);
            }

            return solids;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return [];
        }
    }

    private static bool TryPath(
        DuctFittingPath.BendCurve bend,
        double minimumLength,
        out CurveLoop path,
        out XYZ origin,
        out XYZ tangent)
    {
        path = null!;
        origin = null!;
        tangent = null!;
        XYZ start = ToXyz(bend.Start);
        XYZ arcStart = ToXyz(bend.ArcStart);
        XYZ arcPoint = ToXyz(bend.ArcPoint);
        XYZ arcEnd = ToXyz(bend.ArcEnd);
        XYZ end = ToXyz(bend.End);
        var curves = new List<Curve>();
        if (ShortCurve.IsLongEnough(start.DistanceTo(arcStart), minimumLength))
            curves.Add(Line.CreateBound(start, arcStart));

        if (!ShortCurve.IsLongEnough(arcStart.DistanceTo(arcEnd), minimumLength) ||
            !ShortCurve.IsLongEnough(arcStart.DistanceTo(arcPoint), minimumLength) ||
            !ShortCurve.IsLongEnough(arcEnd.DistanceTo(arcPoint), minimumLength))
        {
            return false;
        }

        curves.Add(Arc.Create(arcStart, arcEnd, arcPoint));
        if (ShortCurve.IsLongEnough(arcEnd.DistanceTo(end), minimumLength))
            curves.Add(Line.CreateBound(arcEnd, end));

        path = CurveLoop.Create(curves);
        Curve first = curves[0];
        origin = first.GetEndPoint(0);
        tangent = first.ComputeDerivatives(0, normalized: true).BasisX;
        if (tangent.GetLength() < 1e-8)
            return false;

        tangent = tangent.Normalize();
        return true;
    }

    private static bool TryAdd(
        List<CurveLoop> profiles,
        Vector3 origin,
        Vector3 axis,
        Vector3 right,
        Vector3 up,
        double t,
        Func<double, IReadOnlyList<(double Across, double Rise)>> section,
        double minimumLength,
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

        CurveLoop? loop = Polygon(point, ToXyz(axis), ToXyz(right), ToXyz(up), section(t), minimumLength);
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
        IReadOnlyList<(double Across, double Rise)> section,
        double minimumLength)
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
            if (!ShortCurve.IsLongEnough(start.DistanceTo(end), minimumLength))
                return null;
        }

        try
        {
            for (int i = 0; i < corners.Count; i++)
                lines.Add(Line.CreateBound(corners[i], corners[(i + 1) % corners.Count]));

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
