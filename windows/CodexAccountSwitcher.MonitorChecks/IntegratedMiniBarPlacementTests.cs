using CodexAccountSwitcher.Core;

// Pure synthetic geometry and monitor-selection checks.
// Never changes monitor DPI, taskbar, display configuration or registry.
internal static class IntegratedMiniBarPlacementTests
{
    internal static void Run(Action<string, bool> check)
    {
        static bool Inside(MiniBarPlacement p, PixelArea area, double dpi)
        {
            var x = Math.Round(p.LeftDip * dpi);
            var y = Math.Round(p.TopDip * dpi);
            var width = Math.Round(p.WidthDip * dpi);
            var height = Math.Round(p.HeightDip * dpi);
            return x >= area.Left - 1 && y >= area.Top - 1 &&
                   x + width <= area.Right + 1 && y + height <= area.Bottom + 1;
        }
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var bounds = new PixelArea(0, 0, 2560, 1440);
            var work = new PixelArea(0, 0, 2560, 1350);
            var placement = MiniBarLayout.Calculate(bounds, work, 360, 52, scale);
            check($"integrated minibar {scale:P0} fits physical pixels",
                placement.FitsInsideWorkArea && Inside(placement, work, scale));
            check($"integrated minibar {scale:P0} bottom taskbar detected",
                placement.Edge == TaskbarEdge.Bottom && placement.HasReservedTaskbarBand);
        }
        var negatives = new PixelArea(-2560, -120, 2560, 1440);
        var topWork = new PixelArea(-2560, -42, 2560, 1362);
        var negativeTop = MiniBarLayout.Calculate(negatives, topWork, 360, 52, 1.5);
        check("secondary monitor negative coordinates stay inside work area",
            negativeTop.FitsInsideWorkArea && Inside(negativeTop, topWork, 1.5));
        check("secondary top taskbar edge remains top", negativeTop.Edge == TaskbarEdge.Top);
        var rightBounds = new PixelArea(2560, 0, 1920, 1080);
        var rightWork = new PixelArea(2560, 0, 1860, 1080);
        var right = MiniBarLayout.Calculate(rightBounds, rightWork, 360, 52, 1.25);
        check("right-edge taskbar reserved area is excluded",
            right.FitsInsideWorkArea && right.Edge == TaskbarEdge.Right &&
            Inside(right, rightWork, 1.25));
        var leftBounds = new PixelArea(0, 0, 1920, 1080);
        var leftWork = new PixelArea(50, 0, 1870, 1080);
        var left = MiniBarLayout.Calculate(leftBounds, leftWork, 360, 52, 1);
        check("left-edge taskbar reserved area is excluded",
            left.FitsInsideWorkArea && left.Edge == TaskbarEdge.Left &&
            Inside(left, leftWork, 1));
        var autoBounds = new PixelArea(0, 0, 3840, 2160);
        var autohide = MiniBarLayout.Calculate(autoBounds, autoBounds, 360, 52, 2);
        check("auto-hidden taskbar uses larger inset",
            autohide.LikelyAutoHiddenTaskbar && !autohide.HasReservedTaskbarBand &&
            Inside(autohide, autoBounds, 2));
        var small = MiniBarLayout.Calculate(
            new PixelArea(0, 0, 320, 200),
            new PixelArea(0, 0, 320, 180), 360, 52, 1);
        check("undersized screen cannot claim valid docking", !small.FitsInsideWorkArea);

        var first = new[] {
            new MiniBarScreenInfo(@"\\.\DISPLAY2", false),
            new MiniBarScreenInfo(@"\\.\DISPLAY1", true)
        };
        check("primary display used by default",
            MiniBarScreenSelector.Select(null, first) == first[1].DeviceName);
        check("explicit secondary persists through enumeration order change",
            MiniBarScreenSelector.Select(first[0].DeviceName,
                new[] { first[1], first[0] }) == first[0].DeviceName);
        check("unplugged secondary falls back to primary",
            MiniBarScreenSelector.Select("DISCONNECTED", first) == first[1].DeviceName);
        check("empty monitor list yields no invented screen",
            MiniBarScreenSelector.Select(first[0].DeviceName,
                Array.Empty<MiniBarScreenInfo>()) is null);
        check("single monitor works with no primary flag",
            MiniBarScreenSelector.Select(null,
                new[] { new MiniBarScreenInfo("ONLY", false) }) == "ONLY");
    }
}
