using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MetroCIM.Models;
using RevitColor = Autodesk.Revit.DB.Color;

namespace MetroCIM.Services;

public sealed class ColorResolver
{
    private readonly MepSystemTypeColor _systems = new();
    private readonly Dictionary<ElementId, ResolvedAppearance> _byElement = [];
    private readonly Dictionary<ElementId, HashSet<ElementId>?> _filterPasses = [];
    private IList<(FilterElement Filter, OverrideGraphicSettings Overrides)> _activeFilters = [];
    private View3D? _boundView;

    public void Bind(View3D view)
    {
        _boundView = view;
        _byElement.Clear();
        _filterPasses.Clear();
        _activeFilters = [];
        try
        {
            foreach (ElementId filterId in view.GetOrderedFilters())
            {
                if (!view.GetIsFilterEnabled(filterId) || !view.GetFilterVisibility(filterId))
                    continue;
                if (view.Document.GetElement(filterId) is not FilterElement filter)
                    continue;
                _activeFilters.Add((filter, view.GetFilterOverrides(filterId)));
            }
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            _activeFilters = [];
        }
    }

    public ResolvedAppearance Resolve(Element element, View3D view)
    {
        if (!ReferenceEquals(_boundView, view))
            Bind(view);

        if (_byElement.TryGetValue(element.Id, out ResolvedAppearance cached))
            return cached;

        ResolvedAppearance appearance = ResolveUncached(element, view);
        _byElement[element.Id] = appearance;
        return appearance;
    }

    private ResolvedAppearance ResolveUncached(Element element, View3D view)
    {
        Element source = GetAppearanceSource(element);
        bool piping = IsPipingElement(source);
        int? transparency = null;

        if (TryGetFilterAppearance(source, view, out ResolvedAppearance filterColor, out int? filterTransparency))
        {
            transparency = filterTransparency ?? transparency;
            return WithTransparency(filterColor, transparency);
        }

        if (TryGetOverrideAppearance(source, view, piping, out ResolvedAppearance overrideColor, out int? overrideTransparency))
        {
            transparency = overrideTransparency ?? transparency;
            return WithTransparency(overrideColor, transparency);
        }

        if (TryGetSystemTypeAppearance(source, view, piping, out ResolvedAppearance systemColor))
            return WithTransparency(systemColor, transparency);

        if (piping)
            return WithTransparency(ResolvedAppearance.Default, transparency);

        return WithTransparency(GetCategoryDefaultAppearance(source), transparency);
    }

    private static Element GetAppearanceSource(Element element)
    {
        if (element is InsulationLiningBase insulation &&
            insulation.HostElementId != ElementId.InvalidElementId &&
            element.Document.GetElement(insulation.HostElementId) is Element host)
        {
            return host;
        }

        Element current = element;
        for (int i = 0; i < 8; i++)
        {
            if (current is not FamilyInstance instance)
                break;

            Element? parent;
            try
            {
                parent = instance.SuperComponent;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                break;
            }

            if (parent is null || parent.Id == current.Id)
                break;

            if (!IsPipingElement(parent) && !IsPipingElement(current))
                break;

            current = parent;
        }

        return current;
    }

    private bool TryGetFilterAppearance(
        Element element,
        View3D view,
        out ResolvedAppearance appearance,
        out int? transparency)
    {
        appearance = default;
        transparency = null;
        ResolvedAppearance? lastColor = null;

        foreach ((FilterElement filter, OverrideGraphicSettings overrides) in _activeFilters)
        {
            if (!ElementPassesFilter(element, filter))
                continue;

            if (TryGetSurfaceColor(overrides, out ResolvedAppearance color))
                lastColor = color;

            if (TryGetTransparency(overrides, out int t))
                transparency = t;
        }

        if (lastColor is null)
            return false;

        appearance = lastColor.Value;
        return true;
    }

    private static bool TryGetOverrideAppearance(
        Element element,
        View3D view,
        bool piping,
        out ResolvedAppearance appearance,
        out int? transparency)
    {
        appearance = default;
        transparency = null;

        OverrideGraphicSettings elementOverrides = view.GetElementOverrides(element.Id);
        if (TryGetSurfaceColor(elementOverrides, out appearance))
        {
            if (TryGetTransparency(elementOverrides, out int t))
                transparency = t;
            return true;
        }

        if (TryGetTransparency(elementOverrides, out int elementTransparency))
            transparency = elementTransparency;

        if (piping)
            return false;

        if (element.Category is not null)
        {
            try
            {
                OverrideGraphicSettings categoryOverrides = view.GetCategoryOverrides(element.Category.Id);
                if (TryGetSurfaceColor(categoryOverrides, out appearance))
                {
                    if (TryGetTransparency(categoryOverrides, out int t))
                        transparency ??= t;
                    return true;
                }

                if (TryGetTransparency(categoryOverrides, out int categoryTransparency))
                    transparency ??= categoryTransparency;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Category does not support graphic overrides.
            }
        }

        return false;
    }

    private bool TryGetSystemTypeAppearance(
        Element element,
        View3D view,
        bool piping,
        out ResolvedAppearance appearance)
    {
        appearance = default;

        if (element.Category is not null)
        {
            try
            {
                ElementId schemeId = view.GetColorFillSchemeId(element.Category.Id);
                if (schemeId != ElementId.InvalidElementId &&
                    view.Document.GetElement(schemeId) is ColorFillScheme scheme &&
                    !(piping && MepSystemTypeColor.IsPhysicalMaterialParameter(scheme.ParameterDefinition)) &&
                    TryMatchColorFill(element, scheme, out appearance))
                {
                    return true;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Category does not support color fill in this view.
            }
        }

        return _systems.TryGet(element, out appearance);
    }

    private static ResolvedAppearance GetCategoryDefaultAppearance(Element element)
    {
        Document doc = element.Document;

        ICollection<ElementId> materialIds = [];
        try
        {
            materialIds = element.GetMaterialIds(false);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Element does not support material quantities.
        }

        foreach (ElementId materialId in materialIds)
        {
            if (doc.GetElement(materialId) is Material material &&
                TryFromRevitColor(material.Color, material.Transparency, out ResolvedAppearance fromMaterial) &&
                !fromMaterial.IsNearBlack)
            {
                return fromMaterial;
            }
        }

        if (element.Category?.Material is { } categoryMaterial &&
            TryFromRevitColor(categoryMaterial.Color, categoryMaterial.Transparency, out ResolvedAppearance fromCategoryMaterial) &&
            !fromCategoryMaterial.IsNearBlack)
        {
            return fromCategoryMaterial;
        }

        return ResolvedAppearance.Default;
    }

    private bool ElementPassesFilter(Element element, FilterElement filter)
    {
        try
        {
            switch (filter)
            {
                case ParameterFilterElement parameterFilter:
                    if (!CategoryMatches(parameterFilter, element))
                        return false;

                    return ElementPassesRules(parameterFilter, element);

                case SelectionFilterElement selectionFilter:
                    return selectionFilter.GetElementIds().Contains(element.Id);

                default:
                    return false;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static bool CategoryMatches(ParameterFilterElement filter, Element element)
    {
        if (element.Category is null)
            return false;

        ICollection<ElementId> categories = filter.GetCategories();
        if (categories.Contains(element.Category.Id))
            return true;

        try
        {
            Category? parent = element.Category.Parent;
            return parent is not null && categories.Contains(parent.Id);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private bool ElementPassesRules(ParameterFilterElement filter, Element element)
    {
        if (!_filterPasses.TryGetValue(filter.Id, out HashSet<ElementId>? passing))
        {
            passing = TryCollectPassingIds(filter);
            _filterPasses[filter.Id] = passing;
        }

        if (passing is not null)
            return passing.Contains(element.Id);

        try
        {
            ElementFilter? rules = filter.GetElementFilter();
            if (rules is null)
                return true;

            return rules.PassesFilter(element.Document, element.Id) || rules.PassesFilter(element);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private HashSet<ElementId>? TryCollectPassingIds(ParameterFilterElement filter)
    {
        if (_boundView is null)
            return null;

        try
        {
            var collector = new FilteredElementCollector(_boundView.Document, _boundView.Id)
                .WhereElementIsNotElementType();
            ElementFilter? rules = filter.GetElementFilter();
            if (rules is not null)
                collector.WherePasses(rules);

            return collector.ToElementIds().ToHashSet();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    private static bool TryMatchColorFill(Element element, ColorFillScheme scheme, out ResolvedAppearance appearance)
    {
        appearance = default;
        Parameter? parameter = FindParameter(element, scheme.ParameterDefinition);
        if (parameter is null || !parameter.HasValue)
            return false;

        foreach (ColorFillSchemeEntry entry in scheme.GetEntries())
        {
            if (!entry.IsVisible)
                continue;

            if (!EntryMatches(parameter, entry))
                continue;

            if (TryFromRevitColor(entry.Color, 0, out appearance))
                return true;
        }

        return false;
    }

    private static bool EntryMatches(Parameter parameter, ColorFillSchemeEntry entry)
    {
        if (parameter.StorageType != entry.StorageType)
            return false;

        return entry.StorageType switch
        {
            StorageType.Integer => parameter.AsInteger() == entry.GetIntegerValue(),
            StorageType.Double => Math.Abs(parameter.AsDouble() - entry.GetDoubleValue()) < 1e-6,
            StorageType.String => string.Equals(parameter.AsString() ?? string.Empty, entry.GetStringValue() ?? string.Empty, StringComparison.Ordinal),
            StorageType.ElementId => parameter.AsElementId() == entry.GetElementIdValue(),
            _ => false
        };
    }

    private static bool IsPipingElement(Element element)
    {
        if (element is Pipe or FlexPipe or PipeInsulation)
            return true;

        BuiltInCategory category = BuiltInCategory.INVALID;
        try
        {
            if (element.Category is not null)
                category = element.Category.BuiltInCategory;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            category = BuiltInCategory.INVALID;
        }

        if (category == BuiltInCategory.INVALID && element.Category is not null)
        {
            try
            {
                category = (BuiltInCategory)element.Category.Id.Value;
            }
            catch (InvalidCastException)
            {
                return false;
            }
        }

        return category is
            BuiltInCategory.OST_PipeCurves or
            BuiltInCategory.OST_FlexPipeCurves or
            BuiltInCategory.OST_PipeInsulations or
            BuiltInCategory.OST_PipeFitting or
            BuiltInCategory.OST_PipeAccessory or
            BuiltInCategory.OST_PlaceHolderPipes;
    }

    private static Parameter? FindParameter(Element element, ElementId definitionId)
    {
        if (definitionId == ElementId.InvalidElementId)
            return null;

        foreach (Parameter parameter in element.Parameters)
        {
            if (parameter.Id == definitionId)
                return parameter;
        }

        ElementId typeId = element.GetTypeId();
        if (typeId != ElementId.InvalidElementId && element.Document.GetElement(typeId) is Element type)
        {
            foreach (Parameter parameter in type.Parameters)
            {
                if (parameter.Id == definitionId)
                    return parameter;
            }
        }

        return null;
    }

    private static bool TryGetSurfaceColor(OverrideGraphicSettings settings, out ResolvedAppearance appearance)
    {
        if (HasPatternOverride(settings.SurfaceForegroundPatternId) &&
            settings.IsSurfaceForegroundPatternVisible &&
            TryFromRevitColor(settings.SurfaceForegroundPatternColor, settings.Transparency, out appearance))
        {
            return true;
        }

        if (HasPatternOverride(settings.SurfaceBackgroundPatternId) &&
            settings.IsSurfaceBackgroundPatternVisible &&
            TryFromRevitColor(settings.SurfaceBackgroundPatternColor, settings.Transparency, out appearance))
        {
            return true;
        }

        if (HasPatternOverride(settings.CutForegroundPatternId) &&
            settings.IsCutForegroundPatternVisible &&
            TryFromRevitColor(settings.CutForegroundPatternColor, settings.Transparency, out appearance))
        {
            return true;
        }

        if (HasPatternOverride(settings.CutBackgroundPatternId) &&
            settings.IsCutBackgroundPatternVisible &&
            TryFromRevitColor(settings.CutBackgroundPatternColor, settings.Transparency, out appearance))
        {
            return true;
        }

        if (TryFromRevitColor(settings.ProjectionLineColor, settings.Transparency, out appearance))
            return true;

        if (TryFromRevitColor(settings.CutLineColor, settings.Transparency, out appearance))
            return true;

        appearance = default;
        return false;
    }

    private static bool HasPatternOverride(ElementId patternId) =>
        patternId != ElementId.InvalidElementId;

    private static bool TryGetTransparency(OverrideGraphicSettings settings, out int transparency)
    {
        transparency = settings.Transparency;
        return transparency > 0;
    }

    private static bool TryFromRevitColor(RevitColor? color, int transparencyPercent, out ResolvedAppearance appearance)
    {
        appearance = default;
        if (color is null || !color.IsValid)
            return false;

        appearance = ResolvedAppearance.FromRgb(color.Red, color.Green, color.Blue, transparencyPercent);
        return true;
    }

    private static ResolvedAppearance WithTransparency(ResolvedAppearance color, int? transparency)
    {
        if (transparency is null)
            return color;

        return ResolvedAppearance.FromRgb(color.R, color.G, color.B, transparency.Value);
    }
}
