using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using MetroCIM.Models;
using System.Numerics;

namespace MetroCIM.Services;

internal static class FlowFittingProxies
{
    private const double SegmentOverlap = 0.02;

    public static ExportProxySet Add(
        Document document,
        View3D view,
        Dictionary<string, ResolvedAppearance> colorsByIfcGuid)
    {
        var created = new List<ElementId>();
        var hidden = new List<ElementId>();
        var colors = new ColorResolver();
        colors.Bind(view);

        double minimumLength = document.Application.ShortCurveTolerance;
        foreach (Element element in new VisibilityService().GetVisibleElements(document, view))
        {
            if (!MepModelElements.IsFlowFitting(element))
                continue;

            IList<Solid> solids;
            try
            {
                solids = CreateReplacement(element, minimumLength);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }

            if (solids.Count == 0)
                continue;

            DirectShape? shape = CreateShape(document, element, solids);
            if (shape is null)
                continue;

            created.Add(shape.Id);
            ResolvedAppearance appearance = colors.Resolve(element, view);
            CopyMaterial(element, shape);
            IfcAppearanceApplier.Index(colorsByIfcGuid, shape, appearance);
            SuppressOriginal(element);
            hidden.Add(element.Id);
        }

        foreach (ElementId id in hidden)
        {
            try
            {
                view.HideElements([id]);
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

    private readonly record struct FlowEnd(
        XYZ Origin,
        XYZ Outward,
        XYZ WidthDirection,
        double Width,
        double Height,
        bool Round,
        double Reach);

    private static IList<Solid> CreateReplacement(Element element, double minimumLength)
    {
        if (!TryGetEnds(element, out IReadOnlyList<FlowEnd> ends))
            return [];

        List<OrientedEnd> oriented = ends.Select(ToOriented).ToList();
        var solids = new List<Solid>();
        if (oriented.Count == 2)
        {
            AddPath(
                solids,
                DuctFittingPath.Elbow(oriented[0], oriented[1], (float)ends[0].Reach, (float)ends[1].Reach),
                ends[0],
                ends[1],
                SegmentOverlap,
                minimumLength);
        }
        else
        {
            Vector3 junction = DuctFittingPath.Junction(oriented);
            float span = DuctFittingPath.Span(oriented);
            for (int i = 0; i < oriented.Count; i++)
            {
                if (!DuctFittingPath.Meets(oriented[i], junction, span))
                    continue;

                double fuse = Math.Min(ends[i].Width, ends[i].Height) * 0.5;
                AddPath(
                    solids,
                    DuctFittingPath.Branch(oriented[i], junction, (float)ends[i].Reach),
                    ends[i],
                    ends[i],
                    fuse,
                    minimumLength);
            }
        }

        return solids;
    }

    private static void AddPath(
        List<Solid> solids,
        IReadOnlyList<Vector3> path,
        FlowEnd start,
        FlowEnd end,
        double overlap,
        double minimumLength)
    {
        if (path.Count < 2)
            return;

        if (TrySmoothBend(solids, start, end, minimumLength))
            return;

        bool round = start.Round && end.Round;
        Solid? lofted = FittingSolids.LoftSection(path, ToVector(start.WidthDirection), t =>
        {
            double width = Lerp(start.Width, end.Width, t);
            double height = Lerp(start.Height, end.Height, t);
            return round ? CircleSection(width * 0.5) : RectangleSection(width, height);
        }, minimumLength);
        if (lofted is not null)
        {
            solids.Add(lofted);
            return;
        }

        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(path, ToVector(start.WidthDirection));
        bool sameSize = Math.Abs(start.Width - end.Width) < 1e-4 &&
                        Math.Abs(start.Height - end.Height) < 1e-4;
        for (int i = 0; i < frames.Count; i++)
        {
            SweepFrame frame = frames[i];
            double t = !sameSize && frames.Count > 1 ? i / (double)(frames.Count - 1) : 0;
            double width = Lerp(start.Width, end.Width, t);
            double height = Lerp(start.Height, end.Height, t);
            XYZ axis = ToXyz(frame.Axis);
            XYZ origin = ToXyz(frame.Start);
            double length = frame.Length;
            if (i > 0)
            {
                origin -= axis * overlap;
                length += overlap;
            }

            if (i < frames.Count - 1)
                length += overlap;

            Solid? solid = Extrude(
                origin,
                axis,
                ToXyz(frame.Right),
                ToXyz(frame.Up),
                width,
                height,
                length,
                start.Round && end.Round,
                minimumLength);
            if (solid is not null)
                solids.Add(solid);
        }
    }

    private static bool TrySmoothBend(List<Solid> solids, FlowEnd start, FlowEnd end, double minimumLength)
    {
        if (start.Round != end.Round)
            return false;

        if (Math.Abs(start.Width - end.Width) > 1e-4 || Math.Abs(start.Height - end.Height) > 1e-4)
            return false;

        if (!DuctFittingPath.TryBend(
                ToOriented(start),
                ToOriented(end),
                (float)start.Reach,
                (float)end.Reach,
                out DuctFittingPath.BendCurve bend))
        {
            return false;
        }

        Vector3 width = ToVector(start.WidthDirection);
        Vector3 rise = Vector3.Cross(ToVector(start.Outward), width);
        if (rise.LengthSquared() < 1e-8f)
            return false;

        IReadOnlyList<(double Across, double Rise)> profile = start.Round
            ? CircleSection(start.Width * 0.5)
            : RectangleSection(start.Width, start.Height);
        IReadOnlyList<Solid> swept = FittingSolids.SweepProfiles(
            bend,
            Vector3.Normalize(rise),
            [profile],
            minimumLength);
        if (swept.Count == 0)
            return false;

        solids.AddRange(swept);
        return true;
    }

    private static bool TryGetEnds(Element element, out IReadOnlyList<FlowEnd> ends)
    {
        ends = [];
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

        var raw = new List<(XYZ Origin, XYZ Axis, XYZ Width, double WidthSize, double Height, bool Round, XYZ Host, bool HasHost)>();
        foreach (Connector connector in connectors)
        {
            if (!TryDescribe(connector, out var described))
                continue;

            raw.Add(described);
        }

        if (raw.Count < 2)
            return false;

        XYZ center = XYZ.Zero;
        foreach (var item in raw)
            center += item.Origin;
        center /= raw.Count;

        var found = new List<FlowEnd>();
        foreach (var item in raw)
        {
            Vector3 outward = item.HasHost && item.Host.GetLength() > 1e-4
                ? Vector3.Normalize(ToVector(item.Host))
                : DuctFittingPath.Outward(ToVector(item.Origin), ToVector(item.Axis), ToVector(center));
            var away = new XYZ(outward.X, outward.Y, outward.Z);
            double reach = DuctFittingPath.Reach((float)item.WidthSize, (float)item.Height, 0);
            Vector3 right = DuctFittingPath.ProfileRight(ToVector(item.Width), outward);
            found.Add(new FlowEnd(
                item.Origin,
                away,
                new XYZ(right.X, right.Y, right.Z),
                item.WidthSize,
                item.Height,
                item.Round,
                reach));
        }

        ends = found;
        return found.Count >= 2;
    }

    private static bool TryDescribe(
        Connector connector,
        out (XYZ Origin, XYZ Axis, XYZ Width, double WidthSize, double Height, bool Round, XYZ Host, bool HasHost) described)
    {
        described = default;
        try
        {
            if (connector.ConnectorType != ConnectorType.End)
                return false;

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

            bool hasHost = TryHost(connector, round, ref width, ref height, out XYZ host);
            if (width <= 1e-6 || height <= 1e-6)
                return false;

            described = (connector.Origin, frame.BasisZ, frame.BasisX, width, height, round, host, hasHost);
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static bool TryHost(Connector connector, bool round, ref double width, ref double height, out XYZ intoRun)
    {
        intoRun = XYZ.Zero;
        try
        {
            foreach (Connector reference in connector.AllRefs)
            {
                if (reference.Owner is not MEPCurve host || host.Id == connector.Owner?.Id || host is CableTray)
                    continue;

                if (host.Location is not LocationCurve location || location.Curve is not Curve curve)
                    continue;

                ApplyHostSize(host, round, ref width, ref height);
                XYZ end0 = curve.GetEndPoint(0);
                XYZ end1 = curve.GetEndPoint(1);
                XYZ near = connector.Origin.DistanceTo(end0) <= connector.Origin.DistanceTo(end1) ? end0 : end1;
                XYZ far = near.IsAlmostEqualTo(end0) ? end1 : end0;
                intoRun = far - near;
                return intoRun.GetLength() > 1e-4;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        return false;
    }

    private static void ApplyHostSize(MEPCurve host, bool round, ref double width, ref double height)
    {
        switch (host)
        {
            case Duct duct:
                ApplyDuctSize(duct.Diameter, duct.Width, duct.Height, round, ref width, ref height);
                break;
            case FlexDuct flex:
                ApplyDuctSize(flex.Diameter, flex.Width, flex.Height, round, ref width, ref height);
                break;
            case Pipe pipe when pipe.Diameter > 1e-6:
                width = height = pipe.Diameter;
                break;
            case FlexPipe flex when flex.Diameter > 1e-6:
                width = height = flex.Diameter;
                break;
            case Conduit conduit when conduit.Diameter > 1e-6:
                width = height = conduit.Diameter;
                break;
        }
    }

    private static void ApplyDuctSize(
        double diameter,
        double ductWidth,
        double ductHeight,
        bool round,
        ref double width,
        ref double height)
    {
        if (round)
        {
            if (diameter > 1e-6)
                width = height = diameter;
            return;
        }

        if (ductWidth > 1e-6)
            width = ductWidth;
        if (ductHeight > 1e-6)
            height = ductHeight;
    }

    private static Solid? Extrude(
        XYZ start,
        XYZ axis,
        XYZ right,
        XYZ up,
        double width,
        double height,
        double length,
        bool round,
        double minimumLength)
    {
        try
        {
            if (!ShortCurve.IsLongEnough(length, minimumLength) ||
                !ShortCurve.IsLongEnough(width, minimumLength) ||
                !ShortCurve.IsLongEnough(height, minimumLength))
                return null;

            IList<XYZ> profile = round
                ? Circle(right, up, width * 0.5)
                : Rectangle(right, up, width, height);
            var lines = new List<Curve>();
            for (int i = 0; i < profile.Count; i++)
            {
                XYZ a = start + profile[i];
                XYZ b = start + profile[(i + 1) % profile.Count];
                if (!ShortCurve.IsLongEnough(a.DistanceTo(b), minimumLength))
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

    private static IReadOnlyList<(double Across, double Rise)> RectangleSection(double width, double height)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return
        [
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight)
        ];
    }

    private static IReadOnlyList<(double Across, double Rise)> CircleSection(double radius)
    {
        const int sides = 16;
        var points = new (double Across, double Rise)[sides];
        for (int i = 0; i < sides; i++)
        {
            double angle = i * Math.PI * 2 / sides;
            points[i] = (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
        }

        return points;
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

    private static DirectShape? CreateShape(Document document, Element source, IList<Solid> solids)
    {
        var categories = new List<ElementId>();
        if (source.Category is not null)
            categories.Add(source.Category.Id);

        foreach (BuiltInCategory builtIn in new[]
                 {
                     BuiltInCategory.OST_DuctFitting,
                     BuiltInCategory.OST_PipeFitting,
                     BuiltInCategory.OST_ConduitFitting,
                     BuiltInCategory.OST_GenericModel
                 })
        {
            Category? category = Category.GetCategory(document, builtIn);
            if (category is not null)
                categories.Add(category.Id);
        }

        IList<GeometryObject> geometry = solids.Cast<GeometryObject>().ToList();
        foreach (ElementId categoryId in categories)
        {
            try
            {
                DirectShape shape = DirectShape.CreateElement(document, categoryId);
                shape.SetShape(geometry);
                shape.SetName(source.Category?.Name ?? "Fitting");
                return shape;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        return null;
    }

    private static void SuppressOriginal(Element element)
    {
        try
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.IsReadOnly || parameter.StorageType != StorageType.String)
                    continue;

                string? name = parameter.Definition?.Name;
                if (name is "IfcExportAs" or "IfcExportAs [Type]")
                    parameter.Set("DontExport");
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
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

    private static OrientedEnd ToOriented(FlowEnd end) =>
        new(
            ToVector(end.Origin),
            ToVector(end.Outward),
            -ToVector(end.Outward),
            ToVector(end.WidthDirection),
            (float)end.Width,
            (float)end.Height,
            end.Round);

    private static double Lerp(double start, double end, double t) => start + ((end - start) * t);

    private static Vector3 ToVector(XYZ point) => new((float)point.X, (float)point.Y, (float)point.Z);

    private static XYZ ToXyz(Vector3 point) => new(point.X, point.Y, point.Z);
}
