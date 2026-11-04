namespace CodexAccountSwitcher.Core;

// Pure selection of a monitor by its stable device name, not its enumeration
// index. Windows can reorder Screen.AllScreens after docking/undocking.
public sealed record MiniBarScreenInfo(string DeviceName, bool IsPrimary);

public static class MiniBarScreenSelector
{
    public static string? Select(
        string? preferredDeviceName, IReadOnlyList<MiniBarScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            foreach (var screen in screens)
                if (string.Equals(screen.DeviceName, preferredDeviceName,
                    StringComparison.OrdinalIgnoreCase))
                    return screen.DeviceName;
        }
        foreach (var screen in screens)
            if (screen.IsPrimary)
                return screen.DeviceName;
        return screens[0].DeviceName;
    }
}
