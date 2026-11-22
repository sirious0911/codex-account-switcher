using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// Single Windows-owner orchestration. Never calls native Codex itself.
/// The child is the ONLY process allowed to claim a consumed one-shot grant;
/// parent only acquires a fenced lease, reserves the exact scheduled event,
/// then supervises one worker. The provider's independent completion proof
/// and reconciliation remain server-owned, not a forged client ACK.
/// </summary>
public sealed record CodexWindowsSenderEnrollment(
    string DeviceId,
    string AuthorityUrl,
    string AuthorityCertificatePem,
    string AuthorityCertSha256,
    string AuthorityHmacKeyHex,
    string NativeWorkerExecutable,
    string NativeWorkerSha256,
    bool EnableLiveDispatch = false);

// Host-local handoff only. This is NOT remote sender authorization and is
// unusable without a server-owned, hash-pinned Windows process supervisor.
public sealed record CodexWindowsJobExitEvidence(
    string EventKey,string AccountSha256,string ExistingThreadId,
    string Lane,long ResetAtUnix,string OwnerDevice,long OwnerEpoch,
    string TurnId,bool AllJobProcessesTerminated);
// A pre-reservation is held by the same authority-local owner process that
// will independently attest worker quiescence and settle SQLite. This is NOT
// a remote client claim; readiness revalidates the live owner before turn/start.
public sealed record CodexPreReservedOwnerFence(
    string EventKey,string OwnerDevice,long Epoch,long LeaseExpiresAtMs);
public sealed record CodexOrchestratorResult(
    string Status, bool WorkerStarted, bool ActualTurnProven,
    string? TurnId = null,
    CodexWindowsJobExitEvidence? LocalJobExit = null);

 public interface ICodexWindowsAuthorityTransport
{
    Task<CodexAuthorityLease?> AcquireLeaseAsync(CancellationToken ct);
    Task<string> ReserveAsync(long epoch,CodexChatReservationPreview preview,
        CancellationToken ct);
    Task<CodexDispatchReadiness> CheckReadinessAsync(
        string eventKey,string accountHash,string threadId,string deviceId,
        long epoch,long resetAtUnix,CancellationToken ct);
}
public interface ICodexWindowsSupervisedWorker
{
    bool VerifyWorkerIdentity(string absoluteExe,string expectedSha256);
    Task<WindowsCodexJobSupervisor.Result> StartOnceAsync(
        string absoluteExe,string expectedSha256,string privateJson,
        CancellationToken ct);
}

/// <summary>
/// Adapter requires a previously authenticated TLS/HMAC client. It may NOT
/// issue a Codex turn and cannot take an automatic second attempt.
/// </summary>
public sealed class CodexWindowsAuthorityTransport :
    ICodexWindowsAuthorityTransport
{
    private readonly CodexChatSignedAuthorityClient client;
    public CodexWindowsAuthorityTransport(CodexChatSignedAuthorityClient client)
        => this.client=client ?? throw new ArgumentNullException(nameof(client));

    public Task<CodexAuthorityLease?> AcquireLeaseAsync(CancellationToken ct)
        => client.AcquireSenderLeaseAsync(ct);

    public Task<string> ReserveAsync(long epoch,
        CodexChatReservationPreview preview,CancellationToken ct)
        => client.ReserveResetEventAsync(epoch,preview.EventKey,preview.Lane,
            preview.AccountSha256,preview.ExistingThreadId,
            preview.ResetAtUnix,ct);

    public Task<CodexDispatchReadiness> CheckReadinessAsync(
        string eventKey,string accountHash,string threadId,string deviceId,
        long epoch,long resetAtUnix,CancellationToken ct)
        => client.GetDispatchReadinessAsync(
            eventKey,accountHash,threadId,deviceId,epoch,resetAtUnix,ct);
}
public sealed class CodexWindowsJobWorker : ICodexWindowsSupervisedWorker
{
    private readonly string? reviewedBundleSha256;
    public CodexWindowsJobWorker(string? reviewedBundleSha256=null)
        =>this.reviewedBundleSha256=reviewedBundleSha256;

    public bool VerifyWorkerIdentity(string absoluteExe,string expectedSha256)
    {
        if(string.IsNullOrWhiteSpace(absoluteExe) ||
           !Path.IsPathFullyQualified(absoluteExe) ||
           Path.GetFileName(absoluteExe)!="Codex-Existing-Thread-Fenced-Worker.exe" ||
           !Regex.IsMatch(expectedSha256??"","^[a-f0-9]{64}$"))
            return false;
        try {
            if(string.IsNullOrWhiteSpace(reviewedBundleSha256))
                return false;
            using var lockedBundle=WindowsReviewedDotNetBundle.Acquire(
                absoluteExe,reviewedBundleSha256);
            var file=new FileInfo(absoluteExe);
            if(!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            using var input=new FileStream(file.FullName,FileMode.Open,
                FileAccess.Read,FileShare.Read);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(input),Convert.FromHexString(expectedSha256!));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException
            or System.Security.SecurityException) {
            return false;
        }
    }
    public async Task<WindowsCodexJobSupervisor.Result> StartOnceAsync(
        string absoluteExe,string expectedSha256,string privateJson,
        CancellationToken ct)
    {
        using var supervisor=new WindowsCodexJobSupervisor();
        return await supervisor.RunApprovedWorkerAsync(
            absoluteExe,expectedSha256,privateJson,
            TimeSpan.FromSeconds(35),ct,reviewedBundleSha256);
    }
}

public sealed class CodexWindowsResetOrchestrator
{
    private readonly ICodexWindowsAuthorityTransport authority;
    private readonly ICodexWindowsSupervisedWorker worker;
    private readonly Func<ActiveSwitcherIdentity> currentIdentity;
    private readonly SemaphoreSlim singleEvent=new(1,1);
    private bool disabled;
    private CancellationTokenSource? activeDispatch;

    public CodexWindowsResetOrchestrator(
        ICodexWindowsAuthorityTransport authority,
        ICodexWindowsSupervisedWorker worker,
        Func<ActiveSwitcherIdentity> currentIdentity)
    {
        this.authority=authority??throw new ArgumentNullException(nameof(authority));
        this.worker=worker??throw new ArgumentNullException(nameof(worker));
        this.currentIdentity=currentIdentity??
            throw new ArgumentNullException(nameof(currentIdentity));
    }

    public void Disable()
    {
        Volatile.Write(ref disabled,true);
        try { Volatile.Read(ref activeDispatch)?.Cancel(); }
        catch(ObjectDisposedException) { }
    }

    public async Task<CodexOrchestratorResult> ProcessDueAsync(
        DedicatedChatSettings binding,TrustedResetDeadline deadline,
        CodexWindowsSenderEnrollment enrollment,
        DateTimeOffset nowUtc,CancellationToken ct=default,
        CodexPreReservedOwnerFence? preReserved=null)
    {
        if(disabled || enrollment is null || !enrollment.EnableLiveDispatch)
            return new("HOLD_PRODUCTION_DISPATCH_OFF",false,false);
        var denial=DedicatedThreadWakeup.Gate(binding,deadline,nowUtc);
        if(denial is not null)
            return new(denial,false,false);
        if(binding.ThreadStorage!="desktop-default" || binding.Message!="안녕" ||
           !Guid.TryParseExact(binding.ExistingThreadId,"D",out _) ||
           !MatchesActive(binding,currentIdentity()))
            return new("HOLD_ACCOUNT_OR_EXISTING_THREAD",false,false);

        var preview=CodexChatReservationPlanner.TryCreate(deadline,
            new DedicatedResetPreflightVerdict(
                "EXCLUSIVE_SENDER_AND_DISPATCH_NOT_AUTHORIZED",binding),nowUtc);
        if(preview is null || preview.RealMessageSendAuthorized)
            return new("HOLD_RESET_EVENT_NOT_TRUSTED",false,false);

        // Contention is intentionally single-shot; do not queue a retry.
        if(!await singleEvent.WaitAsync(0,ct))
            return new("HOLD_DISPATCH_ALREADY_IN_PROGRESS",false,false);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref activeDispatch,linked);
        var workerStarted=false;
        try
        {
            if(disabled)linked.Cancel();
            if(disabled || !MatchesActive(binding,currentIdentity()))
                return new("HOLD_ACCOUNT_CHANGED",false,false);
            // Avoid a permanent central reservation when the worker is
            // missing or has been replaced: verify before reserving.
            if(!worker.VerifyWorkerIdentity(enrollment.NativeWorkerExecutable,
                enrollment.NativeWorkerSha256))
                return new("HOLD_WORKER_IDENTITY_BEFORE_RESERVATION",false,false);
            long epoch;
            if(preReserved is null)
            {
                var lease=await authority.AcquireLeaseAsync(linked.Token);
                if(lease is null || lease.Epoch<=0 ||
                   lease.ExpiresAtMs<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+5000)
                    return new("HOLD_AUTHORITY_LEASE_UNAVAILABLE",false,false);
                epoch=lease.Epoch;
                var reserve=await authority.ReserveAsync(epoch,preview,linked.Token);
                if(reserve!="EVENT_RESERVED_NO_SEND")
                    return new("HOLD_CENTRAL_RESERVATION_"+reserve,false,false);
            }
            else
            {
                // An authority-owned Node process already acquired and
                // reserved this exact reset. Never acquire a second lease.
                // Readiness below independently verifies the live owner.
                if(preReserved.EventKey!=preview.EventKey ||
                   preReserved.OwnerDevice!=enrollment.DeviceId ||
                   preReserved.Epoch<=0 ||
                   preReserved.LeaseExpiresAtMs <=
                       DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+5000)
                    return new("HOLD_PRE_RESERVED_FENCE_INVALID",false,false);
                epoch=preReserved.Epoch;
            }

            var ready=await authority.CheckReadinessAsync(
                preview.EventKey,preview.AccountSha256,
                preview.ExistingThreadId,enrollment.DeviceId,
                epoch,preview.ResetAtUnix,linked.Token);
            if(ready.Code!="READY_FOR_SINGLE_USE_CLAIM" ||
               ready.LiveSendAllowed)
                return new("HOLD_LIVE_DISPATCH_APPROVAL_UNAVAILABLE",false,false);
            if(disabled || !MatchesActive(binding,currentIdentity()))
                return new("HOLD_ACCOUNT_CHANGED_AFTER_RESERVE",false,false);

            // Secrets travel by private supervised stdin only. They are
            // neither written to disk nor embedded in command line args.
            var input=JsonSerializer.Serialize(new {
                ProfileId=binding.ProfileId,
                AccountSha256=binding.AccountSha256,
                ThreadId=binding.ExistingThreadId,
                Lane=preview.Lane,EventKey=preview.EventKey,
                ResetAtUnix=preview.ResetAtUnix,
                ObservedAtUnix=deadline.LastTrustedObservationUtc.ToUnixTimeSeconds(),
                OwnerDevice=enrollment.DeviceId,Epoch=epoch,
                AuthorityUrl=enrollment.AuthorityUrl,
                AuthorityCertPem=enrollment.AuthorityCertificatePem,
                AuthorityCertSha256=enrollment.AuthorityCertSha256,
                AuthorityHmacKeyHex=enrollment.AuthorityHmacKeyHex,
                RemoteEndpointApproved=false
            });
            workerStarted=true;
            var result=await worker.StartOnceAsync(
                enrollment.NativeWorkerExecutable,
                enrollment.NativeWorkerSha256,input,linked.Token);
            if(!result.AllJobProcessesTerminated ||
               result.OutcomeUncertain || result.ExitCode!=0)
                return new("HOLD_WORKER_RESULT_UNCERTAIN_NO_RETRY",true,false);
            using var json=JsonDocument.Parse(result.Stdout);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("state",out var status) ||
               status.GetString()!="Accepted" ||
               !root.TryGetProperty("turnId",out var turn) ||
               turn.ValueKind!=JsonValueKind.String ||
               !Guid.TryParseExact(turn.GetString(),"D",out _))
                return new("HOLD_PROVIDER_RESULT_NOT_ACCEPTED_NO_RETRY",true,false);
            // Never claim the provider completed simply because turn/start
            // accepted. Server-side reconciliation is STILL REQUIRED.
            // Only this host-local supervisor knows that its Windows Job
            // Object has no active worker/descendants. The authority must
            // STILL independently verify native completion before settlement.
            var exactTurnId=turn.GetString()!;
            var localExit=new CodexWindowsJobExitEvidence(
                preview.EventKey,preview.AccountSha256,
                preview.ExistingThreadId,preview.Lane,preview.ResetAtUnix,
                enrollment.DeviceId,epoch,exactTurnId,
                result.AllJobProcessesTerminated);
            return new("TURN_START_ACCEPTED_AWAIT_PROVIDER_VERIFICATION",
                true,false,exactTurnId,localExit);
        }
        catch(Exception ex) when(ex is not OutOfMemoryException
            and not StackOverflowException)
        {
            // A network failure may happen after the authority consumed a
            // reservation or after Codex accepted a turn. Never auto-retry.
            return new("HOLD_UNCERTAIN_NO_AUTOMATIC_RETRY",workerStarted,false);
        }
        finally
        {
            Volatile.Write(ref activeDispatch,null);
            singleEvent.Release();
        }
    }

    private static bool MatchesActive(
        DedicatedChatSettings binding,ActiveSwitcherIdentity identity)
    {
        if(!identity.IdentityConfirmed||identity.IsSwitching||
           identity.ActiveProfileId!=binding.ProfileId||
           identity.SelectedActiveAccountId!=binding.ProfileId||
           string.IsNullOrWhiteSpace(identity.ProfileEmail))
            return false;
        return VerifiedExistingThreadPicker.FingerprintEmail(
            identity.ProfileEmail.Trim().ToLowerInvariant())==
            binding.AccountSha256;
    }
}
