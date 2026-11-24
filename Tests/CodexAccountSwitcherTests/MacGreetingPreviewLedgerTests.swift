import Foundation
import Testing
@testable import CodexAccountSwitcher

@MainActor
struct MacGreetingPreviewLedgerTests {
    private let boundary: Int64 = 1_800_000_000_000
    private let key = String(repeating: "a", count: 64)

    @Test func restartAndSecondInstanceDoNotRecordTwice() throws {
        let folder = FileManager.default.temporaryDirectory.appending(path: "greeting-preview-\(UUID())")
        defer { try? FileManager.default.removeItem(at: folder) }
        let id = UUID()
        let binding = MacGreetingThreadProof.Binding(profileID: id,
            emailFingerprint: String(repeating:"b", count:64),
            threadID: UUID().uuidString.lowercased())
        let event = MacGreetingDeadlinePreview.Candidate(profileID:id,
            lane:.fiveHour, resetAtMs:boundary, eventKey:key)
        #expect(try MacGreetingPreviewLedger(directory:folder).recordPreview(
            event,binding:binding,nowMs:boundary) == .recorded)
        #expect(try MacGreetingPreviewLedger(directory:folder).recordPreview(
            event,binding:binding,nowMs:boundary+1000) == .alreadyRecorded)
        #expect(!binding.actualSendEnabled)
        #expect(try FileManager.default.contentsOfDirectory(atPath:folder.path) == [key+".preview"])
    }

    @Test func conflictStaleAndPartialMarkerAreNeverOverwritten() throws {
        let folder = FileManager.default.temporaryDirectory.appending(path: "greeting-preview-\(UUID())")
        defer { try? FileManager.default.removeItem(at: folder) }
        let ledger = try MacGreetingPreviewLedger(directory:folder)
        let id = UUID()
        let binding = MacGreetingThreadProof.Binding(profileID:id,
            emailFingerprint:String(repeating:"b",count:64),threadID:UUID().uuidString.lowercased())
        let event = MacGreetingDeadlinePreview.Candidate(profileID:id,
            lane:.weekly,resetAtMs:boundary,eventKey:key)
        #expect(throws: MacGreetingPreviewLedger.LedgerError.invalidCandidate) {
            _ = try ledger.recordPreview(event,binding:binding,nowMs:boundary+600_001)
        }
        let wrong = MacGreetingThreadProof.Binding(profileID:UUID(),
            emailFingerprint:binding.emailFingerprint,threadID:binding.threadID)
        #expect(throws: MacGreetingPreviewLedger.LedgerError.invalidCandidate) {
            _ = try ledger.recordPreview(event,binding:wrong,nowMs:boundary)
        }
        let marker = folder.appending(path:key+".preview")
        let partial = Data("partial".utf8)
        try partial.write(to:marker)
        #expect(throws: MacGreetingPreviewLedger.LedgerError.corruptRecord) {
            _ = try ledger.recordPreview(event,binding:binding,nowMs:boundary)
        }
        #expect(try Data(contentsOf:marker)==partial)
    }
}
