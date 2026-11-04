# Codex 계정전환 개발팀 #3 — Windows A→B→A 실사용 검증 및 오래된 오류 표시 보완

작성시간: 2026-11-03 22:58:51 KST

## 검증 결과
- `A_TO_B_LIVE_CREDENTIAL_AND_REGISTRY_PASS`: 사용자 브라우저 로그인 완료 후 A·B 두 계정 프로필/인증파일 구분 확인. 원본 A 파일과 저장 A가 같고 B 파일은 별도 보존.
- 사용자가 기존 Codex Desktop을 종료한 뒤 A→B 앱 UI의 정상 Switch 버튼 사용. 앱 registry active profile B와 실제 active Codex auth file이 B 저장본과 일치함을 SHA256 비교로 검증. 인증정보의 원문, 메일, 토큰은 보고서에 기재하지 않음.
- `B_TO_A_LIVE_CREDENTIAL_RESTORE_PASS`: B→A UI 전환 도중 Windows Desktop 완전 종료가 늦어 IPC 실행 명령이 60초 후 시간 초과되었으나, 다시 조사할 때 실제 auth 및 registry가 모두 A로 일치함. 두 계정 저장 사본은 모두 보존됐고 두 번째 실행은 precondition에서 안전 차단되어 이중 전환이 없었음.
- `B_RATE_LIMITS_UNAVAILABLE`: 무료 B의 등록·인증은 성공했으나 7D/5H 사용량 데이터 조회는 기존 UI에서 'Usage unavailable'. 정확한 provider 응답 원인은 미확정. 0이나 A 사용량으로 대체해서는 안 됨.
- 실제 A 계정 5H/7D/reset credits는 정상, PC의 `^` 바로 왼쪽 WPF child taskbar panel에 0px 경계로 표시됨. A 원복 직후 이전 closeDesktop 실패 메시지가 잔류해 active-only gate가 닫혀 5H/7D/R가 '?'로 표시되는 현상을 관찰.
- 실패 메시지를 앱의 정상 dismiss 버튼으로 해제하고 정규 read-only refresh를 요청하니 실제 A 사용량이 **5H 87% / 7D 79% / R 2** 로 다시 표시됨 (2026-11-03 22:53 KST). A 원본 인증파일과 저장 A의 SHA256 동등성 재확인.
- Codex Desktop 17개 프로세스와 메인 창 1개가 마지막 read-only 점검에서 확인됨. 강제 종료/Explorer 재시작/토큰 공개는 없음.

## 근본 원인 및 소스 보완
- 기존 `AccountController.switchAccount`는 이전 OperationError가 남아 있어도 정상 전환 시작 시 `visibleError`를 제거하지 않음.
- Windows `ActiveMonitorPublicationGate.Observe`는 snapshot.Error가 있으면 fail closed. 따라서 이전 실패를 뒤이은 성공 전환 후에도 새 관측 데이터 표시가 막혔음.
- `Sources/SwitcherCore/AccountController.swift`: `isMutating=true`를 먼저 선언해 표시를 닫은 후, 이전 operation error를 `visibleError=nil`로 해제. 이후 전환이 실패하면 기존 catch가 새 오류를 그대로 표시함. 권한/원본/전환 경계 자체 변경 없음.
- `Tests/SwitcherCoreTests/CredentialSyncTests.swift`: 가상 A/B 프로필로 먼저 오류를 발생시키고 정상 A→B→A 전환을 수행하여 매 전환 이후 visibleError nil 및 신원 복원을 확인하는 합성 회귀시험 1개 추가.

## 검증 경계
- 이 패치의 Swift 컴파일/Swift Testing/CI는 이 보고서 작성 시점 **미실행**(Windows 로컬 swift 컴파일러 없음). CI 결과를 확인하기 전 PASS라 주장 금지.
- 이전 Windows UI/.NET 172/172 합성시험은 2026-11-03 통합 Gate에서 PASS였으나 위 Swift 최신 보완을 포함하지 않음.
- Windows Codex Desktop의 일반 'X' 닫기는 프로세스가 남는 사례가 있음. CloseMainWindow가 30초 안에 완전 종료하지 못해 실제 전환이 차단된 관찰. 사용자 동작으로 진짜 종료 뒤 전환 PASS. **완전 자동 A↔B 전환**은 아직 실사용 GO 아님. 비공식 앱 강제 종료/파일 직접 교체 우회 금지.
- 첫 실행에는 A 인증정보를 개인 Fork 저장소에 복사하는 정규 등록 경계가 있으며 사용자의 사전 승인이 있었음.
- 이 변경은 개인 GitHub Fork 기능 브랜치에 한정. 루트 README 등 기존 사용자 미커밋 파일 보존.

## 다음
Swift CI 검증 → B 무료 계정의 사용량 미제공 원인 규격 조사 → Windows Desktop 종료 동작의 정상 지원 경로 개발/검증 → 새 CI-host 포함 포터블 빌드. 기존 실사용 A를 보존하며 독립 검증 완료 전 main/Release/자동시작/운영 전환 금지.
