using System.Drawing;
using System.Windows.Forms;

namespace MetroCIM.Commands;

internal static class DialogStyle
{
    public static readonly Size ClientSize = new(380, 120);
    public static readonly Size ButtonSize = new(64, 21);
    public const int Pad = 12;
    public const int ButtonGap = 6;

    public static Font CreateFont() => new("Segoe UI", 7f, FontStyle.Regular, GraphicsUnit.Point);

    public static void Apply(Form form, string title)
    {
        form.Text = title;
        form.Font = CreateFont();
        form.ClientSize = ClientSize;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.ShowInTaskbar = false;
    }

    public static Label BodyLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(Pad, Pad, Pad, 4),
        UseMnemonic = false
    };

    public static Button CommandButton(string text, DialogResult result) => new()
    {
        Text = text,
        DialogResult = result,
        Size = ButtonSize,
        UseVisualStyleBackColor = true
    };

    public static void AddButtons(Form form, params Button[] buttons)
    {
        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = ButtonSize.Height + Pad + 6
        };

        int x = form.ClientSize.Width - Pad - ButtonSize.Width;
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            Button button = buttons[i];
            button.Size = ButtonSize;
            button.Location = new Point(x, 6);
            bar.Controls.Add(button);
            x -= ButtonSize.Width + ButtonGap;
        }

        form.Controls.Add(bar);
    }
}
