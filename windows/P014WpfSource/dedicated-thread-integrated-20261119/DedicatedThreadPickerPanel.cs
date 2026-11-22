using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DedicatedThreadCycleStarter;

namespace CodexAccountSwitcher;

/// <summary>
/// UI-only isolated integration candidate. No message transmission or
/// automatic reset handler is reachable from this control.
/// </summary>
public sealed class DedicatedThreadPickerPanel : UserControl
{
    private readonly Func<ActiveSwitcherIdentity> identity;
    private readonly Func<ActiveSwitcherIdentity, CancellationToken,
        Task<InitializedCodexReadOnlySession>> openReadOnlySession;
    private readonly Func<ActiveSwitcherIdentity, CancellationToken,
        Task<InitializedCodexReadOnlySession>>? openDesktopSession;
    private readonly ComboBox storageSelect = new() { MinWidth = 250 };
    private readonly string bindingFile;
    private readonly string dataHome;
    private readonly CodexAutoSendDraftStore draftStore;
    private readonly CheckBox wantAutomatic = new() {
        Content = "자동 인사 사용 (마스터)", IsEnabled = false
    };
    private readonly CheckBox wantFiveHour = new() {
        Content = "5H 초기화 시 채팅 시작", IsEnabled = false
    };
    private readonly CheckBox wantSevenDay = new() {
        Content = "7D 초기화 시 채팅 시작", IsEnabled = false
    };
    private readonly Button saveDraft = new() {
        Content = "자동 인사 ON/OFF 저장", IsEnabled = false,
        Margin = new Thickness(0, 6, 0, 4)
    };
    private DedicatedChatSettings? activePinned;
    private readonly TextBox query = new() { MaxLength = 80, MinWidth = 180 };
    private readonly ComboBox choices = new() {
        MinWidth = 230, DisplayMemberPath = "Title", IsEnabled = false
    };
    private readonly TextBlock status = new() {
        FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Text = "전용 채팅방 미지정 · 자동 메시지 발송 OFF"
    };
    private readonly Button search = new() { Content = "현재 계정의 기존 채팅방 조회" };
    private readonly Button save = new() {
        Content = "선택한 기존 채팅방 지정", IsEnabled = false
    };

    private VerifiedThreadCatalog? lastCatalog;
    private ActiveSwitcherIdentity? lastIdentity;
    private string? lastStorage;
    private bool busy;

    public DedicatedThreadPickerPanel(
        Func<ActiveSwitcherIdentity> currentIdentity,
        Func<ActiveSwitcherIdentity, CancellationToken,
            Task<InitializedCodexReadOnlySession>> sessionOpener,
        string dataHome,
        Func<ActiveSwitcherIdentity, CancellationToken,
            Task<InitializedCodexReadOnlySession>>? desktopSessionOpener = null)
    {
        identity = currentIdentity ?? throw new ArgumentNullException(nameof(currentIdentity));
        openReadOnlySession = sessionOpener ??
            throw new ArgumentNullException(nameof(sessionOpener));
        openDesktopSession = desktopSessionOpener;
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathFullyQualified(dataHome))
            throw new ArgumentException("Trusted local data home required", nameof(dataHome));
        this.dataHome=Path.GetFullPath(dataHome);
        bindingFile = Path.Combine(this.dataHome,
            "dedicated-existing-thread-bindings-v1.json");
        draftStore = new CodexAutoSendDraftStore(dataHome);

        var body = new StackPanel { Margin = new Thickness(3, 4, 3, 8) };
        body.Children.Add(Hint("현재 Codex 계정에 이미 존재하는 채팅방을 한 번 선택해 계속 재사용합니다."));
        body.Children.Add(Hint("새 채팅방 자동 생성 없음 · 자동 메시지 발송 OFF"));
        body.Children.Add(new TextBlock {
            Text = "기존 채팅방 저장 위치", FontSize = 11,
            Margin = new Thickness(0, 8, 0, 3)
        });
        storageSelect.ItemsSource = new[] {
            "Codex Desktop 기본 저장소", "계정별 Codex 프로필 저장소"
        };
        storageSelect.SelectedIndex = openDesktopSession is null ? 1 : 0;
        storageSelect.IsEnabled = openDesktopSession is not null;
        AutomationProperties.SetName(storageSelect, "기존 Codex 채팅방 저장 위치");
        storageSelect.SelectionChanged += (_, _) => {
            if (busy) return;
            choices.ItemsSource = null;
            choices.IsEnabled = false;
            lastCatalog = null;
            lastIdentity = null;
            lastStorage = null;
            status.Text = "저장 위치 변경 · 기존 채팅방을 다시 조회하세요. 자동 전송 OFF";
        };
        body.Children.Add(storageSelect);
        body.Children.Add(new TextBlock {
            Text = "채팅방 제목 검색어 (선택)", FontSize = 11,
            Margin = new Thickness(0, 9, 0, 3)
        });
        AutomationProperties.SetName(query, "Codex 기존 채팅방 검색어");
        body.Children.Add(query);
        search.Margin = new Thickness(0, 7, 0, 8);
        AutomationProperties.SetName(search, "현재 계정의 기존 Codex 채팅방 조회");
        body.Children.Add(search);
        AutomationProperties.SetName(choices, "기존 Codex 채팅방 목록");
        body.Children.Add(choices);
        save.Margin = new Thickness(0, 8, 0, 8);
        AutomationProperties.SetName(save, "선택한 기존 Codex 채팅방을 전용으로 지정");
        body.Children.Add(save);
        body.Children.Add(status);

        body.Children.Add(Hint("자동 인사 사용자 설정 · 마스터 및 5H/7D 변경은 운영 Owner가 가동 중이면 즉시 반영됩니다. " +
            "별도 운영 등록과 전송권 검증 없이는 자동 송신 OFF입니다."));
        wantAutomatic.Margin = new Thickness(0, 10, 0, 4);
        wantAutomatic.FontSize = 12;
        AutomationProperties.SetName(wantAutomatic, "Codex 자동 인사 마스터 ON OFF");
        AutomationProperties.SetName(wantFiveHour, "Codex 5H 초기화 자동 인사 ON OFF");
        AutomationProperties.SetName(wantSevenDay, "Codex 7D 초기화 자동 인사 ON OFF");
        body.Children.Add(wantAutomatic);
        body.Children.Add(wantFiveHour);
        body.Children.Add(wantSevenDay);
        body.Children.Add(saveDraft);
        AutomationProperties.SetName(saveDraft,
            "기존 Codex 채팅방 자동 인사 사용자 ON OFF 설정 저장");
        Content = body;
        RefreshBoundStatus();
        saveDraft.Click += (_, _) => SaveUserAutoSendSettings();
        choices.SelectionChanged += (_, _) => UpdateControls();
        search.Click += async (_, _) => { if (!busy) await LoadAsync(); };
        save.Click += async (_, _) =>
        {
            if (!busy && choices.SelectedItem is ExistingThreadChoice selected)
                await SaveAsync(selected);
        };
    }

    /// <summary>
    /// Safe local-only view of an existing account binding. It does not
    /// activate automatic sending or query Codex. Suppress stale or
    /// mismatched-account bindings rather than treating a UUID as authority.
    /// </summary>
    public void RefreshBoundStatus()
    {
        if (busy || lastCatalog is not null)
            return;
        activePinned = null;
        wantAutomatic.IsEnabled = false;
        wantAutomatic.IsChecked = false;
        wantFiveHour.IsEnabled = false;
        wantFiveHour.IsChecked = false;
        wantSevenDay.IsEnabled = false;
        wantSevenDay.IsChecked = false;
        saveDraft.IsEnabled = false;
        ActiveSwitcherIdentity current;
        try { current = identity(); }
        catch (Exception ex) when (ex is InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            status.Text = "계정 상태 확인 전 · 전용 채팅 자동 전송 OFF";
            return;
        }
        if (!current.IdentityConfirmed || current.IsSwitching ||
            current.ActiveProfileId == Guid.Empty ||
            current.SelectedActiveAccountId != current.ActiveProfileId ||
            string.IsNullOrWhiteSpace(current.ProfileEmail))
        {
            status.Text = "계정 확인 대기 · 전용 채팅 자동 전송 OFF";
            return;
        }

        var normalizedEmail = current.ProfileEmail.Trim().ToLowerInvariant();
        if (normalizedEmail.Count(c => c == '@') != 1 ||
            normalizedEmail.Any(char.IsWhiteSpace) ||
            normalizedEmail.Any(char.IsControl))
        {
            status.Text = "계정 정보 불일치 · 전용 채팅 자동 전송 OFF";
            return;
        }
        try
        {
            var hash = VerifiedExistingThreadPicker.FingerprintEmail(
                normalizedEmail);
            var matching = new DedicatedThreadBindingStore(bindingFile)
                .ReadAll().Where(x => x.ProfileId == current.ActiveProfileId).ToArray();
            if (matching.Length == 0)
            {
                status.Text = "전용 채팅방 미지정 · 자동 전송 OFF";
                return;
            }
            if (matching.Length != 1 ||
                matching[0].AccountSha256 != hash ||
                !Guid.TryParseExact(matching[0].ExistingThreadId, "D", out _) ||
                matching[0].ThreadStorage is not ("desktop-default" or "switcher-profile") ||
                matching[0].Message != "안녕")
            {
                status.Text = "기존 전용 채팅 결속 확인 실패 · 자동 전송 차단";
                return;
            }
            var selected = matching[0];
            var storage = selected.ThreadStorage == "desktop-default"
                ? "Codex Desktop" : "프로필별 Codex";
            var sending=selected.AutomaticMessagesEnabled
                ? CodexWindowsOwnerAutoStart.HasEnabledConfiguration(dataHome)
                    ? "사용자 ON · Owner 등록 확인 (실제 전송 미검증)"
                    : "사용자 ON · Owner 미등록으로 실제 전송 OFF"
                : "자동 전송 마스터 OFF";
            status.Text = $"기존 채팅방 연결됨 · {storage} · " +
                $"ID 끝 {selected.ExistingThreadId[^8..]} · {sending}";
            if (selected.ThreadStorage == "desktop-default")
            {
                var draft = draftStore.ReadFor(selected);
                wantAutomatic.IsChecked = draft?.WantAutomatic ?? false;
                wantFiveHour.IsChecked = draft?.WantFiveHour ?? false;
                wantSevenDay.IsChecked = draft?.WantSevenDay ?? false;
                activePinned = selected;
                wantAutomatic.IsEnabled = true;
                wantFiveHour.IsEnabled = true;
                wantSevenDay.IsEnabled = true;
                saveDraft.IsEnabled = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or System.Text.Json.JsonException
            or System.Security.SecurityException)
        {
            status.Text = "채팅방 설정 확인 불가 · 자동 전송 차단";
        }
    }

    private void SaveUserAutoSendSettings()
    {
        if (busy || activePinned is null || !saveDraft.IsEnabled)
            return;
        try
        {
            var active = identity();
            var pinned = activePinned;
            if (!active.IdentityConfirmed || active.IsSwitching ||
                active.ActiveProfileId != pinned.ProfileId ||
                active.SelectedActiveAccountId != pinned.ProfileId ||
                string.IsNullOrWhiteSpace(active.ProfileEmail) ||
                VerifiedExistingThreadPicker.FingerprintEmail(
                    active.ProfileEmail.Trim().ToLowerInvariant()) != pinned.AccountSha256)
                throw new InvalidOperationException("Active account changed");
            var current = new DedicatedThreadBindingStore(bindingFile)
                .ReadAll().Where(x => x.ProfileId == pinned.ProfileId).ToArray();
            if (current.Length != 1 || current[0] != pinned)
                throw new InvalidOperationException("Operational binding changed");
            var master=wantAutomatic.IsChecked==true;
            var lane5=wantFiveHour.IsChecked==true;
            var lane7=wantSevenDay.IsChecked==true;
            // User toggles are the explicit per-account recurring preference.
            // A stale binding must never turn an OFF toggle back ON. Persist
            // the safe, account-pinned operational flags separately from the
            // independent local Owner activation and its secret enrollment.
            draftStore.SaveDraft(pinned,lane5,lane7,master);
            var updated=pinned with {
                AutomaticMessagesEnabled=master,
                StartOnFiveHour=master && lane5,
                StartOnSevenDay=master && lane7
            };
            new DedicatedThreadBindingStore(bindingFile).Upsert(updated);
            activePinned=updated;
            status.Text=master
                ? CodexWindowsOwnerAutoStart.HasEnabledConfiguration(dataHome)
                    ? "자동 인사 설정 ON 저장됨 · Owner 등록 확인, 실제 송신 미검증"
                    : "자동 인사 설정 ON 저장됨 · Owner 미등록으로 실제 송신 OFF"
                : "자동 인사 마스터 OFF · 기존 채팅방 유지";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException
            or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            activePinned = null;
            wantAutomatic.IsEnabled = false;
            wantFiveHour.IsEnabled = false;
            wantSevenDay.IsEnabled = false;
            saveDraft.IsEnabled = false;
            status.Text = "자동 인사 설정 저장 보류 · 계정 또는 기존 채팅 연결 상태를 다시 확인하세요";
        }
    }

    private static TextBlock Hint(string value) => new() {
        Text = value, FontSize = 11, Foreground = Brushes.Gray,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 3)
    };

    private async Task LoadAsync()
    {
        SetBusy(true);
        choices.ItemsSource = null;
        choices.IsEnabled = false;
        lastCatalog = null;
        lastIdentity = null;
        lastStorage = null;
        try
        {
            var snapshot = identity();
            var kind = SelectedStorage();
            var open = kind == "desktop-default" ? openDesktopSession! : openReadOnlySession;
            await using var session = await open(snapshot, CancellationToken.None);
            var picker = new VerifiedExistingThreadPicker(session,
                new DedicatedThreadBindingStore(bindingFile), kind);
            var catalog = await picker.ListAsync(snapshot, query.Text.Trim());
            if (identity() != snapshot || SelectedStorage() != kind)
                throw new InvalidOperationException("Active account or storage changed");
            lastCatalog = catalog;
            lastIdentity = snapshot;
            lastStorage = kind;
            choices.ItemsSource = catalog.Threads;
            choices.IsEnabled = catalog.Threads.Count > 0;
            status.Text = catalog.Threads.Count == 0
                ? "현재 계정에서 기존 대화가 조회되지 않습니다. 새 채팅방을 자동 생성하지 않습니다."
                : $"기존 채팅방 {catalog.Threads.Count}개 확인 · 정확한 대화를 선택하세요.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.Text = "채팅방 조회 보류 · 계정 또는 Codex 연결을 확인할 수 없습니다.";
        }
        finally { SetBusy(false); }
    }

    private async Task SaveAsync(ExistingThreadChoice choice)
    {
        if (lastCatalog is null || lastIdentity is null ||
            identity() != lastIdentity ||
            lastCatalog.Threads.Count(x => x.Id == choice.Id) != 1 ||
            lastStorage != SelectedStorage())
        {
            status.Text = "계정이나 목록이 달라졌습니다. 다시 조회하세요.";
            return;
        }
        if (MessageBox.Show(Window.GetWindow(this),
            $"기존 대화 '{choice.Title}'을 이 계정의 전용 채팅방으로 지정하시겠습니까?\n" +
            "메시지 자동 발송은 활성화하지 않습니다.",
            "전용 채팅방 연결", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        try
        {
            var prior = lastCatalog;
            var kind = lastStorage ?? throw new InvalidOperationException("Storage not bound");
            var open = kind == "desktop-default" ? openDesktopSession! : openReadOnlySession;
            await using var session = await open(lastIdentity, CancellationToken.None);
            var picker = new VerifiedExistingThreadPicker(session,
                new DedicatedThreadBindingStore(bindingFile), kind);
            var pinned = await picker.PinExistingAsync(identity, choice.Id);
            if (prior.AccountFingerprint != pinned.AccountSha256 ||
                prior.ProfileId != pinned.ProfileId)
                throw new InvalidOperationException("Account fingerprint changed");
            status.Text = "기존 전용 채팅방 지정 완료 · 자동 전송 OFF";
            lastCatalog = null;
            lastIdentity = null;
            lastStorage = null;
            choices.ItemsSource = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status.Text = "저장 보류 · 계정 변경 또는 채팅방 상태가 일치하지 않습니다.";
        }
        finally { SetBusy(false); }
    }

    private string SelectedStorage() =>
        storageSelect.SelectedIndex == 0 && openDesktopSession is not null
            ? "desktop-default" : "switcher-profile";

    private void SetBusy(bool inProgress) { busy = inProgress; UpdateControls(); }

    private void UpdateControls()
    {
        search.IsEnabled = !busy;
        storageSelect.IsEnabled = !busy && openDesktopSession is not null;
        save.IsEnabled = !busy && choices.SelectedItem is ExistingThreadChoice;
    }
}
