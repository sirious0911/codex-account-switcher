# Codex 계정전환 개발팀 #3 — 통합 Windows WPF 실제 창 배치 독립검증

작성시간: 2026-11-03 07:48:52 KST

## 판정
- WINDOWS_NATIVE_DOCK_150PCT_LOCAL_PASS
- INTEGRATED_WPF_SYNTHETIC_UI_PASS
- MONITOR_133_OF_133_PASS
- WINDOWS_RELEASE_BUILD_WARN0_ERROR0
- GITHUB_WINDOWS_CI_A622E1C_PASS
- MACOS_CI_QUEUED
- PRODUCTION_NO_GO

## 기준
- 저장소: `https://github.com/sirious0911/codex-account-switcher`
- 기능 브랜치: `feature/provider-extensibility`
- 이전 기준 HEAD: `a622e1c95dcacadf1c7c85e4066ee9a5df6804ba`
- 외부 개발자 upstream 변경 없음, 개인 main 변경 없음.

## 이 단계 수정
- `windows/CodexAccountSwitcher/IntegratedMiniBarWindow.cs`: 미니바의 마지막 `NativeDesktopDock.TryDock` 결과를 `internal NativeDockResult? LastDock`으로 노출. 사용자 설정·실제 계정이나 운영 환경 변경 없음.
- `windows/CodexAccountSwitcher.UiChecks/Program.cs`: 기존 합성 UI smoke가 창 렌더링뿐 아니라 Windows native 배치 호출, 대상 DPI, 실제 `GetWindowRect`의 작업영역 내 좌표를 검증하도록 확대. 화면이 너무 작아 배치가 불가능한 CI 러너는 원인을 출력하고 native 좌표 확인을 SKIP하는 명시적 분기.

## 실제 Windows 로컬 조회
`dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-native-review`
- PASS: integrated native dock targetDpi=144, physicalRectInside=True
- DPI 144는 150% 배율에 해당.
- 시스템 모니터 배율 변경하지 않음. 테스트 WPF 미니바 창만 생성, 배치, 렌더 및 종료.
- 로그인·계정 변경·Codex 실제 조회·네트워크 전송 없음.
- 기존 계정관리 UI 합성시험도 PASS.

## 별도 회귀검증
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 133/133 PASS, realCodexReads=0, actualAccountSwitches=0, telegramSends=0.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: warning 0, error 0.
- `git diff --check`: PASS, Git LF/CRLF 경고만 발생.

## 원격 CI 사실
- GitHub Commit `a622e1c` Windows Actions `41539092999`: 완료 SUCCESS.
- 같은 Commit의 macOS preview, CI macOS test, npm installer macOS는 조회 시 QUEUED. 전체 CI PASS라고 주장하지 않음.
- macOS 작업이 장기 대기/취소되어도 직접 테스트 실행 증거 없이 코드 결함으로 분류하지 않음.

## 한계
- 실제 물리 모니터 2대 이상의 모니터 연결/분리 및 혼합 DPI, 자동숨김 작업표시줄, Explorer 재시작, 절전/복귀는 미실시.
- 실제 일반 Fork 프로세스(현재 계정) 연계, A/B 로그인/전환/복구, 설치·자동시작, 알림 실송신, 배포는 NO_GO.
- 현재 구현은 시스템 트레이 + 작업표시줄 인접 창. Explorer 내부 taskbar 삽입 아님.
- 기존 미커밋 README/인계서/스크립트는 건드리지 않음.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 위 두 SOURCE와 보고서만 기능 브랜치 Commit/Push 후 GitHub Windows CI 재검증. 운영 릴리스 보류.
