using System.Diagnostics;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Read-only binding to the user's existing Windows-only Alert Bot script.
/// Never opens or decrypts its DPAPI token or destination files, never starts
/// a process, and never sends a message. The owner gate is temporary Windows
/// only; the original Mac-primary Telegram plan remains the default.
/// </summary>
public sealed class WindowsTelegramAlertBotScriptBinding
{
    private readonly string directory;

    public WindowsTelegramAlertBotScriptBinding(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        this.directory = Path.GetFullPath(directory);
    }

    public string ScriptPath => Path.Combine(directory, "send-telegram-alert.ps1");

    public WindowsTelegramAlertBotFilesStatus InspectFiles()
    {
        if (!Directory.Exists(directory)) return WindowsTelegramAlertBotFilesStatus.MissingDirectory;
        if (!File.Exists(ScriptPath)) return WindowsTelegramAlertBotFilesStatus.MissingScript;
        if (!File.Exists(Path.Combine(directory, "telegram-token.dpapi")))
            return WindowsTelegramAlertBotFilesStatus.MissingEncryptedTokenFile;
        if (!File.Exists(Path.Combine(directory, "telegram-chat-id.txt")))
            return WindowsTelegramAlertBotFilesStatus.MissingChatDestinationFile;
        // Existence is NOT proof of decryptability, authorized user,
        // valid Chat ID, live bot or successful Telegram delivery.
        return WindowsTelegramAlertBotFilesStatus.FilesPresentUnverified;
    }

    /// <summary>
    /// Prepare argv for a future separately approved single-send runner.
    /// Caller must not execute this until the sole-sender policy is resolved
    /// and the existing script's unredacted PowerShell stderr is contained.
    /// </summary>
    public bool TryPrepare(
        Weekly100Alert? alert,
        TelegramSenderOwnershipDecision? ownership,
        out ProcessStartInfo? invocation)
    {
        invocation = null;
        if (!OperatingSystem.IsWindows() ||
            ownership?.PermitsTemporaryWindowsSender != true ||
            InspectFiles() != WindowsTelegramAlertBotFilesStatus.FilesPresentUnverified ||
            alert is null || alert.Kind != "WEEKLY_100_RECOVERED" ||
            alert.Lane != "week" || alert.RemainingPercent != 100 ||
            alert.Status != "MOCK_RESERVED" || alert.MockOwner != "MOCK_WINDOWS")
            return false;

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable)) return false;
        var formatted = TelegramWeekly100Sender.FormatText(alert);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(ScriptPath);
        info.ArgumentList.Add("-Message");
        info.ArgumentList.Add(formatted);
        // The original script handles its own Windows CurrentUser DPAPI and
        // chat destination. Neither value is passed to or read by this app.
        invocation = info;
        return true;
    }
}

public enum WindowsTelegramAlertBotFilesStatus
{
    MissingDirectory,
    MissingScript,
    MissingEncryptedTokenFile,
    MissingChatDestinationFile,
    FilesPresentUnverified
}
