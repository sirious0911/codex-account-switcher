# Codex 계정전환 개발팀 #3 — A/B 전환 지연 관측 복구 및 통합 합성시험

작성시간: 2026-11-03 08:11:43 KST

## 상태
- `INTEGRATED_A_B_TRANSITION_OFFLINE_PASS`
- `STALE_REPLAY_MUST_NOT_CLEAR_NEW_ACCOUNT_PASS`
- `MALFORMED_STALE_REPLAY_MUST_NOT_CLEAR_NEW_ACCOUNT_PASS`
- `MONITOR_SYNTHETIC_151_OF_151_PASS`
- `WPF_UI_SYNTHETIC_PASS`
- `WINDOWS_RELEASE_BUILD_WARN0_ERROR0`
- `PRODUCTION_NO_GO`

## 대상
- 개인 Fork: https://github.com/sirious0911/codex-account-switcher
- 브랜치: `feature/provider-extensibility`
- 변경 전 기준 Commit: `a6245e8d304fb4f4dd14fbc9903b2ddd2cb95211`
- 외부 원본 upstream과 개인 main은 수정하지 않음.

## 독립 합성 상태전이 시험
신규 `windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarGateTransitionTests.cs`:
- 미확인 활성 계정은 표시 거부.
- 확인된 A만 표시 가능; 백그라운드 B/프로필 미지정 값 배제.
- SwiftHost 오류 시 기존 A의 사용량 즉시 폐기, 오류 지속 동안 차단.
- 정상 A 복구 뒤 새로운 관측만 허용.
- A→B 전환 요청 즉시 구 A 표시 지우고, 전환 진행 중 양쪽 계정 데이터 거부.
- B 전환 완료 후 새 B 관측 표시. 늦게 도착한 A/오래된 B 관측값 차단.
- B 활성 상태에서 식별 불확실 판정 시 기존 데이터 폐기. 다시 확인된 B에서 컷오프 이후 관측만 허용.
- 어떤 경우에도 source account 값은 공식 확인된 계정 신원이나 알림 발송 권한으로 승격하지 않음.

## 실제 재현된 결함과 조치
최초 E2E 시험에서 이전 시각의 B 데이터가 B 신규 정상 관측값 뒤에 도착하면 `IntegratedMiniBarState.TryApply`가 이전 컷오프를 이유로 `RejectObservation`을 호출해 정상 B 수치를 제거함. 시험이 실제로 FAIL로 재현됨.

수정:
- epoch 컷오프 이전 관측 및 마지막 수신 timestamp 이하의 중복/역순 관측은 JSON 처리 이전에 조용히 거부.
- 이미 수신한 신선한 활성 계정 관측값은 보존.
- 현재의 신규 비정상 입력, 미래 timestamp, 6분 이상 지연된 신규 입력은 기존 fail-closed 처리 보존.
- 중복·지연·잘못된 JSON 프레임에 관한 회귀시험 2개 추가.

## 검증 결과
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: `MONITOR_SYNTHETIC_PREVIEW_PASS checks=151 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-enumeration-review`: PASS, DPI144=150%, 물리 모니터 1대.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: warning 0, error 0.
- `git diff --check`: PASS, LF/CRLF 경고 외 오염 없음.
- GitHub Commit `a6245e8` 조회: CI SUCCESS, macOS Preview SUCCESS, npm installer SUCCESS, Windows IN_PROGRESS. 즉 이전 HEAD 전체 4/4는 아직 미확정.

## 변경 파일
1. `windows/CodexAccountSwitcher.Core/IntegratedMiniBarState.cs`
2. `windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarGateTransitionTests.cs` 신규
3. `windows/CodexAccountSwitcher.MonitorChecks/Program.cs` 시험 연결
4. 이 보고서 신규

## 보존 및 한계
- 사용자의 기존 README 및 기타 미커밋 보고서/스크립트 보존.
- 실제 A/B 계정 로그인/전환·Codex 조회·인증정보 접근/변경, 정상 앱 EXE 운영실행, Windows 설치·시작프로그램/Release, Telegram 실송신 수행하지 않음.
- 물리 다중 모니터, Explorer 재시작/절전복귀 미검증.
- Work Window 사용 금지 유지.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 위 4개 파일만 개인 Fork 기능 브랜치 Commit/Push 후 원격 SHA 및 GitHub Actions 검증. 실제 운영 단계는 별도 승인 시 수행.
