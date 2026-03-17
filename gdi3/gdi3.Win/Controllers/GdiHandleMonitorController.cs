using System.Diagnostics;
using System.Runtime.InteropServices;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Win;

namespace gdi3.Win.Controllers;

/// <summary>
/// Adds a live GDI handle counter to the main window's status bar.
/// Updates every second so you can watch handles climb as the
/// Conditional Appearance module applies FontStyle rules on each
/// ListView repaint / scroll.
///
/// To reproduce the leak:
///   1. Run the app and open the Customer ListView.
///   2. Note the GDI handle count in the status bar.
///   3. Scroll the grid up and down repeatedly, or resize the window.
///   4. Watch the GDI handle count increase monotonically — it never decreases.
///   5. With enough scrolling, the count approaches ~10 000 and the app crashes.
/// </summary>
public class GdiHandleMonitorController : WindowController
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private const uint GR_GDIOBJECTS = 0;

    private System.Windows.Forms.Timer _timer;
    private System.Windows.Forms.ToolStripStatusLabel _statusLabel;
    private uint _initialCount;
    private uint _peakCount;

    public GdiHandleMonitorController()
    {
        TargetWindowType = WindowType.Main;
    }

    protected override void OnActivated()
    {
        base.OnActivated();

        _initialCount = GetCurrentGdiHandleCount();
        _peakCount = _initialCount;

        // The template (Form) may not be assigned yet when OnActivated fires.
        // Hook TemplateChanged to catch it when it becomes available.
        if (Window.Template is System.Windows.Forms.Form form)
        {
            AttachToForm(form);
        }
        else
        {
            Window.TemplateChanged += Window_TemplateChanged;
        }
    }

    protected override void OnDeactivated()
    {
        Window.TemplateChanged -= Window_TemplateChanged;
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _timer.Dispose();
            _timer = null;
        }
        _statusLabel = null;
        base.OnDeactivated();
    }

    private void Window_TemplateChanged(object sender, EventArgs e)
    {
        if (Window.Template is System.Windows.Forms.Form form)
        {
            Window.TemplateChanged -= Window_TemplateChanged;
            AttachToForm(form);
        }
    }

    private void AttachToForm(System.Windows.Forms.Form form)
    {
        var statusBar = FindOrCreateStatusStrip(form);
        _statusLabel = new System.Windows.Forms.ToolStripStatusLabel
        {
            Name = "gdiMonitor",
            Spring = false,
            BorderSides = System.Windows.Forms.ToolStripStatusLabelBorderSides.Left,
            Text = FormatStatus(_initialCount)
        };
        statusBar.Items.Add(_statusLabel);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        uint current = GetCurrentGdiHandleCount();
        if (current > _peakCount) _peakCount = current;

        if (_statusLabel != null)
        {
            _statusLabel.Text = FormatStatus(current);

            // Visual warning as we approach the ~10 000 GDI limit.
            _statusLabel.ForeColor = current switch
            {
                > 8000 => System.Drawing.Color.Red,
                > 5000 => System.Drawing.Color.OrangeRed,
                > 2000 => System.Drawing.Color.DarkOrange,
                _ => System.Drawing.SystemColors.ControlText
            };
        }
    }

    private string FormatStatus(uint current)
    {
        uint growth = current - _initialCount;
        return $"GDI: {current} (start: {_initialCount}, +{growth}, peak: {_peakCount})";
    }

    private static uint GetCurrentGdiHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, GR_GDIOBJECTS);
    }

    private static System.Windows.Forms.StatusStrip FindOrCreateStatusStrip(System.Windows.Forms.Form form)
    {
        foreach (System.Windows.Forms.Control control in form.Controls)
        {
            if (control is System.Windows.Forms.StatusStrip existing)
                return existing;
        }
        var strip = new System.Windows.Forms.StatusStrip();
        form.Controls.Add(strip);
        return strip;
    }
}
