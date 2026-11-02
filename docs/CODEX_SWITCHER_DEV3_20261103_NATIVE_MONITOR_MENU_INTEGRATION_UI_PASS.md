# Codex 계정전환 개발팀 #3 — Windows 실제 모니터 메뉴 선택/재배치 검증

작성시간: 2026-11-03 07:57:15 KST

## 현재 상태
- `LOCAL_NATIVE_MONITOR_MENU_SINGLE_DISPLAY_PASS`
- `LOCAL_NATIVE_DOCK_150PCT_PASS`
- `MONITOR_133_OF_133_PASS` (기존 HEAD 기준; UI 테스트 추가는 별도)
- `GITHUB_WINDOWS_CI_13B0932_PASS`
- `GITHUB_CI_13B0932_PASS`
- `GITHUB_NPM_INSTALLER_13B0932_PASS`
- `GITHUB_MACOS_PREVIEW_13B0932_PASS`
- `PRODUCTION_NO_GO`

## 대상 원격
- 개인 Fork: `https://github.com/sirious0911/codex-account-switcher`
- 브랜치: `feature/provider-extensibility`
- 검증 기준 Commit: `13b0932c2d99df69f31021709b967366fedc9c78`
- 외부 개발자 원본, 개인 main, Release 변경 없음.

## 이번 시험 추가
- 파일: `windows/CodexAccountSwitcher.UiChecks/Program.cs`
- 실 Windows `System.Windows.Forms.Screen.AllScreens`에 등록된 물리 디스플레이를 관측.
- 각 디스플레이를 실제 미니바 우클릭 메뉴의 `표시 모니터 선택`에서 선택하는 WPF `MenuItem.Click` 이벤트를 합성으로 발생시킴.
- 변경된 창의 `NativeDesktopDock.LastDock`에서 대상 DPI와 픽셀 좌표를 읽어 실제 선택 모니터 `WorkingArea` 내 포함 여부를 검증.
- 사용자가 설정한 시스템 DPI/해상도/작업표시줄 설정은 변경하지 않음. UI 시험용 창 생성/재배치/종료만 수행.
- 현재 연결된 디스플레이가 하나뿐이면 하나에 대해 실제 native 재배치/픽셀 좌표 검증만 수행; 둘 이상 실제 배치 PASS로 주장하지 않음.
- 배치 불가능한 매우 작은 작업영역은 명시적 `SKIP`으로 기록하도록 함.

## 실제 Windows 테스트 결과
`dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-native-multimonitor`

stdout 확인:
- `PASS: integrated native dock targetDpi=144, physicalRectInside=True`
- `INFO: physical display count=1`
- `PASS: native monitor selection, targetDpi=144, work-area verified`
- `PASS: native taskbar window, focus persistence, compact home, full-row confirmation, management, immediate settings and optional five-hour usage.`
- exit code 0.
- DPI 144 = scaling 150%.
- 사용량 표시 데이터는 합성 fixture만 이용. 로그인, 실제 Codex 조회, 전환, credential 읽기/복사 없음.

## 다른 검증
- 통합 미니바 133/133 기존 회귀(변경 없는 동일 CORE).
- GitHub 13b0932 Windows run `41540537569` SUCCESS.
- GitHub 13b0932 CI run `41540537562` SUCCESS (site + macOS `test`).
- GitHub 13b0932 npm installer run `41540537493` SUCCESS (Windows + macOS).
- macOS preview run `41540537547` 완료 SUCCESS. 동일 Commit의 GitHub Actions 4개 워크플로 모두 SUCCESS (4/4 PASS).

## 잔여 검증
1. 물리 모니터가 2대 이상 실제 연결된 장비에서 동시 모니터 선택·음수 원점·DPI 혼합 실기기 재검증.
2. 자동숨김 taskbar 및 Explorer 재시작 후 유지/복구 관찰.
3. 절전/복귀 및 DisplayChanged, WM_DPICHANGED 이벤트 실제 관찰.
4. 정상 Fork 앱의 실제 Active Codex 프로필 사용량 관측 및 A↔B→A 실계정 전환은 별도 Gate에서 수행.
5. Windows 운영 설치/로그인 자동실행/Release 및 Mac mini 실기기 이식은 보류.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 — 단일 SOURCE+보고서만 개인 Fork 기능 브랜치 반영 후 원격 SHA 검증 및 신규 GitHub Actions 확인. 원본과 main 건드리지 않음.
