using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

public sealed class NativeSettings
{
    private const string Key = @"Software\CodexAccountSwitcher";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string Version => typeof(NativeSettings).Assembly.GetName().Version!.ToString(3);
    public bool LaunchAtLogin {
        get { using var key = Registry.CurrentUser.OpenSubKey(Run); return key?.GetValue("CodexAccountSwitcher") is string; }
        set { using var key = Registry.CurrentUser.CreateSubKey(Run);
            if (value) key.SetValue("CodexAccountSwitcher", "\"" + Environment.ProcessPath + "\" --background");
            else key.DeleteValue("CodexAccountSwitcher", false); }
    }
    // Local safety fork: never check or install upstream releases automatically.
    public bool AutomaticallyCheckUpdates {
        get => false;
        set { /* Ignore persisted/UI requests; do not change registry. */ }
    }
    public Uri? UpdatePage { get; private set; }
    public bool IsChecking { get; private set; }
    public string? UpdateError { get; private set; }
    public event Action? Changed;
    public Task CheckUpdatesAsync()
    {
        // Hard stop: no network request, no updater transition, no upstream URL.
        UpdatePage = null;
        IsChecking = false;
        UpdateError = null;
        Changed?.Invoke();
        return Task.CompletedTask;
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
    public void OpenUpdate() { /* Release installation is not approved in this fork. */ }
}
