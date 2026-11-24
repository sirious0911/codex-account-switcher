import Foundation
import Darwin

/// Durable *preview* deduplication. Records cannot authorize a real send,
/// certify a completed turn, or coordinate owners on different machines.
@MainActor
final class MacGreetingPreviewLedger {
    enum Result: Equatable { case recorded, alreadyRecorded }
    enum LedgerError: Error, Equatable { case invalidCandidate, unsafeDirectory, storageUnavailable, corruptRecord }
    private struct Record: Codable, Equatable {
        let version: Int
        let key: String
        let profileID: UUID
        let emailFingerprint: String
        let threadID: String
        let lane: String
        let resetAtMs: Int64
        let disposition: String
    }
    private let directory: URL

    init(directory: URL) throws {
        guard directory.isFileURL else { throw LedgerError.unsafeDirectory }
        let folder = directory.standardizedFileURL
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        let values = try folder.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey, .volumeIsLocalKey])
        let attrs = try FileManager.default.attributesOfItem(atPath: folder.path)
        guard values.isDirectory == true, values.isSymbolicLink != true,
              values.volumeIsLocal == true,
              let mode = attrs[.posixPermissions] as? NSNumber, mode.intValue & 0o077 == 0
        else { throw LedgerError.unsafeDirectory }
        self.directory = folder
    }

    private static func validHash(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy {
            (48...57).contains($0) || (97...102).contains($0)
        }
    }

    func recordPreview(_ candidate: MacGreetingDeadlinePreview.Candidate,
                       binding: MacGreetingThreadProof.Binding,
                       nowMs: Int64) throws -> Result {
        guard candidate.profileID == binding.profileID,
              Self.validHash(candidate.eventKey), Self.validHash(binding.emailFingerprint),
              UUID(uuidString: binding.threadID) != nil,
              candidate.resetAtMs > 0, nowMs >= candidate.resetAtMs,
              nowMs <= 253_402_300_799_000, nowMs - candidate.resetAtMs <= 600_000
        else { throw LedgerError.invalidCandidate }
        let item = Record(version: 1, key: candidate.eventKey,
            profileID: binding.profileID, emailFingerprint: binding.emailFingerprint,
            threadID: binding.threadID, lane: candidate.lane.rawValue,
            resetAtMs: candidate.resetAtMs, disposition: "PREVIEW_ONLY")
        let dir = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard dir >= 0 else { throw LedgerError.unsafeDirectory }
        defer { _ = Darwin.close(dir) }
        let name = candidate.eventKey + ".preview"
        let fd = Darwin.openat(dir, name, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC,
                               mode_t(0o600))
        if fd < 0 {
            guard errno == EEXIST else { throw LedgerError.storageUnavailable }
            let existing = Darwin.openat(dir, name, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC)
            guard existing >= 0 else { throw LedgerError.corruptRecord }
            defer { _ = Darwin.close(existing) }
            var info = stat()
            guard Darwin.fstat(existing, &info) == 0,
                  info.st_mode & S_IFMT == S_IFREG, info.st_nlink == 1,
                  info.st_size > 0, info.st_size <= 2048
            else { throw LedgerError.corruptRecord }
            let data = try FileHandle(fileDescriptor: existing, closeOnDealloc: false)
                .read(upToCount: 2049) ?? Data()
            guard data.count == Int(info.st_size),
                  let saved = try? JSONDecoder().decode(Record.self, from: data),
                  saved == item else { throw LedgerError.corruptRecord }
            return .alreadyRecorded
        }
        defer { _ = Darwin.close(fd) }
        let data = try JSONEncoder().encode(item)
        let bytes = [UInt8](data)
        var offset = 0
        while offset < bytes.count {
            let n = bytes.withUnsafeBytes { raw in
                Darwin.write(fd, raw.baseAddress!.advanced(by: offset), bytes.count - offset)
            }
            if n < 0 && errno == EINTR { continue }
            guard n > 0 else { throw LedgerError.storageUnavailable }
            offset += n
        }
        // A partial marker remains a hold, never an instruction to retry.
        guard Darwin.fsync(fd) == 0, Darwin.fsync(dir) == 0
        else { throw LedgerError.storageUnavailable }
        return .recorded
    }
}
