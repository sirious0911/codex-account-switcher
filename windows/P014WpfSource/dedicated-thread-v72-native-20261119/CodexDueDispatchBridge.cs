using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// The running UI's trusted reset observer is the ONLY entry for due events.
/// The bridge never infers deadlines from draft flags, and does not own a Codex
/// process. An injected authoritative transport is required. It must complete
/// an OS-supervised turn + independent provider reconciliation server-side.
/// Production passes null until a separately validated owner is installed.
/// </summary>
public sealed class CodexDueDispatchBridge
{
    private readonly Func<ActiveSwitcherIdentity> currentIdentity;
    private readonly Func<DedicatedChatSettings, CodexAutoSendDraft?> readDraft;
    private readonly Func<bool> exclusiveSenderReady;
    private readonly Func<TrustedResetDeadline,DedicatedChatSettings,
        CancellationToken,Task<string>>? sendThroughAuthority;
    private readonly HashSet<string> attempted = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1,1);
    private bool disabled;

    public CodexDueDispatchBridge(
        Func<ActiveSwitcherIdentity> currentIdentity,
        Func<DedicatedChatSettings,CodexAutoSendDraft?> readDraft,
        Func<bool> exclusiveSenderReady,
        Func<TrustedResetDeadline,DedicatedChatSettings,
            CancellationToken,Task<string>>? sendThroughAuthority = null)
    {
        this.currentIdentity=currentIdentity ??
            throw new ArgumentNullException(nameof(currentIdentity));
        this.readDraft=readDraft ??
            throw new ArgumentNullException(nameof(readDraft));
        this.exclusiveSenderReady=exclusiveSenderReady ??
            throw new ArgumentNullException(nameof(exclusiveSenderReady));
        this.sendThroughAuthority=sendThroughAuthority;
    }

    public void Disable() => Volatile.Write(ref disabled,true);

    public async Task<string> OnTrustedDueAsync(
        TrustedResetDeadline due,DedicatedChatSettings? bound,
        DateTimeOffset now,CancellationToken ct=default)
    {
        if(disabled)return "HOLD_DISPATCH_DISABLED";
        if(due is null || bound is null || due.ActiveProfileId==Guid.Empty ||
           !due.TrustedSource || !due.ActiveIdentityConfirmed ||
           !due.OrdinaryUsageAllowed ||
           due.ActiveProfileId!=bound.ProfileId ||
           due.ActiveAccountSha256!=bound.AccountSha256 ||
           bound.ThreadStorage!="desktop-default" ||
           bound.Message!="안녕" ||
           !Guid.TryParseExact(bound.ExistingThreadId,"D",out _) ||
           now<due.PreviousResetAtUtc ||
           now-due.PreviousResetAtUtc>TimeSpan.FromMinutes(10) ||
           due.LastTrustedObservationUtc>now.AddSeconds(30) ||
           now-due.LastTrustedObservationUtc>TimeSpan.FromMinutes(6) ||
           due.WindowDurationMinutes !=
             (due.Lane==LimitLane.FiveHour?300:10080))
            return "HOLD_UNTRUSTED_DUE_OR_THREAD";

        var identity=currentIdentity();
        if(!ExactCurrentIdentity(identity,bound))
            return "HOLD_ACCOUNT_CHANGED";

        CodexAutoSendDraft? draft;
        try { draft=readDraft(bound); }
        catch(Exception ex) when(ex is not OutOfMemoryException
            and not StackOverflowException)
        { return "HOLD_PREFERENCES_NOT_VERIFIED"; }
        if(draft is null || draft.ProfileId!=bound.ProfileId ||
           draft.AccountSha256!=bound.AccountSha256 ||
           draft.ExistingThreadId!=bound.ExistingThreadId ||
           !draft.WantsLane(due.Lane))
            return "HOLD_USER_SWITCH_OFF";

        // Preferred controls are NOT the same as operational permission.
        // Existing binding flags remain OFF until independently activated.
        if(!bound.AutomaticMessagesEnabled ||
           (due.Lane==LimitLane.FiveHour && !bound.StartOnFiveHour) ||
           (due.Lane==LimitLane.SevenDay && !bound.StartOnSevenDay) ||
           !exclusiveSenderReady() || sendThroughAuthority is null)
            return "HOLD_SENDER_NOT_ENROLLED";

        if(!await gate.WaitAsync(0,ct))
            return "HOLD_SENDER_BUSY";
        try
        {
            if(disabled || !ExactCurrentIdentity(currentIdentity(),bound))
                return "HOLD_ACCOUNT_CHANGED";
            if(!exclusiveSenderReady())
                return "HOLD_SENDER_NOT_ENROLLED";

            var key=string.Join("|",bound.ProfileId,bound.AccountSha256,
                bound.ExistingThreadId,due.Lane,
                due.PreviousResetAtUtc.ToUnixTimeSeconds());
            // Mark before touching external transport: a lost reply will NEVER
            // cause an automatic second Codex turn in this process.
            if(!attempted.Add(key))
                return "HOLD_ALREADY_ATTEMPTED";
            try
            {
                var result=await sendThroughAuthority(due,bound,ct);
                return string.IsNullOrWhiteSpace(result)
                    ? "HOLD_SENDER_UNCERTAIN_NO_RETRY" : result;
            }
            catch(Exception ex) when(ex is not OutOfMemoryException
                and not StackOverflowException)
            { return "HOLD_SENDER_UNCERTAIN_NO_RETRY"; }
        }
        finally {gate.Release();}
    }

    private static bool ExactCurrentIdentity(
        ActiveSwitcherIdentity id,DedicatedChatSettings binding)
    {
        if(!id.IdentityConfirmed || id.IsSwitching ||
           id.ActiveProfileId!=binding.ProfileId ||
           id.SelectedActiveAccountId!=binding.ProfileId ||
           string.IsNullOrWhiteSpace(id.ProfileEmail))
            return false;
        var fingerprint=VerifiedExistingThreadPicker.FingerprintEmail(
            id.ProfileEmail.Trim().ToLowerInvariant());
        return fingerprint==binding.AccountSha256;
    }
}
