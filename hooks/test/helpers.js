// Test helpers: run a hook script as a child process against a throwaway home directory.

const fs = require("fs");
const os = require("os");
const path = require("path");
const cp = require("child_process");

const HOOKS = path.join(__dirname, "..");

// A fresh fake home; os.homedir() follows USERPROFILE on Windows and HOME elsewhere.
function tempHome() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), "csb-hooks-"));
  fs.mkdirSync(path.join(home, ".claude"), { recursive: true });
  return home;
}

// Run hooks/<script> with args, feeding payload (object or string) on stdin.
function runHook(home, script, args = [], payload = {}, extraEnv = {}) {
  const input = typeof payload === "string" ? payload : JSON.stringify(payload);
  const res = cp.spawnSync(process.execPath, [path.join(HOOKS, script), ...args], {
    input,
    env: { ...process.env, USERPROFILE: home, HOME: home, CLAUDE_STATUSBAR_DEBUG: "1", ...extraEnv },
    encoding: "utf8",
    timeout: 15000,
  });
  if (res.error) throw res.error;
  return res;
}

const sbDir = (home) => path.join(home, ".claude", "statusbar");
const sessDir = (home) => path.join(sbDir(home), "sessions.d");
const readJson = (file) => JSON.parse(fs.readFileSync(file, "utf8"));
const rmHome = (home) => fs.rmSync(home, { recursive: true, force: true });

module.exports = { HOOKS, tempHome, runHook, sbDir, sessDir, readJson, rmHome };
