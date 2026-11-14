import Foundation

/// Provider-neutral contracts. This layer has no credentials, network requests,
/// background tasks, account changes, or outgoing notifications.
public enum AIServiceKind: String, Codable, CaseIterable, Hashable, Sendable {
    case codex
    case claudeCode
    case claudeDesktop
}

public enum AIServiceCapability: String, Codable, Hashable, Sendable {
    case readUsage
    case manualAccountSwitch
    case readResetCredits
}

/// Stable provider account identity. Never use a display name or email as its ID.
/// IDs must be safe, non-sensitive identifiers supplied by provider adapters.
public struct AIAccountKey: Codable, Hashable, Sendable {
    public let service: AIServiceKind
    public let accountID: String

    public init?(service: AIServiceKind, accountID: String) {
        let normalized = accountID.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !normalized.isEmpty, normalized.utf8.count <= 256,
              !normalized.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        else { return nil }
        self.service = service
        self.accountID = normalized
    }
}

public struct AIAccountSummary: Codable, Equatable, Sendable {
    public let key: AIAccountKey
    public let displayName: String
    public let isActive: Bool

    public init(key: AIAccountKey, displayName: String, isActive: Bool) {
        self.key = key
        self.displayName = displayName
        self.isActive = isActive
    }
}

public enum AIUsageWindowKind: String, Codable, Hashable, Sendable {
    case fiveHour
    case weekly
    case other
}

/// nil is UNKNOWN, not 0%. A window is scoped to a service-owned limit ID.
public struct AIUsageWindow: Codable, Equatable, Sendable {
    public let kind: AIUsageWindowKind
    public let providerWindowID: String?
    public let remainingPercent: Double?
    public let resetsAt: Date?

    public init?(kind: AIUsageWindowKind, providerWindowID: String? = nil,
                 remainingPercent: Double?, resetsAt: Date?) {
        if let percent = remainingPercent {
            guard percent.isFinite && (0...100).contains(percent) else { return nil }
        }
        if let providerWindowID {
            guard !providerWindowID.isEmpty, providerWindowID.utf8.count <= 128 else { return nil }
        }
        self.kind = kind
        self.providerWindowID = providerWindowID
        self.remainingPercent = remainingPercent
        self.resetsAt = resetsAt
    }
}

public enum AIDataFreshness: String, Codable, Sendable {
    case fresh
    case stale
    case unknown
}

/// Data passed to UI/events, never a credential-bearing raw service response.
public struct AIUsageSnapshot: Codable, Equatable, Sendable {
    public let account: AIAccountKey
    public let observedAt: Date?
    public let freshness: AIDataFreshness
    public let windows: [AIUsageWindow]
    public let resetCreditsRemaining: Int?

    public init?(account: AIAccountKey, observedAt: Date?, freshness: AIDataFreshness,
                 windows: [AIUsageWindow], resetCreditsRemaining: Int?) {
        if let credits = resetCreditsRemaining, credits < 0 { return nil }
        if freshness == .fresh && observedAt == nil { return nil }
        self.account = account
        self.observedAt = observedAt
        self.freshness = freshness
        self.windows = windows
        self.resetCreditsRemaining = resetCreditsRemaining
    }
}

/// Privileged mutations are separated from read-only account observation.
public protocol AIUsageReading: Sendable {
    var service: AIServiceKind { get }
    var capabilities: Set<AIServiceCapability> { get }
    func listAccounts() async throws -> [AIAccountSummary]
    func readUsage(for account: AIAccountKey) async throws -> AIUsageSnapshot
}

public protocol AIManualAccountSwitching: Sendable {
    var service: AIServiceKind { get }
    /// Implementations must only be called after an explicit user selection.
    func switchAccount(to account: AIAccountKey) async throws
}

/// Logical deduplication key, not delivery state or exactly-once proof.
public struct AIAlertEventKey: Codable, Hashable, Sendable {
    public enum EventType: String, Codable, Hashable, Sendable {
        case unusualCapacityRecovery
        case resetCreditIncrease
    }

    public let account: AIAccountKey
    public let type: EventType
    public let limitID: String
    public let periodIdentity: String
    public let creditID: String?

    public init?(account: AIAccountKey, type: EventType, limitID: String,
                 periodIdentity: String, creditID: String? = nil) {
        guard !limitID.isEmpty, !periodIdentity.isEmpty,
              limitID.utf8.count <= 128, periodIdentity.utf8.count <= 128,
              !limitID.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              !periodIdentity.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) })
        else { return nil }
        if let creditID {
            guard !creditID.isEmpty, creditID.utf8.count <= 128 else { return nil }
        }
        self.account = account
        self.type = type
        self.limitID = limitID
        self.periodIdentity = periodIdentity
        self.creditID = creditID
    }
}
