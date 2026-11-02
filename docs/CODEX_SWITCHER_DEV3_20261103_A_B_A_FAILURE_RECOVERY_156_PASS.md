# Codex 계정전환 개발팀 #3 — Windows A→B→A 원복·전환 실패 사전검증

작성시간: 2026-11-03 08:20:39 KST

## 상태
- `A_B_A_OFFLINE_RECOVERY_PASS`
- `FAILED_SWITCH_RECOVERY_OFFLINE_PASS`
- `MONITOR_SYNTHETIC_156_OF_156_PASS`
- `A_ONLY_PROTOCOL_NODE_9_OF_9_PASS`
- `WPF_SYNTHETIC_UI_PASS`
- `WINDOWS_RELEASE_BUILD_WARN0_ERROR0`
- `PRODUCTION_NO_GO`

## 대상
- 개인 Fork: `https://github.com/sirious0911/codex-account-switcher`
- 브랜치: `feature/provider-extensibility`
- 기준 Commit: `d1a30d220276d3ae0a97b89db94993b1eab70842`
- 외부 개발자 원본과 개인 main은 수정하지 않음.

## 이번 변경 범위
`windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarGateTransitionTests.cs` 합성 테스트 추가.
SOURCE 기능 수정 없음.

## A→B→A 합성 전환·원복 검증
기존 A→B 완료 상태에서:
- B→A 요청 즉시 B의 기존 표시 무효화.
- 전환 진행 중 B 데이터 차단.
- 전환 완료된 A의 새로운 샘플만 다시 표시.
- 늦게 도착한 B 프레임의 기존 A 표시 덮어쓰기 거부.
- 별도의 전환 실패 뒤 호스트 오류 상태에서는 기존 A 샘플 표시 거부.
- 호스트 오류 해소/계정 확인 이후 컷오프보다 신선한 A 관측만 허용.
- 신규 상태전이 회귀 5개 포함.
- 실제 Codex/계정 로그인/활성 인증파일 변경 없이 메모리 fixture만 실행.

## 실행·검증 결과
1. `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`
   - `MONITOR_SYNTHETIC_PREVIEW_PASS checks=156 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`
2. `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-enumeration-review`
   - PASS, targetDpi=144, physical display count=1, WPF native monitor selection confirmed.
3. `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`
   - 성공, 경고 0/오류 0.
4. `node --test scripts/tests/active-a-no-copy.test.mjs`
   - 9/9 PASS. Codex v0.149 official `account/read`에서 provider `accountId` 직접 인증되지 않는 상태 유지.
   - 소진된 LIVE Gate 재실행 방지, 합성 CLI, 민감정보 무복사 등 확인.
5. `git diff --check`
   - PASS. 기존 README의 LF→CRLF 안내는 기존 환경의 줄바꿈 경고임.

## 실계정 Gate 선행조건
2026-11-02 preflight `docs/windows-live-switch-preflight-20261102.md` 기준 B 대상 계정 프로필 미등록이 확인돼 실제 A→B→A 테스트는 차단.
정상 Fork 앱의 최초 실행은 현재 `~/.codex/auth.json`에서 개인 Fork 프로필 복사를 할 수 있으므로 READONLY 아님.
현재 별도 B 프로필 등록 여부는 이 단계에서 재확인하지 않았으므로, 직전 preflight 근거만으로 운영 진행을 승인하지 않음.
실제 B 계정 등록에는 사용자의 별도 명시적 승인과 공식 브라우저 인증이 선행되어야 함.
공식 `account/read`은 정확한 provider accountId를 노출하지 않으므로 `AUTH_FILE_PINNED_RPC_ID_UNVERIFIED` 유지.

## GitHub Actions 사실
기준 `d1a30d2` GitHub Actions 조회 당시:
- CI SUCCESS
- macOS Preview SUCCESS
- npm installer SUCCESS
- Windows IN_PROGRESS
- 전체 4/4 PASS는 아직 미확정. 최종 Commit의 CI도 별도 확인 필요.

## 제외
- 기존 미커밋 README/인수인계/라이브 시험 파일 보존.
- 실제 계정 등록·로그인·전환/원복, 정상 앱 EXE 운영실행, 인증정보 파일 직접 읽기·복사/수정, Windows 자동시작/운영 설치, Telegram 실송신, Release·main 변경 없음.
- 물리 다중 모니터와 혼합 DPI/Explorer 재시작 실기기 검증은 별도.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 — 이번 시험 소스 1개 및 보고서만 기능 브랜치 Commit/Push·SHA 확인, 신규 CI 최종 확인 및 LIVE 검증 선행조건 Gate 유지.
