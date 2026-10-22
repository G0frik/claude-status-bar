#!/usr/bin/env node
// SessionStart/SessionEnd hook. Launches ClaudeStatusBar.exe and tracks each session as a file
// under sessions.d/ (holding that session's status, which update.js keeps current); the app exits
// on its own once no sessions remain.
// Usage: node lifecycle.js <start|end>   (hook payload, including session_id, arrives on stdin)
// Set CLAUDE_STATUSBAR_DEBUG=1 to append activity to ~/.claude/statusbar/hooks.log.

const fs = require("fs");
const os = require("os");
const path = require("path");
const cp = require("child_process");
const { writeJsonAtomic } = require("./atomic");
const { widgetEnv } = require("./widgetenv");

const dir = path.join(os.homedir(), ".claude", "statusbar");
const sessDir = path.join(dir, "sessions.d");
const statePath = path.join(dir, "state.json");
const exePathFile = path.join(dir, "app-path.txt"); // absolute path to the exe, written by install.js
const event = process.argv[2];

fs.mkdirSync(sessDir, { recursive: true });

function debugLog(line) {
  if (process.env.CLAUDE_STATUSBAR_DEBUG !== "1") return;
  try { fs.appendFileSync(path.join(dir, "hooks.log"), `${new Date().toISOString()} [lifecycle:${event}] ${line}\n`); } catch {}
}

const safeId = (s) => String(s || "").replace(/[^A-Za-z0-9_.-]/g, "").slice(0, 64) || "unknown";

function launch() {
  let exe = "";
  try { exe = fs.readFileSync(exePathFile, "utf8").trim(); } catch {}
  if (!exe || !fs.existsSync(exe)) return;
  const child = cp.spawn(exe, [], { detached: true, stdio: "ignore", windowsHide: true, env: widgetEnv(process.env) });
  child.unref();
}

// Background tasks belong to the Claude Code process that started them. A session started again
// in a new process (startup or resume, unlike compaction) has none running, whatever its status
// file says; Claude Code reports them as stopped in the transcript, which nothing reads until the
// next Stop. Clear them now so the session doesn't wait on them until then.
function clearBackground(sessPath) {
  try {
    const prev = JSON.parse(fs.readFileSync(sessPath, "utf8"));
    if (prev.state !== "background") return;
    const bg = prev.bg ? { offset: prev.bg.offset, tasks: {} } : undefined;
    writeJsonAtomic(sessPath, { ...prev, state: "idle", label: "", startedAt: 0, bg, ts: Math.floor(Date.now() / 1000) }, dir);
  } catch {}
}

// Reset the status to idle when the owning session ends without a Stop event. The session-id
// check ensures a different session's active turn is left untouched.
function clearStaleState(id) {
  try {
    const prev = JSON.parse(fs.readFileSync(statePath, "utf8"));
    if (safeId(prev.sessionId) !== id) return;
    if (!["thinking", "tool", "permission", "background"].includes(prev.state)) return;
    writeJsonAtomic(statePath, { ...prev, state: "idle", label: "", startedAt: 0, ts: Math.floor(Date.now() / 1000) });
  } catch {}
}

let input = "", done = false;
process.stdin.on("data", (d) => (input += d));
process.stdin.on("end", run);
process.stdin.on("error", run);
setTimeout(run, 1000); // fall back if stdin never closes, so the session is never blocked

function run() {
  if (done) return; done = true;
  let p = {};
  try { p = JSON.parse(input) || {}; } catch {}
  const id = safeId(p.session_id);

  if (event === "start") {
    // Empty files are leftovers of hooks older than per-session status (these hooks never write
    // one). Other sessions' files stay, even when several start at once: the widget removes the
    // file of a session whose process is gone.
    try { for (const f of fs.readdirSync(sessDir)) if (f !== id && fs.statSync(path.join(sessDir, f)).size === 0) fs.rmSync(path.join(sessDir, f), { force: true }); } catch {}
    // SessionStart also fires on resume and compaction; keep an existing entry's status.
    const sessPath = path.join(sessDir, id);
    let exists = false;
    try { exists = fs.statSync(sessPath).size > 0; } catch {}
    if (!exists) {
      const project = p.cwd ? path.basename(p.cwd) : "";
      // pid: with exec-form hooks the parent process is the session's Claude Code process.
      // transcript: the file appears with the first prompt; until then the widget hides the
      // session (the editor extension starts spare sessions that no chat tab shows).
      const entry = { state: "idle", label: "", project, sessionId: p.session_id || "", transcript: p.transcript_path || "", startedAt: 0, ts: Math.floor(Date.now() / 1000), pid: process.ppid };
      try { writeJsonAtomic(sessPath, entry, dir); } catch {} // temp file outside sessions.d
    } else if (p.source === "startup" || p.source === "resume") {
      clearBackground(sessPath);
    }
    clearStaleState(id);
    launch();
  } else if (event === "end") {
    try { fs.rmSync(path.join(sessDir, id), { force: true }); } catch {}
    clearStaleState(id);
  }
  debugLog(`session=${id} sessions=${(() => { try { return fs.readdirSync(sessDir).length; } catch { return "?"; } })()}`);
  process.exit(0);
}
