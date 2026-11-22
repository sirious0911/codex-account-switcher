using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

/// <summary>
/// Explicit synthetic-only V2 startup injection for non-operating tests.
/// The public App constructors never provide this value, and no environment
/// variable, saved preference or command line option can enable it.
/// </summary>
internal sealed record P3V2SyntheticStartupEnrollment(
    Uri Origin, string SourceDeviceID,
    Func<CancellationToken, ValueTask<string?>> CredentialProvider,
    HttpMessageHandler SyntheticTransport);

public partial class App : Application
{
    public static Uri IconUri { get; } = new("pack://application:,,,/Codex-Account-Switcher-windows-x64;component/app.ico");
    private readonly bool startRuntime;
    public App() : this(true) { }
    public App(bool startRuntime) { this.startRuntime = startRuntime; }
    internal App(bool startRuntime, P3V2SyntheticStartupEnrollment enrollment)
        : this(startRuntime)
    {
        // Tests can only supply an in-memory HTTP handler. There is no
        // production/real-network startup enrollment entry point.
        syntheticV2Enrollment = enrollment ??
            throw new ArgumentNullException(nameof(enrollment));
    }
    private readonly P3V2SyntheticStartupEnrollment? syntheticV2Enrollment;
    private QuotaRelayV2HttpsPublisher? syntheticV2Publisher;
    private VerifiedQuotaV2PublishRuntimeAdapter? syntheticV2Runtime;
    private Mutex? instance;
    private bool ownsMutex;
    private EventWaitHandle? activateWindow;
    private RegisteredWaitHandle? activationListener;
    private RemoteGracefulQuitChannel? remoteQuitChannel;
    private bool remoteQuitPending;
    private TaskbarQuotaPanel? taskbarPanel;
    private AdaptiveUsageRefreshService? adaptiveUsage;
    private CoreClient? client;
    private LocalMonitorWorker? monitorWorker;
    private Weekly100RecoveryWorker? weekly100Worker;
    private FiveHour100RecoveryWorker? fiveHour100Worker;
    private WindowsAmanoProductionOutbox? telegramOutbox;
    // Per-user DPAPI request for future restart recovery. Never auto-arms.
    private WindowsTelegramRestartIntentStore? restartUserIntent;
    private WindowsTelegramStickyAutoConsentStore? stickyAutoConsent;
    private bool senderInitialized;
    private WindowsTelegramLocalSenderLease? localTelegramSender;
    private readonly WindowsTelegramAutoSendSession autoTelegramSession = new();
    private WindowsTelegramAutoRecoveryDispatcher? autoTelegramDispatcher;
    private int telegramSendActivities;
    private NativeSettings? nativeSettings;
    private string? bundledTelegramHostPath;
    private string existingAmanoBotRouteStatus =
        "기존 주차대행 Telegram 봇: 복구 이벤트 대기 · 실제 발송 차단";
    // Only the confirmed active profile feeds a WPF child HWND embedded
    // before TrayNotifyWnd, immediately to the left of the ^ chevron.
    private readonly ActiveMonitorPublicationGate externalMonitorGate = new();
    private Guid? lastOtherQuotaProfile;
    private DateTimeOffset? lastOtherQuotaReceipt;
    private MainWindow? window;
    private DispatcherTimer? updates;
    private bool exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!startRuntime) return;
        if (e.Args.Contains("--self-test")) { await SelfTestAsync(); return; }
        try {
            var dataPath = Environment.GetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher Personal");
            // Remote quit CLI exits without creating an app, profile, mutex,
            // Telegram sender or WPF taskbar host. Ignore malformed extra args.
            if (e.Args.Contains("--request-quit", StringComparer.Ordinal))
            {
                var status = e.Args.Length == 1
                    ? await RemoteGracefulQuitChannel.RequestExitAsync(dataPath)
                    : 12;
                Shutdown(status);
                return;
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath).ToUpperInvariant())));
            instance = new Mutex(true, @"Local\CodexAccountSwitcher-" + key, out ownsMutex);
            activateWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexAccountSwitcher-Activate-" + key);
            if (!ownsMutex) { activateWindow.Set(); Shutdown(); return; }
            ApplyTheme();
            var hostPath = HostRuntime.Resolve(Assembly.GetExecutingAssembly());
            var bundledBotScripts = WindowsPackagedTelegramScripts.Inspect(hostPath);
            bundledTelegramHostPath = hostPath;
            client = new CoreClient(hostPath, dataPath);
            // Default OFF: App() and App(bool) do not supply an enrollment.
            // Only an explicit internal synthetic test constructor creates a
            // V2 sender. Never read credentials or transmit in normal startup.
            if (syntheticV2Enrollment is { } testEnrollment)
            {
                syntheticV2Publisher = new QuotaRelayV2HttpsPublisher(
                    testEnrollment.Origin, testEnrollment.SourceDeviceID,
                    testEnrollment.CredentialProvider, testEnrollment.SyntheticTransport);
                syntheticV2Runtime = new VerifiedQuotaV2PublishRuntimeAdapter(
                    new CoreClientQuotaEventAdapter(client),
                    testEnrollment.SourceDeviceID, syntheticV2Publisher);
            }
            monitorWorker = new LocalMonitorWorker(dataPath);
            weekly100Worker = new Weekly100RecoveryWorker(dataPath);
            fiveHour100Worker = new FiveHour100RecoveryWorker(dataPath);
            telegramOutbox = new WindowsAmanoProductionOutbox(dataPath);
            restartUserIntent = new WindowsTelegramRestartIntentStore(dataPath);
            try { stickyAutoConsent = new WindowsTelegramStickyAutoConsentStore(dataPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or System.Security.Cryptography.CryptographicException
                or System.Security.SecurityException)
            {
                // Optional sticky consent failure must never kill the app or arm Telegram.
                System.Diagnostics.Trace.TraceWarning(
                    "Persistent Telegram consent unavailable: " + error.GetType().Name);
            }
            autoTelegramDispatcher = new WindowsTelegramAutoRecoveryDispatcher(
                autoTelegramSession, telegramOutbox,
                WindowsExistingAmanoBotProcessAdapter.CreateWindowsTransport());
            client.MonitorObserved += OnMonitorObserved;
            client.SwitchRequested += OnSwitchRequested;
            var native = new NativeSettings();
            nativeSettings = native;
            try {
                localTelegramSender = new WindowsTelegramLocalSenderLease(dataPath);
                RefreshLocalTelegramSender();
                native.TelegramRoutingChanged += RefreshLocalTelegramSender;
            } catch (Exception leaseError) when (leaseError is UnauthorizedAccessException
                or IOException or WaitHandleCannotBeOpenedException) {
                // Sender is optional; failure must not prevent account switching.
                // A missing local lease is never a send permit.
                System.Diagnostics.Trace.TraceWarning(
                    "Local Telegram sender lease unavailable: " + leaseError.GetType().Name);
            }
            window = new MainWindow(client, native, () => monitorWorker?.ReadHistory() ?? [],
                () => OfflineTelegramRecoveryInbox.Read(dataPath),
                () => localTelegramSender?.Display ?? "Windows 로컬 Sender: 확인 불가 · 실제 발송 차단",
                () => bundledBotScripts.Display,
                () => existingAmanoBotRouteStatus,
                () => telegramOutbox?.ReadDisplay() ??
                    "운영 알림 대기함: 확인 불가 · 실제 발송 차단",
                ReadPendingTelegramApprovalRows,
                SendApprovedTelegramOnceAsync,
                QueueTelegramConnectivityTest,
                () => autoTelegramSession.Armed,
                ChangeAutomaticTelegramSession,
                StopWindowsTelegramSenderForHandoff,
                ProbeSharedAuthorityStatusReadOnlyAsync,
                () => restartUserIntent?.Read() ??
                    WindowsTelegramRestartIntentStatus.InvalidOrUnreadable,
                desired => restartUserIntent?.ChangeByUser(
                    desired, DateTimeOffset.UtcNow, explicitConfirmation: true) == true,
                DescribeRestartReadiness,
                () => stickyAutoConsent?.IsEnabledFor(
                    native.TelegramPreferences, native.TelegramBotDirectory) == true);
            MainWindow = window;
            activationListener = ThreadPool.RegisterWaitForSingleObject(activateWindow, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => { if (!exiting) { ApplyTheme(); window.OpenWindow(); } })),
                null, Timeout.Infinite, false);
            taskbarPanel = new TaskbarQuotaPanel(
                () => { ApplyTheme(); window.OpenWindow(); },
                () => _ = QuitAsync(),
                () => !window.IsBusy, native);
            adaptiveUsage = new AdaptiveUsageRefreshService(client);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            client.Changed += OnClientChanged;
            await client.InitializeAsync();
            TryRestoreWindowsOnlyTelegramConsent();
            remoteQuitChannel = new RemoteGracefulQuitChannel(dataPath,
                Dispatcher, RemoteQuitReady, RemoteQuitReserve,
                () => remoteQuitPending = false,
                () => _ = QuitAsync());
            remoteQuitChannel.Start();
            taskbarPanel?.Start();
            adaptiveUsage?.Start();
            if (!e.Args.Contains("--background")) window.OpenWindow();
            updates = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            updates.Tick += async (_, _) => { if (native.AutomaticallyCheckUpdates) await native.CheckUpdatesAsync(); };
            updates.Start();
            if (native.AutomaticallyCheckUpdates) _ = native.CheckUpdatesAsync();
        } catch (Exception ex) {
            MessageBox.Show(ex.Message, "Codex Account Switcher Personal", MessageBoxButton.OK, MessageBoxImage.Error);
            await QuitAsync();
        }
    }

    private string DescribeRestartReadiness()
    {
        // Local-only preflight for the user's settings page. Do not query
        // a server or arm Telegram on application startup or screen render.
        IReadOnlyList<WindowsAmanoOutboxItem>? items = null;
        try { items = telegramOutbox?.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException
            or System.Security.SecurityException)
        {
            // A broken or locked outbox is a HOLD, never an empty outbox.
        }
        return WindowsTelegramRestartReadinessGate.Inspect(
            restartUserIntent?.Read() ??
                WindowsTelegramRestartIntentStatus.InvalidOrUnreadable,
            nativeSettings?.TelegramPreferences,
            localTelegramSender?.State ??
                WindowsLocalSenderLeaseState.LocalClaimUnavailable,
            items,
            durableBoundEnrollment: null,
            installationId: Guid.Empty,
            userBinding: null,
            authorityBinding: null,
            authorityStatus: null,
            enrolledDeviceId: null,
            nowUtc: DateTimeOffset.UtcNow).Description;
    }

    private void RefreshLocalTelegramSender()
    {
        if (exiting || nativeSettings is null || localTelegramSender is null)
            return;
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.BeginInvoke(new Action(RefreshLocalTelegramSender));
            return;
        }
        // Only Telegram preference or bot-directory changes revoke session
        // consent. NativeSettings.Changed also announces update-check UI
        // progress; that must never silently cancel an armed session.
        autoTelegramSession.Disarm();
        // Any explicit sender/bot preference change revokes the saved local-only
        // consent. The first startup update is not a preference change.
        if (senderInitialized)
            stickyAutoConsent?.SaveFromExplicitUserAction(
                false, nativeSettings.TelegramPreferences,
                nativeSettings.TelegramBotDirectory,
                DateTimeOffset.UtcNow, confirmedOnlyWindowsSender: false);
        localTelegramSender.Update(nativeSettings.TelegramPreferences);
        senderInitialized = true;
    }

    private void TryRestoreWindowsOnlyTelegramConsent()
    {
        // Local-only single-Sender opt-in was explicitly confirmed in an earlier
        // WPF session. This is NOT independent evidence about remote Windows/Mac.
        // A RESERVED item may still be in-flight, so do not re-arm.
        // UNCERTAIN is terminal/non-retryable; it must NEVER be replayed,
        // but after the previous app has cleanly exited it need not block
        // brand-new, different event keys on the same Windows-only Sender.
        if (exiting || nativeSettings is null || stickyAutoConsent is null ||
            localTelegramSender is null || bundledTelegramHostPath is null ||
            telegramOutbox is null || !Dispatcher.CheckAccess())
            return;
        try
        {
            var prefs = nativeSettings.TelegramPreferences;
            var botDirectory = nativeSettings.TelegramBotDirectory;
            if (!stickyAutoConsent.IsEnabledFor(prefs, botDirectory) ||
                localTelegramSender.Update(prefs) !=
                    WindowsLocalSenderLeaseState.LocalClaimHeld)
                return;
            if (telegramOutbox.Read().Any(item =>
                item.Status == WindowsAmanoOutboxStatus.Reserved))
                return;
            var now = DateTimeOffset.UtcNow;
            foreach (var lane in new[] {
                TelegramNotificationLane.FiveHourRecovery,
                TelegramNotificationLane.WeeklyRecovery })
            {
                var enabled = lane == TelegramNotificationLane.FiveHourRecovery
                    ? prefs.WantsFiveHour : prefs.WantsWeekly;
                if (!enabled) continue;
                var route = WindowsExistingAmanoBotRoute.Prepare(
                    bundledTelegramHostPath, botDirectory,
                    lane, new string('0', 64), now);
                if (route is null ||
                    !WindowsExistingAmanoBotProcessAdapter.ValidRoute(route))
                    return;
            }
            autoTelegramSession.Arm(
                explicitSessionConsent: true,
                userConfirmedOtherHostsOff: true,
                prefs, localTelegramSender,
                configuredBotVerified: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException
            or System.Security.SecurityException)
        {
            autoTelegramSession.Disarm();
            System.Diagnostics.Trace.TraceWarning(
                "Sticky Telegram auto-resume failed closed: " +
                error.GetType().Name);
        }
    }

    private void OnMonitorObserved(CodexMonitorWireSample sample)
    {
        try { monitorWorker?.Process(sample); }
        catch (Exception ex) {
            // Monitoring is supplementary: a failed local preview does not
            // interfere with the original account-switching UI.
            System.Diagnostics.Trace.TraceWarning("Local monitor persistence failed: " + ex.GetType().Name);
        }
        // The local worker receives all profiles. Only confirmed active-profile
        // observations may update the taskbar child panel.
        if (!externalMonitorGate.Allows(sample)) return;
        if (Dispatcher.CheckAccess()) {
            if (sample.ProfileID is Guid id) adaptiveUsage?.ObserveFreshActiveSample(id, sample);
            taskbarPanel?.Accept(sample);
            ProcessWeekly100Candidate(sample);
            ProcessFiveHour100Candidate(sample);
        }
        else Dispatcher.BeginInvoke(new Action(() => {
            // Source selection must be rechecked after deferred UI dispatch.
            if (!externalMonitorGate.Allows(sample)) return;
            if (sample.ProfileID is Guid id) adaptiveUsage?.ObserveFreshActiveSample(id, sample);
            taskbarPanel?.Accept(sample);
            ProcessWeekly100Candidate(sample);
            ProcessFiveHour100Candidate(sample);
        }));
    }

    private void ProcessWeekly100Candidate(CodexMonitorWireSample sample)
    {
        // Only an already-authorized active profile can enter the local
        // 7D alert detector. No Telegram transport exists in this method.
        try {
            if (client is not null && externalMonitorGate.Allows(sample))
            {
                var events = weekly100Worker?.Process(sample, client.State,
                    externalMonitorGate, DateTimeOffset.UtcNow) ?? [];
                foreach (var detected in events)
                    PrepareExistingAmanoRoute(
                        TelegramNotificationLane.WeeklyRecovery,
                        detected.Key, detected.ObservedAt);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Trace.TraceWarning(
                "Weekly quota mock alert persistence failed: " + ex.GetType().Name);
        }
    }

    private void ProcessFiveHour100Candidate(CodexMonitorWireSample sample)
    {
        // A separate local mock ledger for 5H recovery. Never routes or
        // sends a real Telegram message, irrespective of the bot's mute state.
        try {
            if (client is not null && externalMonitorGate.Allows(sample))
            {
                var events = fiveHour100Worker?.Process(sample, client.State,
                    externalMonitorGate, DateTimeOffset.UtcNow) ?? [];
                foreach (var detected in events)
                    PrepareExistingAmanoRoute(
                        TelegramNotificationLane.FiveHourRecovery,
                        detected.Key, detected.ObservedAt);
            }
        }
        catch (Exception ex) {
            System.Diagnostics.Trace.TraceWarning(
                "Five-hour quota mock alert persistence failed: " + ex.GetType().Name);
        }
    }

    private void PrepareExistingAmanoRoute(
        TelegramNotificationLane lane, string eventKey, DateTimeOffset observedAt)
    {
        // The already-running Amano scripts and their bot credentials are
        // untouched. Preparing a Codex-specific route never starts a process.
        // Only a separately armed in-memory session may auto-dispatch a new
        // real reset after a durable production outbox stage.
        var isFiveHour = lane == TelegramNotificationLane.FiveHourRecovery;
        var preferences = nativeSettings?.TelegramPreferences;
        if (preferences is null ||
            !(isFiveHour ? preferences.WantsFiveHour : preferences.WantsWeekly))
            return;
        var route = WindowsExistingAmanoBotRoute.Prepare(
            bundledTelegramHostPath, nativeSettings?.TelegramBotDirectory,
            lane, eventKey, observedAt);
        var prefix = isFiveHour ? "5H → 기존 모니터 봇(묵음)" :
            "7D → 기존 얼럿 봇";
        existingAmanoBotRouteStatus = prefix +
            (route is null ? ": 봇 폴더/스크립트 미확인" : ": 전송 경로 준비됨") +
            " · 실제 발송 차단";
        // Only persist confirmed, prepared events. This ledger is separate
        // from the MOCK workers. No send, authorization, DPAPI or network.
        if (route is not null)
        {
            try {
                var staged = telegramOutbox?.Stage(lane, eventKey, observedAt,
                    matchingPreparedRoute: true, preferences) == true;
                if (staged && autoTelegramSession.AllowsNewRecovery(
                        lane, isTest: false, preferences, localTelegramSender))
                    _ = DeliverNewRecoveryAutomaticallyAsync(
                        lane, eventKey, observedAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException) {
                // A damaged/locked queue must not block account switching,
                // and MUST NOT fall back to an unrecorded Telegram send.
                existingAmanoBotRouteStatus = prefix +
                    ": 운영 발송 원장 오류 · 실제 발송 차단";
                System.Diagnostics.Trace.TraceWarning(
                    "Telegram production outbox unavailable: " + ex.GetType().Name);
            }
        }
        // Nothing is sent by route creation itself. Automatic dispatch
        // is explicitly gated, separate, and disabled by default.
    }

    private static async Task<string> ProbeSharedAuthorityStatusReadOnlyAsync(
        string origin, string deviceId, string hmacKeyHex, string pinHex)
    {
        // Explicit one-shot GET, initiated solely by a settings-page click.
        // Never grant a lease, reserve an event or invoke any Telegram bot.
        byte[]? key = null;
        try
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                !WindowsSenderAuthorityClient.ValidAuthorityUri(uri) ||
                !WindowsSenderAuthorityClient.ValidDeviceId(deviceId) ||
                hmacKeyHex.Length != 64 ||
                !hmacKeyHex.All(c => c is >= '0' and <= '9' or
                                        >= 'a' and <= 'f'))
                return "Authority: 기기 주소 또는 인증 입력 불일치 · 조회만 가능";

            key = Convert.FromHexString(hmacKeyHex);
            using var http = WindowsSenderAuthorityHttpsTransport.Create(uri, pinHex);
            using var reader = new WindowsSenderAuthorityClient(uri, deviceId, key, http);
            var observed = await reader.StatusAsync();
            return WindowsAuthorityReadOnlyStatus.Format(observed);
        }
        catch (Exception ex) when (ex is ArgumentException or
            HttpRequestException or IOException or ObjectDisposedException or
            InvalidOperationException)
        {
            // Never leak the URI, HMAC secret, certificate pin or response.
            return "Authority: 구성·연결·인증 검증 실패 · 조회만 가능";
        }
        finally
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    private WindowsTelegramHandoffStatus StopWindowsTelegramSenderForHandoff()
    {
        // Only the user's explicit WPF click invokes this. Nothing on the
        // Mac or any third-party application is modified.
        if (!Dispatcher.CheckAccess() || exiting || nativeSettings is null ||
            localTelegramSender is null)
            return WindowsTelegramHandoffStatus.LocalReleaseNotVerified;
        // Revoke persistent Windows-only consent BEFORE handoff, never leave
        // another host racing a remembered Windows auto-Sender on reboot.
        autoTelegramSession.Disarm();
        if (stickyAutoConsent?.SaveFromExplicitUserAction(
            false, nativeSettings.TelegramPreferences,
            nativeSettings.TelegramBotDirectory,
            DateTimeOffset.UtcNow, confirmedOnlyWindowsSender: false) != true)
            return WindowsTelegramHandoffStatus.LocalReleaseNotVerified;
        return new WindowsTelegramSenderHandoff(
            autoTelegramSession,
            localTelegramSender,
            () => nativeSettings.TelegramPreferences,
            prefs => nativeSettings.SetTelegramPreferences(prefs),
            () => Volatile.Read(ref telegramSendActivities) != 0)
            .StopWindowsSender(userConfirmed: true).Status;
    }

    private bool ChangeAutomaticTelegramSession(
        bool enable, bool userConfirmedOtherHostsOff)
    {
        // Invoked only from a WPF UI click. Never from saved preferences,
        // startup, background update, the detector or app CLI flags.
        if (!Dispatcher.CheckAccess() || exiting || nativeSettings is null ||
            localTelegramSender is null || bundledTelegramHostPath is null)
            return false;

        autoTelegramSession.Disarm();
        if (!enable)
        {
            // OFF must be persisted; failure is surfaced instead of silently
            // reviving a prior ON at the next startup.
            return stickyAutoConsent?.SaveFromExplicitUserAction(
                false, nativeSettings.TelegramPreferences,
                nativeSettings.TelegramBotDirectory,
                DateTimeOffset.UtcNow, confirmedOnlyWindowsSender: false) == true;
        }
        if (!userConfirmedOtherHostsOff || stickyAutoConsent is null ||
            telegramOutbox is null) return false;
        try
        {
            if (telegramOutbox.Read().Any(item =>
                item.Status == WindowsAmanoOutboxStatus.Reserved))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException
            or System.Security.SecurityException)
        {
            return false;
        }

        var preferences = nativeSettings.TelegramPreferences;
        if (localTelegramSender.Update(preferences) !=
            WindowsLocalSenderLeaseState.LocalClaimHeld)
            return false;

        var now = DateTimeOffset.UtcNow;
        var botDirectory = nativeSettings.TelegramBotDirectory;
        var lanes = new[] {
            TelegramNotificationLane.FiveHourRecovery,
            TelegramNotificationLane.WeeklyRecovery
        };
        foreach (var lane in lanes)
        {
            var enabled = lane == TelegramNotificationLane.FiveHourRecovery
                ? preferences.WantsFiveHour : preferences.WantsWeekly;
            if (!enabled) continue;
            var route = WindowsExistingAmanoBotRoute.Prepare(
                bundledTelegramHostPath, botDirectory,
                lane, new string('0', 64), now);
            if (route is null ||
                !WindowsExistingAmanoBotProcessAdapter.ValidRoute(route))
                return false;
        }
        if (!autoTelegramSession.Arm(
            explicitSessionConsent: true,
            userConfirmedOtherHostsOff: true,
            preferences, localTelegramSender,
            configuredBotVerified: true)) return false;
        if (!stickyAutoConsent.SaveFromExplicitUserAction(
            true, preferences, botDirectory,
            DateTimeOffset.UtcNow, confirmedOnlyWindowsSender: true))
        {
            autoTelegramSession.Disarm();
            return false;
        }
        return true;
    }

    private async Task DeliverNewRecoveryAutomaticallyAsync(
        TelegramNotificationLane lane, string eventKey,
        DateTimeOffset observedAt)
    {
        // Only a newly committed REAL quota recovery event enters. Pending
        // records from previous runs and all connectivity tests cannot replay.
        try
        {
            if (exiting || remoteQuitPending || !Dispatcher.CheckAccess() ||
                autoTelegramDispatcher is null ||
                nativeSettings is null || localTelegramSender is null)
                return;
            Interlocked.Increment(ref telegramSendActivities);
            try
            {
                var result = await autoTelegramDispatcher.DeliverNewlyStagedAsync(
                    lane, eventKey, observedAt, wasJustStaged: true,
                    nativeSettings.TelegramPreferences, localTelegramSender,
                    bundledTelegramHostPath, nativeSettings.TelegramBotDirectory);
                System.Diagnostics.Trace.TraceInformation(
                    "Telegram recovery automatic result: " + result.Status);
            }
            finally
            {
                Interlocked.Decrement(ref telegramSendActivities);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException or
            System.Security.SecurityException or InvalidOperationException)
        {
            // No automatic retry: a durable reservation might be uncertain.
            autoTelegramSession.Disarm();
            System.Diagnostics.Trace.TraceWarning(
                "Auto Telegram delivery stopped: " + ex.GetType().Name);
        }
    }

    private IReadOnlyList<WindowsTelegramApprovalRow> ReadPendingTelegramApprovalRows()
    {
        try { return telegramOutbox?.ReadPendingRows() ?? []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or
            System.Security.SecurityException)
        {
            // Bad production ledger must never be silently rebuilt or sent.
            System.Diagnostics.Trace.TraceWarning(
                "Telegram approval queue unreadable: " + ex.GetType().Name);
            return [];
        }
    }

    private string QueueTelegramConnectivityTest(TelegramNotificationLane lane)
    {
        // Creates only a clearly labelled, user-reviewed pending TEST event.
        // No Telegram network, credential read, sender execution or approval.
        if (exiting || !Dispatcher.CheckAccess() || telegramOutbox is null ||
            nativeSettings is null || bundledTelegramHostPath is null)
            return "연결 테스트 대기함을 사용할 수 없습니다. 발송 없음";
        try
        {
            var preferences = nativeSettings.TelegramPreferences;
            var enabled = lane == TelegramNotificationLane.FiveHourRecovery
                ? preferences.WantsFiveHour : preferences.WantsWeekly;
            if (!enabled)
                return "현재 PC의 Sender 및 해당 알림 ON 설정이 필요합니다. 발송 없음";
            var now = DateTimeOffset.UtcNow;
            var preview = WindowsExistingAmanoBotRoute.Prepare(
                bundledTelegramHostPath, nativeSettings.TelegramBotDirectory,
                lane, new string('0', 64), now, isTest: true);
            if (preview is null ||
                !WindowsExistingAmanoBotProcessAdapter.ValidRoute(preview))
                return "기존 봇 폴더·내장 테스트 스크립트 확인 실패. 발송 없음";
            var item = telegramOutbox.StageConnectivityTest(
                lane, preferences, now, reviewedBotRouteAvailable: true);
            if (item is null)
                return "테스트 대기 항목 생성 실패 · 발송 없음";
            return item.LaneLabel +
                ": 승인 대기 항목 생성 완료. 실제 발송은 1건 검토 후 별도 확인 필요";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or
            ArgumentException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning(
                "Telegram connectivity test stage error: " + ex.GetType().Name);
            return "테스트 대기함 오류 · 실제 발송하지 않음";
        }
    }

    private async Task<string> SendApprovedTelegramOnceAsync(
        WindowsTelegramApprovalRow requested, bool otherHostsNotSendingConfirmed)
    {
        // The sole entry point for real Telegram transmission is an explicit
        // per-event, double-confirmed WPF click. No monitor callback invokes it.
        if (exiting || remoteQuitPending || !Dispatcher.CheckAccess() ||
            !otherHostsNotSendingConfirmed ||
            telegramOutbox is null || nativeSettings is null ||
            bundledTelegramHostPath is null || localTelegramSender is null)
            return "발송 조건 미충족 · 메시지 전송 없음";
        try
        {
            var current = telegramOutbox.Read().SingleOrDefault(entry =>
                entry.Lane == requested.Lane &&
                entry.EventKey == requested.EventKey &&
                entry.ObservedAt == requested.ObservedAt &&
                entry.IsTest == requested.IsTest &&
                entry.Status == WindowsAmanoOutboxStatus.AwaitingApproval);
            if (current is null)
                return "이미 처리됐거나 대기 기록이 없는 이벤트 · 발송 없음";

            var preferences = nativeSettings.TelegramPreferences;
            if (localTelegramSender.Update(preferences) !=
                WindowsLocalSenderLeaseState.LocalClaimHeld)
                return "Windows 로컬 Sender 점유 확인 실패 · 발송 없음";

            var route = WindowsExistingAmanoBotRoute.Prepare(
                bundledTelegramHostPath, nativeSettings.TelegramBotDirectory,
                current.Lane, current.EventKey, current.ObservedAt,
                isTest: current.IsTest);
            if (route is null)
                return "기존 Telegram 봇 경로 또는 스크립트 확인 실패 · 발송 없음";

            var coordinator = new WindowsAmanoApprovedDeliveryCoordinator(
                telegramOutbox,
                WindowsExistingAmanoBotProcessAdapter.CreateWindowsTransport());
            Interlocked.Increment(ref telegramSendActivities);
            try
            {
                var result = await coordinator.ExecuteApprovedOnceAsync(
                    route, current.Lane, preferences, localTelegramSender,
                    userApprovedExactEvent: true,
                    otherHostsNotSendingConfirmed: otherHostsNotSendingConfirmed);

                // Telegram Bot API ACK does NOT prove end-user phone delivery.
                return result.Status switch
            {
                WindowsAmanoApprovalStatus.Acknowledged =>
                    "Telegram Bot API 응답 확인 (message_id " +
                    result.TelegramMessageId + ") · 휴대전화 수신 별도 확인",
                WindowsAmanoApprovalStatus.Uncertain =>
                    "Telegram 전송 결과 불확실 · 중복 방지를 위해 자동 재발송하지 않음",
                WindowsAmanoApprovalStatus.NotPending =>
                    "대기 중이 아닌 이벤트 · 중복 발송 차단",
                WindowsAmanoApprovalStatus.LedgerUnavailable =>
                    "운영 발송 원장 오류 · 발송 차단 또는 결과 불확실",
                _ => "발송 조건 미충족 · 메시지 전송 없음"
                };
            }
            finally
            {
                Interlocked.Decrement(ref telegramSendActivities);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or
            ArgumentException or System.Security.SecurityException)
        {
            // A transport failure after the durable claim may be ambiguous.
            // Never automatically retry or expose bot credentials.
            System.Diagnostics.Trace.TraceWarning(
                "Approved Telegram dispatch status unknown: " + ex.GetType().Name);
            return "Telegram 승인 처리 결과 확인 불가 · 자동 재발송하지 않음";
        }
    }

    private void InvalidateFiveHour100(Guid? profileID)
    {
        try { fiveHour100Worker?.Invalidate(profileID); }
        catch (Exception ex) {
            System.Diagnostics.Trace.TraceWarning(
                "Five-hour quota mock alert invalidation failed: " + ex.GetType().Name);
        }
    }

    private void InvalidateWeekly100(Guid? profileID)
    {
        try { weekly100Worker?.Invalidate(profileID); }
        catch (Exception ex) {
            System.Diagnostics.Trace.TraceWarning(
                "Weekly quota mock alert invalidation failed: " + ex.GetType().Name);
        }
    }

    private void OnClientChanged()
    {
        if (client is null) return;
        var state = client.State;
        var previouslyActive = externalMonitorGate.ActiveProfile;
        if (externalMonitorGate.Observe(state)) {
            taskbarPanel?.Invalidate();
            InvalidateWeekly100(previouslyActive);
            InvalidateFiveHour100(previouslyActive);
        }
        // On a Free/partial plan there can be no weekly quota observation.
        // Accept only a confirmed active profile's *actual*, newly received
        // non-weekly quota. Do not borrow the old profile's 5H/7D metrics.
        if (state.ActiveAccountID is Guid activeId &&
            externalMonitorGate.ActiveProfile == activeId &&
            state.Accounts.FirstOrDefault(x => x.Profile.Id == activeId) is { } active &&
            active.UsageStatus == "nonWeekly" && active.NonWeeklyUsage is { } other)
        {
            taskbarPanel?.AcceptNonWeekly(other);
            if ((lastOtherQuotaProfile != activeId || lastOtherQuotaReceipt != other.ObservedAt) &&
                DateTimeOffset.UtcNow - other.ObservedAt is var age &&
                age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromMinutes(6))
            {
                lastOtherQuotaProfile = activeId;
                lastOtherQuotaReceipt = other.ObservedAt;
                adaptiveUsage?.ObserveFreshActiveSample(activeId);
            }
        }
        else taskbarPanel?.Refresh();
    }

    private void OnSwitchRequested()
    {
        var previouslyActive = externalMonitorGate.ActiveProfile;
        externalMonitorGate.SwitchRequested();
        InvalidateWeekly100(previouslyActive);
        InvalidateFiveHour100(previouslyActive);
        if (Dispatcher.CheckAccess()) taskbarPanel?.Invalidate();
        else Dispatcher.BeginInvoke(new Action(() => taskbarPanel?.Invalidate()));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!exiting) taskbarPanel?.Refresh(); }));
    }
    private static bool IsDark() {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
    private void ApplyTheme() {
        var dark = IsDark();
        foreach (var pair in new[] { ("ListSurface", dark ? "#2D2D2D" : "#FFFFFF"), ("Surface", dark ? "#282828" : "#F5F5F5"), ("Ink", dark ? "#F5F5F5" : "#1A1A1A"),
            ("Muted", dark ? "#BEBEBE" : "#666666"), ("Line", dark ? "#444444" : "#DDDDDD"),
            ("Accent", dark ? "#60CDFF" : "#0067C0"), ("Selected", dark ? "#293F50" : "#E2EEFA"), ("Hover", dark ? "#15FFFFFF" : "#0C000000") })
            Resources[pair.Item1] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(pair.Item2));
    }
    private string RemoteQuitReady()
    {
        if (!Dispatcher.CheckAccess()) return "WRONG_DISPATCHER";
        bool ledgerReadable;
        bool ledgerUnsettled;
        try
        {
            var entries = telegramOutbox?.Read();
            ledgerReadable = entries is not null;
            ledgerUnsettled = entries?.Any(item =>
                item.Status is WindowsAmanoOutboxStatus.Reserved or
                    WindowsAmanoOutboxStatus.AwaitingApproval) == true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException
            or System.Security.SecurityException)
        {
            ledgerReadable = false;
            ledgerUnsettled = false;
        }
        return RemoteQuitReadiness.Check(exiting, remoteQuitPending,
            window?.IsBusy == true,
            Volatile.Read(ref telegramSendActivities) != 0,
            ledgerReadable, ledgerUnsettled);
    }

    private string RemoteQuitReserve()
    {
        var decision = RemoteQuitReady();
        if (decision == RemoteGracefulQuitChannel.Ready)
            remoteQuitPending = true;
        return decision;
    }

    // Normal local and remote exits share exactly one shutdown routine.
    // Never disarm an in-progress Telegram transport or abort an account switch.
    private async Task QuitAsync() {
        if (exiting || window?.IsBusy == true ||
            Volatile.Read(ref telegramSendActivities) != 0)
        {
            remoteQuitPending = false;
            return;
        }
        exiting = true;
        autoTelegramSession.Disarm();
        updates?.Stop();
        syntheticV2Runtime?.Dispose();
        syntheticV2Publisher?.Dispose();
        adaptiveUsage?.Dispose();
        activationListener?.Unregister(null);
        if (client != null) {
            client.MonitorObserved -= OnMonitorObserved;
            client.Changed -= OnClientChanged;
            client.SwitchRequested -= OnSwitchRequested;
            await client.DisposeAsync();
        }
        window?.CloseForExit();
        Shutdown();
    }
    private async Task SelfTestAsync() {
        var root = Path.Combine(Path.GetTempPath(), "switcher-package-check-" + Guid.NewGuid());
        var result = "PASS"; var exitCode = 0;
        try {
            Directory.CreateDirectory(Path.Combine(root, "active"));
            var host = HostRuntime.Resolve(Assembly.GetExecutingAssembly());
            if (!WindowsPackagedTelegramScripts.Inspect(host).BothVerified)
                throw new IOException("Packaged Telegram sender script hashes failed.");
            await using var core = new CoreClient(host, Path.Combine(root, "data"), Path.Combine(root, "active"));
            await core.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await core.CommandAsync("fiveHour", value: true).WaitAsync(TimeSpan.FromSeconds(10));
            if (!File.Exists(Path.Combine(root, "data", "settings.json"))) throw new IOException("Shared settings were not persisted.");
            if (File.Exists(Path.Combine(root, "active", "auth.json"))) throw new IOException("Empty startup created an unexpected credential.");
            var monitorFixture = """
                {"accountId":"SELFTEST_ACCOUNT","ordinaryUsageAllowed":null,
                 "rateLimitsByLimitId":{"codex":{"planType":null,"primary":{"usedPercent":30,"windowDurationMins":300,"resetsAt":1800030000},
                 "secondary":{"usedPercent":60,"windowDurationMins":10080,"resetsAt":1800600000}}},
                 "rateLimitResetCredits":{"availableCount":1}}
                """;
            var observation = new CodexMonitorWireSample(1_800_000_000_000, monitorFixture);
            var monitor = new LocalMonitorWorker(Path.Combine(root, "data"));
            var first = monitor.Process(observation);
            var restored = new LocalMonitorWorker(Path.Combine(root, "data")).Process(observation);
            if (first.EventKinds.Count != 0 || restored.EventKinds.Count != 0 ||
                !restored.MonitorPreview.Contains("5H 70%") ||
                !Directory.Exists(Path.Combine(root, "data", "usage-monitor")))
                throw new IOException("Bundled local monitor state could not be restored.");
            result += ": bundled .NET, extracted Swift runtime, shared host, isolated settings and local monitor ledger.";
        } catch (Exception ex) { result = "FAIL: " + ex; exitCode = 1; }
        finally { if (Directory.Exists(root)) SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root); }
        if (Environment.GetEnvironmentVariable("CODEX_SWITCHER_SELF_TEST_RESULT") is { } output) File.WriteAllText(output, result);
        Shutdown(exitCode);
    }
    protected override void OnExit(ExitEventArgs e) {
        remoteQuitChannel?.Dispose();
        autoTelegramSession.Disarm();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        taskbarPanel?.Dispose();
        if (nativeSettings is not null)
            nativeSettings.TelegramRoutingChanged -= RefreshLocalTelegramSender;
        localTelegramSender?.Dispose();
        syntheticV2Runtime?.Dispose();
        syntheticV2Publisher?.Dispose();
        adaptiveUsage?.Dispose();
        activateWindow?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose();
        base.OnExit(e);
    }
}
