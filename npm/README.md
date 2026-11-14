# Codex Account Switcher — 개인 Fork 설치 도우미

개인 저장소: https://github.com/sirious0911/codex-account-switcher

원본 작성자: Zhao Liu (liuzhao1225). 원본 [MIT 라이선스](LICENSE)는 그대로 유지합니다.

**계획된 npm 패키지 이름:** `@sirious0911/codex-account-switcher`

> 아직 개인 GitHub에 릴리스가 없고 npm 패키지도 게시 확인되지 않았습니다. 아래 명령어는 **개인 빌드·릴리스·npm 게시 이후**에만 유효하며, 현재는 사용하지 마세요.

```bash
npm install -g @sirious0911/codex-account-switcher
npx @sirious0911/codex-account-switcher@latest install
```

설치 도우미는 개인 저장소 **sirious0911/codex-account-switcher**의 GitHub Releases에서 플랫폼에 맞는 패키지를 조회하고 SHA-256 검증 후 설치하도록 연결됩니다. 원본 계정 `liuzhao1225`의 Release를 설치하지 않습니다.

소스 시험:

```bash
node --test scripts/tests/npm-*.test.mjs
```

실제 설치·업데이트 시험은 개인 저장소에 EXE/DMG가 정상 게시된 뒤 별도로 진행합니다. 사용자 계정 전환·사용량·Telegram 개발은 개인 Fork 저장소에서만 관리합니다.
