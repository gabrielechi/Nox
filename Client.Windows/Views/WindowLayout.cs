using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Client.Windows.Views;

internal static class WindowLayout
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void KeepOnMonitor(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)
            || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return;
        int x = Math.Clamp(rect.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - (rect.Right - rect.Left)));
        int y = Math.Clamp(rect.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - (rect.Bottom - rect.Top)));
        if (x != rect.Left || y != rect.Top)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x15); // No resize, z-order change or activation.
    }

    public static Size WorkSize(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
            return SystemParameters.WorkArea.Size;
        var dpi = VisualTreeHelper.GetDpi(window);
        return new Size((info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                        (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
    }

    public static void FitDialog(Window dialog, FrameworkElement content, bool resizable = false)
    {
        if (resizable)
        {
            dialog.Content = content;
            dialog.ResizeMode = ResizeMode.CanResizeWithGrip;
            void FitBounds()
            {
                Size area = WorkSize(dialog);
                dialog.MaxWidth = Math.Min(780, Math.Max(1, area.Width - 24));
                dialog.MaxHeight = Math.Min(720, Math.Max(1, area.Height - 24));
                dialog.MinWidth = Math.Min(360, dialog.MaxWidth);
                dialog.MinHeight = Math.Min(260, dialog.MaxHeight);
            }
            dialog.SourceInitialized += (_, _) => FitBounds();
            dialog.LocationChanged += (_, _) => FitBounds();
            dialog.DpiChanged += (_, _) => dialog.Dispatcher.InvokeAsync(FitBounds);
            dialog.Loaded += (_, _) => KeepOnMonitor(dialog);
            return;
        }
        double idealWidth = dialog.Width;
        double idealHeight = dialog.Height;
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        dialog.Content = scroll;
        void Fit()
        {
            Size area = WorkSize(dialog);
            double width = Math.Min(idealWidth, Math.Max(1, area.Width - 24));
            dialog.Width = width;
            dialog.MaxHeight = Math.Max(1, area.Height - 24);
            if (!double.IsNaN(idealHeight))
                dialog.Height = Math.Min(idealHeight, dialog.MaxHeight);
            double scale = width / idealWidth;
            content.LayoutTransform = new ScaleTransform(scale, scale);
        }
        dialog.SourceInitialized += (_, _) => Fit();
        dialog.Loaded += (_, _) => KeepOnMonitor(dialog);
        dialog.DpiChanged += (_, _) => dialog.Dispatcher.InvokeAsync(Fit);
    }
}
