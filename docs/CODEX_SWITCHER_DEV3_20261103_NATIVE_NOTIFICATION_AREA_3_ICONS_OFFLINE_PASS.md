# Codex 계정전환 개발팀 #3 — Windows 작업표시줄 알림 영역 3개 아이콘 방식 구현

작성시간: 2026-11-03 15:20:05 KST

## 요구사항
사용자가 2026-11-03 업로드한 Windows 작업표시줄 캡처의 '^' 아이콘 바로 오른쪽 알림 영역에 사용량을 상시 표시. 별도 떠 있는 미니바 창은 원하지 않음. 가능하면 5H, 7D, 리셋권을 각각 보여야 함.

## 구현 결과 (SOURCE 기준)
- Windows 일반 실행 경로에서 기존 IntegratedMiniBarWindow 생성·자동 Show·트레이 토글 메뉴 제거. 소스 파일은 종전 격리 합성 GUI 테스트와 호환성 검증용으로만 유지.
- 운영 런타임은 기존 Codex Account Switcher 한 프로세스에서 WinForms NotifyIcon 3개를 생성: 5H(잔여율), 7D(잔여율), R(리셋권 개수).
- 아이콘은 16×16 등 시스템 SmallIconSize에 맞게 작은 항목 구분 레이블과 숫자를 그림. 잔여값이 없으면 '?'를 표시하며 0을 임의 추정하지 않음.
- 마우스를 올리면 각 항목의 값과 계정 미확인·지연·전환/연결 오류/만료 상태를 표시. 단축 클릭은 기존 계정 관리 화면, 우클릭은 동일 앱의 열기/종료 컨텍스트 메뉴.
- 별도 프로세스·Named Pipe listener·Codex 추가 조회·별도 WPF 미니바 실행 없음.
- 기존 ActiveMonitorPublicationGate가 확인된 활성 프로필의 샘플만 전달하며, 2분 이후 지연 상태, 6분 이후 만료 UNKNOWN, 전환/오류 시 즉시 무효화.
- 앱 종료 시 타이머, 3개 NotifyIcon, Icon GDI 자원 해제.
- 일반 앱 내 기존 계정관리 창은 계속 존재. 새로 제거한 것은 사용량용 별도 팝업형 창임.

## Windows 시스템 제약
알림 아이콘은 기존 작업표시줄의 실제 '^' 오른쪽 트레이 영역에 등록 가능하지만, Windows는 아이콘의 노출 및 정확한 순서를 앱이 강제하도록 허용하지 않음. 사용자 본인이 한 번 '^' 숨김 목록에서 아이콘 3개를 바깥으로 드래그하거나 [작업표시줄 설정 → 기타 시스템 트레이 아이콘]에서 표시 설정 필요.
넓은 텍스트 조각 '5H 85% | 7D 62% | R2'를 Explorer 작업표시줄 내부에 임의 너비로 주입하는 방식이 아니라 작은 개별 Native NotifyIcon 3개 방식임.
관련 Microsoft 공식 안내: https://support.microsoft.com/ko-kr/windows/experience/personalization/customize-the-taskbar-in-windows

## 오프라인 검증
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` → 172/172 PASS; realCodexReads=0 actualAccountSwitches=0 telegramSends=0.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-native-tray-3-icons` → PASS. Normal App instance에 floating minibar 필드 없고 Native TrayQuotaIcons 관리 필드 존재, 5H/7D/R 실제 Icon 생성·16x16 렌더 확인.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` → warning 0 / error 0.
- `git diff --check` → exit 0. 줄바꿈 LF/CRLF 경고는 기능 오류 아님.
- B 계정 등록·실계정 로그인·정상 앱 일반 실행·Desktop 종료·시세/사용량 실조회·인증정보 수정·운영 설치/고정 위치 조정은 수행하지 않음.
- 실제 Windows 우측 ^ 옆 아이콘의 실기기 정렬·고정 상태는 미검증.
- 기존 루트 README 및 미추적 인수인계/사용량 자료 보존. 개인 Fork 기능 브랜치에서만 작업.

## 변경 예정 지정 파일
1. windows/CodexAccountSwitcher.Core/TrayQuotaReadout.cs (신규)
2. windows/CodexAccountSwitcher/TrayQuotaIcons.cs (신규)
3. windows/CodexAccountSwitcher/TrayUsageIcon.cs
4. windows/CodexAccountSwitcher/App.xaml.cs
5. windows/CodexAccountSwitcher.MonitorChecks/Program.cs
6. windows/CodexAccountSwitcher.UiChecks/Program.cs
7. windows/README.md
8. 이 보고서

## 상태
SOURCE_OFFLINE_PASS / USER_DESIRED_NOTIFICATION_AREA_ARCHITECTURE_IMPLEMENTED / PHYSICAL_TRAY_PINNING_NOT_VERIFIED / LIVE_ACCOUNT_REGISTRATION_PENDING / PRODUCTION_NO_GO

다음 수행 주체: Codex 계정전환 개발팀 #3 → 지정 8개 파일만 기능 브랜치 Commit/Push, CI 확인. 사용자 귀가 후 실제 앱 초기 실행·B 계정 등록 승인 뒤 작업표시줄 아이콘을 '^' 옆에 한 번 고정하고 실계정 UI 검증.
