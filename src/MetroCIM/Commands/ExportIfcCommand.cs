using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MetroCIM.Services;

namespace MetroCIM.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ExportIfcCommand : IExternalCommand
{
    public const string Title = "Export IFC";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!CommandUi.TryGetActiveDocument(commandData, Title, out Document document, out Autodesk.Revit.DB.View view))
            return Result.Cancelled;

        try
        {
            if (!IfcExporter.TryCreate(document, out IfcExporter exporter, out string error))
            {
                if (!CommandUi.Confirm(
                        Title,
                        error + "\n\nExport using IFC4 Reference View for elements visible in the active view?"))
                {
                    return Result.Cancelled;
                }

                string? fallbackPath = CommandUi.PickSavePath(
                    Title,
                    "IFC (*.ifc)|*.ifc|IFC XML (*.ifcXML)|*.ifcXML|IFC ZIP (*.ifcZIP)|*.ifcZIP",
                    CommandUi.DefaultBaseName(view, document));
                if (fallbackPath is null)
                    return Result.Cancelled;

                IfcExportOutcome fallback;
                using (var progress = new ExportProgress(Title, 1))
                {
                    progress.UseMarquee("Exporting IFC" + Environment.NewLine + "IFC4 Reference View");
                    fallback = IfcExporter.ExportVisibleView(document, view, fallbackPath);
                }

                if (fallback.Success)
                {
                    CommandUi.Show(Title, $"IFC saved\n{fallback.Path}\n\nRevit IFC setup: {fallback.SetupName}");
                    return Result.Succeeded;
                }

                CommandUi.Show(Title, $"IFC export failed.\n{fallback.Error}");
                return Result.Failed;
            }

            string? setupName = IfcSetupDialog.Pick(Title, exporter.SetupNames, IfcSetupPreference.Read());
            if (setupName is null)
                return Result.Cancelled;

            IfcSetupPreference.Write(setupName);

            string? outputPath = CommandUi.PickSavePath(
                Title,
                "IFC (*.ifc)|*.ifc|IFC XML (*.ifcXML)|*.ifcXML|IFC ZIP (*.ifcZIP)|*.ifcZIP",
                CommandUi.DefaultBaseName(view, document));
            if (outputPath is null)
                return Result.Cancelled;

            IfcExportOutcome outcome;
            using (var progress = new ExportProgress(Title, 1))
            {
                progress.UseMarquee("Exporting IFC" + Environment.NewLine + setupName);
                outcome = exporter.Export(document, view, outputPath, setupName);
            }

            if (outcome.Success)
            {
                CommandUi.Show(Title, $"IFC saved\n{outcome.Path}\n\nRevit IFC setup: {outcome.SetupName}");
                return Result.Succeeded;
            }

            CommandUi.Show(Title, $"IFC export failed.\nSetup: {outcome.SetupName}\n{outcome.Error}");
            return Result.Failed;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            CommandUi.Show(Title, $"IFC export failed.\n{ex.Message}");
            return Result.Failed;
        }
    }
}
