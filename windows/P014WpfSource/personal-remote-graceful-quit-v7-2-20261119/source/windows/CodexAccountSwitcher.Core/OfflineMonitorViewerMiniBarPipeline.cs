namespace CodexAccountSwitcher.Core;

/// <summary>
/// Full in-process, OFFLINE/TEST-ONLY, non-sending simulation of the monitor
/// → active profile gate → V1 viewer cache → existing mini-bar text formatter.
/// This class is NEVER registered with App.xaml.cs, a taskbar HWND, a network
/// listener or an operating user account. Its identity HMAC key is fake test
/// data supplied by an isolated harness. It never authorizes device or sender.
/// </summary>
public sealed class OfflineMonitorViewerMiniBarPipeline
{
    private readonly object guard=new();
    private readonly string sourceDeviceId;
    private readonly OfflineActiveMonitorQuotaViewBridge bridge;

    public OfflineMonitorViewerMiniBarPipeline(
        string sourceDeviceId,string syntheticIdentityKeyHex)
    {
        this.sourceDeviceId=sourceDeviceId;
        bridge=new OfflineActiveMonitorQuotaViewBridge(
            sourceDeviceId,syntheticIdentityKeyHex);
    }

    /// <summary>Blocks and invalidates the prior view before a test switch.</summary>
    public OfflineMonitorViewResult BeginSwitch(long trustedNowUtcMs)
    {
        lock(guard){return bridge.BeginSwitch(trustedNowUtcMs);}
    }

    /// <summary>Reopens a synthetic observation epoch only after the
    /// existing ActiveMonitorPublicationGate accepts the test snapshot.</summary>
    public OfflineMonitorViewResult ObserveSnapshot(
        AccountSnapshot snapshot,long trustedNowUtcMs,
        string syntheticSessionId)
    {
        lock(guard)
        {
            return bridge.ObserveSnapshot(
                snapshot,trustedNowUtcMs,syntheticSessionId);
        }
    }

    /// <summary>Only a V1 cache candidate; cannot update taskbar UI.</summary>
    public OfflineMonitorViewResult TryIngest(
        CodexMonitorWireSample sample,long trustedNowUtcMs)
    {
        lock(guard){return bridge.TryIngest(sample,trustedNowUtcMs);}
    }

    /// <summary>Reads a fresh reviewed V1 cache candidate and formats
    /// the existing taskbar 5H/7D icons and KST tooltips in-memory only.
    /// Never changes app state or calls TaskbarQuotaPanel.Accept.</summary>
    public QuotaViewerMiniBarPreview ReadPreview(
        DateTimeOffset trustedNow)
    {
        lock(guard)
        {
            var reviewed=bridge.ReadView(trustedNow.ToUnixTimeMilliseconds());
            return QuotaViewerMiniBarOfflineAdapter.Project(
                reviewed,sourceDeviceId,trustedNow);
        }
    }

    /// <summary>Pure observer; sender/Telegram authority never returned.</summary>
    public bool LiveUiConnected=>false;
    public bool ActualSendAuthorized=>false;
    public bool RealDeviceAuthenticated=>false;
    public bool RealCrossHostSyncAuthorized=>false;
}
