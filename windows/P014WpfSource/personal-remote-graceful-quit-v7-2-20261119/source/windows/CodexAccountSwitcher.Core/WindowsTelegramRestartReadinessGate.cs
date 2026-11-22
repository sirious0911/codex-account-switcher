namespace CodexAccountSwitcher.Core;

/// <summary>
/// A pure, non-sending reconciliation of restart intent, durable outbox and
/// authenticated single-sender observations. This deliberately cannot arm a
/// Telegram session: status/lease observations are NOT send permission.
/// No network call, persisted credential, Registry change or bot invocation.
/// </summary>
internal enum WindowsTelegramRestartReadinessHold
{
    IntentOff,
    IntentUnreadable,
    SenderSettingsOff,
    LocalSenderUnavailable,
    OutboxUnavailable,
    PreviousSendUnsettled,
    IdentityEnrollmentMissing,
    IdentityEnrollmentRejected,
    AuthorityNotConnected,
    AuthorityResponseUntrusted,
    OtherDeviceOwnsLease,
    SelfLeaseExpired,
    LiveSendTimeFenceNotReady
}

internal sealed record WindowsTelegramRestartReadiness(
    WindowsTelegramRestartReadinessHold Status)
{
    // The current source-only Authority cannot settle real Telegram ACKs.
    // No combination of saved preferences and STATUS GET can authorize send.
    internal bool CanAutomaticallySend => false;

    internal string Description => Status switch
    {
        WindowsTelegramRestartReadinessHold.IntentOff =>
            "자동복구 요청 OFF · 실제 Telegram 자동발송 OFF",
        WindowsTelegramRestartReadinessHold.IntentUnreadable =>
            "자동복구 요청 파일 오류 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.SenderSettingsOff =>
            "Telegram Sender/5H/7D 설정 OFF · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.LocalSenderUnavailable =>
            "이 PC의 Sender 점유 확인 실패 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.OutboxUnavailable =>
            "Telegram 발송 원장 확인 실패 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.PreviousSendUnsettled =>
            "이전 Telegram 발송 결과 미확정 · 중복 발송 차단",
        WindowsTelegramRestartReadinessHold.IdentityEnrollmentMissing =>
            "중앙 Sender 장치 등록 정보 없음 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.IdentityEnrollmentRejected =>
            "중앙 Sender 등록 신원/승인 확인 실패 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.AuthorityNotConnected =>
            "중앙 Sender Authority 미연결 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.AuthorityResponseUntrusted =>
            "중앙 Sender 응답 신원/시각 검증 실패 · 실제 발송 차단",
        WindowsTelegramRestartReadinessHold.OtherDeviceOwnsLease =>
            "다른 기기가 Sender 점유 중 · 중복 발송 차단",
        WindowsTelegramRestartReadinessHold.SelfLeaseExpired =>
            "이 PC의 Sender 점유가 유효하지 않음 · 실제 발송 차단",
        _ => "Sender 확인 중 · 송신시점 예약/ACK 정산 미구현으로 실제 발송 차단"
    };
}

internal static class WindowsTelegramRestartReadinessGate
{
    internal static WindowsTelegramRestartReadiness Inspect(
        WindowsTelegramRestartIntentStatus request,
        TelegramNotificationPreferences? currentPreferences,
        WindowsLocalSenderLeaseState localSenderState,
        IReadOnlyList<WindowsAmanoOutboxItem>? outbox,
        WindowsTelegramAutoResumeRead? durableBoundEnrollment,
        Guid installationId,
        string? userBinding,
        string? authorityBinding,
        WindowsAuthorityResponse? authorityStatus,
        string? enrolledDeviceId,
        DateTimeOffset nowUtc)
    {
        if (request is WindowsTelegramRestartIntentStatus.Missing or
            WindowsTelegramRestartIntentStatus.Disabled)
            return new(WindowsTelegramRestartReadinessHold.IntentOff);
        if (request != WindowsTelegramRestartIntentStatus.Enabled)
            return new(WindowsTelegramRestartReadinessHold.IntentUnreadable);
        var preferences = currentPreferences?.Normalize();
        if (preferences is null || !(preferences.WantsFiveHour || preferences.WantsWeekly))
            return new(WindowsTelegramRestartReadinessHold.SenderSettingsOff);
        if (localSenderState != WindowsLocalSenderLeaseState.LocalClaimHeld)
            return new(WindowsTelegramRestartReadinessHold.LocalSenderUnavailable);
        if (outbox is null)
            return new(WindowsTelegramRestartReadinessHold.OutboxUnavailable);
        if (outbox.Any(item => item.Status is
            (WindowsAmanoOutboxStatus.Reserved or WindowsAmanoOutboxStatus.Uncertain)))
            return new(WindowsTelegramRestartReadinessHold.PreviousSendUnsettled);

        if (durableBoundEnrollment is null ||
            durableBoundEnrollment.Status != WindowsTelegramAutoResumeReadStatus.ValidIntent ||
            !WindowsTelegramAutoResumePolicy.ValidBinding(userBinding) ||
            !WindowsTelegramAutoResumePolicy.ValidBinding(authorityBinding) ||
            installationId == Guid.Empty ||
            !WindowsSenderAuthorityClient.ValidDeviceId(enrolledDeviceId))
            return new(WindowsTelegramRestartReadinessHold.IdentityEnrollmentMissing);

        if (nowUtc.Offset != TimeSpan.Zero ||
            durableBoundEnrollment.Policy is not
                { Desired: true, StructurallyValid: true } enrollment ||
            enrollment.InstallationId != installationId ||
            enrollment.UserBinding != userBinding ||
            enrollment.AuthorityBinding != authorityBinding ||
            enrollment.FiveHour != preferences.WantsFiveHour ||
            enrollment.Weekly != preferences.WantsWeekly ||
            nowUtc < enrollment.ApprovedAtUtc ||
            nowUtc >= enrollment.ExpiresAtUtc)
            return new(WindowsTelegramRestartReadinessHold.IdentityEnrollmentRejected);

        if (authorityStatus is null)
            return new(WindowsTelegramRestartReadinessHold.AuthorityNotConnected);
        if (authorityStatus.Code != "STATUS" ||
            authorityStatus.Lease is null ||
            authorityStatus.AuthorityTimeMs is not long serverTime ||
            Math.Abs(serverTime - nowUtc.ToUnixTimeMilliseconds()) > 30000)
            return new(WindowsTelegramRestartReadinessHold.AuthorityResponseUntrusted);

        var lease = authorityStatus.Lease;
        if (lease.OwnerDevice is not null &&
            !string.Equals(lease.OwnerDevice, enrolledDeviceId, StringComparison.Ordinal))
            return new(WindowsTelegramRestartReadinessHold.OtherDeviceOwnsLease);
        if (lease.OwnerDevice is null || lease.Epoch <= 0 ||
            lease.ExpiresAtMs <= nowUtc.ToUnixTimeMilliseconds() + 2000)
            return new(WindowsTelegramRestartReadinessHold.SelfLeaseExpired);

        // Even an authenticated STATUS proving current ownership is only
        // observational. The server must support live send-time reservation,
        // durable subprocess termination evidence and ACK settlement first.
        return new(WindowsTelegramRestartReadinessHold.LiveSendTimeFenceNotReady);
    }
}
