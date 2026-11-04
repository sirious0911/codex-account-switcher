using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher;

/// <summary>
/// Lightweight *heuristic*, not a claim that a Codex model is still generating.
/// Inspect only the modification timestamps of recent local rollout files and
/// whether an actual Codex desktop window is foreground. Never read session
/// contents, credentials, or chat messages.
/// </summary>
internal sealed class CodexWorkActivityDetector
{
    internal static readonly TimeSpan RecentActivityWindow = TimeSpan.FromMinutes(3);

    private readonly string codexHome;

    internal CodexWorkActivityDetector(string? codexHome = null)
    {
        this.codexHome = codexHome ??
            Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    internal bool HasRecentWork(DateTimeOffset now)
    {
        if (IsCodexDesktopForeground()) return true;
        return HasRecentSessionFileMetadata(codexHome, now);
    }

    internal static bool HasRecentSessionFileMetadata(string codexHome, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return false;
        var root = Path.Combine(codexHome, "sessions");
        // Session date directories may follow local or UTC dates. Scan only
        // recent day buckets, not the full profile's history.
        var candidateDates = new HashSet<DateTime>();
        foreach (var today in new[] { now.Date, now.UtcDateTime.Date, now.ToLocalTime().Date })
            for (var offset = 0; offset <= 2; offset++)
                candidateDates.Add(today.AddDays(-offset));

        foreach (var date in candidateDates)
        {
            var directory = Path.Combine(root,
                date.Year.ToString("0000"),
                date.Month.ToString("00"),
                date.Day.ToString("00"));
            try
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var file in Directory.EnumerateFiles(directory,
                             "rollout-*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(file);
                    var age = now.UtcDateTime - lastWrite;
                    if (age >= TimeSpan.FromSeconds(-30) &&
                        age <= RecentActivityWindow) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Metadata is optional. A locked or missing folder is not
                // evidence of active work and must not block the account UI.
            }
        }
        return false;
    }

    internal static bool IsOfficialCodexDesktopProcess(string processName, string? executablePath)
    {
        // The official Windows Store Codex package currently declares
        // ChatGPT.exe as its main executable. Matching only 'Codex.exe'
        // would silently miss an actual foreground Codex Desktop window.
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !(processName.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
              processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)))
            return false;

        return executablePath.Contains(
            $"{Path.DirectorySeparatorChar}OpenAI.Codex_",
            StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static bool IsCodexDesktopForeground()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foreground, out uint processId);
            if (processId == 0) return false;
            using var process = Process.GetProcessById(checked((int)processId));
            // Match the installed OpenAI.Codex package, not some unrelated
            // ChatGPT process. Idle background processes never qualify.
            return process.MainWindowHandle != IntPtr.Zero &&
                IsOfficialCodexDesktopProcess(
                    process.ProcessName, process.MainModule?.FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception or OverflowException)
        {
            return false;
        }
    }
}
