using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DedicatedThreadCycleStarter;

/// <summary>
/// Staged account-proof gate for selecting an existing dedicated thread.
/// The caller must obtain ActiveProfile/ActiveAccountId from the confirmed,
/// non-mutating Switcher snapshot. RPC must share one initialized Codex session.
/// No login, account switching, thread creation, resume, or message sending.
/// </summary>
public sealed record ActiveSwitcherIdentity(
    Guid ActiveProfileId,
    Guid? SelectedActiveAccountId,
    string? ProfileEmail,
    bool IdentityConfirmed,
    bool IsSwitching);

public sealed record VerifiedThreadCatalog(
    Guid ProfileId, string AccountFingerprint,
    IReadOnlyList<ExistingThreadChoice> Threads);

public sealed class VerifiedExistingThreadPicker
{
    private readonly ICodexJsonRpcSession rpc;
    private readonly ExistingCodexThreadRpc threads;
    private readonly DedicatedThreadBindingStore store;
    private readonly string threadStorage;

    public VerifiedExistingThreadPicker(ICodexJsonRpcSession session,
        DedicatedThreadBindingStore bindingStore, string source = "switcher-profile")
    {
        rpc = session ?? throw new ArgumentNullException(nameof(session));
        threads = new ExistingCodexThreadRpc(session);
        store = bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
        if (source is not ("switcher-profile" or "desktop-default"))
            throw new ArgumentException("Unsupported Codex thread storage", nameof(source));
        threadStorage = source;
    }

    public async Task<VerifiedThreadCatalog> ListAsync(
        ActiveSwitcherIdentity active, string? search = null,
        CancellationToken cancellationToken = default)
    {
        var identity = await ProveAsync(active, cancellationToken);
        var choices = await threads.ListCurrentAccountAsync(search, cancellationToken);
        // A switch between account/read and thread/list invalidates this result.
        var verifiedAgain = await ProveAsync(active, cancellationToken);
        if (verifiedAgain != identity)
            throw new InvalidOperationException("Account changed during discovery");
        return new VerifiedThreadCatalog(active.ActiveProfileId, identity, choices);
    }

    // The UI must pass a live identity provider; static snapshot is for tests.
    public Task<DedicatedChatSettings> PinExistingAsync(
        ActiveSwitcherIdentity active, string selectedId,
        CancellationToken cancellationToken = default) =>
        PinExistingAsync(() => active, selectedId, cancellationToken);

    public async Task<DedicatedChatSettings> PinExistingAsync(
        Func<ActiveSwitcherIdentity> current, string selectedId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var active = current();
        if (!Guid.TryParseExact(selectedId, "D", out _))
            throw new InvalidOperationException("Existing thread UUID required");

        // Do not write until the exact ID is returned by currentAccountOnly.
        var catalog = await ListAsync(active, cancellationToken: cancellationToken);
        if (current() != active)
            throw new InvalidOperationException("Switcher account changed during list");
        if (catalog.Threads.Count(t => t.Id == selectedId) != 1)
            throw new InvalidOperationException("Thread absent from current account");

        var thread = await threads.ReadAsync(selectedId, cancellationToken);
        if (thread is null || thread.ThreadId != selectedId ||
            !thread.IsPersisted || (!thread.IsIdle && !thread.IsNotLoaded))
            throw new InvalidOperationException("Thread not persisted or idle");

        // Re-read auth state immediately before the local-only save.
        if (await ProveAsync(active, cancellationToken) != catalog.AccountFingerprint ||
            current() != active)
            throw new InvalidOperationException("Account changed before saving");

        var pinned = new DedicatedChatSettings(active.ActiveProfileId,
            catalog.AccountFingerprint, selectedId,
            AutomaticMessagesEnabled: false,
            StartOnFiveHour: false, StartOnSevenDay: false,
            ThreadStorage: threadStorage);
        store.Upsert(pinned);
        return pinned;
    }

    public static string FingerprintEmail(string normalizedEmail) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "codex-switcher:verified-chatgpt-email:v1\n" + normalizedEmail)))
            .ToLowerInvariant();

    private async Task<string> ProveAsync(ActiveSwitcherIdentity active,
        CancellationToken cancellationToken)
    {
        if (!active.IdentityConfirmed || active.IsSwitching ||
            active.ActiveProfileId == Guid.Empty ||
            active.SelectedActiveAccountId != active.ActiveProfileId)
            throw new InvalidOperationException("Switcher active identity unconfirmed");

        var email = NormalizeEmail(active.ProfileEmail);
        if (email is null)
            throw new InvalidOperationException("Active profile email unavailable");

        var result = await rpc.CallAsync("account/read",
            new { refreshToken = false }, cancellationToken);
        if (!result.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object ||
            !account.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "chatgpt" ||
            !account.TryGetProperty("email", out var currentEmail) ||
            currentEmail.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Codex ChatGPT identity unavailable");

        var authenticatedEmail = NormalizeEmail(currentEmail.GetString());
        if (authenticatedEmail is null ||
            !string.Equals(email, authenticatedEmail, StringComparison.Ordinal))
            throw new InvalidOperationException("Codex and Switcher accounts differ");
        return FingerprintEmail(email);
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var trimmed = email.Trim();
        if (trimmed.Length > 254 || trimmed.Any(char.IsControl) ||
            trimmed.Any(char.IsWhiteSpace) || trimmed.Count(c => c == '@') != 1)
            return null;
        var parts = trimmed.Split('@');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 ||
            !parts[1].Contains('.')) return null;
        return trimmed.ToLowerInvariant();
    }
}
