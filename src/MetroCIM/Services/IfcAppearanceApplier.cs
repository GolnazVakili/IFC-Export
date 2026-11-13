using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MetroCIM.Models;
using RevitColor = Autodesk.Revit.DB.Color;

namespace MetroCIM.Services;

/// <summary>
/// Pushes <see cref="ColorResolver"/> colors onto elements for the duration of an IFC export.
/// Revit's IFC writer uses physical materials; this makes it use the same system-type / view colors as glTF.
/// </summary>
public sealed class IfcAppearanceApplier
{
    public static string MaterialName(ResolvedAppearance appearance) =>
        $"MetroCIM {appearance.R:D3}-{appearance.G:D3}-{appearance.B:D3}-{appearance.A:D3}";

    public static Dictionary<string, ResolvedAppearance> Apply(Document document, View view)
    {
        var colorsByIfcGuid = new Dictionary<string, ResolvedAppearance>(StringComparer.Ordinal);
        if (view is not View3D view3D || view3D.IsTemplate)
            return colorsByIfcGuid;

        var visibility = new VisibilityService();
        var colors = new ColorResolver();
        colors.Bind(view3D);

        var materials = new Dictionary<(byte R, byte G, byte B, byte A), ElementId>();
        var materialsByName = IndexMaterials(document);
        ElementId? solidFillId = FindSolidFill(document);
        var systemMaterials = new Dictionary<ElementId, ElementId>();
        var systems = new MepSystemTypeColor();
        var lockedFittings = new HashSet<ElementId>();
        var coveredByParent = new HashSet<ElementId>();

        bool changed = false;
        foreach (Element element in visibility.GetVisibleElements(document, view3D))
        {
            ResolvedAppearance appearance = colors.Resolve(element, view3D);
            IndexTree(colorsByIfcGuid, colors, view3D, element, null, lockedFittings, coveredByParent);

            ElementId materialId = GetOrCreateMaterial(document, materials, materialsByName, appearance);
            changed |= TrySetMaterialParameter(element, materialId);
            changed |= TrySetIfcMaterialOverride(element, MaterialName(appearance));
            changed |= TrySetOverrides(view3D, element, appearance, solidFillId);
            changed |= PaintFaces(document, element, materialId);
            changed |= TrySetSystemTypeMaterial(systems, element, materialId, systemMaterials);
        }

        if (changed)
            document.Regenerate();

        return colorsByIfcGuid;
    }

    public static void HarvestStoredGuids(
        Document document,
        View view,
        Dictionary<string, ResolvedAppearance> colorsByIfcGuid)
    {
        if (view is not View3D view3D || view3D.IsTemplate)
            return;

        var visibility = new VisibilityService();
        var colors = new ColorResolver();
        colors.Bind(view3D);
        var lockedFittings = new HashSet<ElementId>();
        var coveredByParent = new HashSet<ElementId>();
        foreach (Element element in visibility.GetVisibleElements(document, view3D))
            IndexTree(colorsByIfcGuid, colors, view3D, element, null, lockedFittings, coveredByParent);
    }

    private static Dictionary<string, ElementId> IndexMaterials(Document document)
    {
        var byName = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        foreach (Material existing in new FilteredElementCollector(document)
                     .OfClass(typeof(Material))
                     .Cast<Material>())
        {
            byName.TryAdd(existing.Name, existing.Id);
        }

        return byName;
    }

    private static ElementId GetOrCreateMaterial(
        Document document,
        Dictionary<(byte R, byte G, byte B, byte A), ElementId> cache,
        Dictionary<string, ElementId> byName,
        ResolvedAppearance appearance)
    {
        var key = (appearance.R, appearance.G, appearance.B, appearance.A);
        if (cache.TryGetValue(key, out ElementId? cached) && cached is not null)
            return cached;

        string name = MaterialName(appearance);
        ElementId id;
        if (byName.TryGetValue(name, out ElementId? existing) && existing is not null)
        {
            id = existing;
        }
        else
        {
            id = Material.Create(document, name);
            byName[name] = id;
        }

        if (document.GetElement(id) is Material material)
            ApplyColor(material, appearance);

        cache[key] = id;
        return id;
    }

    private static void ApplyColor(Material material, ResolvedAppearance appearance)
    {
        var color = new RevitColor(appearance.R, appearance.G, appearance.B);
        int transparency = Math.Clamp((int)Math.Round((255 - appearance.A) * 100d / 255d), 0, 100);
        material.Color = color;
        material.Transparency = transparency;
        material.Shininess = 32;
        material.Smoothness = 50;
        try
        {
            material.SurfaceForegroundPatternColor = color;
            material.CutForegroundPatternColor = color;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static bool TrySetMaterialParameter(Element element, ElementId materialId)
    {
        Parameter? parameter = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
        if (parameter is not { StorageType: StorageType.ElementId, IsReadOnly: false })
            return false;

        return parameter.Set(materialId);
    }

    private static bool TrySetOverrides(
        View3D view,
        Element element,
        ResolvedAppearance appearance,
        ElementId? solidFillId)
    {
        try
        {
            var color = new RevitColor(appearance.R, appearance.G, appearance.B);
            OverrideGraphicSettings overrides = view.GetElementOverrides(element.Id);
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetCutForegroundPatternColor(color);
            if (solidFillId is not null)
            {
                overrides.SetSurfaceForegroundPatternId(solidFillId);
                overrides.SetCutForegroundPatternId(solidFillId);
            }

            int transparency = Math.Clamp((int)Math.Round((255 - appearance.A) * 100d / 255d), 0, 100);
            overrides.SetSurfaceTransparency(transparency);
            view.SetElementOverrides(element.Id, overrides);
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static bool TrySetIfcMaterialOverride(Element element, string materialName)
    {
        Parameter? parameter = element.LookupParameter("IfcSingleMaterialOverride");
        if (parameter is not { StorageType: StorageType.String, IsReadOnly: false })
            return false;

        return parameter.Set(materialName);
    }

    private static bool TrySetSystemTypeMaterial(
        MepSystemTypeColor systems,
        Element element,
        ElementId materialId,
        Dictionary<ElementId, ElementId> assigned)
    {
        MEPSystemType? systemType = systems.ResolveSystemType(element);
        if (systemType is null)
            return false;

        if (assigned.TryGetValue(systemType.Id, out ElementId? existing) && existing == materialId)
            return false;

        try
        {
            if (systemType.MaterialId == materialId)
            {
                assigned[systemType.Id] = materialId;
                return false;
            }

            systemType.MaterialId = materialId;
            assigned[systemType.Id] = materialId;
            return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static void IndexTree(
        Dictionary<string, ResolvedAppearance> colorsByIfcGuid,
        ColorResolver colors,
        View3D view,
        Element element,
        ResolvedAppearance? inherited,
        HashSet<ElementId> lockedFittings,
        HashSet<ElementId> coveredByParent)
    {
        bool fitting = ColorResolver.IsFittingOrAccessory(element);
        ResolvedAppearance appearance;
        if (fitting)
        {
            if (!lockedFittings.Add(element.Id))
                return;

            appearance = colors.Resolve(element, view);
            IndexGuids(colorsByIfcGuid, element, appearance);
        }
        else if (inherited is not null)
        {
            coveredByParent.Add(element.Id);
            appearance = inherited.Value;
            IndexGuids(colorsByIfcGuid, element, appearance);
        }
        else if (coveredByParent.Contains(element.Id))
        {
            return;
        }
        else
        {
            appearance = colors.Resolve(element, view);
            IndexGuids(colorsByIfcGuid, element, appearance);
        }

        if (element is not FamilyInstance instance)
            return;

        try
        {
            foreach (ElementId nestedId in instance.GetSubComponentIds())
            {
                if (nestedId == instance.Id)
                    continue;

                if (element.Document.GetElement(nestedId) is Element nested)
                {
                    IndexTree(
                        colorsByIfcGuid,
                        colors,
                        view,
                        nested,
                        appearance,
                        lockedFittings,
                        coveredByParent);
                }
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static void IndexGuids(
        Dictionary<string, ResolvedAppearance> colorsByIfcGuid,
        Element element,
        ResolvedAppearance appearance)
    {
        try
        {
            Guid exportId = ExportUtils.GetExportId(element.Document, element.Id);
            colorsByIfcGuid[IfcGuid.From(exportId)] = appearance;
            if (!string.IsNullOrWhiteSpace(element.UniqueId))
                colorsByIfcGuid[element.UniqueId] = appearance;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        try
        {
            Parameter? stored = element.get_Parameter(BuiltInParameter.IFC_GUID);
            if (stored is { HasValue: true, StorageType: StorageType.String } &&
                stored.AsString() is { Length: 22 } value)
            {
                colorsByIfcGuid[value] = appearance;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static bool PaintFaces(Document document, Element element, ElementId materialId)
    {
        Options options = new()
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = false
        };

        GeometryElement? geometry;
        try
        {
            geometry = element.get_Geometry(options);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }

        return geometry is not null && PaintGeometry(document, element.Id, geometry, materialId);
    }

    private static bool PaintGeometry(Document document, ElementId elementId, GeometryElement geometry, ElementId materialId)
    {
        bool painted = false;
        foreach (GeometryObject obj in geometry)
        {
            switch (obj)
            {
                case Solid solid:
                    painted |= PaintSolid(document, elementId, solid, materialId);
                    break;
                case GeometryInstance instance:
                    GeometryElement? instanceGeometry = instance.GetInstanceGeometry();
                    if (instanceGeometry is not null)
                        painted |= PaintGeometry(document, elementId, instanceGeometry, materialId);
                    break;
                case GeometryElement nested:
                    painted |= PaintGeometry(document, elementId, nested, materialId);
                    break;
            }
        }

        return painted;
    }

    private static bool PaintSolid(Document document, ElementId elementId, Solid solid, ElementId materialId)
    {
        if (solid.Faces.IsEmpty)
            return false;

        bool painted = false;
        foreach (Face face in solid.Faces)
        {
            if (face.Area < 1e-6)
                continue;

            try
            {
                document.Paint(elementId, face, materialId);
                painted = true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Face has no reference or cannot be painted.
            }
        }

        return painted;
    }

    private static ElementId? FindSolidFill(Document document)
    {
        foreach (FillPatternElement pattern in new FilteredElementCollector(document)
                     .OfClass(typeof(FillPatternElement))
                     .Cast<FillPatternElement>())
        {
            try
            {
                if (pattern.GetFillPattern()?.IsSolidFill == true)
                    return pattern.Id;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
            }
        }

        return null;
    }
}
