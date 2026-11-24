import Darwin
import Foundation

/// SOURCE-ONLY local send boundary for a future, single Mac mini Telegram
/// executor. Not connected to AppModel, Keychain, a bot or any network code.
/// A marker is persisted BEFORE a caller may start an external send.
@MainActor
final class MacTelegramLocalOutbox {
    enum OutboxError: Error, Equatable {
        case invalidEvent
        case unsafeDirectory
        case anotherExecutorActive
        case corruptLedger
        case notPending
        case notReserved
        case manualReviewRequired
        case storageUnavailable
    }

    enum Status: Equatable {
        case absent
        case pending
        case reservedBeforeSend
        case acknowledged
        case uncertainNoRetry
    }

    struct ConfirmedRecovery {
        let lane: MacTelegramQuotaEventIdentity.Lane
        let accountIdSHA256: String
        let previousResetAtUnixMs: Int64
        let currentResetAtUnixMs: Int64
        let observedAtUnixMs: Int64
        /// Must come from a future verified TWO-reading recovery detector.
        /// Merely setting this flag never authorizes an actual send.
        let confirmedByTwoObservations: Bool
    }

    private struct Pending: Codable {
        let version: Int
        let eventKey: String
        let lane: String
        let previousResetAtUnixMs: Int64
        let currentResetAtUnixMs: Int64
        let observedAtUnixMs: Int64
    }

    private struct Receipt: Codable {
        let version: Int
        let eventKey: String
        let messageId: Int64
    }

    private let directory: URL
    private let lockFD: Int32
    private let files = FileManager.default

    init(directory: URL) throws {
        guard directory.isFileURL else { throw OutboxError.unsafeDirectory }
        let folder = directory.standardizedFileURL
        do {
            try FileManager.default.createDirectory(at: folder,
                withIntermediateDirectories: true,
                attributes: [.posixPermissions: 0o700])
            let resource = try folder.resourceValues(forKeys: [
                .isDirectoryKey, .isSymbolicLinkKey, .volumeIsLocalKey
            ])
            let attributes = try FileManager.default.attributesOfItem(atPath: folder.path)
            guard resource.isDirectory == true, resource.isSymbolicLink != true,
                  resource.volumeIsLocal == true,
                  let permissions = attributes[.posixPermissions] as? NSNumber,
                  permissions.intValue & 0o077 == 0
            else { throw OutboxError.unsafeDirectory }
        } catch let error as OutboxError {
            throw error
        } catch {
            throw OutboxError.unsafeDirectory
        }

        // flock is an OS-process lock. A second running app instance cannot
        // start a sender against this same local filesystem directory.
        let lock = Darwin.open(folder.appending(path: ".executor.lock").path,
            O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, mode_t(0o600))
        guard lock >= 0 else { throw OutboxError.storageUnavailable }
        guard Darwin.flock(lock, LOCK_EX | LOCK_NB) == 0 else {
            _ = Darwin.close(lock)
            throw OutboxError.anotherExecutorActive
        }
        self.directory = folder
        self.lockFD = lock
    }

    deinit {
        _ = Darwin.flock(lockFD, LOCK_UN)
        _ = Darwin.close(lockFD)
    }

    /// Staging only. A recovery candidate is NEVER a permission to send.
    @discardableResult
    func stage(_ candidate: ConfirmedRecovery) throws -> String {
        guard candidate.confirmedByTwoObservations,
              candidate.observedAtUnixMs > 0,
              candidate.currentResetAtUnixMs > candidate.observedAtUnixMs,
              let key = MacTelegramQuotaEventIdentity.build(
                lane: candidate.lane,
                accountIdSHA256: candidate.accountIdSHA256,
                previousResetAtUnixMs: candidate.previousResetAtUnixMs,
                currentResetAtUnixMs: candidate.currentResetAtUnixMs)
        else { throw OutboxError.invalidEvent }
        let pending = Pending(version: 1, eventKey: key,
            lane: candidate.lane.canonicalKind,
            previousResetAtUnixMs: candidate.previousResetAtUnixMs,
            currentResetAtUnixMs: candidate.currentResetAtUnixMs,
            observedAtUnixMs: candidate.observedAtUnixMs)
        if exists(key, "pending") {
            let previous = try readPending(key)
            guard previous.eventKey == pending.eventKey,
                  previous.lane == pending.lane,
                  previous.previousResetAtUnixMs == pending.previousResetAtUnixMs,
                  previous.currentResetAtUnixMs == pending.currentResetAtUnixMs
            else { throw OutboxError.corruptLedger }
            return key
        }
        try writeExclusive(key, suffix: "pending",
            data: JSONEncoder().encode(pending))
        return key
    }

    func status(_ key: String) throws -> Status {
        guard Self.validKey(key) else { throw OutboxError.invalidEvent }
        if !exists(key, "pending") { return .absent }
        _ = try readPending(key)
        let reserved = exists(key, "reserved")
        let acknowledged = exists(key, "ack")
        let uncertain = exists(key, "uncertain")
        if acknowledged && uncertain { throw OutboxError.corruptLedger }
        if (acknowledged || uncertain) && !reserved {
            throw OutboxError.corruptLedger
        }
        if reserved { try ensureRegularFile(key, "reserved") }
        if acknowledged {
            let receipt = try readReceipt(key)
            guard receipt.version == 1, receipt.eventKey == key,
                  receipt.messageId > 0
            else { throw OutboxError.corruptLedger }
            return .acknowledged
        }
        if uncertain {
            try ensureRegularFile(key, "uncertain")
            return .uncertainNoRetry
        }
        return reserved ? .reservedBeforeSend : .pending
    }

    /// Explicit route binding for a future local bot executor. A 5H event may
    /// never be routed to the 7D alert token (or the reverse).
    func isPending(_ key: String,
                   for lane: MacTelegramQuotaEventIdentity.Lane) throws -> Bool {
        guard try status(key) == .pending else { return false }
        return try readPending(key).lane == lane.canonicalKind
    }

    /// This is a local irreversible one-shot reservation, not a send call.
    /// The marker is fsynced before the method can return successfully.
    func reserveBeforeSend(_ key: String) throws {
        guard try status(key) == .pending else { throw OutboxError.notPending }
        if try hasUnresolvedSend() { throw OutboxError.manualReviewRequired }
        try writeExclusive(key, suffix: "reserved", data: Data("RESERVED_V1".utf8))
    }

    /// A future SAME-HOST supervised transport may call this only after its
    /// child or HTTP task has truly completed and parsed a matching Bot API
    /// success. Never accept a remote client's asserted messageId here.
    func acknowledgeCompletedLocalSend(_ key: String, messageId: Int64) throws {
        guard messageId > 0 else { throw OutboxError.invalidEvent }
        guard try status(key) == .reservedBeforeSend else {
            throw OutboxError.notReserved
        }
        try writeExclusive(key, suffix: "ack",
            data: JSONEncoder().encode(
                Receipt(version: 1, eventKey: key, messageId: messageId)))
    }

    /// No retry after timeout/crash/unknown result. This also blocks any new
    /// sends until a separate, explicitly reviewed operator procedure exists.
    func markUncertainNoRetry(_ key: String) throws {
        guard try status(key) == .reservedBeforeSend else {
            throw OutboxError.notReserved
        }
        try writeExclusive(key, suffix: "uncertain", data: Data("UNCERTAIN_V1".utf8))
    }

    func hasUnresolvedSend() throws -> Bool {
        let names: [String]
        do { names = try files.contentsOfDirectory(atPath: directory.path) }
        catch { throw OutboxError.storageUnavailable }
        // Validate ALL relevant markers, not just the event being sent:
        // unrelated corrupt history must never be silently skipped.
        let markerSuffixes = [".pending", ".reserved", ".ack", ".uncertain"]
        for name in names {
            guard let suffix = markerSuffixes.first(where: { name.hasSuffix($0) })
            else { continue }
            let key = String(name.dropLast(suffix.count))
            guard Self.validKey(key) else { throw OutboxError.corruptLedger }
            let current = try status(key)
            if current == .reservedBeforeSend || current == .uncertainNoRetry {
                return true
            }
            if current == .absent { throw OutboxError.corruptLedger }
        }
        return false
    }

    private static func validKey(_ key: String) -> Bool {
        key.utf8.count == 64 && key.utf8.allSatisfy {
            (48...57).contains(Int($0)) || (97...102).contains(Int($0))
        }
    }

    private func url(_ key: String, _ suffix: String) -> URL {
        directory.appending(path: key + "." + suffix)
    }

    private func exists(_ key: String, _ suffix: String) -> Bool {
        files.fileExists(atPath: url(key, suffix).path)
    }

    private func ensureRegularFile(_ key: String, _ suffix: String) throws {
        do {
            let attrs = try files.attributesOfItem(atPath: url(key, suffix).path)
            guard attrs[.type] as? FileAttributeType == .typeRegular else {
                throw OutboxError.corruptLedger
            }
        } catch let error as OutboxError { throw error }
        catch { throw OutboxError.corruptLedger }
    }

    private func readPending(_ key: String) throws -> Pending {
        try ensureRegularFile(key, "pending")
        do {
            let bytes = try Data(contentsOf: url(key, "pending"))
            guard bytes.count <= 2048 else { throw OutboxError.corruptLedger }
            let pending = try JSONDecoder().decode(Pending.self, from: bytes)
            guard pending.version == 1, pending.eventKey == key,
                  (pending.lane == "FIVE_HOUR_100" || pending.lane == "WEEKLY_100"),
                  pending.currentResetAtUnixMs > pending.previousResetAtUnixMs,
                  pending.currentResetAtUnixMs > pending.observedAtUnixMs,
                  pending.observedAtUnixMs > 0
            else { throw OutboxError.corruptLedger }
            return pending
        } catch { throw OutboxError.corruptLedger }
    }

    private func readReceipt(_ key: String) throws -> Receipt {
        try ensureRegularFile(key, "ack")
        do {
            let bytes = try Data(contentsOf: url(key, "ack"))
            guard bytes.count <= 512 else { throw OutboxError.corruptLedger }
            return try JSONDecoder().decode(Receipt.self, from: bytes)
        } catch { throw OutboxError.corruptLedger }
    }

    private func writeExclusive(_ key: String, suffix: String, data: Data) throws {
        guard Self.validKey(key), !data.isEmpty, data.count <= 2048 else {
            throw OutboxError.invalidEvent
        }
        let destination = url(key, suffix).path
        let fd = Darwin.open(destination,
            O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW, mode_t(0o600))
        guard fd >= 0 else {
            throw errno == EEXIST ? OutboxError.notPending :
                OutboxError.storageUnavailable
        }
        defer { _ = Darwin.close(fd) }
        let bytes = [UInt8](data)
        var offset = 0
        while offset < bytes.count {
            let written = bytes.withUnsafeBytes { raw -> Int in
                guard let pointer = raw.baseAddress else { return -1 }
                return Darwin.write(fd, pointer.advanced(by: offset),
                    bytes.count - offset)
            }
            if written < 0 && errno == EINTR { continue }
            guard written > 0 else { throw OutboxError.storageUnavailable }
            offset += written
        }
        guard Darwin.fsync(fd) == 0 else {
            throw OutboxError.storageUnavailable
        }
        // Ensure the new directory entry is also committed. An fsync failure
        // refuses sending even though the marker may already exist.
        let dirFD = Darwin.open(directory.path,
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
        guard dirFD >= 0 else { throw OutboxError.storageUnavailable }
        defer { _ = Darwin.close(dirFD) }
        guard Darwin.fsync(dirFD) == 0 else {
            throw OutboxError.storageUnavailable
        }
        // A file with incomplete contents is an immutable hold, never a
        // reason to retry or overwrite an event after a sudden process exit.
    }
}
