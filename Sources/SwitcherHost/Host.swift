import Foundation
import SwitcherCore

private struct Request: Decodable, Sendable {
    let id: Int
    let command: String
    var accountID: UUID?
    var value: Bool?
    var language: String?
    var error: String?
}

private struct DesktopAdapter: DesktopControlling {
    let host: Host
    func closeDesktop() async throws { try await host.platform("closeDesktop") }
    func reopenDesktop() async throws { try await host.platform("reopenDesktop") }
}

@MainActor
private final class Host {
    private var controller: AccountController?
    private var nextID = 0
    private var pending: [Int: CheckedContinuation<Void, any Error>] = [:]
    private var commands: [Int: Task<Void, Never>] = [:]
    private let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }()

    func platform(_ method: String, url: URL? = nil) async throws {
        nextID += 1
        let id = nextID
        try await withCheckedThrowingContinuation { continuation in
            pending[id] = continuation
            var message: [String: Any] = ["kind": "platform", "id": id, "method": method]
            if let url { message["url"] = url.absoluteString }
            send(message)
        }
    }

    func accept(_ line: String) {
        do {
            let request = try JSONDecoder().decode(Request.self, from: Data(line.utf8))
            if request.command == "platformReply" {
                guard let continuation = pending.removeValue(forKey: request.id) else { return }
                if let error = request.error { continuation.resume(throwing: HostError.message(error)) }
                else { continuation.resume() }
                return
            }
            guard commands[request.id] == nil else { return }
            commands[request.id] = Task {
                do {
                    try await execute(request)
                    send(["kind": "completed", "id": request.id])
                } catch {
                    send(["kind": "completed", "id": request.id, "error": error.localizedDescription])
                }
                commands[request.id] = nil
            }
        } catch {
            send(["kind": "protocolError", "error": "Invalid host command."])
        }
    }

    private func execute(_ request: Request) async throws {
        if request.command == "initialize" {
            guard controller == nil else { throw HostError.message("Already initialized.") }
            let environment = ProcessInfo.processInfo.environment
            let store = AccountStore(baseURL: environment["CODEX_SWITCHER_DATA_HOME"].map { URL(fileURLWithPath: $0) })
            let codex = CodexClient(openBrowser: { [self] url in
                guard ["https", "http"].contains(url.scheme?.lowercased() ?? "") else {
                    throw HostError.message("Unsupported sign-in URL.")
                }
                try await platform("openBrowser", url: url)
            })
            let model = AccountController(store: store, codex: codex,
                switchService: SwitchService(desktop: DesktopAdapter(host: self), store: store, codex: CredentialIdentity()))
            controller = model
            model.onChange = { [weak self] in self?.sendSnapshot() }
            // Send the first *fresh provider* quota from the already-running
            // 300-second background refresh. This seeds idle near-reset
            // watching without another request or a stale cache replay.
            model.onFreshVerifiedActiveQuota = { [weak self] id, accountID, usage in
                self?.sendActiveQuotaObservation(
                    profileID: id, accountID: accountID, usage: usage)
            }
            await model.startBackgroundUsageRefresh()
            sendSnapshot()
            return
        }
        guard let model = controller else { throw HostError.message("Initialize the host first.") }
        switch request.command {
        case "refresh": model.refreshWeeklyUsage()
        case "refreshActive":
            if let verified = try await model.readVerifiedActiveQuota() {
                sendActiveQuotaObservation(
                    profileID: verified.profileID,
                    accountID: verified.accountID,
                    usage: verified.usage)
            }
        case "add": model.addAccount()
        case "cancelAdd": model.cancelAddingAccount()
        case "register": await model.registerCurrentAccount()
        case "switch":
            guard let id = request.accountID else { throw HostError.message("Missing account ID.") }
            await model.switchAccount(to: id)
        case "remove":
            guard let id = request.accountID else { throw HostError.message("Missing account ID.") }
            await model.removeAccount(id: id)
        case "language":
            guard let raw = request.language, let language = AppLanguage(rawValue: raw) else {
                throw HostError.message("Invalid language.")
            }
            await model.setLanguage(language)
        case "percentage":
            guard let value = request.value else { throw HostError.message("Missing setting.") }
            await model.setShowsMenuBarPercentage(value)
        case "fiveHour":
            guard let value = request.value else { throw HostError.message("Missing setting.") }
            await model.setShowsFiveHourUsage(value)
        case "autoReset":
            guard let value = request.value else { throw HostError.message("Missing setting.") }
            await model.setAutoResetEnabled(value)
        case "statusBarUsageWindow":
            guard let raw = request.language, let window = StatusBarUsageWindow(rawValue: raw) else {
                throw HostError.message("Invalid status bar usage window.")
            }
            await model.setStatusBarUsageWindow(window)
        case "dismissError": model.dismissError()
        default: throw HostError.message("Unknown host command.")
        }
    }

    /// Exact, schema-limited local IPC output accepted by the Windows
    /// SwiftHostStrictEnvelopeV1Review. No token, email, reset-credit ID,
    /// chatbot or device identity is included.
    private func sendActiveQuotaObservation(
        profileID: UUID, accountID: String, usage: WeeklyUsage
    ) {
        let weeklyReset = usage.resetsAt.timeIntervalSince1970
        guard weeklyReset.isFinite, weeklyReset >= 1_577_836_800,
              weeklyReset < 4_102_444_800,
              (0...100).contains(usage.remainingPercent)
        else { return }

        let five: Any
        if let remaining = usage.fiveHourRemainingPercent,
           let reset = usage.fiveHourResetsAt?.timeIntervalSince1970,
           reset.isFinite, reset >= 1_577_836_800,
           reset < 4_102_444_800, (0...100).contains(remaining) {
            five = [
                "usedPercent": 100 - remaining,
                "resetsAt": Int64(reset),
                "windowDurationMins": 300
            ] as [String: Any]
        } else {
            five = NSNull()
        }
        let weekly: [String: Any] = [
            "usedPercent": 100 - usage.remainingPercent,
            "resetsAt": Int64(weeklyReset),
            "windowDurationMins": 10_080
        ]
        let raw: [String: Any] = [
            "accountId": accountID,
            // WeeklyUsage does not prove the optional ordinary-use
            // entitlement. Never invent this authorization state.
            "ordinaryUsageAllowed": NSNull(),
            "rateLimitsByLimitId": [
                "codex": [
                    "planType": NSNull(),
                    "primary": five,
                    "secondary": weekly
                ] as [String: Any]
            ],
            "rateLimitResetCredits": [
                "availableCount": usage.resetCreditsRemaining.map { $0 as Any } ?? NSNull()
            ]
        ]
        let milliseconds = Int64(Date().timeIntervalSince1970 * 1000)
        send([
            "kind": "monitorObservation",
            "profileID": profileID.uuidString.lowercased(),
            "observedAtMs": milliseconds,
            "raw": raw
        ])
    }

    private func sendSnapshot() {
        guard let model = controller else { return }
        do {
            let bytes = try encoder.encode(model.snapshot)
            let state = try JSONSerialization.jsonObject(with: bytes)
            send(["kind": "snapshot", "state": state])
        } catch { send(["kind": "protocolError", "error": "Could not encode account state."]) }
    }

    private func send(_ message: [String: Any]) {
        guard var bytes = try? JSONSerialization.data(withJSONObject: message) else { return }
        bytes.append(0x0A)
        try? FileHandle.standardOutput.write(contentsOf: bytes)
    }

    func shutdown() {
        controller?.cancelAddingAccount()
        controller?.stopBackgroundUsageRefresh()
        for task in commands.values { task.cancel() }
        for continuation in pending.values { continuation.resume(throwing: CancellationError()) }
        pending.removeAll()
    }
}

private enum HostError: LocalizedError {
    case message(String)
    var errorDescription: String? { switch self { case let .message(value): value } }
}

@main
struct SwitcherHost {
    static func main() async {
        let host = Host()
        await Task.detached {
            while let line = readLine() { await host.accept(line) }
        }.value
        host.shutdown()
    }
}
