using System.Windows.Forms;

namespace MetroCIM.Commands;

internal sealed class ExportProgress : IDisposable
{
    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Form _form = null!;
    private ProgressBar _bar = null!;
    private Label _label = null!;
    private int _lastPumpTick;

    public ExportProgress(string title, int total)
    {
        _uiThread = new Thread(() => RunUi(title, total))
        {
            IsBackground = true,
            Name = "ExportProgress"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The export progress window did not open.");
    }

    public void UseMarquee(string status)
    {
        OnUi(() =>
        {
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            _label.Text = status;
        });
    }

    public void Pump()
    {
        OnUi(() => _form.Refresh());
    }

    public void Report(int current, string status, int total = 0)
    {
        int now = Environment.TickCount;
        bool pump = unchecked(now - _lastPumpTick) >= 250 || current == 0 || (total > 0 && current >= total);
        if (pump)
            _lastPumpTick = now;

        OnUi(() =>
        {
            if (total > 0 && _bar.Maximum != Math.Max(total, 1))
                _bar.Maximum = Math.Max(total, 1);

            if (_bar.Style != ProgressBarStyle.Continuous)
                _bar.Style = ProgressBarStyle.Continuous;

            _bar.Value = Math.Clamp(current, 0, _bar.Maximum);
            _label.Text = total > 0
                ? $"{status}  ({current}/{total})"
                : status;
            if (pump)
                _form.Refresh();
        });
    }

    public void Dispose()
    {
        try
        {
            if (_form is { IsHandleCreated: true, IsDisposed: false })
                _form.Invoke(() => _form.Close());
        }
        catch (InvalidOperationException)
        {
        }

        _uiThread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void RunUi(string title, int total)
    {
        _form = new Form
        {
            ControlBox = false,
            ShowInTaskbar = true,
            TopMost = true
        };
        DialogStyle.Apply(_form, title);
        _form.ControlBox = false;
        _form.ShowInTaskbar = true;
        _form.TopMost = true;

        _label = DialogStyle.BodyLabel("Starting export...");

        var barHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = DialogStyle.ButtonSize.Height + DialogStyle.Pad,
            Padding = new Padding(DialogStyle.Pad, 12, DialogStyle.Pad, DialogStyle.Pad)
        };
        _bar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = Math.Max(total, 1),
            Style = ProgressBarStyle.Continuous
        };
        barHost.Controls.Add(_bar);
        _form.Controls.Add(_label);
        _form.Controls.Add(barHost);
        _form.Shown += (_, _) => _ready.Set();
        Application.Run(_form);
    }

    private void OnUi(Action action)
    {
        if (_form.IsDisposed)
            return;

        if (_form.InvokeRequired)
            _form.Invoke(action);
        else
            action();
    }
}
