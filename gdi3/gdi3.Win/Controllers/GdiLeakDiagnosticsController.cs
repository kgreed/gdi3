using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Win.Editors;
using DevExpress.XtraGrid.Views.Grid;
using gdi3.Module.BusinessObjects;

namespace gdi3.Win.Controllers;

/// <summary>
/// Attaches to the Customer ListView and logs GDI handle counts during
/// grid painting — proving that each scroll/repaint of a view with an
/// <c>[Appearance]</c> <c>FontStyle</c> rule leaks GDI handles.
///
/// Output goes to the Visual Studio Output window (Debug pane) and also
/// to a log file in the application directory for non-debugger runs.
/// </summary>
public class GdiLeakDiagnosticsController : ViewController<DevExpress.ExpressApp.ListView>
{
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    private const uint GR_GDIOBJECTS = 0;

    private uint _activatedCount;
    private uint _lastPaintCount;
    private int _paintCycleCount;
    private GridView _gridView;
    private readonly string _logFilePath;

    public GdiLeakDiagnosticsController()
    {
        TargetObjectType = typeof(Customer);
        _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gdi-leak-log.txt");
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        _paintCycleCount = 0;
        _activatedCount = GetCurrentGdiHandleCount();
        _lastPaintCount = _activatedCount;

        Log($"Customer ListView ACTIVATED — GDI handles: {_activatedCount}");

        View.ControlsCreated += View_ControlsCreated;
    }

    protected override void OnDeactivated()
    {
        DetachGridView();
        View.ControlsCreated -= View_ControlsCreated;

        uint deactivatedCount = GetCurrentGdiHandleCount();
        uint leaked = deactivatedCount - _activatedCount;

        Log($"Customer ListView DEACTIVATED — GDI handles: {deactivatedCount} " +
            $"(was {_activatedCount} on activate, growth: +{leaked}, paint cycles: {_paintCycleCount})");

        if (leaked > 50)
        {
            Log($"  ⚠ WARNING: {leaked} GDI handles leaked during this view session!");
            Log($"  ⚠ This is caused by [Appearance(\"PromisedIsBold\", FontStyle=Bold)] on Customer.IsCurrent.");
            Log($"  ⚠ Each grid repaint creates new font GDI handles that are never freed.");
        }

        base.OnDeactivated();
    }

    private void View_ControlsCreated(object sender, EventArgs e)
    {
        uint afterControls = GetCurrentGdiHandleCount();
        Log($"Customer ListView CONTROLS CREATED — GDI handles: {afterControls} (+{afterControls - _activatedCount} since activate)");

        AttachGridView();
    }

    private void AttachGridView()
    {
        DetachGridView();

        if (View.Editor is GridListEditor gridEditor)
        {
            _gridView = gridEditor.GridView;
            if (_gridView != null)
            {
                _gridView.CustomDrawCell += GridView_CustomDrawCell;
                _gridView.TopRowChanged += GridView_TopRowChanged;
                Log($"Attached to GridView — monitoring paint and scroll events.");
            }
        }
    }

    private void DetachGridView()
    {
        if (_gridView != null)
        {
            _gridView.CustomDrawCell -= GridView_CustomDrawCell;
            _gridView.TopRowChanged -= GridView_TopRowChanged;
            _gridView = null;
        }
    }

    // Track whether we already logged for this paint cycle to avoid flooding.
    private bool _loggedThisPaintCycle;

    private void GridView_CustomDrawCell(object sender, DevExpress.XtraGrid.Views.Base.RowCellCustomDrawEventArgs e)
    {
        // Log once per paint cycle (first cell drawn), not per cell.
        if (_loggedThisPaintCycle) return;
        _loggedThisPaintCycle = true;

        _paintCycleCount++;
        uint current = GetCurrentGdiHandleCount();
        uint growthSinceLast = current - _lastPaintCount;
        uint growthTotal = current - _activatedCount;

        // Only log when handles actually changed to keep output readable.
        if (growthSinceLast > 0)
        {
            Log($"PAINT #{_paintCycleCount} — GDI: {current} (+{growthSinceLast} this cycle, +{growthTotal} total)");
        }

        _lastPaintCount = current;

        // Reset the flag after the paint cycle completes.
        _gridView?.GridControl?.BeginInvoke(new Action(() => _loggedThisPaintCycle = false));
    }

    private void GridView_TopRowChanged(object sender, EventArgs e)
    {
        uint current = GetCurrentGdiHandleCount();
        uint growthTotal = current - _activatedCount;
        Log($"SCROLL — top row: {_gridView?.TopRowIndex}, GDI: {current} (+{growthTotal} total)");
    }

    private static uint GetCurrentGdiHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, GR_GDIOBJECTS);
    }

    private void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string line = $"[GDI-LEAK {timestamp}] {message}";

        // Output to VS Debug pane (visible when running with F5).
        Trace.WriteLine(line);

        // Also append to a log file so you can see results without the debugger.
        try
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }
        catch
        {
            // Ignore file write errors to avoid interfering with the app.
        }
    }
}
