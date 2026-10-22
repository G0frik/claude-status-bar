// What install.js and uninstall.js both need to know about ~/.claude/settings.json: which hooks
// and statusLine are ours, and how to save it without ever leaving a half-written file.

const os = require("os");
const path = require("path");
const { writeFileAtomic } = require("./atomic");

const sbDir = path.join(os.homedir(), ".claude", "statusbar");
const settingsPath = path.join(os.homedir(), ".claude", "settings.json");
const MARKER = sbDir; // every hook command install.js adds points inside this dir

// Ours in either form: shell form (older installs) names the script in `command`, exec form in `args`.
const isOurHook = (h) => [h.command, ...(h.args || [])].some((s) => String(s || "").includes(MARKER));
const isOurStatusLine = (sl) => !!sl && (sl.command || "").includes(MARKER);

// An event's hook entries without ours; entries left with no hooks are dropped.
const withoutOurs = (entries) =>
  (entries || [])
    .map((e) => ({ ...e, hooks: (e.hooks || []).filter((h) => !isOurHook(h)) }))
    .filter((e) => e.hooks.length > 0);

const saveSettings = (settings) => writeFileAtomic(settingsPath, JSON.stringify(settings, null, 2) + "\n");

module.exports = { sbDir, settingsPath, isOurStatusLine, withoutOurs, saveSettings };
