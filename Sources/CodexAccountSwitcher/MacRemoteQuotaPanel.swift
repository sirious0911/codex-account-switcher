import SwiftUI

/// A second-device read-only view. It never modifies the local active account
/// or changes Telegram Sender/Viewer ownership.
struct MacRemoteQuotaPanel: View {
    @ObservedObject var viewer: MacRemoteQuotaViewerModel
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

            if !viewer.isConnected {
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
                if enrollmentError {
                    Text("HTTPS 주소·기기 ID·조회 자격정보를 확인하세요.")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                Button("원격 사용량 연결") {
                    do {
                        try viewer.connect(
                            origin: httpsOrigin,
                            deviceID: sourceDeviceID,
                            credential: viewerCredential
                        )
                        viewerCredential = ""
                        enrollmentError = false
                        Task { await viewer.refresh() }
                    } catch {
                        // Do not disclose credentials or URLs in error messages.
                        viewerCredential = ""
                        enrollmentError = true
                    }
                }
                .buttonStyle(.bordered)
            } else {
                Text("원격 소스: \(viewer.sourceDeviceID ?? "?")")
                    .font(.caption)
                if let snapshot = viewer.displaySnapshot {
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
                Text("상태: \(viewer.status) · 원격 R은 P3 V1 미지원")
                    .font(.system(size: 10))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 8) {
                    Button(viewer.isFetching ? "조회 중" : "원격 새로고침") {
                        Task { await viewer.refresh() }
                    }
                    .disabled(viewer.isFetching)
                    Button("원격 연결 해제") { viewer.disconnect() }
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
                if viewer.isConnected { await viewer.refresh() }
                do { try await Task.sleep(for: .seconds(360)) }
                catch { break }
            }
        }
    }
}
