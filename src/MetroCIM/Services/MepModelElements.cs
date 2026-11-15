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
}
