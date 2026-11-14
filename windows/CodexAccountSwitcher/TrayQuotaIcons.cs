using System;
using System.Drawing;
using System.Linq;
using System.Windows.Threading;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexAccountSwitcher;

// Native Windows notification-area icons; one process, three labeled metrics.
// Windows owns their ^ overflow / pinned position. No floating window or IPC.
internal sealed class TrayQuotaIcons : IDisposable
{
    private readonly IntegratedMiniBarState state = new();
    private readonly Forms.NotifyIcon[] icons;
    private readonly Icon?[] images = new Icon?[3];
    private readonly DispatcherTimer timer;
    private TrayQuotaReadout? lastReadout;
    private Size lastSize;
    private bool? lastLight;
    private bool disposed;

    internal TrayQuotaIcons(Forms.ContextMenuStrip menu, Action openMainWindow)
    {
        icons = Enumerable.Range(0, 3).Select(_ => new Forms.NotifyIcon
        {
            ContextMenuStrip = menu
        }).ToArray();
        foreach (var icon in icons)
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) openMainWindow();
            };

        Refresh(force: true);
        foreach (var icon in icons) icon.Visible = true;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    internal void Accept(CodexMonitorWireSample sample)
    {
        if (disposed) return;
        state.TryApply(sample, DateTimeOffset.UtcNow);
        Refresh();
    }

    internal void Invalidate()
    {
        if (disposed) return;
        state.Invalidate(DateTimeOffset.UtcNow);
        Refresh();
    }

    internal TrayQuotaReadout CurrentReadout =>
        TrayQuotaReadout.From(state.Read(DateTimeOffset.UtcNow));

    internal void Refresh(bool force = false)
    {
        if (disposed) return;
        var frame = CurrentReadout;
        using var theme = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool light = theme?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        var size = Forms.SystemInformation.SmallIconSize;
        if (!force && frame == lastReadout && size == lastSize && light == lastLight) return;
        var numbers = new[] { frame.FiveHourIcon, frame.WeeklyIcon, frame.CreditsIcon };
        var labels = new[] { "5H", "7D", "R" };
        var tips = new[] { frame.FiveHourTooltip, frame.WeeklyTooltip, frame.CreditsTooltip };

        for (int index = 0; index < icons.Length; index++)
        {
            var newIcon = TrayUsageIcon.CreateMetric(labels[index], numbers[index],
                light, size, frame.Status);
            icons[index].Icon = newIcon;
            var previous = images[index];
            images[index] = newIcon;
            previous?.Dispose();
            // NotifyIcon has a finite native tooltip buffer.
            icons[index].Text = tips[index].Length <= 63 ? tips[index] : tips[index][..63];
        }
        lastReadout = frame;
        lastSize = size;
        lastLight = light;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        foreach (var icon in icons)
        {
            icon.Visible = false;
            icon.Dispose();
        }
        for (int i = 0; i < images.Length; i++)
        {
            images[i]?.Dispose();
            images[i] = null;
        }
    }
}
