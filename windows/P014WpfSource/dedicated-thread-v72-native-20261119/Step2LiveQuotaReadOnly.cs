using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher;

// An explicit STEP2-only one-shot probe. No CoreClient initialization,
// persisted setting, taskbar, owner, sender, account switch, or Codex turn.
internal static class Step2LiveQuotaReadOnly
{
    private sealed record Registry(Guid? ActiveAccountID, Profile[] Accounts);
    private sealed record Profile(Guid Id, string? AccountID, string? Email);
    internal sealed record Snapshot(
        double FiveHourRemainingPercent, DateTimeOffset FiveHourReset,
        double WeeklyRemainingPercent, DateTimeOffset WeeklyReset);

    internal static async Task<Snapshot> ReadOnceAsync()
    {
        var root = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher Personal");
        EnsureDirectory(root);
        var registryPath = Path.Combine(root, "accounts.json");
        var registryBefore = Metadata(registryPath);
        var registry = JsonSerializer.Deserialize<Registry>(
            File.ReadAllBytes(registryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("STEP2_REGISTRY_UNAVAILABLE");
        if (registry.ActiveAccountID is not Guid active || active == Guid.Empty ||
            registry.Accounts is null)
            throw new InvalidDataException("STEP2_ACTIVE_PROFILE_UNAVAILABLE");
        var selected = registry.Accounts.Where(p => p.Id == active).ToArray();
        if (selected.Length != 1 || string.IsNullOrWhiteSpace(selected[0].AccountID))
            throw new InvalidDataException("STEP2_ACTIVE_PROFILE_AMBIGUOUS");

        var profileHome = Path.Combine(root, "accounts", active.ToString("D"));
        var defaultHome = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), ".codex");
        EnsureDirectory(profileHome);
        EnsureDirectory(defaultHome);
        var profileAuth = Path.Combine(profileHome, "auth.json");
        var defaultAuth = Path.Combine(defaultHome, "auth.json");
        var profileBefore = Metadata(profileAuth);
        var defaultBefore = Metadata(defaultAuth);
        var expected = selected[0].AccountID!;
        if (!string.Equals(CredentialAccount(profileAuth), expected, StringComparison.Ordinal) ||
            !string.Equals(CredentialAccount(defaultAuth), expected, StringComparison.Ordinal))
            throw new InvalidDataException("STEP2_ACTIVE_CREDENTIAL_ID_MISMATCH");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var codexExe = Path.Combine(appData, "npm", "node_modules", "@openai", "codex",
            "node_modules", "@openai", "codex-win32-x64", "vendor",
            "x86_64-pc-windows-msvc", "bin", "codex.exe");
        var codexFile = new FileInfo(codexExe);
        if (!codexFile.Exists || codexFile.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            codexFile.Length < 1)
            throw new InvalidDataException("STEP2_CODEX_CLI_NOT_FOUND");
        var start = new ProcessStartInfo(codexExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = profileHome;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(23));
        using var process = Process.Start(start)
            ?? throw new IOException("STEP2_CODEX_PROCESS_UNAVAILABLE");
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await WriteAsync(process, new {
                method = "initialize", id = 0,
                @params = new { clientInfo = new {
                    name = "codex_account_switcher_step2",
                    title = "Codex Switcher STEP2 Read Only", version = "0.1.26" } }
            }, timeout.Token);
            using (var init = await ResponseAsync(process, 0, timeout.Token))
                if (!init.RootElement.TryGetProperty("result", out _))
                    throw new InvalidDataException("STEP2_INITIALIZE_REJECTED");
            await WriteAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await WriteAsync(process, new {
                method = "account/read", id = 1,
                @params = new { refreshToken = false }
            }, timeout.Token);
            using (var identity = await ResponseAsync(process, 1, timeout.Token))
                CheckProviderIdentity(identity.RootElement, expected, selected[0].Email);
            await WriteAsync(process, new {
                method = "account/rateLimits/read", id = 2, @params = new { }
            }, timeout.Token);
            Snapshot snapshot;
            using (var quota = await ResponseAsync(process, 2, timeout.Token))
                snapshot = ParseQuota(quota.RootElement);
            if (Metadata(registryPath) != registryBefore ||
                Metadata(profileAuth) != profileBefore ||
                Metadata(defaultAuth) != defaultBefore)
                throw new InvalidDataException("STEP2_SOURCE_CHANGED_DURING_READ");
            return snapshot;
        }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException) { }
            try { await stderr; }
            catch (OperationCanceledException) { }
        }
    }

    // Optional sanitized test receipt. A single new file directly in the
    // current user's TEMP; no account ID, email, credential or RPC payload.
    internal static void WriteOptionalReport(Snapshot snapshot)
    {
        var requested = Environment.GetEnvironmentVariable("CODEX_SWITCHER_STEP2_REPORT_FILE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        var full = Path.GetFullPath(requested);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), temp,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("STEP2_REPORT_LOCATION_DENIED");
        using var output = new FileStream(full, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, new
        {
            status = "PASS_ACTIVE_PROFILE_BOUND_LIVE_QUOTA",
            fiveHourRemainingPercent = snapshot.FiveHourRemainingPercent,
            fiveHourResetUtc = snapshot.FiveHourReset.ToUniversalTime(),
            weeklyRemainingPercent = snapshot.WeeklyRemainingPercent,
            weeklyResetUtc = snapshot.WeeklyReset.ToUniversalTime(),
            providerCall = "account/rateLimits/read",
            accountIdentityChecked = true,
            protectedFilesMetadataUnchanged = true,
            messagesRequestedByThisProbe = 0
        });
    }

    private static (long Length, DateTime LastWriteUtc) Metadata(string path)
    {
        var f = new FileInfo(path);
        if (!f.Exists || f.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            f.Length < 1 || f.Length > 20_000_000)
            throw new InvalidDataException("STEP2_FILE_UNAVAILABLE_OR_LINKED");
        return (f.Length, f.LastWriteTimeUtc);
    }

    private static void EnsureDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("STEP2_DIRECTORY_UNAVAILABLE_OR_LINKED");
    }

    private static string? CredentialAccount(string path)
    {
        if (Metadata(path).Length > 1_048_576)
            throw new InvalidDataException("STEP2_AUTH_SIZE");
        using var auth = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!auth.RootElement.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Object)
            return null;
        return GetString(tokens, "account_id");
    }

    private static string? GetString(JsonElement json, string name)
        => json.ValueKind == JsonValueKind.Object &&
           json.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task WriteAsync(Process p, object message, CancellationToken ct)
    {
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await p.StandardInput.FlushAsync(ct);
    }

    private static async Task<JsonDocument> ResponseAsync(Process p, int id, CancellationToken ct)
    {
        for (var lines = 0; lines < 120; lines++)
        {
            var line = await p.StandardOutput.ReadLineAsync(ct)
                ?? throw new IOException("STEP2_CODEX_EARLY_EXIT");
            if (line.Length > 524288)
                throw new InvalidDataException("STEP2_RESPONSE_OVERSIZE");
            using var message = JsonDocument.Parse(line);
            if (message.RootElement.TryGetProperty("id", out var actual) &&
                actual.ValueKind == JsonValueKind.Number &&
                actual.TryGetInt32(out var value) && value == id)
                return JsonDocument.Parse(line);
        }
        throw new InvalidDataException("STEP2_RESPONSE_LIMIT");
    }

    private static void CheckProviderIdentity(JsonElement message, string expected, string? email)
    {
        if (!message.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("STEP2_PROVIDER_IDENTITY_UNAVAILABLE");
        var providerID = GetString(account, "accountId") ??
                         GetString(account, "accountID") ??
                         GetString(account, "chatgptAccountId") ??
                         GetString(account, "id");
        var providerEmail = GetString(account, "email");
        if ((providerID is not null &&
             !string.Equals(providerID, expected, StringComparison.Ordinal)) ||
            (providerEmail is not null && email is not null &&
             !string.Equals(providerEmail, email, StringComparison.OrdinalIgnoreCase)) ||
            (providerID is null && (providerEmail is null || email is null)))
            throw new InvalidDataException("STEP2_PROVIDER_ACCOUNT_MISMATCH");
    }

    internal static Snapshot ParseQuota(JsonElement message)
    {
        if (!message.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimitsByLimitId", out var ids) ||
            !ids.TryGetProperty("codex", out var bucket))
            throw new InvalidDataException("STEP2_QUOTA_UNAVAILABLE");
        var windows = new[] { bucket.GetProperty("primary"), bucket.GetProperty("secondary") };
        var five = windows.Single(w => w.GetProperty("windowDurationMins").GetInt32() == 300);
        var week = windows.Single(w => w.GetProperty("windowDurationMins").GetInt32() == 10080);
        static (double Remaining, DateTimeOffset Reset) Normalize(JsonElement item)
        {
            var used = item.GetProperty("usedPercent").GetDouble();
            var reset = item.GetProperty("resetsAt").GetInt64();
            if (!double.IsFinite(used) || used < 0 || used > 100)
                throw new InvalidDataException("STEP2_INVALID_QUOTA_PERCENT");
            return (100 - used, DateTimeOffset.FromUnixTimeSeconds(reset));
        }
        var a = Normalize(five);
        var b = Normalize(week);
        return new Snapshot(a.Remaining, a.Reset, b.Remaining, b.Reset);
    }
}
