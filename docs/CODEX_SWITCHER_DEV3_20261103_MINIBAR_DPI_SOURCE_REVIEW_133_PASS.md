# Codex 계정전환 개발팀 #3 — Windows 미니바 모니터/DPI 경계 및 인프로세스 출처 검증

작성시간: 2026-11-03 07:38:02 KST

## 상태
- WINDOWS_CI_B2E71AD_PASS
- INTEGRATED_MINIBAR_MONITOR_133_OF_133_OFFLINE_PASS
- WPF_SYNTHETIC_GUI_PASS
- WINDOWS_RELEASE_BUILD_ZERO_WARNINGS_ZERO_ERRORS
- MULTI_MONITOR_SYNTHETIC_ONLY
- MACOS_CI_PENDING
- PRODUCTION_NO_GO

## GitHub 확인
- 개인 Fork: `https://github.com/sirious0911/codex-account-switcher`
- 기능 브랜치: `feature/provider-extensibility`
- 이전 Commit: `b2e71ad944830594a38cafb461b331a37da03db6`
- GitHub Windows run 41536812909 completed SUCCESS, npm installer Windows 성공, CI site 성공.
- macOS Preview, npm installer macOS, CI macOS test 대기(최종 4/4 PASS 아님).
- 외부 upstream, main, Release 변경 없음.

## 신규 합성 배치시험
`windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarPlacementTests.cs`
- 100/125/150/200% DPI의 물리 픽셀 좌표 안전성
- 아래·위·왼쪽·오른쪽 작업표시줄 예약영역
- 보조 모니터 가상 데스크톱 음수 원점
- 자동숨김 추정 시 확대 여백
- 작은 화면 배치 거부
- 디스플레이 열거 순서 변경 시 기기명 기준 선택
- 보조 모니터 연결 해제→기본 모니터 복귀
- 화면 0개 상태 및 기본 플래그 없는 단일 모니터
- 총 19개 신규 PASS; Windows 실제 모니터 구성/배율 변경 없음.

## 출처 처리 정적 검토 발견·보완
- 이전 `IntegratedMiniBarState.TryApply`에서는 잘못된 샘플 때문에 `Invalidate(now)`를 호출하면 기존 `sourceAccount` 및 `mixedSource`까지 초기화될 수 있었다.
- 따라서 동일 활성 게이트 epoch에서 잘못된 관측 후 다른 provider `accountId`의 새 관측이 들어왔을 때 출처 혼합 탐지 없이 받아들일 가능성이 있었다.
- `RejectObservation(now)` 추가: 표시값과 요청 상태만 폐기하고 원래 출처 및 혼합 잠금은 유지. 외부 상위 계정 게이트의 명시적인 `Invalidate`만 출처 epoch를 초기화.
- 불량 프레임/다른 출처/혼합 이후 재표시 거부/명시적 초기화 복구의 5개 신규 합성시험 PASS.
- Provider ID를 공식 RPC ID 검증으로 취급하지 않음; 계정확인·이벤트/Telegram 권한 모두 false.

## 직접 검증
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 133/133 PASS, realCodexReads=0, actualAccountSwitches=0, telegramSends=0.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-review`: PASS.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: PASS, warning 0/error 0.
- `git diff --check`: PASS, LF/CRLF 경고 외 오류 없음.

## 변경 파일
1. `windows/CodexAccountSwitcher.Core/IntegratedMiniBarState.cs`
2. `windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarStateTests.cs`
3. `windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarPlacementTests.cs` (신규)
4. `windows/CodexAccountSwitcher.MonitorChecks/Program.cs` (시험 연결)
5. 이 보고서 (신규)

## 보존·제한
- 사용자가 이전부터 보존 중인 `README.md`, 오래된 인수인계/라이브 보고서/스크립트 등의 미커밋 파일 유지.
- 실제 계정 로그인, A↔B 전환, 일반 Fork EXE 운영 실행, 설치·자동시작, Telegram 실송신, 인증파일 변경, main 병합 및 Release 금지.
- 물리 다중 모니터·Explorer 재시작·절전복귀·실제 100/125/200% 혼합 DPI는 아직 미검증.
- 시스템 트레이+인접형 미니바로 구현됨. Explorer-native 작업표시줄 내장형은 아님.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 해당 5개 파일만 Commit 및 개인 Fork의 기능 브랜치 Push, 원격 SHA 재검증 및 신규 CI 확인. 실사용은 HOLD.
