using MetroCIM.Services;
using System.Drawing;
using System.Windows.Forms;

namespace MetroCIM.Commands;

internal static class IfcSetupDialog
{
    public static string? Pick(string title, IReadOnlyList<string> names, string? preferred)
    {
        if (names.Count == 0)
            return null;

        using var form = new Form();
        DialogStyle.Apply(form, title);

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(DialogStyle.Pad, DialogStyle.Pad, DialogStyle.Pad, 4)
        };
        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 18,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            Text = "Revit IFC setup"
        };
        var combo = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            IntegralHeight = false
        };
        foreach (string name in names)
            combo.Items.Add(name);

        combo.SelectedItem = IfcSetupName.Resolve(names, preferred) ?? names[0];
        content.Controls.Add(combo);
        content.Controls.Add(label);

        var export = DialogStyle.CommandButton("Export", DialogResult.OK);
        var cancel = DialogStyle.CommandButton("Cancel", DialogResult.Cancel);
        form.Controls.Add(content);
        DialogStyle.AddButtons(form, export, cancel);
        form.AcceptButton = export;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK && combo.SelectedItem is string selected
            ? selected
            : null;
    }
}
