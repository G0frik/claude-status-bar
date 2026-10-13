#!/usr/bin/env node
// Claude Code hook handler. Reads the hook payload from stdin, maps the event to a status,
// and atomically writes it to ~/.claude/statusbar/sessions.d/<session_id> (one file per session,
// so the widget can show several sessions) and to state.json (the latest event of any session).
// Usage: node update.js <prompt|pre|post|notify|permreq|stop>
// Set CLAUDE_STATUSBAR_DEBUG=1 to append every invocation to ~/.claude/statusbar/hooks.log.

const fs = require("fs");
const os = require("os");
const path = require("path");
const { writeJsonAtomic } = require("./atomic");

const dir = path.join(os.homedir(), ".claude", "statusbar");
const statePath = path.join(dir, "state.json");
const sessDir = path.join(dir, "sessions.d");
const event = process.argv[2] || "";

const TOOL_LABELS = {
  Bash: "Running command", PowerShell: "Running command", Edit: "Editing", Write: "Writing", MultiEdit: "Editing",
  NotebookEdit: "Editing", Read: "Reading", Grep: "Searching", Glob: "Searching",
  WebFetch: "Browsing web", WebSearch: "Searching web", Task: "Delegating",
  TodoWrite: "Planning",
};

function debugLog(line) {
  if (process.env.CLAUDE_STATUSBAR_DEBUG !== "1") return;
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.appendFileSync(path.join(dir, "hooks.log"), `${new Date().toISOString()} ${line}\n`);
  } catch {}
}

let raw = "";
process.stdin.on("data", (d) => (raw += d));
process.stdin.on("end", () => {
  let p = {};
  try { p = JSON.parse(raw || "{}"); } catch {}

  debugLog(`[${event}] tool=${p.tool_name || "-"} mode=${p.permission_mode || "-"} ` +
    `msg=${JSON.stringify(p.message || "").slice(0, 160)} keys=${Object.keys(p).join(",")}`);

  const sid = String(p.session_id || "").replace(/[^A-Za-z0-9_.-]/g, "").slice(0, 64);
  const sessPath = sid ? path.join(sessDir, sid) : "";

  // This session's previous status; fall back to state.json for sessions without a file yet.
  let prev = {};
  try { prev = JSON.parse(fs.readFileSync(sessPath || statePath, "utf8")); } catch {}

  const project = p.cwd ? path.basename(p.cwd) : prev.project || "";
  const ts = Math.floor(Date.now() / 1000);

  // Track the session so one that predates the hook install is still counted, even when this
  // event doesn't change its status.
  if (sessPath && !prev.state) write(sessPath, { state: "idle", label: "", project, sessionId: p.session_id || "", startedAt: 0, ts });
  let state = "idle", label = "", startedAt = prev.startedAt || 0;

  switch (event) {
    case "prompt":
      state = "thinking"; label = "Thinking…"; startedAt = ts; break;
    case "pre": {
      const t = p.tool_name || "";
      state = "tool"; label = TOOL_LABELS[t] || "Using tool";
      if (!startedAt) startedAt = ts;
      break;
    }
    case "post":
      state = "thinking"; label = "Thinking…";
      if (!startedAt) startedAt = ts;
      break;
    case "notify": {
      // Only permission prompts drive the icon on the CLI path; ignore other notifications.
      const m = (p.message || "").toLowerCase();
      const isPerm = p.notification_type === "permission_prompt" ||
        m.includes("permission") || m.includes("approve") || m.includes("allow");
      if (!isPerm) return;
      state = "permission"; label = "Awaiting permission"; startedAt = 0;
      break;
    }
    case "permreq":
      state = "permission"; label = "Awaiting permission"; startedAt = 0; break;
    case "stop":
      state = "done"; label = "Done"; startedAt = 0; break;
    default:
      return;
  }

  const out = {
    state, label,
    tool: p.tool_name || "",
    project,
    sessionId: p.session_id || "",
    transcript: p.transcript_path || prev.transcript || "",
    startedAt, ts,
  };
  if (sessPath) write(sessPath, out);
  write(statePath, out);
  debugLog(`-> state=${state} label="${label}" project=${project}`);
});

// The temp file goes in the status-bar dir, not sessions.d, where the widget would count it.
function write(file, obj) {
  try { writeJsonAtomic(file, obj, dir); }
  catch (e) { debugLog(`write failed: ${path.basename(file)} ${e.code || e.message}`); }
}
