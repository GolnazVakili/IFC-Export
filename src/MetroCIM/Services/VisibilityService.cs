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

                try
                {
                    if (element.get_BoundingBox(view) is null)
                        continue;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                results.Add(element);
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
