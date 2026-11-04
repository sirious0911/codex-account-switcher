# Codex 계정전환 개발팀 #3 — Windows 활성 작업 감지 1분 적응형 사용량 조회

작성시간: 2026-11-04 09:16:42 KST

## 판정

- `WINDOWS_ADAPTIVE_USAGE_REFRESH_IMPLEMENTED`
- `ACTIVE_WORK_HEURISTIC_PASS`
- `LIVE_SUB_FIVE_MINUTE_REFRESH_OBSERVED`
- `MONITOR_SYNTHETIC_192_OF_192_PASS`
- `WINDOWS_WPF_UI_CHECKS_PASS`
- `WINDOWS_BUILD_WARN0_ERROR0`
- `SINGLE_PORTABLE_EXE_SELFTEST_PASS`
- `TASKBAR_BEFORE_CARET_LIVE_PASS`
- `A_CREDENTIAL_SHA_UNCHANGED`
- `USER_AUTOSTART_PATH_UPDATED`
- `LATEST_GITHUB_CI_UNVERIFIED`
- `PRODUCTION_RELEASE_NO_GO`

## 사용자 요구사항

이전 구현은 Swift Core의 `startBackgroundUsageRefresh(every: .seconds(300))`로 서버 사용량 **5분 간격** 조회, Windows 작업표시줄 `DispatcherTimer`로 **4초 간격** 화면 표시 갱신. 사용자는 Codex 작업 중 더 빠른 잔여 사용량 반영을 요청함.

## 구현

1. `windows/CodexAccountSwitcher.Core/AdaptiveUsageRefreshPolicy.cs` 신규:
   - 계정 신원 확인 상태·`IsMutating`·오류 확인 후 추가 조회만 허용.
   - 최근 작업 흔적이 있으면 성공한 활성 계정 관측 이후 **최소 60초**를 지킨 뒤 추가 조회 1회.
   - 새 실제 관측 샘플이 오기 전 중복 요청 금지. 40초 동안 새 샘플이 오지 않거나 명령 전달이 실패하면 추가 조회 간격 **2 / 4 / 5분**으로 늘림.
   - 다른 계정에서 온 샘플은 성공으로 인정하지 않음. 계정전환·신원 불일치 상태에서는 추가 조회 중지.
   - 작업 흔적이 없으면 추가 요청 없음. 기존 Swift 5분 주기 유지. 5분 넘는 백오프는 기존 Swift 기본 타이머와 경쟁하지 않음.
   - 순수 상태 정책이며 파일 읽기, 계정 조회, 인터넷 접속, 계정 저장 기능 없음.

2. `windows/CodexAccountSwitcher/CodexWorkActivityDetector.cs` 신규:
   - 현재 사용자의 `.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` **수정 시각**만 확인. 내용, 대화, 토큰, 자격증명은 읽지 않음.
   - 최근 3분 내 변경된 기록을 추가 조회의 *작업 흔적*으로 사용.
   - 현재 활성 창이 실제 `OpenAI.Codex` 공식 설치 패키지인지 확인. 공식 Windows 앱은 실행 파일명이 `ChatGPT.exe`일 수 있음을 PC의 AppxManifest로 확인; 단순 `ChatGPT.exe`/ `Codex.exe` 이름만으로는 작업 중이라고 판정하지 않음.
   - 현재/UTC/로컬 날짜와 최대 과거 2일 버킷 검색. 오류·빈 폴더는 작업 흔적 없음으로 처리. 프로세스나 Codex 파일을 수정하지 않음.
   - *활성 작업 상태의 완벽한 증명은 아님*: 장시간 조용한 작업은 5분 기본 주기로 전환될 수 있고, 사용자가 Codex를 전면에 놓기만 해도 1분 추가 조회를 할 수 있음.

3. `windows/CodexAccountSwitcher/AdaptiveUsageRefreshService.cs` 신규:
   - Win32/WPF 동일 앱 인프로세스 `DispatcherTimer`로 12초마다 작업 흔적 평가.
   - 활성 신호 때 기존 `CoreClient.CommandAsync("refresh")`를 호출. Swift Core가 서버/API 요청·5분 기본 타이머·계정전환 직후 즉시 조회를 계속 단독 소유.
   - 별도 네트워크 클라이언트나 계정전환, 데스크톱 종료, 메시지 전송 없음.
   - 기존 `refreshWeeklyUsage` 명령은 호출 때마다 Swift의 다음 5분 자동 조회 타이머를 재설정하기 때문에 독립적인 중첩 5분 폴링 루프가 생기지 않음.

4. `windows/CodexAccountSwitcher/App.xaml.cs`: 애플리케이션 초기화 이후 서비스 시작, 기존 `ActiveMonitorPublicationGate.Allows`로 검증된 신선한 계정 관측에만 성공 신호, 종료 시 서비스 정리. 기존 작업표시줄/계정관리 클릭·파일·아이콘·자동실행 기능 변경 없음.

5. `windows/CodexAccountSwitcher.MonitorChecks/Program.cs`: 추가 요청 최소 간격, 1/2/4/5분 백오프, 비활성 차단, 전환/오류/신원 차단, 교차 계정 샘플 차단 등 회귀시험 추가.
6. `windows/CodexAccountSwitcher.UiChecks/Program.cs`: 임시 소유 session metadata의 최신/오래됨/미래/무관 파일 구분, 공식 OpenAI.Codex Appx 실행 파일명 ChatGPT/Codex 감지 및 임의 ChatGPT 프로세스 오인 차단.
7. `windows/README.md`: Windows 적응형 조회 흐름/한계 문서화.

## 검증

- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: `MONITOR_SYNTHETIC_PREVIEW_PASS checks=192 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-adaptive-usage-v2-20261104`: PASS.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: 경고0/오류0.
- `git diff --check`: PASS, root README 기존 사용자 수정 보호.
- `dotnet publish ... -p:SwiftRuntimeArchive=../artifacts/before-caret-ci-runtime.zip`: 단일 Windows x64 EXE 생성(108148552 bytes), SHA256 `f433855998db4dfd72323260b2c932a6f7384aa6ec25e839d307ecc4dcdbeb8c`.
- `--self-test`: bundled .NET, extracted Swift runtime, shared host, isolated settings/local monitor ledger PASS. 전후 A auth·A/B 원장 SHA256 유지.
- 버전별 새 폴더 설치: `%LOCALAPPDATA%\Programs\Codex Account Switcher Personal\releases\adaptive-20261104\Codex-Account-Switcher-windows-x64.exe`. PID **15552**로 백그라운드 실행, 시작프로그램 `CodexAccountSwitcherPersonal` 경로 변경 PASS. 이전 `centered-20261104` EXE 유지.
- 실기기 작업표시줄: `Shell_TrayWnd` 자식, 물리 패널 RECT [1853,1528,2102,1600], 알림 영역 시작 X=2102, ^ 바로 왼쪽 간격0. 활성 A 샘플 5H90% / 7D67% / R2 확인(값은 수시 변경).
- 실기기 활성 신호: 최근 rollout 파일 변경 시각이 관측 시점 약 **31초 전**으로 확인.
- 실제 사용량 캐시: **2026-11-04 09:15:35 KST** 조회 당시 마지막 파일 갱신 약 63초 전(09:14:32경), 이후 **09:15:53 KST** 재조회 시 캐시 최종 수정 시각 **09:15:44.908 KST**(8초 전). 이 연속 관측은 기본 5분을 기다리지 않고 **약 73초 후 새 조회 결과가 기록된 것**을 의미. 정확히 60초마다 서버 통계를 갱신한다는 보장은 없고 작업/서버 응답 시간 포함.

## 잔여 위험·미검증 사항

- **작업 중 신호는 추정치**: 새 rollout 파일 기록 또는 공식 Codex Desktop 전경 창으로 판단하므로, 백그라운드에서 긴 무출력 실행이나 도구별 로그 미기록의 경우 실제 작업 중이라도 5분으로 복귀할 수 있음.
- **Swift refresh는 모든 등록 계정을 조회**: 활성 A만 1분 요청한다는 뜻이 아님. B 무료 계정의 Usage unavailable은 그대로이며 추가 사용량 API 트래픽이 발생. 장기적으로 active-only 스코프 공식 Swift 프로토콜 설계·검증 필요.
- **호스트가 오래된 Swift runtime ZIP 사용**: 본 변경은 Windows 애플리케이션/C# 측. 원본 Swift의 stale-error 보완 Commit은 새 CI 검증·runtime 동기화 전까지 이 EXE에 적용됐다고 주장하지 않음.
- 다음 1분 조회와 서버 쿼터 숫자 반영 사이에는 서버 집계 지연 가능.
- Windows 실제 재부팅·다중모니터·자동숨김·최신 GitHub CI는 해당 수정본을 대상으로 별도 확인 전. 정식 Release/배포 GO 아님.
- GitHub 개인 Fork 기능 브랜치만 배포 대상. 원본 저장소, main 브랜치 및 기존 사용자 미추적 소스·인수인계 파일 보존.

## 다음 수행 주체
Codex 계정전환 개발팀 #3: 정해진 소스7개 + 보고서1개만 기능 브랜치 Commit/Push, 원격 SHA 확인. 다음 회차에는 무료 B Usage unavailable/active-only 조회 범위와 Codex Desktop 정상 종료 지연 분리 대응.
