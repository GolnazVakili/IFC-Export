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

        if (!MepModelElements.IsCableTrayFitting(element) ||
            !GeometryBuilder.TryGetCableTrayConnectors(element, out IReadOnlyList<CableTrayConnectorLeg> legs, out XYZ bend))
        {
            return [];
        }

        TryConnectedTrayProfile(element, legs[0], out double width, out double height, out XYZ up);
        double reach = JointReach(width, height);
        var solids = new List<Solid>();
        if (legs.Count == 2)
        {
            AddPath(solids, ConnectedCurve(legs[0], legs[1], reach), up, width, height);
            return solids;
        }

        foreach (CableTrayConnectorLeg leg in legs)
        {
            XYZ toward = bend - leg.Origin;
            if (toward.GetLength() < 1e-4)
                continue;

            AddPath(solids, [leg.Origin - (leg.Inward * reach), leg.Origin, bend], up, width, height);
        }

        return solids;
    }

    private static void TryConnectedTrayProfile(
        Element fitting,
        CableTrayConnectorLeg fallback,
        out double width,
        out double height,
        out XYZ up)
    {
        width = fallback.Width;
        height = fallback.Height;
        up = fallback.Inward.CrossProduct(fallback.WidthDirection);
        up = up.GetLength() < 1e-8 ? XYZ.BasisZ : up.Normalize();
        if (fitting is not FamilyInstance instance)
            return;

        try
        {
            ConnectorSet? connectors = instance.MEPModel?.ConnectorManager?.Connectors;
            if (connectors is null)
                return;

            foreach (Connector connector in connectors)
            {
                foreach (Connector reference in connector.AllRefs)
                {
                    if (reference.Owner is not CableTray tray)
                        continue;

                    width = tray.Width;
                    height = tray.Height;
                    up = tray.CurveNormal;
                    return;
                }
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static IReadOnlyList<XYZ> ConnectedCurve(
        CableTrayConnectorLeg start,
        CableTrayConnectorLeg end,
        double reach)
    {
        IReadOnlyList<Vector3> path = CableTrayFittingBody.ExtendIntoRuns(
            CableTrayFittingBody.Centerline(
                ToVector(start.Origin),
                ToVector(start.Inward),
                ToVector(end.Origin),
                ToVector(-end.Inward)),
            ToVector(-start.Inward),
            ToVector(-end.Inward),
            (float)reach);
        return path.Select(point => new XYZ(point.X, point.Y, point.Z)).ToList();
    }

    private static double JointReach(double width, double height) =>
        Math.Clamp(Math.Min(width, height) * 0.2, 0.04, 0.12);

    private static Vector3 ToVector(XYZ point) => new((float)point.X, (float)point.Y, (float)point.Z);

    private static void AddPath(List<Solid> solids, IReadOnlyList<XYZ> path, XYZ up, double width, double height)
    {
        if (path.Count < 2)
            return;

        XYZ opening = up.GetLength() < 1e-8 ? XYZ.BasisZ : up.Normalize();
        XYZ startStep = path[1] - path[0];
        if (startStep.GetLength() < 1e-4)
            return;

        XYZ startAxis = startStep.Normalize();
        XYZ across = startAxis.CrossProduct(opening);
        if (across.GetLength() < 1e-8)
            across = Perpendicular(XYZ.BasisX, startAxis);
        else
            across = across.Normalize();

        IReadOnlyList<SweepFrame> frames = DuctFittingFrames.Sweep(
            path.Select(point => new Vector3((float)point.X, (float)point.Y, (float)point.Z)).ToList(),
            new Vector3((float)across.X, (float)across.Y, (float)across.Z));

        for (int i = 0; i < frames.Count; i++)
        {
            SweepFrame frame = frames[i];
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

            var thin = new XYZ(frame.Up.X, frame.Up.Y, frame.Up.Z);
            Solid? solid = ExtrudeChannel(origin, axis, thin, width, height, length);
            if (solid is not null)
                solids.Add(solid);
        }
    }

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

    private static XYZ Perpendicular(XYZ preferred, XYZ axis)
    {
        XYZ projected = preferred - (axis * preferred.DotProduct(axis));
        if (projected.GetLength() < 1e-8)
        {
            XYZ fallback = Math.Abs(axis.X) < 0.9 ? XYZ.BasisX : XYZ.BasisY;
            projected = fallback - (axis * fallback.DotProduct(axis));
        }

        return projected.Normalize();
    }
}
