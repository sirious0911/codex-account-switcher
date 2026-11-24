import Foundation
import Testing
@testable import SwitcherCore
@testable import CodexAccountSwitcher

struct MacGreetingReadEvidenceTests {
    private let id = "11111111-1111-4111-8111-111111111111"
    private func value(_ object: [String:Any]) throws -> JSONValue {
        try JSONDecoder().decode(JSONValue.self, from: JSONSerialization.data(withJSONObject:object))
    }

    @Test func fourReadSequenceAndProofRemainReadOnly() async throws {
        var steps: [CodexExistingThreadReadEvidence.Step] = []
        let result = try await CodexExistingThreadReadEvidence.collect(selectedID:id) { step in
            steps.append(step)
            switch step {
            case .accountBefore, .accountAfter:
                #expect(step.parameters(selectedID:id)["refreshToken"] as? Bool == false)
                return try value(["account":["type":"chatgpt","email":"mock@example.test"]])
            case .catalog:
                #expect(step.parameters(selectedID:id)["currentAccountOnly"] as? Bool == true)
                return try value(["data":[["id":id,"name":MacGreetingThreadProof.dedicatedTitle]]])
            case .thread:
                #expect(step.parameters(selectedID:id)["includeTurns"] as? Bool == false)
                #expect(step.parameters(selectedID:id)["threadId"] as? String == id)
                return try value(["thread":["id":id,"ephemeral":false,"path":"/synthetic/thread.jsonl",
                    "name":MacGreetingThreadProof.dedicatedTitle,"status":["type":"idle"]]])
            }
        }
        #expect(steps.map(\.method) == ["account/read","thread/list","thread/read","account/read"])
        let active = MacGreetingThreadProof.Identity(profileID:UUID(),email:"mock@example.test",confirmed:true,busy:false)
        let proof = try #require(MacGreetingThreadProof.verify(before:active,after:active,
            selectedID:id,accountBefore:result.accountBefore,catalog:result.catalog,
            thread:result.thread,accountAfter:result.accountAfter))
        #expect(!proof.actualSendEnabled)
    }

    @Test func incompleteCatalogStopsBeforeThreadRead() async throws {
        var steps: [String] = []
        do {
            _ = try await CodexExistingThreadReadEvidence.collect(selectedID:id) { step in
                steps.append(step.method)
                if step == .accountBefore {
                    return try value(["account":["type":"chatgpt","email":"mock@example.test"]])
                }
                return try value(["data":[],"nextCursor":"more"])
            }
            Issue.record("Incomplete catalog accepted")
        } catch {}
        #expect(steps == ["account/read","thread/list"])
    }
}
