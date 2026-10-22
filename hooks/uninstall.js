#!/usr/bin/env node
// Removes the status-bar hooks from ~/.claude/settings.json and stops the running app.
// Other hooks are left intact. Our statusLine is replaced by the one it was chained to, if any.
// The settings backup and the app executable are not touched.

const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const { sbDir, settingsPath, isOurStatusLine, withoutOurs, saveSettings } = require("./claudesettings");

const chainPath = path.join(sbDir, "statusline-chain.json"); // saved by install.js

try { cp.execSync('taskkill /IM ClaudeStatusBar.exe /F', { stdio: "ignore" }); } catch {}

if (!fs.existsSync(settingsPath)) {
  console.log("No settings.json; nothing to remove.");
  process.exit(0);
}

const settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
for (const evt of Object.keys(settings.hooks || {})) {
  settings.hooks[evt] = withoutOurs(settings.hooks[evt]);
  if (settings.hooks[evt].length === 0) delete settings.hooks[evt];
}

if (isOurStatusLine(settings.statusLine)) {
  let chained = null;
  try { chained = JSON.parse(fs.readFileSync(chainPath, "utf8")); } catch {}
  if (chained) settings.statusLine = chained;
  else delete settings.statusLine;
  fs.rmSync(chainPath, { force: true });
}

saveSettings(settings);
console.log("Removed status-bar hooks from", settingsPath);
