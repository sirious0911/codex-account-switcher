using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed class NativeSettings
{
    private const string Key = @"Software\Sirious0911\CodexAccountSwitcher";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string Version => typeof(NativeSettings).Assembly.GetName().Version!.ToString(3);
    public bool LaunchAtLogin {
        get { using var key = Registry.CurrentUser.OpenSubKey(Run); return key?.GetValue("CodexAccountSwitcherPersonal") is string; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (value) key.SetValue("CodexAccountSwitcherPersonal", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("CodexAccountSwitcherPersonal", false); }
    }
    public bool AutomaticallyCheckUpdates {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("AutomaticallyCheckUpdates") is not int number || number != 0; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue("AutomaticallyCheckUpdates", value ? 1 : 0); }
    }
    // These four Windows preferences are per-device UI intent only.
    // The installed application intentionally has no Telegram send transport.
    public TelegramNotificationPreferences TelegramPreferences
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            var role = key?.GetValue("TelegramDeviceRole") is string value &&
                       string.Equals(value, "sender", StringComparison.Ordinal)
                ? TelegramNotificationDeviceRole.Sender
                : TelegramNotificationDeviceRole.Viewer;
            bool GetFlag(string name, bool fallback) =>
                key?.GetValue(name) is int v ? v == 1 : fallback;
            return new TelegramNotificationPreferences(
                role,
                GetFlag("TelegramNotificationsRequested", false),
                GetFlag("TelegramFiveHourRequested", true),
                GetFlag("TelegramWeeklyRequested", true));
        }
    }

    public void SetTelegramPreferences(TelegramNotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var selected = preferences.Normalize();
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("TelegramDeviceRole",
            selected.DeviceRole == TelegramNotificationDeviceRole.Sender
                ? "sender" : "viewer", RegistryValueKind.String);
        key.SetValue("TelegramNotificationsRequested",
            selected.NotificationsRequested ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("TelegramFiveHourRequested",
            selected.FiveHourRequested ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("TelegramWeeklyRequested",
            selected.WeeklyRequested ? 1 : 0, RegistryValueKind.DWord);
        Changed?.Invoke();
    }

    public Uri? UpdatePage { get; private set; }
    public bool IsChecking { get; private set; }
    public string? UpdateError { get; private set; }
    public event Action? Changed;
    public async Task CheckUpdatesAsync()
    {
        if (IsChecking) return;
        IsChecking = true; UpdateError = null; Changed?.Invoke();
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CodexAccountSwitcher/" + Version);
            Version? latest = null;
            for (var page = 1; ; page++) {
                using var document = JsonDocument.Parse(await http.GetStringAsync(
                    "https://api.github.com/repos/sirious0911/codex-account-switcher/releases?per_page=100&page=" + page));
                var candidate = LatestWindowsVersion(document.RootElement);
                if (candidate != null && (latest == null || candidate > latest)) latest = candidate;
                if (document.RootElement.GetArrayLength() < 100) break;
            }
            UpdatePage = latest != null && latest > new Version(Version)
                ? new Uri("https://github.com/sirious0911/codex-account-switcher/releases/tag/v" + latest.ToString(3)) : null;
        } catch (Exception) { UpdateError = "update_check_failed"; }
        finally { IsChecking = false; Changed?.Invoke(); }
    }
    // Stable unified releases must include a Windows executable.
    public static Version? LatestWindowsVersion(JsonElement releases) => releases.EnumerateArray()
        .Where(release => !release.GetProperty("draft").GetBoolean() &&
            (!release.TryGetProperty("prerelease", out var preview) || !preview.GetBoolean()))
        .Where(release => release.GetProperty("assets").EnumerateArray().Any(asset =>
            asset.GetProperty("name").GetString() == "Codex-Account-Switcher-windows-x64.exe"))
        .Select(release => release.GetProperty("tag_name").GetString())
        .Where(tag => tag != null && Regex.IsMatch(tag, @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
        .Select(tag => System.Version.TryParse(tag![1..], out var version) ? version : null)
        .Where(version => version != null).OrderDescending().FirstOrDefault();
    public void OpenUpdate() { if (UpdatePage != null) Process.Start(new ProcessStartInfo(UpdatePage.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
}
