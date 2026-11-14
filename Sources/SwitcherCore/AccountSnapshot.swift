import Foundation

/// Presentation data only. Credentials and authentication tokens never leave the core.
public struct AccountSnapshot: Encodable, Sendable {
    public struct Row: Encodable, Sendable {
        public let profile: AccountProfile
        public let initials: String
        public let usage: WeeklyUsage?
        public let nonWeeklyUsage: NonWeeklyUsage?
        public let usageError: String?
        public let usageStatus: String
        public let subscription: SubscriptionSnapshot?
    }
    public let accounts: [Row]
    public let activeAccountID: UUID?
    public let settings: AppSettings
    public let isMutating: Bool
    public let isAddingAccount: Bool
    public let activeIdentityConfirmed: Bool
    public let error: String?
    public let strings: [String: String]
}

extension AccountController {
    public var snapshot: AccountSnapshot {
        AccountSnapshot(
            accounts: accounts.map { account in
                AccountSnapshot.Row(profile: account, initials: account.initials,
                    usage: usageStates[account.id]?.displayedUsage,
                    nonWeeklyUsage: usageStates[account.id]?.nonWeeklyUsage,
                    usageError: usageStates[account.id]?.presentationError,
                    usageStatus: usageStates[account.id]?.presentationStatus ?? "idle",
                    subscription: subscriptionSnapshots[account.id])
            },
            activeAccountID: activeAccountID, settings: settings,
            isMutating: isMutating, isAddingAccount: isAddingAccount,
            activeIdentityConfirmed: activeIdentityConfirmed,
            error: visibleError.map { $0.messageKey.map(text) ?? $0.message },
            strings: L10n.allStrings(language: settings.language)
        )
    }
}

private extension UsageViewState {
    var presentationStatus: String {
        switch self {
        case .idle: "idle"
        case .loaded: "loaded"
        case .stale: "stale"
        case .nonWeekly: "nonWeekly"
        case .staleNonWeekly: "staleNonWeekly"
        case .unavailable: "unavailable"
        }
    }
    var presentationError: String? {
        switch self {
        case let .stale(_, error), let .staleNonWeekly(_, error),
             let .unavailable(error): error
        default: nil
        }
    }
}
