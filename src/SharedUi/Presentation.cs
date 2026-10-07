using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Stock.Presentation;

/// <summary>Shared presentation helpers; no inventory or recognition dependencies.</summary>
public static class Presentation
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Presentation), new PropertyMetadata(null));
    public static Geometry? GetIcon(DependencyObject value) => (Geometry?)value.GetValue(IconProperty);
    public static void SetIcon(DependencyObject value, Geometry? icon) => value.SetValue(IconProperty, icon);

    public static readonly DependencyProperty FitWorkAreaProperty = DependencyProperty.RegisterAttached(
        "FitWorkArea", typeof(bool), typeof(Presentation), new PropertyMetadata(false, FitChanged));
    public static bool GetFitWorkArea(DependencyObject value) => (bool)value.GetValue(FitWorkAreaProperty);
    public static void SetFitWorkArea(DependencyObject value, bool fit) => value.SetValue(FitWorkAreaProperty, fit);

    private static void FitChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= Fit;
        if ((bool)args.NewValue) window.SourceInitialized += Fit;
    }

    private static void Fit(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        var area = SystemParameters.WorkArea;
        var ownerHandle = window.Owner is null ? IntPtr.Zero : new WindowInteropHelper(window.Owner).Handle;
        var handle = ownerHandle == IntPtr.Zero ? new WindowInteropHelper(window).Handle : ownerHandle;
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
            var bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
            area = new Rect(topLeft, bottomRight);
        }
        var width = Math.Max(320, area.Width - 24);
        var height = Math.Max(300, area.Height - 24);
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        if (!double.IsNaN(window.Width)) window.Width = Math.Min(window.Width, width);
        if (!double.IsNaN(window.Height)) window.Height = Math.Min(window.Height, height);
        // Test windows intentionally use manual positions outside the display.
        if (window.WindowStartupLocation != WindowStartupLocation.Manual)
        {
            window.Left = area.Left + (area.Width - window.Width) / 2;
            window.Top = area.Top + (area.Height - window.Height) / 2;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
