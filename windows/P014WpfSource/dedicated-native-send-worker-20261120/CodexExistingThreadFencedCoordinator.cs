using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Central-authority permit is distinct from HMAC authentication and an
/// EVENT_RESERVED_NO_SEND receipt. Neither confers permission to send.
/// There is NO production implementation that returns AllowLiveTurn=true.
/// </summary>
public sealed record CodexFencedSendPermission(
    string EventKey,
    Guid ActiveProfileId,
    string AccountSha256,
    string ThreadId,
    long ResetAtUnix,
    string OwnerDevice,
    long OwnerEpoch,
    bool DurableReservationConfirmed,
    bool AllowLiveTurn);

public interface ICodexFencedLiveSendGate
{
    Task<CodexFencedSendPermission?> RevalidateAsync(
        CodexChatReservationPreview preview, CancellationToken cancellationToken);
    Task<CodexFencedSendPermission?> ClaimOnceAsync(
        CodexChatReservationPreview preview, CancellationToken cancellationToken);
}

/// <summary>
/// Installed/isolated production default. Never grants a Codex turn.
/// </summary>
public sealed class CodexSendAlwaysBlockedGate : ICodexFencedLiveSendGate
{
    public Task<CodexFencedSendPermission?> RevalidateAsync(
        CodexChatReservationPreview preview, CancellationToken cancellationToken) =>
        Task.FromResult<CodexFencedSendPermission?>(null);
    public Task<CodexFencedSendPermission?> ClaimOnceAsync(
        CodexChatReservationPreview preview, CancellationToken cancellationToken) =>
        Task.FromResult<CodexFencedSendPermission?>(null);
}

public enum CodexFencedSendState
{
    Blocked,
    Uncertain,
    Accepted
}
public sealed record CodexFencedSendOutcome(
    CodexFencedSendState State, string Reason, string? TurnId = null);

/// <summary>
/// Existing-thread-only Codex turn algorithm. Read/Resume/StartTurn are only
/// reached after a verified account, dedicated chat, and a durable central
/// reservation with fresh exclusive ownership. Never creates a thread.
/// </summary>
public sealed class CodexExistingThreadFencedCoordinator
{
    private const string DedicatedTitle = "Codex 사용량 초기화 전용";
    private readonly ICodexJsonRpcSession native;
    private readonly IExistingThreadClient threadClient;
    private readonly ICodexFencedLiveSendGate gate;
    private readonly Func<ActiveSwitcherIdentity> activeIdentity;

    /// <summary>
    /// Production constructor guarantees native account reads, list/read,
    /// resume and eventual turn/start use the SAME initialized Codex process.
    /// </summary>
    public CodexExistingThreadFencedCoordinator(
        ICodexJsonRpcSession nativeSession,
        ICodexFencedLiveSendGate gate,
        Func<ActiveSwitcherIdentity> activeIdentity)
        : this(nativeSession, new ExistingCodexThreadRpc(nativeSession),
            gate, activeIdentity) { }

    /// <summary>Explicit fake-client injection exists for offline tests.</summary>
    public CodexExistingThreadFencedCoordinator(
        ICodexJsonRpcSession nativeSession,
        IExistingThreadClient existingThreadClient,
        ICodexFencedLiveSendGate gate,
        Func<ActiveSwitcherIdentity> activeIdentity)
    {
        native = nativeSession ?? throw new ArgumentNullException(nameof(nativeSession));
        threadClient = existingThreadClient ?? throw new ArgumentNullException(nameof(existingThreadClient));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.activeIdentity = activeIdentity ?? throw new ArgumentNullException(nameof(activeIdentity));
    }

    public async Task<CodexFencedSendOutcome> SendOneExistingAsync(
        DedicatedChatSettings binding,
        TrustedResetDeadline deadline,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var reason = DedicatedThreadWakeup.Gate(binding,deadline,nowUtc);
        if(reason is not null)
            return new(CodexFencedSendState.Blocked,reason);

        // This stage does NOT have an actual live authority returning true;
        // the installed application always supplies the BLOCKED gate.
        if(binding.ThreadStorage != "desktop-default" ||
           binding.Message != "안녕")
            return new(CodexFencedSendState.Blocked,"DEDICATED_THREAD_OR_TEXT_NOT_APPROVED");
        var current = activeIdentity();
        if(!IsExactlyCurrentAccount(current,binding))
            return new(CodexFencedSendState.Blocked,"ACTIVE_ACCOUNT_NOT_CONFIRMED");
        var preview = CodexChatReservationPlanner.TryCreate(
            deadline, new DedicatedResetPreflightVerdict(
                "EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED",binding),
            nowUtc);
        if(preview is null || preview.RealMessageSendAuthorized)
            return new(CodexFencedSendState.Blocked,"RESET_EVENT_NOT_ELIGIBLE");

        var permission = await gate.RevalidateAsync(preview,cancellationToken);
        if(!ValidPermission(permission,preview,binding,requireClaim:false) ||
           !IsExactlyCurrentAccount(activeIdentity(),binding))
            return new(CodexFencedSendState.Blocked,"TRUSTED_LIVE_SENDER_PERMISSION_MISSING");

        // Before contacting the selected thread, compare native authentication
        // against the active Switcher identity, not a stale account registry.
        if(!await CurrentNativeAuthMatchesAsync(current,binding,cancellationToken))
            return new(CodexFencedSendState.Blocked,"NATIVE_ACCOUNT_MISMATCH");
        var result = await native.CallAsync("thread/list",new {
            currentAccountOnly=true,limit=100
        },cancellationToken);
        if(!ExactlyListedDedicatedThread(result,binding.ExistingThreadId))
            return new(CodexFencedSendState.Blocked,"EXACT_DEDICATED_THREAD_NOT_IN_CURRENT_ACCOUNT");

        // A selected stored chat may be notLoaded but it must become idle
        // after resuming before a user turn can be started.
        var before = await threadClient.ReadAsync(binding.ExistingThreadId,cancellationToken);
        if(before is null || before.ThreadId != binding.ExistingThreadId ||
            !before.IsPersisted || (!before.IsIdle && !before.IsNotLoaded))
            return new(CodexFencedSendState.Blocked,"THREAD_UNSAVED_OR_BUSY");
        await threadClient.ResumeAsync(binding.ExistingThreadId,cancellationToken);
        var after = await threadClient.ReadAsync(binding.ExistingThreadId,cancellationToken);
        if(after is null || after.ThreadId != binding.ExistingThreadId ||
            !after.IsPersisted || !after.IsIdle)
            return new(CodexFencedSendState.Blocked,"THREAD_NOT_IDLE_AFTER_RESUME");

        // Refetch active account + provider currentAccountOnly list after resume.
        // Event source and account MUST remain unchanged.
        if(activeIdentity()!=current ||
            !await CurrentNativeAuthMatchesAsync(current,binding,cancellationToken))
            return new(CodexFencedSendState.Blocked,"ACCOUNT_CHANGED_BEFORE_SEND");
        var finalList=await native.CallAsync("thread/list",new {
            currentAccountOnly=true,limit=100
        },cancellationToken);
        if(!ExactlyListedDedicatedThread(finalList,binding.ExistingThreadId))
            return new(CodexFencedSendState.Blocked,"TARGET_CHANGED_BEFORE_SEND");

        after=await threadClient.ReadAsync(binding.ExistingThreadId,cancellationToken);
        if(after is null || !after.IsPersisted || !after.IsIdle ||
            after.ThreadId!=binding.ExistingThreadId)
            return new(CodexFencedSendState.Blocked,"THREAD_BECAME_BUSY");

        // One-shot claim is made LAST. Its durable SQLite transition
        // happens before turn/start, so even a lost reply cannot be retried.
        // No more auth, resume, listing or thread reads after claiming.
        try
        {
            permission=await gate.ClaimOnceAsync(preview,cancellationToken);
        }
        catch(Exception ex) when(ex is not StackOverflowException
            and not OutOfMemoryException)
        {
            return new(CodexFencedSendState.Uncertain,
                "DISPATCH_CLAIM_OUTCOME_UNCERTAIN_NO_RETRY");
        }
        if(!ValidPermission(permission,preview,binding,requireClaim:true) ||
            activeIdentity()!=current)
            return new(CodexFencedSendState.Uncertain,
                "DISPATCH_CLAIM_NOT_PROVEN_NO_RETRY");

        // A true durable central reservation and exclusive live-send permit
        // must already exist before entering this boundary. There is no
        // automatic retry if this RPC times out or returns uncertain.
        try
        {
            var accepted = await threadClient.StartTurnAsync(
                binding.ExistingThreadId,"안녕",cancellationToken);
            if(!Guid.TryParseExact(accepted,"D",out _))
                return new(CodexFencedSendState.Uncertain,
                    "TURN_OUTCOME_UNCERTAIN_NO_AUTOMATIC_RETRY");
            return new(CodexFencedSendState.Accepted,
                "TURN_ACCEPTED_COMPLETION_NOT_YET_PROVED",accepted);
        }
        catch(Exception ex) when(ex is not StackOverflowException
            and not OutOfMemoryException)
        {
            // Cancellation is also outcome-uncertain: the provider may have
            // accepted turn/start before the caller was interrupted.
            return new(CodexFencedSendState.Uncertain,
                "TURN_OUTCOME_UNCERTAIN_NO_AUTOMATIC_RETRY");
        }
    }

    private static bool ValidPermission(
        CodexFencedSendPermission? grant,
        CodexChatReservationPreview preview,
        DedicatedChatSettings binding,bool requireClaim) =>
        grant is not null &&
        (!requireClaim || grant.AllowLiveTurn) &&
        grant.DurableReservationConfirmed &&
        grant.EventKey==preview.EventKey &&
        grant.ActiveProfileId==binding.ProfileId &&
        grant.AccountSha256==binding.AccountSha256 &&
        grant.ThreadId==binding.ExistingThreadId &&
        grant.ResetAtUnix==preview.ResetAtUnix &&
        !string.IsNullOrWhiteSpace(grant.OwnerDevice) &&
        grant.OwnerEpoch>0;

    private static bool IsExactlyCurrentAccount(
        ActiveSwitcherIdentity current,DedicatedChatSettings binding)
    {
        if(!current.IdentityConfirmed || current.IsSwitching ||
           current.ActiveProfileId!=binding.ProfileId ||
           current.SelectedActiveAccountId!=binding.ProfileId ||
           string.IsNullOrWhiteSpace(current.ProfileEmail))
            return false;
        return VerifiedExistingThreadPicker.FingerprintEmail(
            current.ProfileEmail.Trim().ToLowerInvariant())==binding.AccountSha256;
    }

    private async Task<bool> CurrentNativeAuthMatchesAsync(
        ActiveSwitcherIdentity expected,DedicatedChatSettings binding,
        CancellationToken cancellationToken)
    {
        var json=await native.CallAsync("account/read",
            new{refreshToken=false},cancellationToken);
        if(!json.TryGetProperty("account",out var account) ||
            account.ValueKind!=JsonValueKind.Object ||
            !TryString(account,"type",out var type) ||
            type!="chatgpt" ||
            !TryString(account,"email",out var email))
            return false;
        return string.Equals(email.Trim(),expected.ProfileEmail?.Trim(),
                StringComparison.OrdinalIgnoreCase) &&
            VerifiedExistingThreadPicker.FingerprintEmail(
                email.Trim().ToLowerInvariant())==binding.AccountSha256;
    }

    private static bool ExactlyListedDedicatedThread(JsonElement list,
        string target)
    {
        if(!list.TryGetProperty("data",out var data) ||
           data.ValueKind!=JsonValueKind.Array)
            return false;
        var matches=data.EnumerateArray().Where(t=>
            t.ValueKind==JsonValueKind.Object &&
            TryString(t,"id",out var id) && id==target).ToArray();
        return matches.Length==1 &&
            ((TryString(matches[0],"name",out var name) && name==DedicatedTitle) ||
             (TryString(matches[0],"title",out var title) && title==DedicatedTitle));
    }

    private static bool TryString(JsonElement json,string key,out string value)
    {
        value="";
        if(json.ValueKind!=JsonValueKind.Object ||
           !json.TryGetProperty(key,out var property) ||
           property.ValueKind!=JsonValueKind.String)
            return false;
        value=property.GetString()??"";
        return value.Length>0;
    }
}
