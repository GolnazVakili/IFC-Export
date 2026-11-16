using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using MetroCIM.Models;
using System.Numerics;

namespace MetroCIM.Services;

internal static class DuctFittingProxies
{
    private const double JointOverlap = 0.015;

    public static ExportProxySet Add(
        Document document,
        View3D view,
        Dictionary<string, ResolvedAppearance> colorsByIfcGuid)
    {
        var created = new List<ElementId>();
        var hidden = new List<ElementId>();
        var colors = new ColorResolver();
        colors.Bind(view);

        foreach (Element element in new VisibilityService().GetVisibleElements(document, view))
        {
            if (!MepModelElements.IsDuctFitting(element))
                continue;

            IList<Solid> solids = CreateReplacement(element);
            if (solids.Count == 0)
                continue;

            DirectShape? shape = CreateShape(document, solids);
            if (shape is null)
                continue;

            created.Add(shape.Id);
            ResolvedAppearance appearance = colors.Resolve(element, view);
            CopyMaterial(element, shape);
            IfcAppearanceApplier.Index(colorsByIfcGuid, shape, appearance);
            hidden.Add(element.Id);
        }

        if (hidden.Count > 0)
        {
            try
            {
                view.HideElements(hidden);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        if (created.Count > 0)
        {
            try
            {
                document.Regenerate();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        return new ExportProxySet(created, hidden);
    }

    public static void Remove(Document document, View view, ExportProxySet proxies) =>
        CableTrayFittingProxies.Remove(document, view, proxies);

    private readonly record struct DuctEnd(
        XYZ Origin,
        XYZ Inward,
        XYZ WidthDirection,
        double Width,
        double Height,
        bool Round);

    private static IList<Solid> CreateReplacement(Element element)
    {
        if (!TryGetEnds(element, out IReadOnlyList<DuctEnd> ends, out XYZ bend))
            return [];

        if (ends.Count == 2)
            return Sweep(ConnectedCurve(ends[0], ends[1]), ends[0], ends[1]);

        var solids = new List<Solid>();
        foreach (DuctEnd end in ends)
        {
            XYZ toward = bend - end.Origin;
            if (toward.GetLength() < 1e-4)
                continue;

            double reach = Math.Clamp(Math.Min(end.Width, end.Height) * 0.2, 0.04, 0.15);
            double overlap = Math.Min(end.Width, end.Height) * 0.5;
            IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(
                [ToVector(end.Origin - (end.Inward * reach)), ToVector(end.Origin), ToVector(bend)],
                ToVector(end.WidthDirection));
            AddFrames(solids, frames, end, end, overlap);
        }

        return solids;
    }

    private static IList<Solid> Sweep(IReadOnlyList<XYZ> path, DuctEnd start, DuctEnd end)
    {
        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(
            path.Select(ToVector).ToList(),
            ToVector(start.WidthDirection));
        var solids = new List<Solid>();
        AddFrames(solids, frames, start, end, JointOverlap);
        return solids;
    }

    private static void AddFrames(
        List<Solid> solids,
        IReadOnlyList<SweepFrame> frames,
        DuctEnd start,
        DuctEnd end,
        double overlap)
    {
        bool sameSize = Math.Abs(start.Width - end.Width) < 1e-4 &&
                        Math.Abs(start.Height - end.Height) < 1e-4;
        for (int i = 0; i < frames.Count; i++)
        {
            SweepFrame frame = frames[i];
            double t = !sameSize && frames.Count > 1 ? i / (double)(frames.Count - 1) : 0;
            double width = Lerp(start.Width, end.Width, t);
            double height = Lerp(start.Height, end.Height, t);
            bool round = start.Round && end.Round;
            XYZ axis = ToXyz(frame.Axis);
            XYZ origin = ToXyz(frame.Start);
            double length = frame.Length;
            if (overlap > 1e-6)
            {
                if (i > 0)
                {
                    origin -= axis * overlap;
                    length += overlap;
                }

                if (i < frames.Count - 1)
                    length += overlap;
            }

            Solid? solid = Extrude(
                origin,
                axis,
                ToXyz(frame.Right),
                ToXyz(frame.Up),
                width,
                height,
                length,
                round);
            if (solid is not null)
                solids.Add(solid);
        }
    }

    private static bool TryGetEnds(Element element, out IReadOnlyList<DuctEnd> ends, out XYZ bend)
    {
        ends = [];
        bend = XYZ.Zero;
        if (element is not FamilyInstance instance)
            return false;

        ConnectorSet? connectors;
        try
        {
            connectors = instance.MEPModel?.ConnectorManager?.Connectors;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }

        if (connectors is null || connectors.Size < 2)
            return false;

        var found = new List<DuctEnd>();
        foreach (Connector connector in connectors)
        {
            if (!TryDescribe(connector, out DuctEnd end))
                continue;

            found.Add(end);
        }

        if (found.Count < 2)
            return false;

        bend = found.Count == 2 ? XYZ.Zero : BendOf(instance, found);
        ends = found;
        return true;
    }

    private static bool TryDescribe(Connector connector, out DuctEnd end)
    {
        end = default;
        try
        {
            Transform frame = connector.CoordinateSystem;
            bool round = connector.Shape == ConnectorProfileType.Round;
            double width;
            double height;
            if (round)
            {
                double diameter = connector.Radius * 2;
                if (diameter <= 1e-6)
                    diameter = Math.Max(connector.Width, connector.Height);
                width = height = diameter;
            }
            else
            {
                width = connector.Width;
                height = connector.Height;
            }

            TryConnectedDuct(connector, round, ref width, ref height);
            if (width <= 1e-6 || height <= 1e-6)
                return false;

            end = new DuctEnd(connector.Origin, -frame.BasisZ, frame.BasisX, width, height, round);
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static void TryConnectedDuct(Connector connector, bool round, ref double width, ref double height)
    {
        try
        {
            foreach (Connector reference in connector.AllRefs)
            {
                if (reference.Owner is not Duct duct)
                    continue;

                if (round)
                {
                    double diameter = duct.Diameter;
                    if (diameter > 1e-6)
                        width = height = diameter;
                }
                else
                {
                    if (duct.Width > 1e-6)
                        width = duct.Width;
                    if (duct.Height > 1e-6)
                        height = duct.Height;
                }

                return;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static XYZ BendOf(FamilyInstance instance, IReadOnlyList<DuctEnd> ends)
    {
        try
        {
            if (instance.Location is LocationPoint point)
                return point.Point;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        XYZ sum = XYZ.Zero;
        foreach (DuctEnd end in ends)
            sum += end.Origin;
        return sum / ends.Count;
    }

    private static IReadOnlyList<XYZ> ConnectedCurve(DuctEnd start, DuctEnd end)
    {
        double reach = Math.Clamp(Math.Min(Math.Min(start.Width, start.Height), Math.Min(end.Width, end.Height)) * 0.2, 0.04, 0.15);
        IReadOnlyList<Vector3> path = CableTrayFittingBody.ExtendIntoRuns(
            CableTrayFittingBody.Centerline(
                ToVector(start.Origin),
                ToVector(start.Inward),
                ToVector(end.Origin),
                ToVector(-end.Inward)),
            ToVector(-start.Inward),
            ToVector(-end.Inward),
            (float)reach);
        return path.Select(ToXyz).ToList();
    }

    private static Solid? Extrude(
        XYZ start,
        XYZ axis,
        XYZ right,
        XYZ up,
        double width,
        double height,
        double length,
        bool round)
    {
        try
        {
            if (length < 1e-4 || width <= 1e-6 || height <= 1e-6)
                return null;

            IList<XYZ> profile = round
                ? Circle(right, up, width * 0.5)
                : Rectangle(right, up, width, height);
            var lines = new List<Curve>();
            for (int i = 0; i < profile.Count; i++)
            {
                XYZ a = start + profile[i];
                XYZ b = start + profile[(i + 1) % profile.Count];
                if (a.DistanceTo(b) < 1e-6)
                    return null;
                lines.Add(Line.CreateBound(a, b));
            }

            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([CurveLoop.Create(lines)], axis, length);
            return solid.Volume > 1e-9 ? solid : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static IList<XYZ> Rectangle(XYZ right, XYZ up, double width, double height)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return
        [
            (-halfWidth * right) - (halfHeight * up),
            (halfWidth * right) - (halfHeight * up),
            (halfWidth * right) + (halfHeight * up),
            (-halfWidth * right) + (halfHeight * up)
        ];
    }

    private static IList<XYZ> Circle(XYZ right, XYZ up, double radius)
    {
        const int sides = 16;
        var points = new XYZ[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = i * Math.PI * 2 / sides;
            points[i] = (Math.Cos(angle) * radius * right) + (Math.Sin(angle) * radius * up);
        }

        return points;
    }

    private static DirectShape? CreateShape(Document document, IList<Solid> solids)
    {
        foreach (BuiltInCategory category in new[]
                 {
                     BuiltInCategory.OST_DuctFitting,
                     BuiltInCategory.OST_DuctCurves,
                     BuiltInCategory.OST_GenericModel
                 })
        {
            Category? cat = Category.GetCategory(document, category);
            if (cat is null)
                continue;

            try
            {
                DirectShape shape = DirectShape.CreateElement(document, cat.Id);
                shape.SetShape(solids.Cast<GeometryObject>().ToList());
                shape.SetName("Duct fitting");
                return shape;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        return null;
    }

    private static void CopyMaterial(Element source, Element target)
    {
        try
        {
            Parameter? material = source.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            Parameter? targetMaterial = target.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (material is { StorageType: StorageType.ElementId, HasValue: true } &&
                targetMaterial is { IsReadOnly: false, StorageType: StorageType.ElementId })
            {
                targetMaterial.Set(material.AsElementId());
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static double Lerp(double start, double end, double t) => start + ((end - start) * t);

    private static Vector3 ToVector(XYZ point) => new((float)point.X, (float)point.Y, (float)point.Z);

    private static XYZ ToXyz(Vector3 point) => new(point.X, point.Y, point.Z);
}
