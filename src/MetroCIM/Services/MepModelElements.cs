using Autodesk.Revit.DB;

namespace MetroCIM.Services;

internal static class MepModelElements
{
    public static readonly BuiltInCategory[] Categories =
    [
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_PlaceHolderPipes,
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_PlaceHolderDucts,
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting
    ];

    public static readonly BuiltInCategory[] FittingCategories =
    [
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_CableTrayFitting,
        BuiltInCategory.OST_ConduitFitting
    ];

    /// <summary>
    /// Categories Revit's IFC exporter should write from Fine solids: ducts, pipes,
    /// conduits, and their fittings. Cable tray runs stay on the view detail level.
    /// </summary>
    public static readonly BuiltInCategory[] IfcSolidCategories =
    [
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_PlaceHolderPipes,
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_DuctFitting,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_PlaceHolderDucts,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_ConduitFitting,
        BuiltInCategory.OST_CableTrayFitting
    ];

    public static bool IsCableTrayRun(Element element) =>
        CategoryOf(element) == BuiltInCategory.OST_CableTray;

    public static bool IsCableTrayFitting(Element element) =>
        CategoryOf(element) == BuiltInCategory.OST_CableTrayFitting ||
        CategoryNameHas(element, "Cable Tray", "Fitting");

    public static bool IsFlowFitting(Element element)
    {
        BuiltInCategory category = CategoryOf(element);
        if (category is BuiltInCategory.OST_DuctFitting or
            BuiltInCategory.OST_PipeFitting or
            BuiltInCategory.OST_ConduitFitting)
        {
            return true;
        }

        return CategoryNameHas(element, "Duct", "Fitting") ||
               CategoryNameHas(element, "Pipe", "Fitting") ||
               CategoryNameHas(element, "Conduit", "Fitting");
    }

    public static bool IsFitting(Element element)
    {
        if (IsCableTrayFitting(element))
            return true;

        BuiltInCategory category = CategoryOf(element);
        foreach (BuiltInCategory candidate in FittingCategories)
        {
            if (category == candidate)
                return true;
        }

        return false;
    }

    public static bool IsRunOrFitting(Element element)
    {
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

        foreach (BuiltInCategory candidate in Categories)
        {
            if (category == candidate)
                return true;
        }

        return false;
    }

    private static bool CategoryNameHas(Element element, string first, string second)
    {
        string name;
        try
        {
            name = element.Category?.Name ?? string.Empty;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }

        return name.Contains(first, StringComparison.OrdinalIgnoreCase) &&
               name.Contains(second, StringComparison.OrdinalIgnoreCase);
    }

    private static BuiltInCategory CategoryOf(Element element)
    {
        try
        {
            if (element.Category is not null)
                return element.Category.BuiltInCategory;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        if (element.Category is null)
            return BuiltInCategory.INVALID;

        try
        {
            return (BuiltInCategory)element.Category.Id.Value;
        }
        catch (InvalidCastException)
        {
            return BuiltInCategory.INVALID;
        }
    }
}
