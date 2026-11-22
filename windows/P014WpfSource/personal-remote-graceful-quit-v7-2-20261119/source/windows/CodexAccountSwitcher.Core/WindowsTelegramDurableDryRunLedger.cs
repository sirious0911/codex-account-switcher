using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// OFFLINE ONLY: Windows synthetic attempts are claimed on disk with
/// FileMode.CreateNew before fake receipt evaluation. No process, network,
/// credential read, or real notification dispatch exists in this class.
/// This local marker does NOT grant ownership across Windows and Mac.
/// </summary>
public sealed class WindowsTelegramDurableDryRunLedger
{
    private readonly string root;
    private const string ScratchPrefix="codex-switcher-telegram-offline-";

    public WindowsTelegramDurableDryRunLedger(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        var temp=Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchRoot));
        // Require a DIRECT child of OS temp with an explicit synthetic-only
        // name. Never write to LocalAppData, .codex, Program Files, etc.
        if (!string.Equals(Path.GetDirectoryName(root),temp,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(ScratchPrefix,
                StringComparison.Ordinal) ||
            Path.GetFileName(root).Length<=ScratchPrefix.Length)
            throw new ArgumentException(
                "Synthetic ledger must be in a dedicated temporary test folder",
                nameof(scratchRoot));
    }

    public TelegramDurableDryRunInspection SimulateOnce(
        WindowsDualBotDispatchPlanResult? disabledPlan,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? pinnedScripts,
        TelegramNotificationLane lane,
        string? localDeviceId,
        TelegramSharedLeaseWitness? syntheticLease,
        long expectedEpoch,
        TelegramSyntheticOneShotApproval? syntheticApproval,
        DateTimeOffset nowUtc,
        WindowsAlertBotDryRunResult? fakeReply)
    {
        var precheck=WindowsTelegramOneShotDryRunGate.InspectBeforeSyntheticConsume(
            disabledPlan,preferences,pinnedScripts,lane,localDeviceId,
            syntheticLease,expectedEpoch,syntheticApproval,nowUtc);
        if (precheck is not null)
            return new(TelegramDurableDryRunStatus.PreconditionHold,false,precheck);

        var key=disabledPlan!.EventKey!;
        var sub=lane==TelegramNotificationLane.FiveHourRecovery
            ? "five-hour" : "weekly";
        string marker;
        try
        {
            Directory.CreateDirectory(root);
            if (IsReparse(root))
                return new(TelegramDurableDryRunStatus.StorageUntrusted);
            var folder=Path.Combine(root,sub);
            Directory.CreateDirectory(folder);
            if (IsReparse(folder))
                return new(TelegramDurableDryRunStatus.StorageUntrusted);
            marker=Path.Combine(folder,key+".attempt");

            // FileMode.CreateNew gives an atomic, cross-process "only one"
            // claim on the local file system. No delete/overwrite or retry.
            // Even an empty/truncated claim after a crash suppresses replay.
            using var stream=new FileStream(marker,FileMode.CreateNew,
                FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            var json=JsonSerializer.Serialize(new SyntheticClaim(
                1,sub,key,localDeviceId!,expectedEpoch,
                nowUtc.ToUnixTimeMilliseconds(),"MOCK_RESERVED_NO_SEND"));
            stream.Write(Encoding.UTF8.GetBytes(json));
            stream.Flush(flushToDisk:true);
        }
        catch (IOException)
        {
            // Existing claim or uncertain creation/write failure: never
            // auto retry an attempt. No deletion to "repair" a claim.
            return new(TelegramDurableDryRunStatus.AlreadyClaimedOrUncertain);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or
            System.Security.SecurityException or ArgumentException or
            NotSupportedException)
        {
            return new(TelegramDurableDryRunStatus.StorageUntrusted);
        }

        // The persistent claim was sealed first. Re-examine all synthetic
        // prerequisites and atomically consume the in-memory test approval.
        // No sender is invoked: fake reply is merely parsed.
        var simulated=WindowsTelegramOneShotDryRunGate.Simulate(
            disabledPlan,preferences,pinnedScripts,lane,localDeviceId,
            syntheticLease,expectedEpoch,syntheticApproval,nowUtc,fakeReply);
        return simulated.Status==TelegramOneShotDryRunStatus.FakeReceiptMatchesOnly
            ? new(TelegramDurableDryRunStatus.FakeReceiptRecordedOnly,true)
            : new(TelegramDurableDryRunStatus.SimulatedAttemptUncertain,
                simulated.SyntheticApprovalConsumed,simulated.Status);
    }

    private static bool IsReparse(string directory)=>
        (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0;

    private sealed record SyntheticClaim(
        int Version,string Lane,string EventKey,string DeviceId,
        long FencingEpoch,long ObservedAtUnixMs,string Status);
}

public enum TelegramDurableDryRunStatus
{
    PreconditionHold,
    StorageUntrusted,
    AlreadyClaimedOrUncertain,
    FakeReceiptRecordedOnly,
    SimulatedAttemptUncertain
}

public sealed record TelegramDurableDryRunInspection(
    TelegramDurableDryRunStatus Status,
    bool SyntheticApprovalConsumed=false,
    TelegramOneShotDryRunStatus? UnderlyingGateStatus=null)
{
    // Local marker + mock receipt is NOT a cross-host real-send permission.
    public bool ActualTelegramSendAuthorized=>false;
}
