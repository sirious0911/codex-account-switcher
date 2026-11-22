using System.Diagnostics;
using System.Threading;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// A synthetic, deliberately non-transmitting approval for exercising the
/// future single-send boundary in memory. This object is NOT an authorization
/// credential; even a successful test receipt cannot enable a real send.
/// </summary>
public sealed class TelegramSyntheticOneShotApproval
{
    private int consumed;

    public TelegramNotificationLane Lane { get; }
    public string EventKey { get; }
    public string DeviceId { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    private TelegramSyntheticOneShotApproval(
        TelegramNotificationLane lane, string key, string device,
        DateTimeOffset expiresUtc)
    {
        Lane=lane;
        EventKey=key;
        DeviceId=device;
        ExpiresAtUtc=expiresUtc;
    }

    // Explicitly named for synthetic tests. Never instantiate this from a
    // production setting, observed quota change, or user notification toggle.
    public static TelegramSyntheticOneShotApproval ForOfflineTest(
        TelegramNotificationLane lane, string key, string deviceId,
        DateTimeOffset expiresAtUtc)
        => new(lane,key,deviceId,expiresAtUtc);

    internal bool TryConsume() =>
        Interlocked.CompareExchange(ref consumed,1,0)==0;

    public bool IsConsumed => Volatile.Read(ref consumed)!=0;
}

/// <summary>
/// Pure orchestration of a single mock Telegram attempt.
/// No process launch, token read, HTTP traffic or durable outbox mutation.
/// Windows and Mac shared lease authority remains unimplemented.
/// </summary>
public static class WindowsTelegramOneShotDryRunGate
{
    public static TelegramOneShotDryRunInspection Simulate(
        WindowsDualBotDispatchPlanResult? disabledPlan,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? pinnedScripts,
        TelegramNotificationLane lane,
        string? localDeviceId,
        TelegramSharedLeaseWitness? syntheticLease,
        long expectedEpoch,
        TelegramSyntheticOneShotApproval? syntheticApproval,
        DateTimeOffset nowUtc,
        WindowsAlertBotDryRunResult? fakeProcessReply)
    {
        var status=InspectBeforeSyntheticConsume(
            disabledPlan,preferences,pinnedScripts,lane,localDeviceId,
            syntheticLease,expectedEpoch,syntheticApproval,nowUtc);
        if (status is not null) return Result(status.Value);
        if (!syntheticApproval!.TryConsume())
            return Result(TelegramOneShotDryRunStatus.SyntheticApprovalAlreadyUsed);

        // After this point a single real request would have been attempted.
        // Neither uncertainty nor malformed receipts allow re-use of consent.
        // Critically, there is NO call to Process.Start or SendAsync here.
        if (fakeProcessReply is null)
            return Result(TelegramOneShotDryRunStatus.SimulatedAttemptUncertain,
                consumed:true);

        return disabledPlan!.MatchesFakeReceipt(fakeProcessReply)
            ? Result(TelegramOneShotDryRunStatus.FakeReceiptMatchesOnly,consumed:true)
            : Result(TelegramOneShotDryRunStatus.SimulatedAttemptUncertain,
                consumed:true);
    }

    // Exposed only to another *offline* coordinator. No result from this
    // method is a sender approval or an authority attestation.
    internal static TelegramOneShotDryRunStatus? InspectBeforeSyntheticConsume(
        WindowsDualBotDispatchPlanResult? disabledPlan,
        TelegramNotificationPreferences? preferences,
        WindowsDualBotLocalGateResult? pinnedScripts,
        TelegramNotificationLane lane,
        string? localDeviceId,
        TelegramSharedLeaseWitness? syntheticLease,
        long expectedEpoch,
        TelegramSyntheticOneShotApproval? syntheticApproval,
        DateTimeOffset nowUtc)
    {
        if (!OperatingSystem.IsWindows())
            return TelegramOneShotDryRunStatus.UnsupportedPlatform;
        if (nowUtc.Offset!=TimeSpan.Zero)
            return TelegramOneShotDryRunStatus.InvalidAuthorityTime;
        if (TelegramSenderDispatchPolicy.Inspect(preferences,lane).Reason!=
            TelegramDispatchHoldReason.SingleSenderLeaseUnverified)
            return TelegramOneShotDryRunStatus.NotRequestedByLocalSettings;
        if (pinnedScripts?.BothLocalArtifactsVerified!=true)
            return TelegramOneShotDryRunStatus.PinnedScriptPreflightNotMet;
        if (!ValidDisabledPlan(disabledPlan,lane))
            return TelegramOneShotDryRunStatus.DisabledPlanNotVerified;
        var ownership=TelegramCrossHostHandoffReview.Inspect(
            preferences,lane,localDeviceId,syntheticLease,expectedEpoch,nowUtc);
        if (!ownership.WouldPassSharedLeaseSimulation)
            return TelegramOneShotDryRunStatus.SyntheticOwnerNotVerified;
        if (syntheticApproval is null ||
            syntheticApproval.Lane!=lane ||
            !string.Equals(syntheticApproval.EventKey,disabledPlan!.EventKey,
                StringComparison.Ordinal) ||
            !string.Equals(syntheticApproval.DeviceId,localDeviceId,
                StringComparison.Ordinal) ||
            syntheticApproval.ExpiresAtUtc.Offset!=TimeSpan.Zero ||
            syntheticApproval.ExpiresAtUtc<=nowUtc ||
            syntheticApproval.ExpiresAtUtc-nowUtc>TimeSpan.FromMinutes(2))
            return TelegramOneShotDryRunStatus.SyntheticApprovalInvalid;
        if (syntheticApproval.IsConsumed)
            return TelegramOneShotDryRunStatus.SyntheticApprovalAlreadyUsed;
        return null;
    }

    private static TelegramOneShotDryRunInspection Result(
        TelegramOneShotDryRunStatus status,bool consumed=false) =>
        new(status,consumed);

    private static bool ValidDisabledPlan(
        WindowsDualBotDispatchPlanResult? plan,TelegramNotificationLane lane)
    {
        if (plan is null ||
            plan.Status!=WindowsDispatchPlanStatus.OfflinePlanOnly ||
            plan.ActualTelegramDeliveryAuthorized ||
            plan.Lane!=lane ||
            plan.EventKey is not { Length:64 } key ||
            !key.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            plan.DisabledCommand is not { } command ||
            command.UseShellExecute || !command.CreateNoWindow ||
            !command.RedirectStandardOutput || !command.RedirectStandardError ||
            command.ArgumentList.Count!=10 ||
            command.ArgumentList[0]!="-NoProfile" ||
            command.ArgumentList[1]!="-NonInteractive" ||
            command.ArgumentList[2]!="-File" ||
            command.ArgumentList[4]!="-LegacyBotDirectory" ||
            command.ArgumentList[6]!="-EventKey" ||
            command.ArgumentList[7]!=key ||
            command.ArgumentList[8]!="-Message" ||
            command.ArgumentList[9].Length>250 ||
            command.ArgumentList[9].Length<10 ||
            command.ArgumentList.Contains("-ApprovedSingleSend") ||
            command.ArgumentList.Contains("-WindowsSenderVerified"))
            return false;

        string script=lane==TelegramNotificationLane.FiveHourRecovery
            ? "send-fivehour100-existing-monitor-bot.ps1"
            : "send-weekly100-existing-alert-bot.ps1";
        return string.Equals(
            Path.GetFileName(command.ArgumentList[3]),script,
            StringComparison.OrdinalIgnoreCase);
    }
}

public enum TelegramOneShotDryRunStatus
{
    UnsupportedPlatform,
    InvalidAuthorityTime,
    NotRequestedByLocalSettings,
    PinnedScriptPreflightNotMet,
    DisabledPlanNotVerified,
    SyntheticOwnerNotVerified,
    SyntheticApprovalInvalid,
    SyntheticApprovalAlreadyUsed,
    FakeReceiptMatchesOnly,
    SimulatedAttemptUncertain
}

public sealed record TelegramOneShotDryRunInspection(
    TelegramOneShotDryRunStatus Status,bool SyntheticApprovalConsumed)
{
    /// <summary>
    /// No caller may promote fake receipts or user preferences into real send.
    /// </summary>
    public bool ActualTelegramSendAuthorized=>false;
}
