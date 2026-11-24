import Foundation

/// Local preview intent only. These settings cannot enable an actual sender.
@MainActor
struct MacGreetingPreviewSettings: Equatable {
    var enabled = false
    var fiveHour = true
    var weekly = true

    var enabledLanes: Set<MacGreetingDeadlinePreview.Lane> {
        guard enabled else { return [] }
        var result: Set<MacGreetingDeadlinePreview.Lane> = []
        if fiveHour { result.insert(.fiveHour) }
        if weekly { result.insert(.weekly) }
        return result
    }

    static func read(from defaults: UserDefaults) -> Self {
        Self(enabled: defaults.bool(forKey:"GreetingPreviewEnabled"),
             fiveHour: defaults.object(forKey:"GreetingPreviewFiveHour") == nil ||
                       defaults.bool(forKey:"GreetingPreviewFiveHour"),
             weekly: defaults.object(forKey:"GreetingPreviewWeekly") == nil ||
                     defaults.bool(forKey:"GreetingPreviewWeekly"))
    }

    var actualSendEnabled: Bool { false }
}
