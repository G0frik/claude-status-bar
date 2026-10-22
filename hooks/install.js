#!/usr/bin/env node
// Installer. Merges the status-bar hooks into ~/.claude/settings.json, preserving any other
// hooks, copies the hook scripts to ~/.claude/statusbar/, and records the path to
// ClaudeStatusBar.exe for lifecycle.js. Also sets the statusLine to statusline.js (which feeds
// the usage bars); an existing statusLine is saved to statusline-chain.json and kept running by
// piping into it. Safe to run repeatedly; existing status-bar hooks are removed before being
// re-added.
// Usage: node install.js [path\to\ClaudeStatusBar.exe]

const fs = require("fs");
const path = require("path");
const { sbDir, settingsPath, isOurStatusLine, withoutOurs, saveSettings } = require("./claudesettings");

const updateDest = path.join(sbDir, "update.js");
const lifecycleDest = path.join(sbDir, "lifecycle.js");
const statuslineDest = path.join(sbDir, "statusline.js");
const chainPath = path.join(sbDir, "statusline-chain.json"); // the user's own statusLine, if any
const exePathFile = path.join(sbDir, "app-path.txt");

fs.mkdirSync(sbDir, { recursive: true });
fs.copyFileSync(path.join(__dirname, "update.js"), updateDest);
fs.copyFileSync(path.join(__dirname, "lifecycle.js"), lifecycleDest);
fs.copyFileSync(path.join(__dirname, "statusline.js"), statuslineDest);
fs.copyFileSync(path.join(__dirname, "atomic.js"), path.join(sbDir, "atomic.js")); // shared by the above
fs.copyFileSync(path.join(__dirname, "background.js"), path.join(sbDir, "background.js")); // used by update.js
fs.copyFileSync(path.join(__dirname, "widgetenv.js"), path.join(sbDir, "widgetenv.js")); // used by lifecycle.js

// Record the exe location for the launcher. Prefer an explicit arg; else guess next to install.js.
const exeArg = process.argv[2];
const guessed = path.join(__dirname, "..", "ClaudeStatusBar.exe");
const exe = exeArg || (fs.existsSync(guessed) ? guessed : "");
if (exe) fs.writeFileSync(exePathFile, path.resolve(exe));
else console.log("(!) No exe path given and none found; set", exePathFile, "manually.");

// Hook commands, in exec form: Claude Code starts node directly instead of through a shell, so
// a hook's parent process is the session's own Claude Code process, whose PID the hooks record
// (the widget uses it to find the session's window).
const q = (p) => `"${p}"`;
const cmd = (evt) => ({ command: "node", args: [updateDest, evt] });
const life = (evt) => ({ command: "node", args: [lifecycleDest, evt] });

let settings = {};
if (fs.existsSync(settingsPath)) {
  settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
  const bak = settingsPath + ".bak-statusbar";
  if (!fs.existsSync(bak)) fs.copyFileSync(settingsPath, bak);
}
settings.hooks = settings.hooks || {};

const addUnmatched = (evt, hook) => {
  settings.hooks[evt] = withoutOurs(settings.hooks[evt]);
  settings.hooks[evt].push({ hooks: [{ type: "command", ...hook }] });
};
const addMatched = (evt, hook) => {
  settings.hooks[evt] = withoutOurs(settings.hooks[evt]);
  settings.hooks[evt].push({ matcher: "*", hooks: [{ type: "command", ...hook }] });
};

// Status hooks (drive the animation/label)
addUnmatched("UserPromptSubmit", cmd("prompt"));
addMatched("PreToolUse", cmd("pre"));
addMatched("PostToolUse", cmd("post"));
addUnmatched("Notification", cmd("notify"));
addMatched("PermissionRequest", cmd("permreq"));
addUnmatched("Stop", cmd("stop"));
// Lifecycle hooks (launch the app on open; the app quits itself when no longer needed)
addUnmatched("SessionStart", life("start"));
addUnmatched("SessionEnd", life("end"));

// Status line (feeds the usage bars). A statusLine that isn't ours is saved and chained after
// ours, so the user's own status line keeps rendering. On a re-run the saved one is reused.
let chained = null;
if (settings.statusLine && !isOurStatusLine(settings.statusLine)) {
  chained = settings.statusLine;
  fs.writeFileSync(chainPath, JSON.stringify(chained, null, 2) + "\n");
} else if (isOurStatusLine(settings.statusLine)) {
  try { chained = JSON.parse(fs.readFileSync(chainPath, "utf8")); } catch {}
} else {
  fs.rmSync(chainPath, { force: true });
}
const statusCmd = `node ${q(statuslineDest)}`;
settings.statusLine = {
  ...(chained || {}), // keeps padding / refreshInterval
  type: "command",
  command: chained && chained.command ? `${statusCmd} --tee | ${chained.command}` : statusCmd,
};

saveSettings(settings);
console.log("Installed status-bar hooks into", settingsPath);
console.log("Scripts:", updateDest, lifecycleDest, "and", statuslineDest);
if (chained) console.log("Existing statusLine kept (chained after ours); saved to", chainPath);
if (exe) console.log("App path recorded:", path.resolve(exe));
console.log("Backup (first run only):", settingsPath + ".bak-statusbar");
