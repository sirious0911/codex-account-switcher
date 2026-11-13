using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Real Windows PowerShell 5.1 process transport for the previously-reviewed
/// Codex-specific scripts that reuse Amano bot credentials in place.
/// NOT wired into the running app. An external, durable production send
/// reservation, explicit send consent and sole-sender proof are prerequisites.
/// </summary>
internal sealed class WindowsExistingAmanoBotProcessAdapter
{
    private readonly IWindowsAmanoScriptRunner runner;
    internal WindowsExistingAmanoBotProcessAdapter(IWindowsAmanoScriptRunner runner)
        => this.runner = runner ?? throw new ArgumentNullException(nameof(runner));

    internal static WindowsExistingAmanoBotProcessAdapter CreateWindowsTransport()
        => new(new WindowsAmanoPowerShellRunner());

    internal async Task<WindowsAmanoDeliveryResult> ExecuteOnceAsync(
        WindowsExistingAmanoBotInvocation? route,
        WindowsAmanoInvocationGate gate,
        CancellationToken cancellationToken = default)
    {
        // No process, DPAPI access or network under any missing condition.
        if (!OperatingSystem.IsWindows() || route is null ||
            !gate.SingleSendApproved || !gate.SoleSenderVerified ||
            !gate.DurableReservationConfirmed ||
            !ValidRoute(route))
            return new(WindowsAmanoDeliveryStatus.Blocked);

        WindowsAmanoProcessResult result;
        try
        {
            result = await runner.RunAsync(route, cancellationToken);
        }
        catch (Exception)
        {
            // A process start, timeout or I/O failure might follow a send.
            // Fail UNCERTAIN, never automatically retry an externally visible action.
            return new(WindowsAmanoDeliveryStatus.Uncertain);
        }

        if (result.ExitCode != 0 || result.TimedOut || result.HadStandardError ||
            !TryParseAck(route, result.StandardOutput, out var messageId))
            return new(WindowsAmanoDeliveryStatus.Uncertain);
        return new(WindowsAmanoDeliveryStatus.Acknowledged, messageId);
    }

    private static bool ValidRoute(WindowsExistingAmanoBotInvocation route)
    {
        if (route.EventKey is not { Length: 64 } ||
            !route.EventKey.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            string.IsNullOrWhiteSpace(route.ExistingAmanoBotDirectory) ||
            string.IsNullOrWhiteSpace(route.ReviewedCodexScriptPath))
            return false;

        var scriptName = route.Silent
            ? WindowsPackagedTelegramScripts.MonitorScriptName
            : WindowsPackagedTelegramScripts.AlertScriptName;
        var expected = route.Silent
            ? WindowsPackagedTelegramScripts.MonitorSha256
            : WindowsPackagedTelegramScripts.AlertSha256;
        try
        {
            if (!LocalFixedDirectory(route.ExistingAmanoBotDirectory) ||
                !LocalFixedDirectory(Path.GetDirectoryName(route.ReviewedCodexScriptPath)))
                return false;
            if (!string.Equals(Path.GetFileName(route.ReviewedCodexScriptPath),
                    scriptName, StringComparison.Ordinal))
                return false;
            var bundled = WindowsPackagedTelegramScripts.Inspect(
                Path.Combine(Path.GetDirectoryName(
                    Path.GetFullPath(route.ReviewedCodexScriptPath))!, "SwitcherHost.exe"));
            var selectedVerified = route.Silent
                ? bundled.MonitorVerified : bundled.AlertVerified;
            if (!selectedVerified || expected.Length != 64)
                return false;
            var legacy = TelegramBotFileStatus.Inspect(route.ExistingAmanoBotDirectory);
            if (!legacy.ChatDestinationPresent ||
                !(route.Silent ? legacy.MonitorBotFilesPresent :
                    legacy.AlertBotFilesPresent))
                return false;

            // The reviewed PowerShell scripts independently enforce these
            // exact message forms before reading any credential files.
            var prefix = route.Silent
                ? "Codex 5시간 한도 복구: 잔여 사용량 100% (확인 "
                : "Codex 주간(7일) 한도 복구: 잔여 사용량 100% (확인 ";
            if (!route.Message.StartsWith(prefix, StringComparison.Ordinal) ||
                !route.Message.EndsWith(" KST)", StringComparison.Ordinal))
                return false;
            var dateText = route.Message.AsSpan(prefix.Length,
                route.Message.Length - prefix.Length - " KST)".Length);
            return DateTime.TryParseExact(dateText,
                "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool LocalFixedDirectory(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) ||
                !Path.IsPathFullyQualified(directory) ||
                directory.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var root = Path.GetPathRoot(full);
            if (root is null || string.Equals(root, full,
                    StringComparison.OrdinalIgnoreCase) ||
                new DriveInfo(root).DriveType != DriveType.Fixed)
                return false;
            for (var current = new DirectoryInfo(full); current is not null;
                current = current.Parent)
            {
                if (!current.Exists ||
                    current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryParseAck(
        WindowsExistingAmanoBotInvocation route,
        string? stdout,
        out long messageId)
    {
        messageId = 0;
        if (stdout is not { Length: > 0 and <= 160 }) return false;
        if (stdout.EndsWith("\r\n", StringComparison.Ordinal)) stdout = stdout[..^2];
        else if (stdout.EndsWith("\n", StringComparison.Ordinal)) stdout = stdout[..^1];
        if (stdout.Contains('\r') || stdout.Contains('\n') || stdout.Contains('\0'))
            return false;
        var parts = stdout.Split('|');
        var marker = route.Silent
            ? "CODEX_FIVEHOUR100_TELEGRAM_ACK_V1"
            : "CODEX_WEEKLY100_TELEGRAM_ACK_V1";
        return parts.Length == 3 &&
            parts[0] == marker && parts[1] == route.EventKey &&
            parts[2] is { Length: >= 1 and <= 19 } &&
            parts[2][0] is >= '1' and <= '9' &&
            parts[2].All(c => c is >= '0' and <= '9') &&
            long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture,
                out messageId) && messageId > 0;
    }
}

internal readonly record struct WindowsAmanoInvocationGate(
    bool SingleSendApproved,
    bool SoleSenderVerified,
    bool DurableReservationConfirmed);

internal enum WindowsAmanoDeliveryStatus
{
    Blocked,
    Acknowledged,
    Uncertain
}

internal sealed record WindowsAmanoDeliveryResult(
    WindowsAmanoDeliveryStatus Status,
    long? MessageId = null);

internal readonly record struct WindowsAmanoProcessResult(
    int ExitCode,
    string? StandardOutput,
    bool HadStandardError,
    bool TimedOut = false);

internal interface IWindowsAmanoScriptRunner
{
    Task<WindowsAmanoProcessResult> RunAsync(
        WindowsExistingAmanoBotInvocation route, CancellationToken cancellationToken);
}

/// <summary>
/// Production-capable subprocess boundary. Only the adapter above invokes it
/// after validating all three gates and pinned script + existing bot metadata.
/// This class is not referenced from Windows App.xaml.cs.
/// </summary>
internal sealed class WindowsAmanoPowerShellRunner : IWindowsAmanoScriptRunner
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(18);

    public async Task<WindowsAmanoProcessResult> RunAsync(
        WindowsExistingAmanoBotInvocation route, CancellationToken cancellationToken)
    {
        var start = CreateProcessStartInfo(route);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        if (!process.Start()) throw new IOException("Windows PowerShell did not start.");
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, 160, deadline.Token);
            var stderr = DrainRedactedAsync(process.StandardError, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            // The caller marks this event uncertain and never retries.
            throw;
        }
    }

    internal static ProcessStartInfo CreateProcessStartInfo(
        WindowsExistingAmanoBotInvocation route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var systemWindows = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(systemWindows)) throw new IOException("No Windows directory.");
        var executable = Path.Combine(systemWindows, "System32",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) throw new IOException("Windows PowerShell unavailable.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(route.ReviewedCodexScriptPath)!
        };
        foreach (var item in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
            route.ReviewedCodexScriptPath,
            "-LegacyBotDirectory", route.ExistingAmanoBotDirectory,
            "-EventKey", route.EventKey,
            "-Message", route.Message,
            "-ApprovedSingleSend", "-WindowsSenderVerified"
        })
            start.ArgumentList.Add(item);

        return start;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, int limit, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[64];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (builder.Length + read > limit)
                throw new InvalidDataException("Unexpected sender stdout size");
            builder.Append(buffer, 0, read);
            Array.Clear(buffer);
        }
        return builder.ToString();
    }

    private static async Task<bool> DrainRedactedAsync(
        StreamReader reader, CancellationToken token)
    {
        var buffer = new char[128];
        var anyOutput = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            anyOutput = true;
            Array.Clear(buffer);
        }
        return anyOutput;
    }
}
