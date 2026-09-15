using Autodesk.Revit.UI;
using MetroCIM.Commands;
using System.Reflection;

namespace MetroCIM;

public sealed class App : IExternalApplication
{
    public const string TabName = "Automation";
    public const string PanelName = "Export";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // Tab already exists from a previous load.
        }

        RibbonPanel panel = GetOrCreatePanel(application);
        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        var gltfButton = new PushButtonData(
            "ExportGltf",
            "Export\nglTF",
            assemblyPath,
            typeof(ExportGltfCommand).FullName)
        {
            ToolTip = "Export glTF (View Colors)",
            LongDescription =
                "Exports the active 3D view to glTF (.glb/.gltf) using on-screen visibility and graphic overrides."
        };

        var xktButton = new PushButtonData(
            "ExportXkt",
            "Export\nXKT",
            assemblyPath,
            typeof(ExportXktCommand).FullName)
        {
            ToolTip = "Export XKT (View Colors)",
            LongDescription =
                "Exports the active 3D view to XKT via an intermediate glTF, using xeokit-convert. " +
                "Requires Node.js and `npm install -g @xeokit/xeokit-convert`."
        };

        var ifcButton = new PushButtonData(
            "ExportIfc",
            "Export\nIFC",
            assemblyPath,
            typeof(ExportIfcCommand).FullName)
        {
            ToolTip = "Export IFC (Revit IFC setup)",
            LongDescription =
                "Exports the model to IFC using a Revit IFC export setup (in-session, built-in, or saved in this project)."
        };

        panel.AddItem(gltfButton);
        panel.AddItem(xktButton);
        panel.AddItem(ifcButton);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static RibbonPanel GetOrCreatePanel(UIControlledApplication application)
    {
        foreach (RibbonPanel existing in application.GetRibbonPanels(TabName))
        {
            if (existing.Name == PanelName)
                return existing;
        }

        return application.CreateRibbonPanel(TabName, PanelName);
    }
}
