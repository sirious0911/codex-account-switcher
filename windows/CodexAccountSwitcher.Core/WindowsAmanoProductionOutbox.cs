using System.Security;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// Production ledger for Codex 5H/7D events. Unlike MOCK worker state,
/// these entries may only be acknowledged using a Telegram delivery receipt.
/// Creating an entry never authorizes or starts a sender process.
/// </summary>
internal sealed class WindowsAmanoProductionOutbox
{
    private const int MaxItems = 256;
    private const long MaxFileBytes = 512 * 1024;
    private const int Schema = 1;
    private readonly string directory;
    private readonly string target;
    private readonly object sync = new();

    internal WindowsAmanoProductionOutbox(string userDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        var root = Path.GetFullPath(userDataDirectory);
        directory = Path.Combine(root, "telegram-production-outbox");
        target = Path.Combine(directory, "state.json");
    }

    internal string ReadDisplay()
    {
        try
        {
            var all = Read();
            if (all.Count == 0)
                return "운영 알림 대기함: 기록 없음 · 실제 전송 미승인";
            var waiting = all.Count(item =>
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval);
            var reserved = all.Count(item =>
                item.Status == WindowsAmanoOutboxStatus.Reserved);
            var acknowledged = all.Count(item =>
                item.Status == WindowsAmanoOutboxStatus.Acknowledged);
            var uncertain = all.Count(item =>
                item.Status == WindowsAmanoOutboxStatus.Uncertain);
            return "운영 알림 대기함: 승인 대기 " + waiting +
                "건 / 예약 중단 " + reserved +
                "건 / Telegram 응답 확인 " + acknowledged +
                "건 / 결과 불확실 " + uncertain +
                "건 · 실제 전송은 별도 승인 필요";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or SecurityException)
        {
            return "운영 알림 대기함: 원장 읽기 오류 · 실제 전송 차단";
        }
    }

    // The WPF view gets only human-review fields, never a reservation token,
    // bot credentials, or any authority to send a message.
    internal IReadOnlyList<WindowsTelegramApprovalRow> ReadPendingRows()
    {
        return Read()
            .Where(item => item.Status == WindowsAmanoOutboxStatus.AwaitingApproval)
            .OrderByDescending(item => item.ObservedAt)
            .Take(10)
            .Select(item => new WindowsTelegramApprovalRow(
                item.Lane, item.EventKey, item.ObservedAt))
            .ToArray();
    }

    internal IReadOnlyList<WindowsAmanoOutboxItem> Read()
    {
        lock (sync)
        {
            if (!Directory.Exists(directory)) return [];
            using var locked = LockState();
            return Load().Items.ToArray();
        }
    }

    /// <summary>
    /// Stage only a newly confirmed recovery event which already passed the
    /// configured bot-lane and sender-intent checks. This is NOT a send.
    /// </summary>
    internal bool Stage(
        TelegramNotificationLane lane,
        string? eventKey,
        DateTimeOffset observedAt,
        bool matchingPreparedRoute,
        TelegramNotificationPreferences? preferences)
    {
        var allow = lane switch
        {
            TelegramNotificationLane.FiveHourRecovery =>
                preferences?.Normalize().WantsFiveHour == true,
            TelegramNotificationLane.WeeklyRecovery =>
                preferences?.Normalize().WantsWeekly == true,
            _ => false
        };
        if (!allow || !matchingPreparedRoute || !ValidKey(eventKey) ||
            observedAt == default)
            return false;

        lock (sync)
        {
            Directory.CreateDirectory(directory);
            using var locked = LockState();
            var state = Load();
            if (state.Items.Any(item =>
                    item.Lane == lane && item.EventKey == eventKey))
                return false;
            if (state.Items.Count >= MaxItems)
                return false; // no eviction of uncertain or reserved events
            state.Items.Add(new WindowsAmanoOutboxItem(
                lane, eventKey!, observedAt, DateTimeOffset.UtcNow,
                WindowsAmanoOutboxStatus.AwaitingApproval, null, null));
            Save(state);
            return true;
        }
    }

    /// <summary>
    /// A durable reservation can be made only by an explicitly authorized
    /// future product UI action. Caller must prove the exact selected event
    /// and verify exclusive Windows Sender state before requesting a claim.
    /// </summary>
    internal WindowsAmanoOutboxItem? ReserveOnce(
        TelegramNotificationLane lane,
        string? eventKey,
        bool userApprovedThisExactEvent,
        bool verifiedLocalSender,
        bool crossHostSenderHandoffConfirmed)
    {
        if (!userApprovedThisExactEvent || !verifiedLocalSender ||
            !crossHostSenderHandoffConfirmed || !ValidKey(eventKey))
            return null;
        lock (sync)
        {
            if (!Directory.Exists(directory)) return null;
            using var locked = LockState();
            var state = Load();
            var index = state.Items.FindIndex(item =>
                item.Lane == lane && item.EventKey == eventKey &&
                item.Status == WindowsAmanoOutboxStatus.AwaitingApproval);
            if (index < 0) return null;
            var reserved = state.Items[index] with
            {
                Status = WindowsAmanoOutboxStatus.Reserved,
                AttemptToken = Guid.NewGuid().ToString("N")
            };
            state.Items[index] = reserved;
            Save(state); // durable reservation before any external side-effect
            return reserved;
        }
    }

    internal bool Complete(
        TelegramNotificationLane lane, string eventKey,
        string? attemptToken, long telegramMessageId) =>
        Finish(lane, eventKey, attemptToken,
            WindowsAmanoOutboxStatus.Acknowledged, telegramMessageId);

    internal bool MarkUncertain(
        TelegramNotificationLane lane, string eventKey, string? attemptToken) =>
        Finish(lane, eventKey, attemptToken,
            WindowsAmanoOutboxStatus.Uncertain, null);

    private bool Finish(
        TelegramNotificationLane lane, string eventKey, string? token,
        WindowsAmanoOutboxStatus status, long? messageId)
    {
        if (!ValidKey(eventKey) ||
            !Guid.TryParseExact(token, "N", out _) ||
            (status == WindowsAmanoOutboxStatus.Acknowledged &&
             messageId is null or <= 0))
            return false;
        lock (sync)
        {
            if (!Directory.Exists(directory)) return false;
            using var locked = LockState();
            var state = Load();
            var index = state.Items.FindIndex(item =>
                item.Lane == lane && item.EventKey == eventKey &&
                item.Status == WindowsAmanoOutboxStatus.Reserved &&
                item.AttemptToken == token);
            if (index < 0) return false;
            state.Items[index] = state.Items[index] with
            {
                Status = status,
                TelegramMessageId = messageId
            };
            Save(state);
            return true;
        }
    }

    private FileStream LockState()
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Unsafe Telegram outbox directory.");
        return new FileStream(Path.Combine(directory, ".outbox.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private WindowsAmanoOutboxState Load()
    {
        var file = new FileInfo(target);
        if (!file.Exists) return new();
        if (file.Length is <= 0 or > MaxFileBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Invalid Telegram outbox file.");
        using var input = new FileStream(target, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var state = JsonSerializer.Deserialize<WindowsAmanoOutboxState>(input);
        if (state is null || state.SchemaVersion != Schema || state.Items is null ||
            state.Items.Count > MaxItems ||
            state.Items.Any(item =>
                !ValidKey(item.EventKey) ||
                item.Lane is not (TelegramNotificationLane.FiveHourRecovery or
                    TelegramNotificationLane.WeeklyRecovery) ||
                item.ObservedAt == default || item.CreatedAt == default ||
                !Enum.IsDefined(item.Status) ||
                (item.Status == WindowsAmanoOutboxStatus.AwaitingApproval &&
                 (item.AttemptToken is not null || item.TelegramMessageId is not null)) ||
                (item.Status != WindowsAmanoOutboxStatus.AwaitingApproval &&
                 !Guid.TryParseExact(item.AttemptToken, "N", out _)) ||
                (item.Status == WindowsAmanoOutboxStatus.Acknowledged &&
                 item.TelegramMessageId is null or <= 0) ||
                (item.Status != WindowsAmanoOutboxStatus.Acknowledged &&
                 item.TelegramMessageId is not null)) ||
            state.Items.GroupBy(item => (item.Lane, item.EventKey))
                .Any(group => group.Count() > 1))
            throw new InvalidDataException("Invalid Telegram outbox contents.");
        return state;
    }

    private void Save(WindowsAmanoOutboxState state)
    {
        var temp = Path.Combine(directory, "state.json.tmp");
        if (File.Exists(temp)) throw new IOException("Uncertain incomplete prior outbox write.");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            // A prior reservation may have succeeded before the failure;
            // never clear an ambiguous on-disk record and retry a send.
            throw;
        }
    }

    private static bool ValidKey(string? key) =>
        key is { Length: 64 } &&
        key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal enum WindowsAmanoOutboxStatus
{
    AwaitingApproval,
    Reserved,
    Acknowledged,
    Uncertain
}

internal sealed record WindowsAmanoOutboxItem(
    TelegramNotificationLane Lane,
    string EventKey,
    DateTimeOffset ObservedAt,
    DateTimeOffset CreatedAt,
    WindowsAmanoOutboxStatus Status,
    string? AttemptToken,
    long? TelegramMessageId);

internal sealed class WindowsAmanoOutboxState
{
    public int SchemaVersion { get; set; } = 1;
    public List<WindowsAmanoOutboxItem> Items { get; set; } = [];
}


/// <summary>
/// Review-only projection of one unsent, confirmed 5H/7D recovery event.
/// </summary>
public sealed record WindowsTelegramApprovalRow(
    TelegramNotificationLane Lane,
    string EventKey,
    DateTimeOffset ObservedAt)
{
    public string LaneLabel => Lane == TelegramNotificationLane.FiveHourRecovery
        ? "5H · 기존 모니터 봇(묵음)" : "7D · 기존 얼럿 봇";
}
