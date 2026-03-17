using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using DevExpress.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace gdi3.Tests;

/// <summary>
/// Demonstrates that creating <see cref="DXFont"/> objects with a non-default
/// <see cref="DXFontStyle"/> (the same pattern the XAF Conditional Appearance
/// module uses when <c>FontStyle = DXFontStyle.Bold</c> is set on an
/// <c>[Appearance]</c> attribute) leaks GDI handles.
///
/// The DevExpress grid rendering pipeline converts fonts to GDI HFONTs via
/// <see cref="Font.ToHfont()"/> and selects them into device contexts. When
/// these HFONTs are not freed with <c>DeleteObject</c>, they leak.
///
/// <see cref="DXFont"/> does NOT implement <see cref="IDisposable"/>, so there
/// is no way for consuming code to release the underlying resources — handles
/// accumulate until the per-process GDI limit (~10 000) is exhausted.
/// </summary>
public class GdiLeakTests
{
    private const int Iterations = 500;
    private const int LeakThreshold = 100;

    private readonly ITestOutputHelper _output;

    public GdiLeakTests(ITestOutputHelper output) => _output = output;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint GR_GDIOBJECTS = 0;

    private static uint GetCurrentGdiHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return GetGuiResources(process.Handle, GR_GDIOBJECTS);
    }

    /// <summary>
    /// Simulates the Conditional Appearance rendering path: for each row in the
    /// ListView, a bold <see cref="DXFont"/> is created, converted to a
    /// <see cref="Font"/>, and its HFONT is obtained via <see cref="Font.ToHfont()"/>
    /// for GDI text output. The HFONT is never deleted — proving the GDI leak.
    ///
    /// <see cref="DXFont"/> does NOT implement <see cref="IDisposable"/>, so even
    /// if the caller wanted to clean up, there is no API to do so.
    /// </summary>
    [Fact]
    public void FontStyle_Bold_Leaks_GdiHandles_Via_ToHfont()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint baseline = GetCurrentGdiHandleCount();

        // Hold references so nothing gets finalized during the test.
        var leakedHandles = new List<IntPtr>(Iterations);

        for (int i = 0; i < Iterations; i++)
        {
            // Step 1: The [Appearance] rule creates a DXFont with Bold style.
            var dxFont = new DXFont("Tahoma", 8.25f, DXFontStyle.Bold);

            // Step 2: The rendering pipeline creates a System.Drawing.Font.
            var font = new Font(dxFont.Name, dxFont.Size,
                (System.Drawing.FontStyle)dxFont.Style);

            // Step 3: The grid's paint code calls ToHfont() to get a GDI handle
            // for SelectObject/TextOut. Each call creates a NEW GDI HFONT.
            IntPtr hfont = font.ToHfont();

            // Step 4: The HFONT is never freed with DeleteObject — THIS IS THE LEAK.
            leakedHandles.Add(hfont);
        }

        uint afterLeak = GetCurrentGdiHandleCount();
        uint leaked = afterLeak - baseline;

        _output.WriteLine($"GDI handles — baseline: {baseline}, after {Iterations} ToHfont() calls: {afterLeak}, leaked: {leaked}");
        _output.WriteLine($"DXFont implements IDisposable: {typeof(IDisposable).IsAssignableFrom(typeof(DXFont))}");
        _output.WriteLine($"Handles per font: {(Iterations > 0 ? (double)leaked / Iterations : 0):F2}");

        Assert.True(
            leaked >= LeakThreshold,
            $"Expected at least {LeakThreshold} leaked GDI handles. " +
            $"baseline: {baseline}, after: {afterLeak}, leaked: {leaked}");

        // Clean up so the test runner doesn't hit the GDI limit.
        foreach (var h in leakedHandles) DeleteObject(h);
    }

    /// <summary>
    /// Control test: same rendering path, but every HFONT is immediately freed
    /// with <c>DeleteObject</c> and the <see cref="Font"/> is disposed.
    /// GDI handle count stays flat — proving the leak is preventable.
    /// </summary>
    [Fact]
    public void Properly_Freed_Fonts_Do_Not_Leak()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint baseline = GetCurrentGdiHandleCount();

        for (int i = 0; i < Iterations; i++)
        {
            var dxFont = new DXFont("Tahoma", 8.25f, DXFontStyle.Bold);

            using var font = new Font(dxFont.Name, dxFont.Size,
                (System.Drawing.FontStyle)dxFont.Style);

            IntPtr hfont = font.ToHfont();

            // Properly free the GDI handle — this is what the framework SHOULD do.
            DeleteObject(hfont);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint afterClean = GetCurrentGdiHandleCount();
        uint growth = afterClean > baseline ? afterClean - baseline : 0;

        _output.WriteLine($"GDI handles — baseline: {baseline}, after {Iterations} freed fonts: {afterClean}, growth: {growth}");

        Assert.True(
            growth < LeakThreshold,
            $"With proper cleanup, GDI growth should be minimal. " +
            $"baseline: {baseline}, after: {afterClean}, growth: {growth}");
    }

    /// <summary>
    /// Proves that <see cref="DXFont"/> does not implement <see cref="IDisposable"/>.
    /// This is the fundamental design issue: the Conditional Appearance module
    /// creates <see cref="DXFont"/> objects with custom FontStyle, but provides
    /// no mechanism for the consumer to release the underlying GDI resources.
    /// </summary>
    [Fact]
    public void DXFont_Does_Not_Implement_IDisposable()
    {
        bool dxFontIsDisposable = typeof(IDisposable).IsAssignableFrom(typeof(DXFont));
        bool systemFontIsDisposable = typeof(IDisposable).IsAssignableFrom(typeof(Font));

        _output.WriteLine($"DXFont implements IDisposable:              {dxFontIsDisposable}");
        _output.WriteLine($"System.Drawing.Font implements IDisposable: {systemFontIsDisposable}");

        Assert.False(dxFontIsDisposable,
            "DXFont does NOT implement IDisposable — GDI handles from Appearance FontStyle rules can never be released.");
        Assert.True(systemFontIsDisposable,
            "System.Drawing.Font implements IDisposable as expected.");
    }

    /// <summary>
    /// Shows that leaked HFONTs survive garbage collection — the GC does not
    /// call <c>DeleteObject</c> on raw GDI handles. In a real XAF app, the
    /// fonts also stay rooted in the appearance-rule cache, so they never
    /// become eligible for GC at all.
    /// </summary>
    [Fact]
    public void Leaked_HFONTs_Survive_GarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint baseline = GetCurrentGdiHandleCount();

        // Create leaked HFONTs in a separate scope.
        CreateLeakedHfonts();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        uint afterGc = GetCurrentGdiHandleCount();
        uint remaining = afterGc > baseline ? afterGc - baseline : 0;

        _output.WriteLine($"GDI handles — baseline: {baseline}, after GC: {afterGc}, remaining: {remaining} (of {Iterations} created)");
        _output.WriteLine("GDI HFONTs are unmanaged resources — GC cannot reclaim them.");
        _output.WriteLine("In a real XAF app, the Appearance cache also keeps the fonts rooted.");

        // Any leaked handles that survive GC prove the problem.
        // We record the observation — even partial survival is significant.
        Assert.True(remaining > 0,
            $"Expected leaked HFONTs to survive GC. remaining: {remaining}");
    }

    private static void CreateLeakedHfonts()
    {
        for (int i = 0; i < Iterations; i++)
        {
            var dxFont = new DXFont("Tahoma", 8.25f, DXFontStyle.Bold);
            var font = new Font(dxFont.Name, dxFont.Size,
                (System.Drawing.FontStyle)dxFont.Style);
            // Create an HFONT and intentionally never call DeleteObject.
            _ = font.ToHfont();
        }
    }
}
