using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

// Same-PC preview reads only published metadata. It never initializes the
// account host, opens auth files, attaches to Explorer or constructs a sender.
internal static class Step2ReadOnlyPreview
{
    private sealed record Registry(Guid? ActiveAccountID, Profile[] Accounts);
    private sealed record Profile(Guid Id);
    private sealed record Cache(Entry[] Entries);
    private sealed record Entry(Guid ProfileID, Quota Usage, DateTimeOffset FetchedAt);
    private sealed record Quota(int RemainingPercent, DateTimeOffset ResetsAt,
        int? FiveHourRemainingPercent, DateTimeOffset? FiveHourResetsAt);
    private sealed record Settings(bool ShowsFiveHourUsage, string StatusBarUsageWindow);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    internal static Window Create()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codex Account Switcher Personal");
        if (!Directory.Exists(root) || File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("STEP2_METADATA_HOME_UNAVAILABLE");
        var registry = Read<Registry>(root, "accounts.json");
        var cache = Read<Cache>(root, "usage-cache.json");
        var settings = Read<Settings>(root, "settings.json");
        var active = registry.ActiveAccountID ?? throw new InvalidDataException("STEP2_ACTIVE_PROFILE_UNAVAILABLE");
        if (active == Guid.Empty || registry.Accounts.Count(row => row.Id == active) != 1)
            throw new InvalidDataException("STEP2_ACTIVE_PROFILE_UNAVAILABLE");
        var usage = cache.Entries.Single(row => row.ProfileID == active);
        if (usage.Usage.RemainingPercent is < 0 or > 100 ||
            usage.Usage.FiveHourRemainingPercent is < 0 or > 100 || usage.FetchedAt == default)
            throw new InvalidDataException("STEP2_CACHE_INVALID");

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Windows 사용량 · 읽기 전용 캐시", FontSize = 22 });
        panel.Children.Add(new TextBlock { Text = $"주간 잔여 {usage.Usage.RemainingPercent}% · 초기화 {usage.Usage.ResetsAt:yyyy-MM-dd HH:mm zzz}" });
        if (usage.Usage.FiveHourRemainingPercent is int five)
            panel.Children.Add(new TextBlock { Text = $"5시간 잔여 {five}% · 초기화 {usage.Usage.FiveHourResetsAt:yyyy-MM-dd HH:mm zzz}" });
        panel.Children.Add(new TextBlock { Text = $"캐시 기록 시각 {usage.FetchedAt:yyyy-MM-dd HH:mm:ss zzz}" });
        panel.Children.Add(new TextBlock { Text = $"5시간 표시 설정 {(settings.ShowsFiveHourUsage ? "ON" : "OFF")}" });
        return new Window { Title = "Codex Switcher · 비송신 확인", Content = panel,
            Width = 510, Height = 245, ShowInTaskbar = false, ShowActivated = false };
    }

    internal static MainWindow CreateSettings()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codex Account Switcher Personal");
        var window = new MainWindow(new ReadOnlyAccountClient(),
            dedicatedExistingThreadDataHome: root)
        {
            Title = "Codex Switcher · 설정 읽기 전용",
            ShowActivated = false,
            ShowInTaskbar = false,
            IsEnabled = false
        };
        window.Navigate("settings");
        return window;
    }

    private sealed class ReadOnlyAccountClient : IAccountClient
    {
        public AccountSnapshot State => AccountSnapshot.Empty;
        public event Action? Changed { add { } remove { } }
        public Task CommandAsync(string command, Guid? accountID = null, bool? value = null,
            string? language = null) => Task.FromException(new InvalidOperationException("READ_ONLY_PREVIEW"));
    }
    private static T Read<T>(string root, string name)
    {
        var path = Path.Combine(root, name);
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("STEP2_METADATA_REPARSE");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length is < 2 or > 1_048_576)
            throw new InvalidDataException("STEP2_METADATA_SIZE");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("STEP2_METADATA_CHANGED");
        return JsonSerializer.Deserialize<T>(bytes, Options) ??
            throw new InvalidDataException("STEP2_METADATA_INVALID");
    }
}
