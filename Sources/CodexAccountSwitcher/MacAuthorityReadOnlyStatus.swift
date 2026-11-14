import Foundation

/// Presentation only. A signed Authority status is never a Telegram permit,
/// an automatic Sender-role change, or consent to send an external message.
enum MacAuthorityReadOnlyStatus {
    static func describe(_ observation: MacSenderAuthorityResult) -> String {
        let suffix = " · 조회 전용 · Telegram 발송 권한 없음"
        guard observation.code == "STATUS",
              let lease = observation.lease,
              let now = observation.authorityTimeMs
        else {
            return "Authority: 연결·인증·응답 검증 불가" + suffix
        }
        guard let owner = lease.ownerDevice else {
            guard lease.expiresAtMs == 0 else {
                return "Authority: 임대 원장 불일치" + suffix
            }
            return "Authority: 조회 시점 Sender 없음 · epoch \(lease.epoch)" + suffix
        }
        guard lease.epoch > 0, lease.expiresAtMs > now else {
            return "Authority: 임대 만료 또는 상태 불명확" + suffix
        }
        let expiry = Date(timeIntervalSince1970:
            Double(lease.expiresAtMs) / 1000)
        guard expiry.timeIntervalSince1970.isFinite,
              (0...253_402_300_799).contains(expiry.timeIntervalSince1970)
        else {
            return "Authority: 임대 시각 검증 불가" + suffix
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return "Authority: 조회 시점 Sender \(owner) · epoch \(lease.epoch)" +
            " · 만료 UTC \(formatter.string(from: expiry))" + suffix
    }
}
