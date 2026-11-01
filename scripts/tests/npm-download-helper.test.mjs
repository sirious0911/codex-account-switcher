import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";

const cli = new URL("../../npm/bin/download.mjs", import.meta.url);
const metadata = JSON.parse(readFileSync(new URL("../../npm/package.json", import.meta.url), "utf8"));
const run = (...args) => spawnSync(process.execPath, [fileURLToPath(cli), ...args], { encoding: "utf8" });

test("download helper identifies the native project and both exact release assets", () => {
  const result = run("--json");
  assert.equal(result.status, 0, result.stderr);
  const info = JSON.parse(result.stdout);
  assert.equal(info.repository, "https://github.com/sirious0911/codex-account-switcher");
  assert.equal(info.author, "Zhao Liu (liuzhao1225)");
  assert.equal(info.downloads.macOS, `${info.repository}/releases/latest/download/Codex-Account-Switcher-macos-arm64.dmg`);
  assert.equal(info.downloads.windows, `${info.repository}/releases/latest/download/Codex-Account-Switcher-windows-x64.exe`);
  assert.equal(metadata.name, "@sirious0911/codex-account-switcher");
});

test("default invocation prints guidance and rejects unsupported commands", () => {
  const result = run();
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Account management runs in the native app/);
  const unsupported = run("switch");
  assert.equal(unsupported.status, 1);
  assert.match(unsupported.stderr, /Usage:/);
});

test("installer packages only its commands and library, with a global-install lifecycle entry", () => {
  assert.deepEqual(metadata.scripts, { postinstall: "node bin/postinstall.mjs" });
  assert.equal(metadata.dependencies, undefined);
  assert.equal(metadata.optionalDependencies, undefined);
  assert.equal(metadata.publishConfig.access, "public");
  assert.deepEqual(metadata.files, ["bin/", "lib/"]);
  const result = spawnSync(process.execPath, [fileURLToPath(new URL("../../npm/bin/postinstall.mjs", import.meta.url))], {
    encoding: "utf8", env: { ...process.env, npm_config_global: "false" },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Desktop installation: npx/);
});
