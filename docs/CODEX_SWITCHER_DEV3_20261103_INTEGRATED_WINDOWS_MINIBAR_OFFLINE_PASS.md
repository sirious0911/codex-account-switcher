# Codex 계정전환 개발팀 #3 — Windows WPF 미니바 앱 내 통합

작성시간: 2026-11-03 06:31 KST

STATUS: IN_PROCESS_MINIBAR_SOURCE_PASS / MONITOR_108_OF_108_PASS / WPF_SYNTHETIC_GUI_PASS / PRODUCTION_NO_GO

## 인계 출처

- 별도 CodexPro 미니바 프로젝트: `C:\Codex\projects\codex-usage-monitor-prototype`
- 인계 파일: `docs/DEV15_20261102_WINDOWS_MINIBAR_OFFLINE_UI_HANDOFF_421_PASS.md`
- 개발팀 #15의 **기존 단독 미니바** 기준 421/421 + GUI 5/5 PASS는 인계 기준선으로 보존. 이번 통합 빌드의 421개 직접 재실행을 의미하지 않음.
- 계정전환 저장소: `C:\Codex\projects\codex-account-switcher`, 사전 HEAD `42a052043f343e59602558c8be94f1644b23fa85`

## 통합 구현 내용

- Windows 계정전환 WPF 실행파일 내부에 `IntegratedMiniBarWindow`를 편입, 새 프로세스/추가 트레이/Named Pipe listener 없이 기존 `CoreClient.MonitorObserved`를 직접 구독.
- 기존 활성 계정 전용 `ActiveMonitorPublicationGate` 재사용. 전환 요청, 비확인 계정, SwiftHost 오류에서 값 즉시 UNKNOWN 무효화. UI Dispatcher가 지연된 관측을 받으면 활성 프로필 재검증.
- 기본 계정전환 앱의 별도 `SwitcherLocalPipeSender` 자동 실행 비활성화. *기존 IPC 구현/88개 합성시험*은 호환·독립검증용으로 유지.
- 기존 미니바 스타일: 360×52 DIP, 상태 점, 5H/7D/리셋권, 계정 미확인/UNKNOWN 안내, 툴팁, 우클릭 모니터 선택/Always-on-top/숨김, 더블클릭 계정관리 진입.
- 기존 위치 로직을 이식한 `MiniBarLayout`/`MiniBarScreenSelector`/`NativeDesktopDock`로 모니터 작업영역에 인접 배치. Explorer-native 태스크바 삽입은 아님.
- 기존 앱의 단일 트레이 메뉴에서 미니바 표시/숨김. 앱 시작 후 표시, `--background`에서도 미니바 유지, 앱 종료 시 함께 종료.
- 인프로세스 상태 투영 `IntegratedMiniBarState`: 관측 2분 미만 FreshUnbound, 2~6분 AgedUnbound, 6분부터 UNKNOWN. 구세대/중복·역순 데이터 거부, 소스 계정 식별값 변경 감지/메모리 내 비교 후 무효화. 인증확인/알림/Telegram 권한 항상 false.
- 신규 실계정 조회, 추가 API 호출, 실제 계정전환, 인증정보 접근·복사, 운영 설치·배포 없음.

## 변경된 파일

- `windows/CodexAccountSwitcher/App.xaml.cs`
- `windows/CodexAccountSwitcher/IntegratedMiniBarWindow.cs` (신규)
- `windows/CodexAccountSwitcher/NativeDesktopDock.cs` (이식)
- `windows/CodexAccountSwitcher/AssemblyInfo.cs` (신규)
- `windows/CodexAccountSwitcher.Core/IntegratedMiniBarState.cs` (신규)
- `windows/CodexAccountSwitcher.Core/MiniBarLayout.cs` (이식)
- `windows/CodexAccountSwitcher.Core/MiniBarScreenSelector.cs` (이식)
- `windows/CodexAccountSwitcher.MonitorChecks/IntegratedMiniBarStateTests.cs` (신규)
- `windows/CodexAccountSwitcher.MonitorChecks/Program.cs`
- `windows/CodexAccountSwitcher.UiChecks/Program.cs`
- 이 문서 외 기존 README·인수인계·사용자 미커밋 파일은 수정하지 않음.

## 현재 수행한 오프라인 검증

1. `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` → PASS, 경고0 오류0.
2. `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` → 108/108 PASS, realCodexReads=0, actualAccountSwitches=0, telegramSends=0. 기존 88개 + 신규 20개.
3. `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated` → PASS. 기존 계정관리 GUI 회귀 + 통합 미니바 UNKNOWN/합성 관측/무효화/숨김/복원.
4. 합성 GUI 이미지: `windows/artifacts/ui-integrated/integrated-minibar-{unknown,fresh,invalidated}.png`. Fresh/Invalidated 화면 이미지 직접 조회 확인.
5. `git`: 원래 변경/미추적 파일 유지, 신규 통합 파일만 추가/수정. Stage0, Commit0, Push0, Release0.
6. Work Window ACTIVE 0, 생성 0.

## 미검증/운영 차단

- **일반 Fork EXE를 실제 계정 인증 상태로 실행하지 않음**. 따라서 실제 A 계정의 인프로세스 갱신, B 등록 및 A→B→A는 미검증.
- 물리 다중 모니터/100·125·200% 혼합 DPI/작업표시줄 자동숨김/Explorer 재시작/절전 복귀 실기기 검증 미실시.
- 실제 계정전환/운영 설치/자동시작/릴리스/외부 알림은 별도 승인 전 NO_GO.
- 기존 단독 미니바 421/421과 이번 통합 108/108·WPF GUI를 합산한 통일 검증 수치로 표현하지 않음.
- 독립 검증과 기능 브랜치 CI는 이번 통합 SOURCE에 대해 미수행.
- Windows 작업표시줄 **내장형**은 별도 조사 후보이고, 현재 기능은 공식 트레이 + 인접 미니바 구조.

## 다음 수행 주체

Codex 계정전환 개발팀 #3 → 독립 코드리뷰·CI 및 실제 Windows 배치 경계 추가 검증. 실계정 로그인/전환과 운영 설치는 별도 승인 시에만 수행.
