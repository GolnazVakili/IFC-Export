using Autodesk.Revit.DB;

namespace MetroCIM.Services;

public sealed class VisibilityService
{
    public IReadOnlyList<Element> GetVisibleElements(Document doc, View3D view, Action<int>? heartbeat = null)
    {
        var results = new List<Element>();

        using var collector = new FilteredElementCollector(doc, view.Id)
            .WherePasses(new VisibleInViewFilter(doc, view.Id))
            .WhereElementIsNotElementType();

        bool tempHideIsolate = view.IsTemporaryHideIsolateActive();
        var hiddenCategories = new Dictionary<ElementId, bool>();
        var hiddenWorksets = new Dictionary<WorksetId, bool>();
        int scanned = 0;

        foreach (Element element in collector)
        {
            scanned++;
            if ((scanned & 127) == 0)
                heartbeat?.Invoke(scanned);

            if (!IsExportable(element, view, tempHideIsolate, hiddenCategories, hiddenWorksets))
                continue;

            results.Add(element);
        }

        AddMepRunsAndFittings(doc, view, tempHideIsolate, hiddenCategories, hiddenWorksets, results);
        return results;
    }

    private static void AddMepRunsAndFittings(
        Document doc,
        View3D view,
        bool tempHideIsolate,
        Dictionary<ElementId, bool> hiddenCategories,
        Dictionary<WorksetId, bool> hiddenWorksets,
        List<Element> results)
    {
        var seen = new HashSet<ElementId>(results.Select(element => element.Id));
        foreach (BuiltInCategory category in MepModelElements.Categories)
        {
            IList<Element> extra;
            try
            {
                extra = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .ToElements();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                continue;
            }

            foreach (Element element in extra)
            {
                if (!seen.Add(element.Id))
                    continue;

                if (!IsExportable(element, view, tempHideIsolate, hiddenCategories, hiddenWorksets))
                    continue;

                if (!IsInsideView(view, element))
                    continue;

                results.Add(element);
            }
        }
    }

    private static bool IsInsideView(View3D view, Element element)
    {
        try
        {
            if (element.get_BoundingBox(view) is not null)
                return true;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }

        if (!MepModelElements.IsFitting(element))
            return false;

        BoundingBoxXYZ? model;
        try
        {
            model = element.get_BoundingBox(null);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }

        if (model is null)
            return false;

        if (!view.CropBoxActive)
            return true;

        try
        {
            BoundingBoxXYZ crop = view.CropBox;
            Transform toView = crop.Transform.Inverse;
            bool any = false;
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            foreach (XYZ corner in Corners(model))
            {
                XYZ point = toView.OfPoint(corner);
                if (!any)
                {
                    minX = maxX = point.X;
                    minY = maxY = point.Y;
                    minZ = maxZ = point.Z;
                    any = true;
                    continue;
                }

                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
                minZ = Math.Min(minZ, point.Z);
                maxZ = Math.Max(maxZ, point.Z);
            }

            return any &&
                   minX <= crop.Max.X && maxX >= crop.Min.X &&
                   minY <= crop.Max.Y && maxY >= crop.Min.Y &&
                   minZ <= crop.Max.Z && maxZ >= crop.Min.Z;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return true;
        }
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
    {
        XYZ min = box.Min;
        XYZ max = box.Max;
        Transform transform = box.Transform;
        for (int x = 0; x < 2; x++)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int z = 0; z < 2; z++)
                {
                    var point = new XYZ(
                        x == 0 ? min.X : max.X,
                        y == 0 ? min.Y : max.Y,
                        z == 0 ? min.Z : max.Z);
                    yield return transform.OfPoint(point);
                }
            }
        }
    }

    private static bool IsExportable(
        Element element,
        View3D view,
        bool tempHideIsolate,
        Dictionary<ElementId, bool> hiddenCategories,
        Dictionary<WorksetId, bool> hiddenWorksets)
    {
        if (element is RevitLinkInstance or ElementType)
            return false;

        Category? category = element.Category;
        if (category is null || category.CategoryType != CategoryType.Model)
            return false;

        if (IsHiddenInView(element, view))
            return false;

        if (IsCategoryHidden(view, category.Id, hiddenCategories))
            return false;

        if (tempHideIsolate &&
            !view.IsElementVisibleInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate, element.Id))
        {
            return false;
        }

        if (IsHiddenByWorkset(element, view, hiddenWorksets))
            return false;

        return true;
    }

    private static bool IsHiddenInView(Element element, View view)
    {
        try
        {
            return element.IsHidden(view);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    private static bool IsCategoryHidden(View view, ElementId categoryId, Dictionary<ElementId, bool> cache)
    {
        if (cache.TryGetValue(categoryId, out bool hidden))
            return hidden;

        try
        {
            hidden = view.GetCategoryHidden(categoryId);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            hidden = false;
        }

        cache[categoryId] = hidden;
        return hidden;
    }

    private static bool IsHiddenByWorkset(Element element, View view, Dictionary<WorksetId, bool> cache)
    {
        WorksetId worksetId = element.WorksetId;
        if (worksetId == WorksetId.InvalidWorksetId)
            return false;

        if (cache.TryGetValue(worksetId, out bool hidden))
            return hidden;

        try
        {
            WorksetVisibility visibility = view.GetWorksetVisibility(worksetId);
            hidden = visibility == WorksetVisibility.Hidden
                     || (visibility != WorksetVisibility.Visible
                         && !element.Document.GetWorksetTable().GetWorkset(worksetId).IsVisibleByDefault);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            hidden = false;
        }

        cache[worksetId] = hidden;
        return hidden;
    }
}
