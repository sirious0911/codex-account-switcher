using System;
using System.Runtime.InteropServices;
using System.Windows;
using CodexAccountSwitcher.Core;
using FormsScreen = System.Windows.Forms.Screen;

namespace CodexAccountSwitcher;

internal sealed record NativeDockResult(
    bool Success, MiniBarPlacement Placement, uint TargetDpi,
    int PixelLeft, int PixelTop, bool ActualRectInsideWorkArea);

internal static class NativeDesktopDock
{
    private const uint MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; public Point(int x,int y) { X=x; Y=y; } }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    public static NativeDockResult TryDock(
        IntPtr hwnd, FormsScreen screen, double widthDip, double heightDip)
    {
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        var empty = new MiniBarPlacement(0,0,widthDip,heightDip,false,false,true);
        if (hwnd == IntPtr.Zero || widthDip <= 0 || heightDip <= 0 ||
            bounds.Width <= 0 || bounds.Height <= 0)
            return new NativeDockResult(false, empty, 0, 0, 0, false);

        var monitor = MonitorFromPoint(
            new Point(bounds.Left + bounds.Width/2, bounds.Top + bounds.Height/2), MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero || GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out uint dpiY) != 0 ||
            dpiX < 96 || dpiY < 96 || dpiX != dpiY)
            return new NativeDockResult(false, empty, 0, 0, 0, false);

        double scale = dpiX / 96.0;
        // Calculate in physical-pixel rectangles; DO NOT divide the virtual desktop
        // origin by one monitor's DPI and assign that as a global WPF Left/Top.
        var placement = MiniBarLayout.Calculate(
            new PixelArea(bounds.Left,bounds.Top,bounds.Width,bounds.Height),
            new PixelArea(work.Left,work.Top,work.Width,work.Height),
            widthDip,heightDip,scale);

        int x = checked((int)Math.Round(placement.LeftDip * scale));
        int y = checked((int)Math.Round(placement.TopDip * scale));
        int widthPx = checked((int)Math.Round(widthDip * scale));
        int heightPx = checked((int)Math.Round(heightDip * scale));
        if (!placement.FitsInsideWorkArea)
            return new NativeDockResult(false, placement, dpiX, x, y, false);

        if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, widthPx, heightPx,
                SwpNoZOrder | SwpNoActivate))
            return new NativeDockResult(false, placement, dpiX, x, y, false);

        bool actualInside = GetWindowRect(hwnd, out var actual) &&
            actual.Left >= work.Left - 2 && actual.Top >= work.Top - 2 &&
            actual.Right <= work.Right + 2 && actual.Bottom <= work.Bottom + 2;
        // Do not treat a successfully requested position as a verified actual position.
        return new NativeDockResult(actualInside, placement, dpiX, x, y, actualInside);
    }
}
