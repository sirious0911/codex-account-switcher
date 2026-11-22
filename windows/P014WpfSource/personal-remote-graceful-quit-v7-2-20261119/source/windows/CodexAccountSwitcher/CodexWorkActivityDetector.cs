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
    private readonly Func<bool> foregroundCheck;
    private static readonly TimeSpan ArchiveMetadataScanInterval =
        TimeSpan.FromSeconds(10);
    private DateTimeOffset lastArchiveScanAt = DateTimeOffset.MinValue;
    private DateTime? lastArchiveWriteUtc;

    internal CodexWorkActivityDetector(
        string? codexHome = null, Func<bool>? foregroundCheck = null)
    {
        this.codexHome = codexHome ??
            Environment.GetEnvironmentVariable("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        this.foregroundCheck = foregroundCheck ?? IsCodexDesktopForeground;
    }

    internal bool HasRecentWork(DateTimeOffset now)
    {
        if (foregroundCheck()) return true;
        if (HasRecentSessionFileMetadata(codexHome, now)) return true;
        // Codex can continue writing a session CREATED weeks/months ago.
        // The existing recent-day bucket scan then misses legitimate work.
        // Check *metadata only* of archived sessions, at most once per 10s.
        return HasRecentArchivedWork(now);
    }

    private bool HasRecentArchivedWork(DateTimeOffset now)
    {
        if (lastArchiveScanAt == DateTimeOffset.MinValue ||
            now < lastArchiveScanAt ||
            now - lastArchiveScanAt >= ArchiveMetadataScanInterval)
        {
            lastArchiveScanAt = now;
            lastArchiveWriteUtc = LatestArchivedRolloutWriteUtc(codexHome, now);
        }
        return lastArchiveWriteUtc is DateTime stamp &&
            IsRecentWrite(stamp, now);
    }

    internal static bool HasRecentArchivedSessionFileMetadata(
        string codexHome, DateTimeOffset now) =>
        LatestArchivedRolloutWriteUtc(codexHome, now) is DateTime stamp &&
        IsRecentWrite(stamp, now);

    private static bool IsRecentWrite(DateTime stamp, DateTimeOffset now)
    {
        var age = now.UtcDateTime - stamp;
        return age >= TimeSpan.FromSeconds(-30) && age <= RecentActivityWindow;
    }

    private static DateTime? LatestArchivedRolloutWriteUtc(
        string codexHome, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(codexHome)) return null;
        var root = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(root)) return null;
        DateTime? latest = null;
        try
        {
            // Do not follow reparse points or read any session content.
            // Enumerate metadata once/10s only after recent-day scan misses.
            var options = new EnumerationOptions {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var path in Directory.EnumerateFiles(
                root, "rollout-*.jsonl", options))
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(path); }
                catch (Exception error) when (error is IOException or
                    UnauthorizedAccessException) { continue; }
                if (latest is null || stamp > latest.Value) latest = stamp;
                if (IsRecentWrite(stamp, now)) return stamp;
            }
        }
        catch (Exception error) when (error is IOException or
            UnauthorizedAccessException)
        {
            // Never use missing/inaccessible metadata as proof of work.
        }
        return latest;
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
