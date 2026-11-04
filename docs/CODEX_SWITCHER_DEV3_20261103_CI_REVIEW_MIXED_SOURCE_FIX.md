# Codex 계정전환 개발팀 #3 — Windows 미니바 통합 CI 확인 및 계정 혼합 결함 보완

작성시간: 2026-11-03 07:21 KST

## 상태
- `WINDOWS_CI_PASS`
- `MIXED_SOURCE_LATCH_REMEDIATED`
- `LOCAL_MONITOR_109_OF_109_PASS`
- `WPF_SYNTHETIC_GUI_PASS`
- `MACOS_CI_PENDING`
- `PRODUCTION_NO_GO`

## 범위 및 원격
- 개인 Fork: `https://github.com/sirious0911/codex-account-switcher`
- 기능 브랜치 `feature/provider-extensibility`
- 검토 기준선 `aa580c3e74300a60df8174bafee430b4637a6fc1`
- 외부 원본 저장소 수정·원격 push 없음; main 및 Release 변경 없음.

## 실제 GitHub Actions 조회
대상 Commit `aa580c3e74300a60df8174bafee430b4637a6fc1`
- Windows run `41534721633`: completed/success. Windows job `125583530000`의 Swift Core test, SwiftHost build, native transport, WPF synthetic tests, monitor checks, EXE package/selftest 모든 관련 단계 success.
- CI run `41534721772`: site success / macos-26 test queued.
- macOS preview run `41534721635`: macos-26 job queued.
- npm installer run `41534721632`: Windows job success / macos-26 job queued.
- 즉, 현재 **전체 4/4 PASS 미확정**. 대기 중 job을 PASS 또는 FAIL이라고 표시하지 않음. 추가 재실행/취소 없음.

## 2차 정적 리뷰 발견사항
- 기존 단독 WPF 수신부 `SwitcherSourceProfileGate`는 한 연결에서 서로 다른 provider 계정 관측이 섞이면 해당 연결 epoch의 남은 관측 전체를 차단한다.
- 최초 통합 `IntegratedMiniBarState`는 서로 다른 `accountId` 관측 직후 표시를 무효화했으나, 이후 새로운 프레임을 동일 활성 게이트 epoch에서 허용할 수 있었다.
- 변경: `mixedSource` 래치를 도입해 source 혼합 감지 시 계속 거부, 명시적인 상위 활성 계정 게이트 무효화(`Invalidate`)로 새 epoch가 시작된 경우에만 차단 해제.
- 계정 식별 문자열은 프로세스 메모리에서만 비교. 화면/로그에 노출하지 않음. RPC ID 검증으로 승격하지 않음.

## 검증
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 109/109 PASS, 신규 혼합 프로필 래치 및 활성 epoch 전환 케이스 포함. 실제 Codex 조회·계정전환·Telegram 전송 0.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-review`: PASS, 화면 렌더 및 기존 계정관리 UI 합성회귀.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: PASS, warning 0/error 0.
- `git diff --check`: PASS (Windows Git의 LF→CRLF 경고는 별도이며 내용 위반 아님).

## 수행 제한
- 정상 Fork 일반 실행, 실제 A/B 로그인, A↔B 전환, 운영 자동시작, 정식 Release, Telegram 메시지는 수행하지 않음.
- Git 기존 README 및 이전 인계문서 미커밋 상태 보존. Git Commit·Push는 오직 이 수정과 검증 보고서만.
- macos-26 CI가 완료되기 전 전체 4/4 PASS 판정 금지.
- 다중 모니터/혼합 DPI/Explorer 재시작 실기기 시험 및 정상 앱 LIVE 검증 별도.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 개인 Fork 기능 브랜치 Commit/Push 이후 원격 SHA 검증 및 CI 결과 최종 확인. 운영 배포는 보류.
