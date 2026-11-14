import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const cases = [
  ["original event engine", "offline-tests.mjs", /OFFLINE_TESTS_PASS checks=45 actualCodexQueries=0 telegramSends=0/],
  ["existing mock delivery and deduplication", "mock-outbox-tests.mjs", /MOCK_OUTBOX_PASS checks=13 realTelegramSend=0 realAccountRead=0/],
  ["existing Node parity golden vectors", "parity-tests.mjs", /PARITY_NODE_GOLDEN_PASS checks=4/]
];

for (const [name, file, expected] of cases) {
  test(name + " regression remains part of fork CI", () => {
    const result = spawnSync(process.execPath, [path.join(root, "monitor", "tests", file)], {
      cwd: root,
      encoding: "utf8",
      windowsHide: true,
      timeout: 20_000,
      maxBuffer: 2 * 1024 * 1024
    });
    assert.equal(result.error, undefined, String(result.error));
    assert.equal(result.status, 0, result.stderr || result.stdout);
    assert.match(result.stdout, expected);
  });
}
