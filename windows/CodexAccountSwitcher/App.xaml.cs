using System;
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
    private TaskbarQuotaPanel? taskbarPanel;
    private AdaptiveUsageRefreshService? adaptiveUsage;
    private CoreClient? client;
    private LocalMonitorWorker? monitorWorker;
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
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataPath).ToUpperInvariant())));
            instance = new Mutex(true, @"Local\CodexAccountSwitcher-" + key, out ownsMutex);
            activateWindow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexAccountSwitcher-Activate-" + key);
            if (!ownsMutex) { activateWindow.Set(); Shutdown(); return; }
            ApplyTheme();
            client = new CoreClient(HostRuntime.Resolve(Assembly.GetExecutingAssembly()), dataPath);
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
            client.MonitorObserved += OnMonitorObserved;
            client.SwitchRequested += OnSwitchRequested;
            var native = new NativeSettings();
            window = new MainWindow(client, native, () => monitorWorker?.ReadHistory() ?? []); MainWindow = window;
            activationListener = ThreadPool.RegisterWaitForSingleObject(activateWindow, (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => { if (!exiting) { ApplyTheme(); window.OpenWindow(); } })),
                null, Timeout.Infinite, false);
            taskbarPanel = new TaskbarQuotaPanel(
                () => { ApplyTheme(); window.OpenWindow(); },
                () => _ = QuitAsync(),
                () => !window.IsBusy);
            adaptiveUsage = new AdaptiveUsageRefreshService(client);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            client.Changed += OnClientChanged;
            await client.InitializeAsync();
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
            if (sample.ProfileID is Guid id) adaptiveUsage?.ObserveFreshActiveSample(id);
            taskbarPanel?.Accept(sample);
        }
        else Dispatcher.BeginInvoke(new Action(() => {
            // Source selection must be rechecked after deferred UI dispatch.
            if (!externalMonitorGate.Allows(sample)) return;
            if (sample.ProfileID is Guid id) adaptiveUsage?.ObserveFreshActiveSample(id);
            taskbarPanel?.Accept(sample);
        }));
    }

    private void OnClientChanged()
    {
        if (client is null) return;
        var state = client.State;
        if (externalMonitorGate.Observe(state)) taskbarPanel?.Invalidate();
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
        externalMonitorGate.SwitchRequested();
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
    private async Task QuitAsync() {
        if (exiting || window?.IsBusy == true) return;
        exiting = true; updates?.Stop();
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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        taskbarPanel?.Dispose();
        syntheticV2Runtime?.Dispose();
        syntheticV2Publisher?.Dispose();
        adaptiveUsage?.Dispose();
        activateWindow?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex(); instance?.Dispose();
        base.OnExit(e);
    }
}
