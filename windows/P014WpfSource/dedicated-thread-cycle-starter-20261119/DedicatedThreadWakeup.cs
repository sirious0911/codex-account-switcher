using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DedicatedThreadCycleStarter;

/// <summary>A new Codex conversation is NEVER created. This feature only uses a pinned,
/// pre-existing, persisted thread ID for the currently verified account.</summary>
public sealed record DedicatedChatSettings(
    Guid ProfileId,
    string AccountSha256,
    string ExistingThreadId,
    bool AutomaticMessagesEnabled = false,
    bool StartOnFiveHour = false,
    bool StartOnSevenDay = false,
    string Message = "안녕",
    string ThreadStorage = "switcher-profile");

public enum LimitLane { FiveHour, SevenDay }

/// <summary>The verified reset deadline from a prior trusted usage observation.
/// The server may NOT advance its next-reset timestamp until the first new turn,
/// so waiting for provider rollover alone would not start the idle window.</summary>
public sealed record TrustedResetDeadline(
    Guid ActiveProfileId,
    string ActiveAccountSha256,
    LimitLane Lane,
    DateTimeOffset PreviousResetAtUtc,
    DateTimeOffset LastTrustedObservationUtc,
    int WindowDurationMinutes,
    bool ActiveIdentityConfirmed,
    bool OrdinaryUsageAllowed,
    bool TrustedSource);

// "notLoaded" is selectable as a persisted existing chat, but it is NOT
// equivalent to an idle runtime for outbound turn/start authorization.
public sealed record ExistingThread(
    string ThreadId, bool IsPersisted, bool IsIdle, bool IsNotLoaded = false);

/// <summary>Transport implementation MUST permit only thread/read, thread/resume,
/// and turn/start. No thread/start, fork, deletion, account change, or UI automation.</summary>
public interface IExistingThreadClient
{
    Task<ExistingThread?> ReadAsync(string pinnedThreadId, CancellationToken cancellationToken);
    Task ResumeAsync(string pinnedThreadId, CancellationToken cancellationToken);
    /// <returns>Nonempty turn ID only if server accepted turn/start.</returns>
    Task<string?> StartTurnAsync(string pinnedThreadId, string text, CancellationToken cancellationToken);
}

public enum WakeupState
{
    Skipped,
    ReservedUncertain,
    TurnStartAccepted
}

public sealed record WakeupOutcome(WakeupState State, string Reason, string? TurnId = null);
public sealed record WakeupLedgerEntry(
    string Fingerprint,
    string AccountSha256,
    string ThreadId,
    LimitLane Lane,
    DateTimeOffset ResetAtUtc,
    DateTimeOffset ReservedAtUtc,
    string State,
    string? TurnId = null);
public sealed class WakeupLedger
{
    private readonly string path;
    private readonly object sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WakeupLedger(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required");
        this.path = Path.GetFullPath(path);
    }

    /// <summary>Cross-process write lock + atomic write. A failed/uncertain send never replays.</summary>
    public WakeupReservation TryReserve(TrustedResetDeadline deadline, DedicatedChatSettings settings,
        DateTimeOffset now)
    {
        lock (sync)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var list = Load();
            var key = Fingerprint(deadline);
            if (list.Any(e => e.Fingerprint == key))
                return new WakeupReservation(false, key, "ALREADY_RESERVED");

            // A simultaneous weekly and five-hour reset must create one turn only,
            // even if the two notifications arrive in separate refresh iterations.
            if (list.Any(e => e.AccountSha256 == settings.AccountSha256 &&
                e.ThreadId == settings.ExistingThreadId &&
                e.Lane != deadline.Lane &&
                Math.Abs((e.ResetAtUtc - deadline.PreviousResetAtUtc).TotalMinutes) <= 5))
                return new WakeupReservation(false, key, "SIMULTANEOUS_RESET_ALREADY_RESERVED");

            list.Add(new WakeupLedgerEntry(key, settings.AccountSha256,
                settings.ExistingThreadId, deadline.Lane,
                deadline.PreviousResetAtUtc, now, "RESERVED_UNCERTAIN"));
            Save(list);
            return new WakeupReservation(true, key, "RESERVED_UNCERTAIN");
        }
    }

    public void MarkAccepted(string fingerprint, string turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId)) throw new ArgumentException("Turn ID required");
        lock (sync)
        {
            using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            var entries = Load();
            var index = entries.FindIndex(x => x.Fingerprint == fingerprint);
            if (index < 0) throw new InvalidOperationException("Reservation missing");
            entries[index] = entries[index] with { State = "TURN_START_ACCEPTED", TurnId = turnId };
            Save(entries);
        }
    }

    public IReadOnlyList<WakeupLedgerEntry> Snapshot()
    {
        lock (sync) { return Load(); }
    }

    private List<WakeupLedgerEntry> Load() =>
        File.Exists(path) ? JsonSerializer.Deserialize<List<WakeupLedgerEntry>>(
            File.ReadAllText(path)) ?? throw new InvalidDataException("Empty ledger")
            : new List<WakeupLedgerEntry>();

    private void Save(List<WakeupLedgerEntry> entries)
    {
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Fingerprint(TrustedResetDeadline deadline)
    {
        var raw = string.Join("|", deadline.ActiveAccountSha256.ToLowerInvariant(),
            deadline.Lane.ToString(), deadline.PreviousResetAtUtc.ToUnixTimeSeconds());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }
}

public sealed record WakeupReservation(bool Created, string Fingerprint, string Reason);

/// <summary>Staged, OFF-by-default controller. No transport is constructed or called by itself.
/// A production settings UI and trusted quota-monitor gate must explicitly wire it.</summary>
public sealed class DedicatedThreadWakeup
{
    private readonly WakeupLedger ledger;
    private readonly IExistingThreadClient client;
    public DedicatedThreadWakeup(WakeupLedger ledger, IExistingThreadClient client)
    {
        this.ledger = ledger;
        this.client = client;
    }

    public async Task<WakeupOutcome> OnScheduledResetAsync(
        DedicatedChatSettings settings, TrustedResetDeadline eventSource,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var denied = Gate(settings, eventSource, now);
        if (denied is not null) return new(WakeupState.Skipped, denied);

        ExistingThread? thread;
        try
        {
            // Discovery is read-only; failure means STOP, not thread/start.
            thread = await client.ReadAsync(settings.ExistingThreadId, cancellationToken);
            // A stored desktop conversation may be "notLoaded" but must never
            // be considered idle for direct turn/start until resumed and reread.
            if (thread is null || thread.ThreadId != settings.ExistingThreadId ||
                !thread.IsPersisted || (!thread.IsIdle && !thread.IsNotLoaded))
                return new(WakeupState.Skipped, "PINNED_THREAD_NOT_PERSISTED_OR_IDLE");
            await client.ResumeAsync(settings.ExistingThreadId, cancellationToken);
            var resumed = await client.ReadAsync(settings.ExistingThreadId, cancellationToken);
            if (resumed is null || resumed.ThreadId != settings.ExistingThreadId ||
                !resumed.IsPersisted || !resumed.IsIdle)
                return new(WakeupState.Skipped, "THREAD_NOT_IDLE_AFTER_RESUME");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(WakeupState.Skipped, "THREAD_READ_OR_RESUME_FAILED");
        }

        // Durable reservation must precede any external turn/start request.
        WakeupReservation reservation;
        try { reservation = ledger.TryReserve(eventSource, settings, now); }
        catch (IOException) { return new(WakeupState.Skipped, "LEDGER_UNAVAILABLE"); }
        catch (UnauthorizedAccessException) { return new(WakeupState.Skipped, "LEDGER_UNAVAILABLE"); }
        catch (JsonException) { return new(WakeupState.Skipped, "LEDGER_INVALID"); }
        if (!reservation.Created) return new(WakeupState.Skipped, reservation.Reason);
        try
        {
            var turnId = await client.StartTurnAsync(settings.ExistingThreadId,
                settings.Message, cancellationToken);
            if (string.IsNullOrWhiteSpace(turnId))
                return new(WakeupState.ReservedUncertain, "TURN_START_NO_ACK_NO_RETRY");
            ledger.MarkAccepted(reservation.Fingerprint, turnId);
            return new(WakeupState.TurnStartAccepted, "TURN_START_ACCEPTED_NOT_COMPLETED", turnId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(WakeupState.ReservedUncertain, "TURN_START_UNCERTAIN_NO_RETRY");
        }
    }

    public static string? Gate(DedicatedChatSettings s, TrustedResetDeadline e, DateTimeOffset now)
    {
        if (!s.AutomaticMessagesEnabled) return "AUTOMATIC_MESSAGES_OFF";
        if (e.Lane == LimitLane.FiveHour && !s.StartOnFiveHour) return "5H_OFF";
        if (e.Lane == LimitLane.SevenDay && !s.StartOnSevenDay) return "7D_OFF";
        if (s.ProfileId == Guid.Empty || s.ProfileId != e.ActiveProfileId ||
            !e.ActiveIdentityConfirmed || !e.TrustedSource || !e.OrdinaryUsageAllowed)
            return "ACCOUNT_NOT_CONFIRMED";
        if (s.AccountSha256 != e.ActiveAccountSha256 ||
            !Regex.IsMatch(s.AccountSha256, "^[a-fA-F0-9]{64}$"))
            return "ACCOUNT_HASH_MISMATCH";
        if (!Guid.TryParseExact(s.ExistingThreadId, "D", out _))
            return "MISSING_PINNED_EXISTING_THREAD_ID";
        if (s.Message != "안녕") return "UNAPPROVED_MESSAGE_CONTENT";
        if (e.WindowDurationMinutes != (e.Lane == LimitLane.FiveHour ? 300 : 10080))
            return "INVALID_WINDOW_DURATION";
        if (e.PreviousResetAtUtc > now ||
            now - e.PreviousResetAtUtc > TimeSpan.FromMinutes(10))
            return "NOT_WITHIN_SCHEDULED_RESET_WINDOW";
        if (e.LastTrustedObservationUtc > now + TimeSpan.FromSeconds(30) ||
            now - e.LastTrustedObservationUtc > TimeSpan.FromMinutes(6))
            return "NO_FRESH_TRUSTED_OBSERVATION";
        return null;
    }
}
