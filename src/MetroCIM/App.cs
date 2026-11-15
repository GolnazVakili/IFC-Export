using Autodesk.Revit.UI;
using MetroCIM.Commands;
using System.Reflection;
using System.Windows.Media.Imaging;

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
            "glTF",
            assemblyPath,
            typeof(ExportGltfCommand).FullName)
        {
            ToolTip = "Export glTF (View Colors)",
            LongDescription =
                "Exports the active 3D view to glTF (.glb/.gltf) using on-screen visibility and graphic overrides.",
            LargeImage = LoadPng("Resources/gltf_32.png"),
            Image = LoadPng("Resources/gltf_16.png")
        };

        var xktButton = new PushButtonData(
            "ExportXkt",
            "XKT",
            assemblyPath,
            typeof(ExportXktCommand).FullName)
        {
            ToolTip = "Export XKT (View Colors)",
            LongDescription =
                "Exports the active 3D view to XKT via an intermediate glTF, using xeokit-convert. " +
                "Requires Node.js and `npm install -g @xeokit/xeokit-convert`.",
            LargeImage = LoadPng("Resources/xkt_32.png"),
            Image = LoadPng("Resources/xkt_16.png")
        };

        var ifcButton = new PushButtonData(
            "ExportIfc",
            "IFC",
            assemblyPath,
            typeof(ExportIfcCommand).FullName)
        {
            ToolTip = "Export IFC (Revit IFC setup)",
            LongDescription =
                "Exports the model to IFC using a Revit IFC export setup (in-session, built-in, or saved in this project).",
            LargeImage = LoadPng("Resources/ifc_32.png"),
            Image = LoadPng("Resources/ifc_16.png")
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

    private static BitmapImage LoadPng(string relativePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/MetroCIM;component/{relativePath}");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
