import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const read = relative => fs.readFileSync(path.join(root, relative), "utf8");
const ours = "sirious0911/codex-account-switcher";
const upstream = "liuzhao1225/codex-account-switcher";

test("Windows updater looks up and opens releases in our personal repository", () => {
  const code = read("windows/CodexAccountSwitcher/NativeSettings.cs");
  assert.ok(code.includes("https://api.github.com/repos/" + ours + "/releases"));
  assert.ok(code.includes("https://github.com/" + ours + "/releases/tag/"));
  assert.ok(!code.includes("https://api.github.com/repos/" + upstream));
  assert.ok(!code.includes("https://github.com/" + upstream + "/releases/"));
});

test("npm package metadata, CLI, and smoke installer target our fork", () => {
  const pkg = JSON.parse(read("npm/package.json"));
  assert.equal(pkg.name, "@sirious0911/codex-account-switcher");
  assert.equal(pkg.repository.url, "git+https://github.com/" + ours + ".git");
  assert.equal(pkg.homepage, "https://github.com/" + ours);
  const smoke = read("scripts/check-npm-install.mjs");
  const workflow = read(".github/workflows/npm-installer.yml");
  assert.ok(smoke.includes("https://github.com/" + ours));
  assert.ok(workflow.includes("sirious0911-codex-account-switcher-0.2.1.tgz"));
  assert.ok(workflow.includes("steps.fork_release.outputs.available"));
  assert.ok(!smoke.includes("https://github.com/" + upstream));
});

test("macOS update URL and feed publication use our fork", () => {
  const mac = read("scripts/package-local-app.sh");
  const feed = read("scripts/prepare-macos-feed.mjs");
  assert.ok(mac.includes("https://sirious0911.github.io/codex-account-switcher/updates/macos/appcast.xml"));
  assert.ok(feed.includes("const repository = 'sirious0911/codex-account-switcher'"));
  assert.ok(!mac.includes("liuzhao1225.github.io/codex-account-switcher/updates"));
  assert.ok(!feed.includes("const repository = 'liuzhao1225/codex-account-switcher'"));
});

test("fork source, citations and site publication point to our repository", () => {
  const metadata = read("windows/Directory.Build.props");
  const citation = read("CITATION.cff");
  const site = read("site/index.html");
  const readme = read("README.md");
  const geo = read("scripts/check-site-geo.mjs");
  assert.ok(metadata.includes("<RepositoryUrl>https://github.com/" + ours + "</RepositoryUrl>"));
  assert.ok(citation.includes('repository-code: "https://github.com/' + ours + '"'));
  assert.ok(site.includes('<link rel="canonical" href="https://sirious0911.github.io/codex-account-switcher/">'));
  assert.ok(geo.includes('const baseURL = "https://sirious0911.github.io/codex-account-switcher/"'));
  assert.ok(readme.includes("https://github.com/" + ours));
});

test("original MIT copyright and attribution remain intact", () => {
  const license = read("LICENSE");
  const original = read("npm/package.json");
  assert.ok(license.includes("Copyright (c) 2026 liuzhao1225"));
  assert.ok(original.includes("Zhao Liu (liuzhao1225)"));
});
