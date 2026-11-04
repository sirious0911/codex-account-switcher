namespace CodexAccountSwitcher.Core;

// Pure geometry: pixel monitor bounds + DIP-based WPF size. No Win32 calls or screen changes.
public readonly record struct PixelArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public enum TaskbarEdge { Bottom, Top, Left, Right }

public sealed record MiniBarPlacement(
    double LeftDip,
    double TopDip,
    double WidthDip,
    double HeightDip,
    bool FitsInsideWorkArea,
    bool HasReservedTaskbarBand,
    bool LikelyAutoHiddenTaskbar,
    TaskbarEdge Edge = TaskbarEdge.Bottom);

public static class MiniBarLayout
{
    public static MiniBarPlacement Calculate(
        PixelArea monitorBounds, PixelArea workArea,
        double windowWidthDip, double windowHeightDip, double dpiScale,
        double edgeInsetDip = 12, double autoHideInsetDip = 32)
    {
        if (dpiScale <= 0 || !double.IsFinite(dpiScale) ||
            windowWidthDip <= 0 || windowHeightDip <= 0 ||
            windowWidthDip > 5000 || windowHeightDip > 5000 ||
            !double.IsFinite(windowWidthDip) || !double.IsFinite(windowHeightDip) ||
            workArea.Width <= 0 || workArea.Height <= 0 ||
            monitorBounds.Width <= 0 || monitorBounds.Height <= 0 ||
            edgeInsetDip < 0 || autoHideInsetDip < 0)
            throw new ArgumentOutOfRangeException(nameof(windowWidthDip), "Invalid synthetic placement geometry.");

        // Windows Screen bounds are physical pixels, WPF Window.Left/Top are DIPs.
        var leftBand = Math.Max(0, workArea.Left - monitorBounds.Left);
        var topBand = Math.Max(0, workArea.Top - monitorBounds.Top);
        var rightBand = Math.Max(0, monitorBounds.Right - workArea.Right);
        var bottomBand = Math.Max(0, monitorBounds.Bottom - workArea.Bottom);
        bool reserved = Math.Max(Math.Max(leftBand, rightBand),
                                 Math.Max(topBand, bottomBand)) > 2;
        bool mightAutoHide = !reserved;
        // Prefer bottom in a tie to preserve previous conventional behavior.
        TaskbarEdge edge = TaskbarEdge.Bottom;
        double band = bottomBand;
        if (topBand > band) { edge = TaskbarEdge.Top; band = topBand; }
        if (leftBand > band) { edge = TaskbarEdge.Left; band = leftBand; }
        if (rightBand > band) { edge = TaskbarEdge.Right; }
        double margin = edgeInsetDip * dpiScale;
        double bottomMargin = (mightAutoHide ? autoHideInsetDip : edgeInsetDip) * dpiScale;
        double desiredWidth = windowWidthDip * dpiScale;
        double desiredHeight = windowHeightDip * dpiScale;
        bool fits = workArea.Width >= desiredWidth + margin * 2 &&
                    workArea.Height >= desiredHeight + bottomMargin + margin;

        // Clamp even for unusual small working areas; FitsInsideWorkArea signals unsafe overlap.
        double x = edge == TaskbarEdge.Left && reserved
            ? workArea.Left + margin
            : Math.Max(workArea.Left, workArea.Right - desiredWidth - margin);
        double y = edge == TaskbarEdge.Top && reserved
            ? workArea.Top + margin
            : Math.Max(workArea.Top, workArea.Bottom - desiredHeight - bottomMargin);
        return new MiniBarPlacement(x / dpiScale, y / dpiScale,
            windowWidthDip, windowHeightDip, fits, reserved, mightAutoHide, edge);
    }
}
