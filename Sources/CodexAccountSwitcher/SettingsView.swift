import Foundation
import SwitcherCore
import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var updater: AppUpdater
    let onBack: () -> Void

    // Per-Mac UI intent, intentionally not cross-device synchronization
    // or an authorization to send a Telegram message.
    @AppStorage("TelegramDeviceRole") private var telegramDeviceRole = "viewer"
    @AppStorage("TelegramNotificationsRequested") private var telegramNotificationsRequested = false
    @AppStorage("TelegramFiveHourRequested") private var telegramFiveHourRequested = true
    @AppStorage("TelegramWeeklyRequested") private var telegramWeeklyRequested = true

    // Deliberately transient, never AppStorage/Keychain/host auto-start.
    // No Authority network request is made before explicit button press.
    @State private var authorityOrigin = ""
    @State private var authorityDeviceID = ""
    @State private var authorityHmacKey = ""
    @State private var authorityCertificatePin = ""
    @State private var authorityReading = false
    @State private var authorityStatus =
        "Authority: 미연결 · 수동 조회만 가능 · Telegram 송신 권한 없음"

    var body: some View {
        VStack(spacing: 0) {
            PopoverHeader(
                title: model.text("settings"),
                backTitle: model.text("back"),
                onBack: onBack
            )
            Divider()
            // Keep the expanded Telegram controls reachable within a compact
            // macOS menu-bar popover instead of growing beyond the display.
            ScrollView {
                VStack(spacing: 0) {
                    sectionLabel("settings_general")
            settingRow("launch_at_login") {
                settingSwitch("launch_at_login", isOn: Binding(
                    get: { model.launchesAtLogin },
                    set: { model.setLaunchAtLogin($0) }
                ))
            }
            if model.launchAtLoginRequiresApproval || model.launchAtLoginUnavailable {
                VStack(alignment: .leading, spacing: 6) {
                    Text(model.text(model.launchAtLoginRequiresApproval
                        ? "launch_at_login_requires_approval" : "launch_at_login_unavailable"))
                        .foregroundStyle(.orange)
                    if model.launchAtLoginRequiresApproval {
                        Button(model.text("open_system_settings")) { model.openLoginItemsSettings() }
                            .buttonStyle(.link)
                    }
                }
                .modifier(SettingsDetail())
            }
            rowDivider
            settingRow("show_menu_bar_percentage") {
                settingSwitch("show_menu_bar_percentage", isOn: Binding(
                    get: { model.settings.showsMenuBarPercentage },
                    set: { enabled in Task { await model.setShowsMenuBarPercentage(enabled) } }
                ))
            }
            rowDivider
            settingRow("status_bar_usage_window") {
                Picker(model.text("status_bar_usage_window"), selection: Binding(
                    get: { model.settings.statusBarUsageWindow },
                    set: { window in Task { await model.setStatusBarUsageWindow(window) } }
                )) {
                    Text(model.text("five_hour")).tag(StatusBarUsageWindow.fiveHour)
                    Text(model.text("weekly")).tag(StatusBarUsageWindow.weekly)
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
            }
            rowDivider
            settingRow("show_five_hour_usage") {
                settingSwitch("show_five_hour_usage", isOn: Binding(
                    get: { model.settings.showsFiveHourUsage },
                    set: { enabled in Task { await model.setShowsFiveHourUsage(enabled) } }
                ))
            }
            rowDivider
            settingRow("language") {
                Picker(model.text("language"), selection: Binding(
                    get: { model.settings.language },
                    set: { language in Task { await model.setLanguage(language) } }
                )) {
                    Text(model.text("system_default")).tag(AppLanguage.system)
                    Text(model.text("english")).tag(AppLanguage.english)
                    Text(model.text("simplified_chinese")).tag(AppLanguage.simplifiedChinese)
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
            }

            sectionLabel("settings_automation")
            settingRow("auto_reset") {
                settingSwitch("auto_reset", isOn: Binding(
                    get: { model.settings.autoResetEnabled },
                    set: { enabled in Task { await model.setAutoResetEnabled(enabled) } }
                ))
            }
            VStack(alignment: .leading, spacing: 6) {
                Text(model.text("auto_reset_hint"))
                    .foregroundStyle(.secondary)
                Button(model.text("open_reset_log")) { model.revealResetExpiryLog() }
                    .buttonStyle(.link)
            }
            .modifier(SettingsDetail())

            Text("Telegram 알림")
                .font(.system(size: 11, weight: .medium))
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 14)
                .padding(.top, 12)
                .padding(.bottom, 2)
            HStack(spacing: 12) {
                Text("이 Mac의 역할")
                Spacer(minLength: 8)
                Picker("Telegram PC 역할", selection: $telegramDeviceRole) {
                    Text("Viewer — 사용량 표시만").tag("viewer")
                    Text("Sender — 이 Mac 발송 담당(예정)").tag("sender")
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
            }
            .modifier(SettingsRowLayout())
            HStack(spacing: 12) {
                Text("Telegram 알림 ON/OFF (발송 준비 설정)")
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                settingSwitch("Telegram 알림 ON/OFF (발송 준비 설정)", isOn: $telegramNotificationsRequested)
            }
            .modifier(SettingsRowLayout())
            rowDivider
            HStack(spacing: 12) {
                Text("5시간 100% 초기화 → 모니터 봇(묵음)")
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                settingSwitch("5시간 100% 초기화 → 모니터 봇(묵음)", isOn: $telegramFiveHourRequested)
            }
            .modifier(SettingsRowLayout())
            rowDivider
            HStack(spacing: 12) {
                Text("7일 100% 초기화 → 얼럿 봇")
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                settingSwitch("7일 100% 초기화 → 얼럿 봇", isOn: $telegramWeeklyRequested)
            }
            .modifier(SettingsRowLayout())
            Text("이 기기: \(ProcessInfo.processInfo.hostName) · " +
                 "저장된 역할: \(telegramRoleSummary) · " +
                 "알림 요청: \(telegramRequestSummary)")
                .foregroundStyle(.secondary)
                .modifier(SettingsDetail())
            Text("실제 Telegram 발송: 미연결. 설정은 이 Mac에만 저장됩니다. " +
                 "Sender 선택만으로 다른 PC의 Sender를 중지하거나 메시지를 발송하지 않습니다. " +
                 "단일 발송자 확인 및 별도 전송 검증이 필요합니다.")
                .foregroundStyle(.secondary)
                .modifier(SettingsDetail())

            authorityReadOnlySection

            sectionLabel("settings_updates")
            settingRow("automatically_check_updates") {
                settingSwitch("automatically_check_updates", isOn: Binding(
                    get: { updater.automaticallyChecks },
                    set: { updater.setAutomaticallyChecks($0) }
                ))
            }
            Text(model.text("update_check_hint"))
                .foregroundStyle(.secondary)
                .modifier(SettingsDetail())
            rowDivider
            HStack(spacing: 12) {
                Text(model.format("current_version", updater.currentVersion))
                    .monospacedDigit()
                Spacer(minLength: 8)
                Button(model.text("check_for_updates")) { updater.checkForUpdates() }
                    .buttonStyle(.bordered)
                    .fixedSize()
                    .disabled((!updater.canCheckForUpdates && updater.availableVersion == nil)
                        || updater.isInstalling || model.isMutating || model.isAddingAccount)
            }
            .modifier(SettingsRowLayout())
            if let error = updater.lastError {
                Text(model.text("update_check_failed") + " " + error)
                    .foregroundStyle(.orange)
                    .modifier(SettingsDetail())
            }
                }
                .padding(.bottom, 6)
            }
            .frame(maxHeight: 520)
        }
        .font(.system(size: 13))
        .controlSize(.small)
        .onAppear { model.refreshLaunchAtLoginStatus() }
        .onDisappear {
            authorityHmacKey = ""
            authorityCertificatePin = ""
        }
    }

    private var authorityReadOnlySection: some View {
        DisclosureGroup("공통 Sender Authority · 현재 소유권 조회 (읽기 전용)") {
            VStack(alignment: .leading, spacing: 8) {
                Text("서버와 등록된 기기 정보가 준비된 경우에만 한 번 조회합니다. " +
                     "이 결과로 Sender가 활성화되거나 Telegram을 보내지 않습니다.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                TextField("HTTPS Authority 서버", text: $authorityOrigin)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("HTTPS Authority 서버")
                TextField("등록된 장치 ID", text: $authorityDeviceID)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("등록된 장치 ID")
                SecureField("장치 HMAC 키 64자리 (저장 안 함)",
                            text: $authorityHmacKey)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("장치 HMAC 키 64자리 (저장 안 함)")
                TextField("서버 인증서 SHA256 64자리",
                          text: $authorityCertificatePin)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("서버 인증서 SHA256 64자리")
                Text(authorityStatus)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Authority 상태 1회 조회 (발송 없음)") {
                    inspectAuthorityReadOnlyOnce()
                }
                .buttonStyle(.bordered)
                .disabled(authorityReading)
            }
            .padding(.top, 8)
        }
        .modifier(SettingsDetail())
    }

    private func inspectAuthorityReadOnlyOnce() {
        guard !authorityReading else { return }
        let origin = authorityOrigin.trimmingCharacters(
            in: .whitespacesAndNewlines)
        let device = authorityDeviceID.trimmingCharacters(
            in: .whitespacesAndNewlines)
        let hmacKey = authorityHmacKey
        let certPin = authorityCertificatePin.trimmingCharacters(
            in: .whitespacesAndNewlines)
        authorityHmacKey = ""
        guard let client = MacSenderAuthorityClient(
            origin: origin, deviceID: device,
            keyHex: hmacKey, certificateSHA256: certPin)
        else {
            authorityStatus = "Authority: 입력·인증 정보 검증 불가 · 요청 없음"
            return
        }
        authorityReading = true
        authorityStatus = "Authority: 인증된 상태 조회 중 · Telegram 발송 없음"
        Task { @MainActor in
            let result = await client.status()
            client.dispose()
            authorityStatus = MacAuthorityReadOnlyStatus.describe(result)
            authorityReading = false
        }
    }

    private var telegramRoleSummary: String {
        telegramDeviceRole == "sender" ? "Sender 요청(미승인)" : "Viewer"
    }

    private var telegramRequestSummary: String {
        telegramNotificationsRequested ? "ON(요청)" : "OFF"
    }

    private var rowDivider: some View {
        Divider().padding(.horizontal, 14)
    }

    private func sectionLabel(_ key: String) -> some View {
        Text(model.text(key))
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14)
            .padding(.top, 12)
            .padding(.bottom, 2)
    }

    private func settingRow<Control: View>(
        _ key: String, @ViewBuilder control: () -> Control
    ) -> some View {
        HStack(spacing: 12) {
            Text(model.text(key))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            control()
        }
        .modifier(SettingsRowLayout())
    }

    private func settingSwitch(_ key: String, isOn: Binding<Bool>) -> some View {
        Toggle(model.text(key), isOn: isOn)
            .labelsHidden()
            .toggleStyle(.switch)
            .fixedSize()
    }
}

private struct SettingsRowLayout: ViewModifier {
    func body(content: Content) -> some View {
        content
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .frame(minHeight: 44)
    }
}

private struct SettingsDetail: ViewModifier {
    func body(content: Content) -> some View {
        content
            .font(.system(size: 11))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 14)
            .padding(.bottom, 10)
    }
}
