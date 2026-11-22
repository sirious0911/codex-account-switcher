using System.Security.Cryptography;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Metadata-only local preflight for a FUTURE one-time Telegram send.
/// It never reads encrypted token/chat contents, decrypts DPAPI data, creates
/// processes, connects to a remote service, or modifies any user file.
/// Passing this check is NOT proof of authorized sender ownership or receipt.
/// </summary>
public sealed class WindowsTelegramSenderPreflight
{
    // Preserve the original weekly-only API for existing callers.
    public WindowsTelegramSenderPreflightResult Inspect(
        WindowsTelegramSenderPreflightPaths paths) =>
        InspectCore(paths, "send-telegram-alert.ps1", "telegram-token.dpapi");

    // Bind public sender names to the requested role; no credential values.
    public WindowsTelegramSenderPreflightResult Inspect(
        WindowsTelegramSenderPreflightPaths paths, QuotaTelegramBot role)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var binding = role switch
        {
            QuotaTelegramBot.MonitorMuted => (
                "send-fivehour100-existing-monitor-bot.ps1",
                "send-telegram-health.ps1", "telegram-health-token.dpapi"),
            QuotaTelegramBot.AlertImportant => (
                "send-weekly100-existing-alert-bot.ps1",
                "send-telegram-alert.ps1", "telegram-token.dpapi"),
            _ => default
        };
        if (binding.Item1 is null ||
            !IsLocalAbsolutePath(paths.ReceiptSenderScript) ||
            !string.Equals(Path.GetFileName(paths.ReceiptSenderScript),
                binding.Item1, StringComparison.OrdinalIgnoreCase))
            return new(WindowsTelegramSenderPreflightStatus.BotRoleMismatch);
        return InspectCore(paths, binding.Item2!, binding.Item3!);
    }

    private WindowsTelegramSenderPreflightResult InspectCore(
        WindowsTelegramSenderPreflightPaths paths,
        string originalName, string encryptedName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows())
            return new(WindowsTelegramSenderPreflightStatus.UnsupportedPlatform);

        if (!IsLocalAbsolutePath(paths.PowerShellExecutable) ||
            !IsLocalAbsolutePath(paths.ReceiptSenderScript) ||
            !IsLocalAbsolutePath(paths.LegacyBotDirectory) ||
            !IsSha256(paths.ExpectedReceiptSenderSha256) ||
            !IsSha256(paths.ExpectedLegacySenderSha256))
            return new(WindowsTelegramSenderPreflightStatus.UntrustedPathOrDigest);

        var original = Path.Combine(paths.LegacyBotDirectory, originalName);
        var encrypted = Path.Combine(paths.LegacyBotDirectory, encryptedName);
        var destination = Path.Combine(paths.LegacyBotDirectory, "telegram-chat-id.txt");

        if (!File.Exists(paths.PowerShellExecutable))
            return new(WindowsTelegramSenderPreflightStatus.MissingPowerShell);
        if (!File.Exists(paths.ReceiptSenderScript))
            return new(WindowsTelegramSenderPreflightStatus.MissingReceiptSenderScript);
        if (!File.Exists(original))
            return new(WindowsTelegramSenderPreflightStatus.MissingLegacySenderScript);
        if (!File.Exists(encrypted))
            return new(WindowsTelegramSenderPreflightStatus.MissingEncryptedBotToken);
        if (!File.Exists(destination))
            return new(WindowsTelegramSenderPreflightStatus.MissingChatDestination);

        // Files under a reparse-point parent can change between inspection and
        // actual invocation. This remains a separate TOCTOU risk for any future
        // live implementation; NEVER treat this check as execution permission.
        try
        {
            foreach (var file in new[]
                { paths.PowerShellExecutable, paths.ReceiptSenderScript,
                  original, encrypted, destination })
            {
                if (ContainsReparsePoint(file))
                    return new(WindowsTelegramSenderPreflightStatus.ReparsePointNotAllowed);
            }
            // Stat only. NEVER File.ReadAllText on the token/chat files.
            if (new FileInfo(encrypted).Length == 0 ||
                new FileInfo(destination).Length == 0)
                return new(WindowsTelegramSenderPreflightStatus.EmptyBotConfigFile);

            // Hash only NON-SECRET script source files.
            var senderSha = HashFile(paths.ReceiptSenderScript);
            if (!string.Equals(senderSha, paths.ExpectedReceiptSenderSha256,
                    StringComparison.OrdinalIgnoreCase))
                return new(WindowsTelegramSenderPreflightStatus.SenderScriptChanged);
            var legacySha = HashFile(original);
            if (!string.Equals(legacySha, paths.ExpectedLegacySenderSha256,
                    StringComparison.OrdinalIgnoreCase))
                return new(WindowsTelegramSenderPreflightStatus.LegacyScriptChanged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(WindowsTelegramSenderPreflightStatus.FileMetadataUnavailable);
        }

        // Explicitly not READY_TO_SEND: bot validity, Telegram network,
        // live user consent and single Mac/Windows sender are all unverified.
        return new(WindowsTelegramSenderPreflightStatus.LocalArtifactsVerifiedOnly);
    }

    private static string HashFile(string filename)
    {
        using var source = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
    }

    // Never probe a network UNC/device path just because it is absolute.
    // All source and DPAPI configuration preflight stays on a drive-local path.
    private static bool IsLocalAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Length >= 3 &&
        char.IsLetter(path[0]) && path[1] == ':' &&
        (path[2] == '\\' || path[2] == '/') &&
        Path.IsPathFullyQualified(path);

    private static bool IsSha256(string? digest) =>
        digest?.Length == 64 && digest.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool ContainsReparsePoint(string path)
    {
        var item = new FileInfo(Path.GetFullPath(path));
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return true;
        var ancestor = item.Directory;
        while (ancestor is not null)
        {
            if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            ancestor = ancestor.Parent;
        }
        return false;
    }
}

public sealed record WindowsTelegramSenderPreflightPaths(
    string PowerShellExecutable,
    string ReceiptSenderScript,
    string LegacyBotDirectory,
    string ExpectedReceiptSenderSha256,
    string ExpectedLegacySenderSha256);

public sealed record WindowsTelegramSenderPreflightResult(
    WindowsTelegramSenderPreflightStatus Status)
{
    public bool LocalArtifactsVerified =>
        Status == WindowsTelegramSenderPreflightStatus.LocalArtifactsVerifiedOnly;
}

public enum WindowsTelegramSenderPreflightStatus
{
    UnsupportedPlatform,
    UntrustedPathOrDigest,
    MissingPowerShell,
    MissingReceiptSenderScript,
    MissingLegacySenderScript,
    MissingEncryptedBotToken,
    MissingChatDestination,
    ReparsePointNotAllowed,
    EmptyBotConfigFile,
    SenderScriptChanged,
    LegacyScriptChanged,
    FileMetadataUnavailable,
    LocalArtifactsVerifiedOnly,
    BotRoleMismatch
}
