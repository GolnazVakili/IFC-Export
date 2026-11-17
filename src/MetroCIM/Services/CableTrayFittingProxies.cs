using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using MetroCIM.Models;
using System.Numerics;

namespace MetroCIM.Services;

internal readonly record struct ExportProxySet(
    IReadOnlyList<ElementId> Created,
    IReadOnlyList<ElementId> Hidden);

internal static class CableTrayFittingProxies
{
    private const double WallFeet = 8.0 / 304.8;
    private const double JointOverlap = 0.01;

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
            IList<Solid> solids = CreateReplacement(element);
            if (solids.Count == 0)
                continue;

            DirectShape? shape = CreateShape(document, solids, MepModelElements.IsCableTrayFitting(element));
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

    public static void Remove(Document document, View view, ExportProxySet proxies)
    {
        if (proxies.Hidden.Count > 0)
        {
            try
            {
                view.UnhideElements(proxies.Hidden.ToList());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        if (proxies.Created.Count == 0)
            return;

        try
        {
            document.Delete(proxies.Created.ToList());
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static IList<Solid> CreateReplacement(Element element)
    {
        if (element is CableTray tray)
            return CreateRun(tray);

        if (!MepModelElements.IsCableTrayFitting(element) || !TryGetLegs(element, out IReadOnlyList<TrayLeg> legs))
            return [];

        List<OrientedEnd> oriented = legs.Select(ToOriented).ToList();
        var solids = new List<Solid>();
        if (legs.Count == 2)
        {
            AddPath(
                solids,
                DuctFittingPath.Elbow(oriented[0], oriented[1], (float)legs[0].Reach, (float)legs[1].Reach),
                legs[0],
                legs[1]);
            return solids;
        }

        Vector3 junction = DuctFittingPath.Junction(oriented);
        float span = DuctFittingPath.Span(oriented);
        for (int i = 0; i < legs.Count; i++)
        {
            if (!DuctFittingPath.Meets(oriented[i], junction, span))
                continue;

            AddPath(solids, DuctFittingPath.Branch(oriented[i], junction, (float)legs[i].Reach), legs[i], legs[i]);
        }

        if (solids.Count > 0)
            return solids;

        (int first, int second) = FarthestPair(legs);
        AddPath(
            solids,
            DuctFittingPath.Elbow(oriented[first], oriented[second], (float)legs[first].Reach, (float)legs[second].Reach),
            legs[first],
            legs[second]);
        return solids;
    }

    private readonly record struct TrayLeg(
        XYZ Origin,
        XYZ Outward,
        XYZ Opening,
        double Width,
        double Height,
        double Reach);

    private readonly record struct RawTray(
        XYZ Origin,
        XYZ Axis,
        XYZ Opening,
        double Width,
        double Height,
        XYZ IntoTray,
        bool HasTray);

    private static bool TryGetLegs(Element element, out IReadOnlyList<TrayLeg> legs)
    {
        legs = [];
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

        List<RawTray> raw = ReadConnectors(connectors, endOnly: true);
        if (raw.Count < 2)
            raw = ReadConnectors(connectors, endOnly: false);
        if (raw.Count < 2)
            return false;

        InheritSize(raw);
        raw = raw.Where(item => item.Width > 1e-6 && item.Height > 1e-6).ToList();
        if (raw.Count < 2)
            return false;

        XYZ center = XYZ.Zero;
        foreach (RawTray item in raw)
            center += item.Origin;
        center /= raw.Count;

        var found = new List<TrayLeg>(raw.Count);
        foreach (RawTray item in raw)
        {
            bool hasDirection = item.HasTray && item.IntoTray.GetLength() > 1e-4;
            Vector3 outward = hasDirection
                ? Vector3.Normalize(ToVector(item.IntoTray))
                : DuctFittingPath.Outward(ToVector(item.Origin), ToVector(item.Axis), ToVector(center));
            var away = new XYZ(outward.X, outward.Y, outward.Z);
            Vector3 opening = CableTrayFittingBody.AlignOpening(ToVector(item.Opening), outward);
            double distance = hasDirection ? Math.Max(0, item.IntoTray.DotProduct(away)) : 0;
            if (distance > Math.Max(item.Width, item.Height))
                distance = 0;
            double reach = DuctFittingPath.Reach((float)item.Width, (float)item.Height, (float)distance);
            found.Add(new TrayLeg(
                item.Origin,
                away,
                new XYZ(opening.X, opening.Y, opening.Z),
                item.Width,
                item.Height,
                reach));
        }

        legs = found;
        return found.Count >= 2;
    }

    private static List<RawTray> ReadConnectors(ConnectorSet connectors, bool endOnly)
    {
        var raw = new List<RawTray>();
        foreach (Connector connector in connectors)
        {
            try
            {
                if (endOnly && connector.ConnectorType != ConnectorType.End)
                    continue;

                Transform frame = connector.CoordinateSystem;
                double width = connector.Width;
                double height = connector.Height;
                XYZ opening = frame.BasisY;
                bool hasTray = TryHostTray(connector, ref width, ref height, ref opening, out XYZ intoTray);
                raw.Add(new RawTray(connector.Origin, frame.BasisZ, opening, width, height, intoTray, hasTray));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        return raw;
    }

    private static void InheritSize(List<RawTray> raw)
    {
        double width = 0;
        double height = 0;
        foreach (RawTray item in raw)
        {
            width = Math.Max(width, item.Width);
            height = Math.Max(height, item.Height);
        }

        if (width <= 1e-6 || height <= 1e-6)
            return;

        for (int i = 0; i < raw.Count; i++)
        {
            RawTray item = raw[i];
            if (item.Width > 1e-6 && item.Height > 1e-6)
                continue;

            raw[i] = item with
            {
                Width = item.Width > 1e-6 ? item.Width : width,
                Height = item.Height > 1e-6 ? item.Height : height
            };
        }
    }

    private static bool TryHostTray(
        Connector connector,
        ref double width,
        ref double height,
        ref XYZ opening,
        out XYZ intoTray)
    {
        intoTray = XYZ.Zero;
        try
        {
            foreach (Connector reference in connector.AllRefs)
            {
                if (reference.Owner is not CableTray tray)
                    continue;
                if (connector.Owner is not null && tray.Id == connector.Owner.Id)
                    continue;

                if (tray.Width > 1e-6)
                    width = tray.Width;
                if (tray.Height > 1e-6)
                    height = tray.Height;
                try
                {
                    if (tray.CurveNormal.GetLength() > 1e-8)
                        opening = tray.CurveNormal.Normalize();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                }

                if (tray.Location is LocationCurve location && location.Curve is Curve curve)
                {
                    XYZ end0 = curve.GetEndPoint(0);
                    XYZ end1 = curve.GetEndPoint(1);
                    XYZ near = connector.Origin.DistanceTo(end0) <= connector.Origin.DistanceTo(end1) ? end0 : end1;
                    XYZ far = near.IsAlmostEqualTo(end0) ? end1 : end0;
                    intoTray = far - near;
                }

                return true;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        return false;
    }

    private static (int First, int Second) FarthestPair(IReadOnlyList<TrayLeg> legs)
    {
        int first = 0;
        int second = 1;
        double farthest = 0;
        for (int i = 0; i < legs.Count; i++)
        {
            for (int j = i + 1; j < legs.Count; j++)
            {
                double distance = legs[i].Origin.DistanceTo(legs[j].Origin);
                if (distance > farthest)
                {
                    farthest = distance;
                    first = i;
                    second = j;
                }
            }
        }

        return (first, second);
    }

    private static void AddPath(List<Solid> solids, IReadOnlyList<Vector3> path, TrayLeg start, TrayLeg end)
    {
        if (path.Count < 2)
            return;

        Vector3 step = path[1] - path[0];
        if (step.Length() < 1e-4f)
            return;

        Vector3 opening = CableTrayFittingBody.AlignOpening(ToVector(start.Opening), Vector3.Normalize(step));
        Vector3 across = Vector3.Cross(Vector3.Normalize(step), opening);
        if (across.LengthSquared() < 1e-8f)
            return;

        across = Vector3.Normalize(across);
        Solid? lofted = FittingSolids.LoftSection(path, across, t =>
        {
            double sectionWidth = Lerp(start.Width, end.Width, t);
            double sectionHeight = Lerp(start.Height, end.Height, t);
            double wall = Math.Min(WallFeet, Math.Min(sectionWidth, sectionHeight) * 0.45);
            return sectionHeight <= wall * 2.5
                ? PlateSection(sectionWidth, wall)
                : ChannelSection(sectionWidth, sectionHeight, wall);
        });
        if (lofted is not null)
        {
            solids.Add(lofted);
            return;
        }

        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(path, across);
        bool sameSize = Math.Abs(start.Width - end.Width) < 1e-4 &&
                        Math.Abs(start.Height - end.Height) < 1e-4;
        Vector3 preferred = opening;
        for (int i = 0; i < frames.Count; i++)
        {
            SweepFrame frame = frames[i];
            double t = !sameSize && frames.Count > 1 ? i / (double)(frames.Count - 1) : 0;
            double width = Lerp(start.Width, end.Width, t);
            double height = Lerp(start.Height, end.Height, t);
            var axis = new XYZ(frame.Axis.X, frame.Axis.Y, frame.Axis.Z);
            var origin = new XYZ(frame.Start.X, frame.Start.Y, frame.Start.Z);
            double length = frame.Length;
            if (i > 0)
            {
                origin -= axis * JointOverlap;
                length += JointOverlap;
            }

            if (i < frames.Count - 1)
                length += JointOverlap;

            Vector3 thin = frame.Up;
            if (Vector3.Dot(thin, preferred) < 0)
                thin = -thin;
            preferred = thin;
            Solid? solid = ExtrudeChannel(origin, axis, ToXyz(thin), width, height, length);
            if (solid is not null)
                solids.Add(solid);
        }
    }

    private static OrientedEnd ToOriented(TrayLeg leg) =>
        new(
            ToVector(leg.Origin),
            ToVector(leg.Outward),
            -ToVector(leg.Outward),
            Vector3.Cross(ToVector(leg.Outward), ToVector(leg.Opening)),
            (float)leg.Width,
            (float)leg.Height,
            false);

    private static double Lerp(double start, double end, double t) => start + ((end - start) * t);

    private static Vector3 ToVector(XYZ point) => new((float)point.X, (float)point.Y, (float)point.Z);

    private static XYZ ToXyz(Vector3 point) => new(point.X, point.Y, point.Z);

    private static IList<Solid> CreateRun(CableTray tray)
    {
        Curve? curve;
        try
        {
            curve = (tray.Location as LocationCurve)?.Curve;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return [];
        }

        if (curve is null || tray.Width <= 1e-6 || tray.Height <= 1e-6)
            return [];

        XYZ up;
        try
        {
            up = tray.CurveNormal;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            up = XYZ.BasisZ;
        }

        IList<XYZ> points;
        try
        {
            points = curve.Tessellate();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return [];
        }

        var solids = new List<Solid>();
        for (int i = 1; i < points.Count; i++)
        {
            XYZ step = points[i] - points[i - 1];
            double length = step.GetLength();
            if (length < 1e-4)
                continue;

            Solid? solid = ExtrudeChannel(points[i - 1], step.Normalize(), up, tray.Width, tray.Height, length);
            if (solid is not null)
                solids.Add(solid);
        }

        return solids;
    }

    private static Solid? ExtrudeChannel(XYZ start, XYZ axis, XYZ up, double width, double height, double length)
    {
        try
        {
            if (Math.Abs(axis.DotProduct(up)) > 0.99)
                up = Math.Abs(axis.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;

            XYZ right = axis.CrossProduct(up);
            if (right.GetLength() < 1e-8)
                return null;

            right = right.Normalize();
            up = right.CrossProduct(axis).Normalize();
            double wall = Math.Min(WallFeet, Math.Min(width, height) * 0.45);
            IList<XYZ> profile = height <= wall * 2.5
                ? Plate(right, up, width, wall)
                : Channel(right, up, width, height, wall);

            var lines = new List<Curve>();
            for (int i = 0; i < profile.Count; i++)
            {
                XYZ a = start + profile[i];
                XYZ b = start + profile[(i + 1) % profile.Count];
                if (a.DistanceTo(b) < 1e-6)
                    return null;
                lines.Add(Line.CreateBound(a, b));
            }

            CurveLoop loop = CurveLoop.Create(lines);
            Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry([loop], axis, length);
            return solid.Volume > 1e-9 ? solid : null;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<(double Across, double Rise)> PlateSection(double width, double thickness)
    {
        double halfWidth = width * 0.5;
        double halfThickness = thickness * 0.5;
        return
        [
            (-halfWidth, -halfThickness),
            (halfWidth, -halfThickness),
            (halfWidth, halfThickness),
            (-halfWidth, halfThickness)
        ];
    }

    private static IReadOnlyList<(double Across, double Rise)> ChannelSection(double width, double height, double wall)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return
        [
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (halfWidth - wall, halfHeight),
            (halfWidth - wall, -halfHeight + wall),
            (-halfWidth + wall, -halfHeight + wall),
            (-halfWidth + wall, halfHeight),
            (-halfWidth, halfHeight)
        ];
    }

    private static IList<XYZ> Plate(XYZ right, XYZ up, double width, double thickness)
    {
        double halfWidth = width * 0.5;
        double halfThickness = thickness * 0.5;
        return
        [
            (-halfWidth * right) - (halfThickness * up),
            (halfWidth * right) - (halfThickness * up),
            (halfWidth * right) + (halfThickness * up),
            (-halfWidth * right) + (halfThickness * up)
        ];
    }

    private static IList<XYZ> Channel(XYZ right, XYZ up, double width, double height, double wall)
    {
        double halfWidth = width * 0.5;
        double halfHeight = height * 0.5;
        return
        [
            (-halfWidth * right) - (halfHeight * up),
            (halfWidth * right) - (halfHeight * up),
            (halfWidth * right) + (halfHeight * up),
            ((halfWidth - wall) * right) + (halfHeight * up),
            ((halfWidth - wall) * right) + ((-halfHeight + wall) * up),
            ((-halfWidth + wall) * right) + ((-halfHeight + wall) * up),
            ((-halfWidth + wall) * right) + (halfHeight * up),
            (-halfWidth * right) + (halfHeight * up)
        ];
    }

    private static DirectShape? CreateShape(Document document, IList<Solid> solids, bool fitting)
    {
        foreach (BuiltInCategory category in new[]
                 {
                     BuiltInCategory.OST_CableTrayFitting,
                     BuiltInCategory.OST_CableTray,
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
                shape.SetName(fitting ? "Cable tray fitting" : "Cable tray");
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

}
