using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Adapter from the running Switcher account snapshot to a local, explicit
/// per-profile CODEX_HOME. This candidate is never activated automatically.
/// </summary>
public sealed class PersonalExistingThreadContext
{
    private readonly IAccountClient client;
    private readonly string dataHome;
    private readonly Func<string> localCodexBinary;

    public PersonalExistingThreadContext(IAccountClient accountClient,
        string switcherDataHome, Func<string>? binaryResolver = null)
    {
        client = accountClient ?? throw new ArgumentNullException(nameof(accountClient));
        if (string.IsNullOrWhiteSpace(switcherDataHome) ||
            !Path.IsPathFullyQualified(switcherDataHome))
            throw new ArgumentException("Absolute data home required", nameof(switcherDataHome));
        dataHome = Path.GetFullPath(switcherDataHome);
        localCodexBinary = binaryResolver ?? ResolveLocalCodexExecutable;
    }

    public string DataHome => dataHome;

    public ActiveSwitcherIdentity CurrentIdentity()
    {
        var snapshot = client.State;
        var id = snapshot.ActiveAccountID;
        var selected = id is null ? Array.Empty<AccountRow>() :
            snapshot.Accounts.Where(a => a.Profile.Id == id.Value).ToArray();
        // A no-account startup snapshot is not proof of a valid current user.
        return new ActiveSwitcherIdentity(
            id ?? Guid.Empty,
            id,
            selected.Length == 1 ? selected[0].Profile.Email : null,
            snapshot.ActiveIdentityConfirmed && selected.Length == 1,
            snapshot.IsMutating || snapshot.IsAddingAccount);
    }

    public async Task<InitializedCodexReadOnlySession> OpenReadOnlyAsync(
        ActiveSwitcherIdentity expected, CancellationToken cancellationToken)
    {
        if (CurrentIdentity() != expected || !expected.IdentityConfirmed ||
            expected.IsSwitching || expected.ActiveProfileId == Guid.Empty)
            throw new InvalidOperationException("Current account cannot be proven");

        var codexExe = localCodexBinary();
        var opened = await InitializedCodexReadOnlySession.StartForSwitcherProfileAsync(
            codexExe, expected.ActiveProfileId, dataHome, cancellationToken);
        // There is no active account switch or previous identity between
        // constructing the process and serving the first UI read-only query.
        if (CurrentIdentity() != expected)
        {
            await opened.DisposeAsync();
            throw new InvalidOperationException("Account changed while opening Codex session");
        }
        return opened;
    }

    public async Task<InitializedCodexReadOnlySession> OpenDesktopReadOnlyAsync(
        ActiveSwitcherIdentity expected, CancellationToken cancellationToken)
    {
        if (CurrentIdentity() != expected || !expected.IdentityConfirmed ||
            expected.IsSwitching || expected.ActiveProfileId == Guid.Empty)
            throw new InvalidOperationException("Current account cannot be proven");

        // Desktop Codex runs the Windows Store packaged runtime, not the npm CLI.
        // Their current-account thread inventories can differ even for the
        // same authenticated email; never silently fall back to npm.
        var codexExe = ResolveRunningDesktopCodexExecutable();
        var opened = await InitializedCodexReadOnlySession.StartForDesktopDefaultAsync(
            codexExe, cancellationToken);
        if (CurrentIdentity() != expected)
        {
            await opened.DisposeAsync();
            throw new InvalidOperationException("Account changed while opening Desktop session");
        }
        // VerifiedExistingThreadPicker must still prove account/read email
        // equality before listing or selecting any Desktop conversation.
        return opened;
    }

    /// <summary>
    /// Read only the image paths of already running Codex processes. The exact
    /// executable must belong to OpenAI's WindowsApps Codex package.
    /// Never use an arbitrary PATH command or npm fallback for Desktop threads.
    /// </summary>
    private static string ResolveRunningDesktopCodexExecutable()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) ||
            !Path.IsPathFullyQualified(programFiles))
            throw new InvalidOperationException("Program Files root unavailable");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcessesByName("codex"))
        {
            try
            {
                var binary = process.MainModule?.FileName;
                if (binary is null ||
                    !IsTrustedDesktopBinaryPath(binary, programFiles))
                    continue;
                var file = new FileInfo(binary);
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                found.Add(file.FullName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException or System.Security.SecurityException
                or UnauthorizedAccessException or IOException)
            {
                // Unreadable processes cannot confer trust.
            }
            finally { process.Dispose(); }
        }
        if (found.Count != 1)
            throw new InvalidOperationException(
                "Unique running packaged Codex Desktop executable not confirmed");
        return found.Single();
    }

    public static bool IsTrustedDesktopBinaryPath(string? binary, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(binary) ||
            string.IsNullOrWhiteSpace(programFiles) ||
            !Path.IsPathFullyQualified(binary) ||
            !Path.IsPathFullyQualified(programFiles))
            return false;
        try
        {
            var root = Path.Combine(Path.GetFullPath(programFiles), "WindowsApps");
            var relative = Path.GetRelativePath(root, Path.GetFullPath(binary));
            var parts = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 4 &&
                parts[0].StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                parts[0].EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[1], "app", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[2], "resources", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[3], "codex.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string ResolveLocalCodexExecutable()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var package = Path.Combine(appData, "npm", "node_modules", "@openai", "codex");
        if (!Directory.Exists(package))
            throw new InvalidOperationException("Local Codex CLI unavailable");
        var candidates = Directory.EnumerateFiles(package, "codex.exe",
            SearchOption.AllDirectories).Where(file => {
                var info = new FileInfo(file);
                return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }).Take(2).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("Unique Codex CLI executable not found");
        return candidates[0];
    }
}
