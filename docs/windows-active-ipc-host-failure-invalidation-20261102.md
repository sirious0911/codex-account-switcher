# Windows active-only IPC: SwiftHost failure invalidation

작성시간: 2026-11-02 22:30 KST

STATUS: WINDOWS_ACTIVE_GATE_HOST_ERROR_REMEDIATED / OFFLINE_88_PASS / PRODUCTION_NO_GO

## Context
- CodexPro dev #15 continues taskbar-adjacent WPF UI QA independently; IPC sender contract and #15 UI source are not changed.
- Existing SwiftHost CoreClient transports failure as `AccountSnapshot.Error` while preserving prior ActiveAccountID and ActiveIdentityConfirmed values. Previously ActiveMonitorPublicationGate.Observe relied only on ActiveAccountID, IsMutating and ActiveIdentityConfirmed, leaving the old Named Pipe connected after process failure. Receiver could retain stale numbers instead of clearing immediately.

## Exact source changes
- windows/CodexAccountSwitcher.Core/ActiveMonitorPublicationGate.cs: fail closed when AccountSnapshot.Error is not empty; OnClientChanged already calls SwitcherLocalPipeSender.Invalidate on changed active profile, disconnecting receiver.
- windows/CodexAccountSwitcher.MonitorChecks/SwitcherLocalPipeSenderTests.cs: synthetic failure invalidation, persistent failure suppression and healthy new-snapshot reopening, keeping existing A to B invalidation and no-old-frame replay tests.
- No new credential storage, login, switch or provider usage calls. No #15 repository changes.

## Verification
- dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release: 88 PASS / 0 FAIL, realCodexReads=0, actualAccountSwitches=0, telegramSends=0.
- dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo: 0 warnings / 0 errors.
- dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release: PASS.
- git diff --check: PASS (existing LF/CRLF conversion warning only).

## Limits / handoff
- Host-failure state transition tested using AccountSnapshot fixture, not full live SwiftHost crash end-to-end; complete deployed behavior remains pending.
- Independent CodexPro dev #15 WPF UI validation, multi-monitor/DPI/auto-hide and separate actual A-B-A live gate remain pending.
- Do not start normal account switcher with missing profile registry under a 'read-only' assumption; its startup can copy active auth.json. Do not reuse consumed A-only live gates.
- Next: CodexPro dev #15 may use updated source after GitHub CI PASS, then supervised integration and separate install gate. No production install/merge/release.
