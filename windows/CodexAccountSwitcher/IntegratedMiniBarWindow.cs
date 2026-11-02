using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using FormsScreen = System.Windows.Forms.Screen;

namespace CodexAccountSwitcher;

// Integrated, display-only counterpart of the validated CodexUsageMonitor
// preview. No second tray icon, IPC listener, Codex reader, or login path.
// All source events come from App's existing CoreClient and active-only gate.
internal sealed class IntegratedMiniBarWindow : Window
{
    private readonly IntegratedMiniBarState state = new();
    private readonly Action openMainWindow;
    private readonly TextBlock fiveValue;
    private readonly TextBlock weekValue;
    private readonly TextBlock creditValue;
    private readonly TextBlock statusText;
    private readonly TextBlock statusDot;
    private readonly DispatcherTimer timer;
    private readonly ContextMenu menu = new();
    private HwndSource? hwndSource;
    private string? preferredScreen;
    private bool positionLocked = true;
    private bool closingForExit;

    internal MiniBarReadout DisplayFrame => state.Read(DateTimeOffset.UtcNow);

    internal IntegratedMiniBarWindow(Action openMainWindow)
    {
        this.openMainWindow = openMainWindow;
        Title = "Codex Account Switcher — 사용량";
        Width = 360;
        Height = 52;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var values = new StackPanel {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusDot = Add(values, "●", 11, FontWeights.Bold,
            Color.FromRgb(145, 159, 176), 7);
        Add(values, "CODEX", 12, FontWeights.Bold,
            Color.FromRgb(227, 237, 249), 10);
        Add(values, "5H", 10, FontWeights.Medium,
            Color.FromRgb(148, 166, 190), 4);
        fiveValue = Add(values, "—", 13, FontWeights.SemiBold, Colors.White, 9);
        Add(values, "7D", 10, FontWeights.Medium,
            Color.FromRgb(148, 166, 190), 4);
        weekValue = Add(values, "—", 13, FontWeights.SemiBold, Colors.White, 9);
        Add(values, "리셋권", 10, FontWeights.Medium,
            Color.FromRgb(148, 166, 190), 4);
        creditValue = Add(values, "—", 13, FontWeights.SemiBold, Colors.White, 0);
        statusText = new TextBlock {
            Text = "미조회 · UNKNOWN",
            Foreground = new SolidColorBrush(Color.FromRgb(244, 187, 107)),
            FontWeight = FontWeights.Medium,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 0, 0)
        };
        var layout = new StackPanel { Orientation = Orientation.Vertical };
        layout.Children.Add(values);
        layout.Children.Add(statusText);
        var surface = new Border {
            Background = new SolidColorBrush(Color.FromRgb(21, 29, 39)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(77, 91, 113)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 5, 11, 5),
            Child = layout
        };
        surface.MouseLeftButtonDown += (_, e) => {
            if (e.ClickCount == 2) openMainWindow();
        };
        surface.MouseRightButtonUp += (_, _) => menu.IsOpen = true;
        Content = surface;
        ContextMenu = menu;
        BuildMenu();
        RenderFrame();

        SourceInitialized += (_, _) => {
            hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            hwndSource?.AddHook(WindowMessages);
            Dock();
        };
        Loaded += (_, _) => Dock();
        Closing += (_, e) => {
            if (closingForExit) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => {
            timer?.Stop();
            hwndSource?.RemoveHook(WindowMessages);
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => {
            if (positionLocked && IsVisible) Dock();
            RenderFrame();
        };
        timer.Start();
    }

    internal void Accept(CodexMonitorWireSample sample)
    {
        state.TryApply(sample, DateTimeOffset.UtcNow);
        RenderFrame();
    }

    internal void Invalidate()
    {
        state.Invalidate(DateTimeOffset.UtcNow);
        RenderFrame();
    }

    internal void ShowFromTray()
    {
        if (!IsVisible) Show();
        Dock();
    }

    internal void CloseForExit()
    {
        closingForExit = true;
        Close();
    }

    private static TextBlock Add(Panel parent, string value, double size,
        FontWeight weight, Color color, double gap)
    {
        var block = new TextBlock {
            Text = value, FontSize = size, FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, gap, 0)
        };
        parent.Children.Add(block);
        return block;
    }

    private void RenderFrame()
    {
        var frame = DisplayFrame;
        static string Percent(double? value) => value is double v ? $"{v:0.#}%" : "—";
        fiveValue.Text = Percent(frame.FiveHourRemaining);
        weekValue.Text = Percent(frame.WeeklyRemaining);
        creditValue.Text = frame.ResetCreditsAvailable?.ToString() ?? "—";
        var now = DateTimeOffset.UtcNow;
        statusText.Text = frame.Status switch {
            MiniBarDisplayStatus.FreshUnbound => "계정 미확인 · 앱 내부 관측",
            MiniBarDisplayStatus.AgedUnbound => frame.ObservedAt is DateTimeOffset at
                ? $"지연된 값 {Math.Max(2, (int)(now - at).TotalMinutes)}분 전 · 계정 미확인"
                : "지연된 값 · 계정 미확인",
            MiniBarDisplayStatus.Invalidated => "계정 변경 또는 연결 오류 · UNKNOWN",
            _ => "미조회 또는 만료 · UNKNOWN"
        };
        statusDot.Foreground = new SolidColorBrush(frame.Status switch {
            MiniBarDisplayStatus.FreshUnbound => Color.FromRgb(98, 178, 235),
            MiniBarDisplayStatus.AgedUnbound => Color.FromRgb(244, 187, 107),
            _ => Color.FromRgb(145, 159, 176)
        });
        ToolTip = $"5H 잔여: {fiveValue.Text} / 7D 잔여: {weekValue.Text} / 리셋권: {creditValue.Text}\n" +
                  $"{statusText.Text}\n관측: {frame.ObservedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "확인 불가"}" +
                  "\n—: 미조회 또는 제공되지 않음";
    }

    private FormsScreen SelectedScreen
    {
        get {
            var all = FormsScreen.AllScreens;
            var id = MiniBarScreenSelector.Select(preferredScreen,
                all.Select(s => new MiniBarScreenInfo(s.DeviceName, s.Primary)).ToArray());
            return all.First(s => string.Equals(
                s.DeviceName, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void BuildMenu()
    {
        menu.Items.Clear();
        var screens = new MenuItem { Header = "표시 모니터 선택" };
        foreach (var screen in FormsScreen.AllScreens) {
            var name = screen.DeviceName;
            var item = new MenuItem {
                Header = name + (screen.Primary ? " (기본)" : ""),
                IsCheckable = true,
                IsChecked = string.Equals(name, SelectedScreen.DeviceName,
                    StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => {
                preferredScreen = name;
                positionLocked = true;
                Dock();
                BuildMenu();
            };
            screens.Items.Add(item);
        }
        menu.Items.Add(screens);
        var alwaysOnTop = new MenuItem {
            Header = "항상 위에 표시 (다른 창과 겹칠 수 있음)",
            IsCheckable = true, IsChecked = Topmost
        };
        alwaysOnTop.Click += (_, _) => Topmost = alwaysOnTop.IsChecked;
        menu.Items.Add(alwaysOnTop);
        var locked = new MenuItem {
            Header = "작업표시줄 옆에 고정",
            IsCheckable = true, IsChecked = positionLocked
        };
        locked.Click += (_, _) => {
            positionLocked = locked.IsChecked;
            if (positionLocked) Dock();
        };
        menu.Items.Add(locked);
        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "계정전환 관리 열기" };
        manage.Click += (_, _) => openMainWindow();
        menu.Items.Add(manage);
        var hide = new MenuItem { Header = "사용량 미니바 숨기기 (트레이에서 복원)" };
        hide.Click += (_, _) => Hide();
        menu.Items.Add(hide);
    }

    private void Dock()
    {
        if (hwndSource?.CompositionTarget is null || FormsScreen.AllScreens.Length == 0)
            return;
        var result = NativeDesktopDock.TryDock(hwndSource.Handle, SelectedScreen, Width, Height);
        if (!result.Success) Topmost = false;
    }

    private IntPtr WindowMessages(IntPtr hwnd, int msg, IntPtr wParam,
        IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DPICHANGED = 0x02E0;
        if (msg is WM_DISPLAYCHANGE or WM_SETTINGCHANGE or WM_DPICHANGED)
            Dispatcher.BeginInvoke(new Action(() => {
                if (FormsScreen.AllScreens.Length == 0) return;
                BuildMenu();
                if (positionLocked) Dock();
            }), DispatcherPriority.ContextIdle);
        return IntPtr.Zero;
    }
}
