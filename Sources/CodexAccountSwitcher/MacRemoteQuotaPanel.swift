import SwiftUI

/// A second-device read-only view. It never modifies the local active account
/// or changes Telegram Sender/Viewer ownership.
struct MacRemoteQuotaPanel: View {
    @ObservedObject var viewer: MacRemoteQuotaViewerModel
    @StateObject private var v2Viewer = MacRemoteQuotaV2ViewerModel()
    @State private var useV2 = false
    @State private var httpsOrigin = ""
    @State private var sourceDeviceID = ""
    @State private var viewerCredential = ""
    @State private var enrollmentError = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("원격 기기 사용량 (P3)")
                .font(.system(size: 12, weight: .semibold))

            Text("이 Mac의 로컬 사용량과 별도로 표시합니다. 원격 조회는 수동 연결 후에만 시작되며, 인증정보는 이 실행 중 메모리에만 보관합니다.")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if !viewer.isConnected && !v2Viewer.isConnected {
                Text("원격 5H ? · 원격 7D ? · R ?")
                    .font(.system(size: 11, weight: .medium).monospacedDigit())
                TextField("HTTPS 주소 (https://.../)", text: $httpsOrigin)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("원격 HTTPS 주소")
                TextField("원격 기기 ID", text: $sourceDeviceID)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("원격 기기 ID")
                SecureField("조회 전용 자격정보 (저장 안 함)", text: $viewerCredential)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("원격 조회 자격정보")
                Toggle("V2 조회 (R 초기화권·만료일 포함)", isOn: $useV2)
                    .toggleStyle(.checkbox)
                    .font(.caption)
                if enrollmentError {
                    Text("HTTPS 주소·기기 ID·조회 자격정보를 확인하세요.")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                Button("원격 사용량 연결") {
                    do {
                        if useV2 {
                            try v2Viewer.connect(
                                origin: httpsOrigin, deviceID: sourceDeviceID,
                                credential: viewerCredential)
                        } else {
                            try viewer.connect(
                                origin: httpsOrigin, deviceID: sourceDeviceID,
                                credential: viewerCredential)
                        }
                        viewerCredential = ""
                        enrollmentError = false
                        Task {
                            if useV2 { await v2Viewer.refresh() }
                            else { await viewer.refresh() }
                        }
                    } catch {
                        // Do not disclose credentials or URLs in error messages.
                        viewerCredential = ""
                        enrollmentError = true
                    }
                }
                .buttonStyle(.bordered)
            } else {
                Text("원격 소스: \(v2Viewer.isConnected ? (v2Viewer.sourceDeviceID ?? "?") : (viewer.sourceDeviceID ?? "?"))")
                    .font(.caption)
                if v2Viewer.isConnected, let snapshot = v2Viewer.displaySnapshot {
                    let five = snapshot.fiveHourPercent.map { "\($0)%" } ?? "?"
                    let seven = snapshot.sevenDayPercent.map { "\($0)%" } ?? "?"
                    let credits = snapshot.resetCreditsRemaining.map { String($0) } ?? "?"
                    Text("원격 5H \(five) · 원격 7D \(seven) · R \(credits)")
                        .font(.system(size: 12, weight: .medium).monospacedDigit())
                    Text("관측: \(snapshot.observedAt.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if let reset = snapshot.fiveHourReset {
                        Text("5H 초기화: \(reset.formatted(date: .abbreviated, time: .shortened))")
                            .font(.caption)
                    }
                    if let reset = snapshot.sevenDayReset {
                        Text("7D 초기화: \(reset.formatted(date: .abbreviated, time: .shortened))")
                            .font(.caption)
                    }
                    if let count = snapshot.resetCreditsRemaining, count > 0 {
                        ForEach(0..<min(count, 2), id: \.self) { index in
                            let expiry = (snapshot.resetCreditExpiries?.count ?? 0) > index
                                ? snapshot.resetCreditExpiries?[index] : nil
                            if let date = expiry {
                                Text("R\(index + 1) 만료: \(date.formatted(date: .abbreviated, time: .shortened))" +
                                     (date <= Date() ? " (만료 경과·새로고침 필요)" : ""))
                                    .font(.caption)
                            } else {
                                Text("R\(index + 1) 만료: UNKNOWN (만료일 미제공)")
                                    .font(.caption)
                            }
                        }
                    }
                } else if let snapshot = viewer.displaySnapshot {
                    let five = snapshot.fiveHourPercent.map { "\($0)%" } ?? "?"
                    let seven = snapshot.sevenDayPercent.map { "\($0)%" } ?? "?"
                    Text("원격 5H \(five) · 원격 7D \(seven) · R ?")
                        .font(.system(size: 12, weight: .medium).monospacedDigit())
                    Text("관측: \(snapshot.observedAt.formatted(date: .abbreviated, time: .shortened))")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    if let reset = snapshot.fiveHourReset {
                        Text("5H 초기화: \(reset.formatted(date: .abbreviated, time: .shortened))")
                            .font(.caption)
                    }
                    if let reset = snapshot.sevenDayReset {
                        Text("7D 초기화: \(reset.formatted(date: .abbreviated, time: .shortened))")
                            .font(.caption)
                    }
                } else {
                    Text("원격 5H ? · 원격 7D ? · R ?")
                        .font(.system(size: 12, weight: .medium).monospacedDigit())
                }
                Text(v2Viewer.isConnected
                     ? "상태: \(v2Viewer.status) · V2 조회 전용"
                     : "상태: \(viewer.status) · 원격 R은 P3 V1 미지원")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    Button((v2Viewer.isConnected ? v2Viewer.isFetching : viewer.isFetching)
                           ? "조회 중" : "원격 새로고침") {
                        Task {
                            if v2Viewer.isConnected { await v2Viewer.refresh() }
                            else { await viewer.refresh() }
                        }
                    }
                    .disabled(v2Viewer.isConnected ? v2Viewer.isFetching : viewer.isFetching)
                    Button("원격 연결 해제") {
                        if v2Viewer.isConnected { v2Viewer.disconnect() }
                        else { viewer.disconnect() }
                    }
                }
                .buttonStyle(.bordered)
            }
            Text("원격 조회는 계정 전환·Telegram 발송 권한을 부여하지 않습니다.")
                .font(.system(size: 10))
                .foregroundStyle(.secondary)
        }
        .task {
            // Read-only 6 minute refresh while this monitor page is visible.
            while !Task.isCancelled {
                if v2Viewer.isConnected { await v2Viewer.refresh() }
                else if viewer.isConnected { await viewer.refresh() }
                do { try await Task.sleep(for: .seconds(360)) }
                catch { break }
            }
        }
    }
}
