#!/usr/bin/env node
import { main } from "./cli.mjs";

if (process.env.npm_config_global === "true") {
  await main(["install"]);
} else {
  console.log("Desktop installation: npx @sirious0911/codex-account-switcher install");
}
