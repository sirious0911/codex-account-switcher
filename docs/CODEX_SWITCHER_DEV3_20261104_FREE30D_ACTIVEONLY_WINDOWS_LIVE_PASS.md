# Codex 계정전환 개발팀 #3 — Free 30D + Active-only Windows CI·실사용 검증

작성시간: 2026-11-04 16:09:45 KST

## 최종 판정

- `GITHUB_ACTIONS_WORKFLOW_DISPATCH_RECOVERED`
- `WINDOWS_CI_FULL_PASS`
- `SWIFT_SHARED_CORE_TEST_PASS`
- `CI_BINARY_SHA256_MANIFEST_PASS`
- `WINDOWS_PORTABLE_SELFTEST_PASS`
- `A_ACCOUNT_CREDENTIAL_SHA_UNCHANGED`
- `FREE_B_30D_ACCOUNT_MANAGER_LIVE_PASS`
- `TASKBAR_BEFORE_CARET_LIVE_PASS`
- `WINDOWS_AUTOSTART_PATH_PASS`
- `A_TO_B_SWITCH_NOT_RUN`
- `REBOOT_TEST_PENDING`
- `FORMAL_RELEASE_HOLD`

## CI 복구와 소스 검증

- 사용자가 GitHub Actions 웹 UI에서 비활성화 경고를 확인하고 직접 Enable 전환.
- 최초 `gh workflow run windows.yml --ref feature/provider-extensibility`: run ID **41786708110**. Windows / .NET 기본 빌드는 PASS; Swift Tests 컴파일 단계에서 시험 소스의 `[REDACTED_SECRET]`이 실제 소스에 남아 `cannot find 'REDACTED_SECRET' in scope` 오류 발생.
- 정확히 1개 Swift 시험 파일 `Tests/SwitcherCoreTests/CredentialSyncTests.swift`의 82행에 실제 비밀이 아닌 **합성 JWT** `"e30." + claims + ".signature"` 복구. `CredentialIdentity.decodeClaims`가 요구하는 3-segment 구조도 확인.
- 개인 Fork 기능 브랜치 Commit: `125448dc900222f214c1e64b7249930e0e5647e9`. 원격 Push 확인.
- 두 번째 Windows workflow_dispatch run ID **41788422534**, 정확히 위 Commit을 기준으로 전체 PASS:
  - Windows .NET 빌드 PASS
  - Codex Desktop 닫기 합성시험 PASS
  - 동일 Swift Core의 `swift test` PASS
  - Shared Swift Core Host Release 빌드 PASS
  - Native transport + credential-safe isolated checks PASS
  - WPF UI synthetic PASS
  - Monitor quota synthetic PASS
  - Windows 단일 포터블 EXE packaging PASS
  - EXE isolated self-test PASS
  - GitHub artifact upload PASS.
- CI URL: https://github.com/sirious0911/codex-account-switcher/actions/runs/41788422534

## 패키지 검증

- CI artifact `Codex-Account-Switcher-windows-x64`을 개인 로컬 프로젝트
  `windows/artifacts/free30d-activeonly-ci-41788422534`로 다운로드.
- EXE 이름: `Codex-Account-Switcher-windows-x64.exe`, 크기 109386464 bytes.
- EXE SHA256: `18d2ea89edfcafe2411b1d766a8de870a3b6e4ec9300fcd761d362f5c489e55c`.
- 동봉 .sha256 manifest와 실제 EXE SHA 일치.
- Windows 사용자 PC에서 실제 `--self-test` 재실행:
  `PASS: bundled .NET, extracted Swift runtime, shared host, isolated settings and local monitor ledger.`
- self-test 전후 실제 A auth SHA, A/B 등록 원장 SHA 불변.

## 실제 Windows 업데이트

- 기존 적응형 버전 `releases\adaptive-20261104` EXE만 종료 후, 최신 CI EXE를 새 폴더에 배치.
- 현재 설치 경로:
  `%LOCALAPPDATA%\Programs\Codex Account Switcher Personal\releases\free30d-activeonly-ci-20261104\Codex-Account-Switcher-windows-x64.exe`.
- PID **18908** / 정확히 1개 실행.
- Windows `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexAccountSwitcherPersonal`이 새 경로의 `--background`를 가리킴.
- 이전 EXE 보존(복구 가능). GitHub main/Release 불변.
- 실제 A 계정 activeIndex=0, 등록 2개, `%USERPROFILE%\.codex\auth.json` SHA와 저장 A credential SHA 동일.
- A/B 전환, reset credits 소비, Telegram 발송, Codex Desktop 강제/정상 종료 **수행 0회**.

## 실사용 UI

- 새 EXE를 single-instance 방식으로 호출하여 계정 관리 창 열기 확인.
- Windows UI Automation `Text` control 21개 중 Free B **30D 문구 1개 감지**, `Usage unavailable` 문구 0개. 이는 A 활성인 채 전체 프로필을 조회하여 B의 실제 30D 구간을 정상 표시했음을 뜻함.
- 운영 B 계정으로 실제 전환하지 않았으므로 **B 활성 상태의 작업표시줄 30D 표시는 미검증**.
- 기존 활성 A의 작업표시줄 `5H 90% | 7D 51% | R 2` 실제 확인(시간에 따라 변동).
- 실기기 DPI-aware RECT 작업표시줄 [0,1528,2560,1600], 패널 [1853,1528,2102,1600] => 249×72 px, ^ 알림 영역 앞 간격 0 px.
- 별도 떠 있는 팝업이 아니라 taskbar HWND child 유지, 세로 중앙 정렬 확인.
- 계정 관리 창 닫기 후 `APP_ALIVE=True`, `MANAGER_HIDDEN=True`.
- 앱 인스턴스 1개, 자동실행 경로 일치 확인.

## 남은 독립 Gate

1. 사용자 종료/재부팅 계획에 맞춰 Windows 실제 재부팅 후 자동실행 확인.
2. B 활성 상태에서 직접 30D 작업표시줄을 보는 A→B→A 전환은 Codex Desktop 활성 업무 보호를 위해 이번에는 수행하지 않음. 사용자의 종료 상태에서 안전한 별도 전환 Gate 필요.
3. 최신 active-only 1분 조회의 **실기기 요청 범위**는 CI 및 Swift synthetic 검증 완료, 운영 API 단위 호출 횟수 별도 계측은 아직 수행하지 않음.
4. Codex Desktop 정상 종료의 장시간 백그라운드 프로세스 잔류 문제 미해결.
5. 다중 모니터/Explorer 재시작/auto-hide, macOS 실제 설치, 정식 개인 릴리스 미검증.
6. 원본 Fork 업스트림 변경 없음, 개인 Fork 기능 브랜치만 관리. 사용자 root README 기존 변경 및 미추적 문서 보존.

## 수행

개발팀 #3 → 본 보고서만 Commit/Push; 기존 운영 A 계정·프로그램 유지.
