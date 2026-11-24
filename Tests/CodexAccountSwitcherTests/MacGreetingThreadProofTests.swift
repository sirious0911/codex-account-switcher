import Foundation
import Testing
@testable import CodexAccountSwitcher

struct MacGreetingThreadProofTests {
    private let selected = "11111111-1111-4111-8111-111111111111"
    private let other = "22222222-2222-4222-8222-222222222222"
    private let title = MacGreetingThreadProof.dedicatedTitle
    private func account(_ email: String = "mock@example.test") throws -> Data {
        try JSONSerialization.data(withJSONObject: ["account": ["type":"chatgpt","email":email]])
    }
    private func catalog(duplicate: Bool = false, incomplete: Bool = false) throws -> Data {
        var entries = [["id":selected,"name":title]]
        if duplicate { entries.append(["id":other,"name":title]) }
        var result: [String: Any] = ["data":entries]
        if incomplete { result["nextCursor"] = "more" }
        return try JSONSerialization.data(withJSONObject: result)
    }
    private func thread(id: String? = nil, ephemeral: Bool = false,
                        status: String = "idle") throws -> Data {
        try JSONSerialization.data(withJSONObject: ["thread": [
            "id":id ?? selected, "ephemeral":ephemeral,
            "path":"/synthetic/thread.jsonl", "name":title,
            "status":["type":status]]])
    }
    @Test func proofNeverEnablesSend() throws {
        let identity = MacGreetingThreadProof.Identity(profileID: UUID(),
            email:" Mock@Example.Test ", confirmed:true, busy:false)
        let b = try #require(MacGreetingThreadProof.verify(before:identity,after:identity,
            selectedID:selected, accountBefore:account(), catalog:catalog(),
            thread:thread(), accountAfter:account()))
        #expect(b.threadID == selected)
        #expect(b.emailFingerprint.count == 64)
        #expect(!b.actualSendEnabled)
        let restored = try JSONDecoder().decode(MacGreetingThreadProof.Binding.self,
            from: JSONEncoder().encode(b))
        #expect(restored == b)
        #expect(!restored.actualSendEnabled)
    }
    @Test func wrongAccountDuplicateIncompleteAndUnsafeThreadDenied() throws {
        let identity = MacGreetingThreadProof.Identity(profileID: UUID(),
            email:"mock@example.test", confirmed:true, busy:false)
        for (list, detail, afterAccount) in [
            (try catalog(duplicate:true), try thread(), try account()),
            (try catalog(incomplete:true), try thread(), try account()),
            (try catalog(), try thread(id:other), try account()),
            (try catalog(), try thread(ephemeral:true), try account()),
            (try catalog(), try thread(status:"active"), try account()),
            (try catalog(), try thread(), try account("other@example.test"))
        ] {
            #expect(MacGreetingThreadProof.verify(before:identity,after:identity,
                selectedID:selected, accountBefore:try account(),catalog:list,
                thread:detail,accountAfter:afterAccount) == nil)
        }
        let changed = MacGreetingThreadProof.Identity(profileID: UUID(),
            email:identity.email, confirmed:true, busy:false)
        #expect(try MacGreetingThreadProof.verify(before:identity,after:changed,
            selectedID:selected, accountBefore:account(),catalog:catalog(),
            thread:thread(),accountAfter:account()) == nil)
    }
}
