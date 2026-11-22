using System;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Live granting boundary for one supervised Windows process. The server
/// checks the local recurring opt-in, account, selected existing thread,
/// owner epoch and the already-reserved event. One-shot claim is consumed
/// atomically on the authority BEFORE the native turn/start is invoked.
/// </summary>
public sealed class CodexChatAuthorityDispatchGate :
    ICodexFencedLiveSendGate
{
    private readonly CodexChatSignedAuthorityClient authority;
    private readonly Guid profileId;
    private readonly string accountSha256;
    private readonly string threadId;
    private readonly string ownerDevice;
    private readonly long epoch;
    private bool claimAttempted;

    public CodexChatAuthorityDispatchGate(
        CodexChatSignedAuthorityClient authority,
        Guid profileId,string accountSha256,string threadId,
        string ownerDevice,long epoch)
    {
        this.authority=authority ??
            throw new ArgumentNullException(nameof(authority));
        if(profileId==Guid.Empty ||
           !Guid.TryParseExact(threadId,"D",out _) ||
           accountSha256 is null || accountSha256.Length!=64 ||
           string.IsNullOrWhiteSpace(ownerDevice) || epoch<=0)
            throw new ArgumentException("Pinned dispatch owner required");
        this.profileId=profileId;
        this.accountSha256=accountSha256;
        this.threadId=threadId;
        this.ownerDevice=ownerDevice;
        this.epoch=epoch;
    }

    private bool IsTarget(CodexChatReservationPreview preview) =>
        preview is not null && preview.AccountSha256==accountSha256 &&
        preview.ExistingThreadId==threadId &&
        !preview.RealMessageSendAuthorized;

    public async Task<CodexFencedSendPermission?> RevalidateAsync(
        CodexChatReservationPreview preview,
        CancellationToken cancellationToken)
    {
        if(!IsTarget(preview) || claimAttempted)return null;
        var ready=await authority.GetDispatchReadinessAsync(
            preview.EventKey,accountSha256,threadId,ownerDevice,
            epoch,preview.ResetAtUnix,cancellationToken);
        if(ready.Code!="READY_FOR_SINGLE_USE_CLAIM" ||
           ready.LiveSendAllowed)
            return null;
        return new(preview.EventKey,profileId,accountSha256,threadId,
            preview.ResetAtUnix,ownerDevice,epoch,
            DurableReservationConfirmed:true,AllowLiveTurn:false);
    }

    public async Task<CodexFencedSendPermission?> ClaimOnceAsync(
        CodexChatReservationPreview preview,
        CancellationToken cancellationToken)
    {
        if(!IsTarget(preview) || claimAttempted)return null;
        // Set BEFORE network. If connection drops, do NOT try again.
        claimAttempted=true;
        var response=await authority.ClaimOnceAsync(
            preview.EventKey,profileId,accountSha256,threadId,
            preview.Lane,preview.ResetAtUnix,ownerDevice,epoch,
            cancellationToken);
        if(response is null)return null;
        if(response.EventKey!=preview.EventKey ||
           response.ProfileId!=profileId ||
           response.AccountSha256!=accountSha256 ||
           response.ThreadId!=threadId ||
           response.Lane!=preview.Lane ||
           response.OwnerDevice!=ownerDevice ||
           response.OwnerEpoch!=epoch ||
           response.ResetAtUnix!=preview.ResetAtUnix ||
           response.PermitExpiresAtMs<=
             DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Claim mismatched or expired");
        return new(preview.EventKey,profileId,accountSha256,threadId,
            preview.ResetAtUnix,ownerDevice,epoch,
            DurableReservationConfirmed:true,AllowLiveTurn:true);
    }
}
