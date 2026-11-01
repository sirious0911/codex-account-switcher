// Real release smoke check. Use only an isolated CODEX_ACCOUNT_SWITCHER_INSTALL_DIR.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { downloadVerified, fileHash, installMac, installTarget, installWindows, latestRelease, releaseChecksum } from '../npm/lib/install.mjs';

const directory = process.env.CODEX_ACCOUNT_SWITCHER_INSTALL_DIR;
if (!directory || !directory.includes('npm-installer')) throw new Error('An isolated npm-installer smoke directory is required.');
const repository = 'https://github.com/sirious0911/codex-account-switcher';
const { platform, asset, target } = await installTarget({ directory });
const cli = fileURLToPath(new URL('../npm/bin/cli.mjs', import.meta.url));
function run(command, args) {
  const result = spawnSync(command, args, { encoding: 'utf8', env: process.env });
  if (result.status !== 0 || result.error) throw new Error(result.error?.message ?? result.stderr + result.stdout);
  return result.stdout.trim();
}
const invoke = (...args) => {
  const output = run(process.execPath, [cli, ...args]);
  console.log(output);
  return output;
};
assert.ok(await stat(target), 'Global postinstall must already have installed the app.');
assert.match(invoke(), /Already up to date/);

// A new fork has no older self-owned release to downgrade to.
// Verify a current-version update is idempotent, and leave upgrade
// transitions covered by the offline npm installer fixture tests.
assert.match(invoke('update'), /Already up to date|Updated Codex Account Switcher/);
const latest = await latestRelease(repository);
if (platform === 'darwin') {
  const actual = run('/usr/bin/plutil', ['-extract', 'CFBundleShortVersionString', 'raw', '-o', '-', path.join(target, 'Contents', 'Info.plist')]);
  assert.equal(actual, latest.version);
  run('/usr/bin/codesign', ['--verify', '--deep', '--strict', target]);
} else {
  assert.equal(await fileHash(target), await releaseChecksum(latest.downloadBase, asset));
  const script = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); Add-Type -Path ./npm/lib/windows-shortcut.cs; $link = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Account Switcher Personal.lnk'; [SwitcherShortcut]::Read($link)";
  const actual = run('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')]);
  assert.equal(actual.toLowerCase(), target.toLowerCase());
}
const neighbor = path.join(directory, 'keep-user-file.txt');
await writeFile(neighbor, 'preserve');
assert.match(invoke('uninstall'), /Saved accounts and preferences were preserved/);
await assert.rejects(stat(target), { code: 'ENOENT' });
assert.equal(await readFile(neighbor, 'utf8'), 'preserve');
console.log(`Verified fork release installation, current-version no-op, launcher and uninstall for v${latest.version} on ${platform}.`);
