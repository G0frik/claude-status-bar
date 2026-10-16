#!/usr/bin/env node
// Stress/demo tool: simulates several Claude Code sessions by running the real hook scripts
// (hooks/lifecycle.js and hooks/update.js) with random events, so the widget can be watched
// under load. Sessions start staggered, some end part-way and are replaced, and every session
// is ended at the finish. Prints a JSON summary; a non-zero exit means a hook failed or a write
// was dropped (hooks run with CLAUDE_STATUSBAR_DEBUG=1, which logs failed writes).
//
// Usage: node tools/simulate-sessions.js [--sessions 8] [--seconds 60] [--min-ms 50] [--max-ms 500]
//                                        [--long-turns] [--home <dir>]
// --long-turns keeps every turn going for at least 25 s, so finishing it notifies.
// Without --home it writes to your real ~/.claude/statusbar, where a running widget shows it;
// the widget must be running then, or lifecycle.js would clear the existing session files.

const fs = require("fs");
const os = require("os");
const path = require("path");
const cp = require("child_process");
const crypto = require("crypto");

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) =>
  (a.startsWith("--") ? [...acc, [a.slice(2), all[i + 1]]] : acc), []));
const N = Number(args.sessions || 8);
const SECONDS = Number(args.seconds || 60);
const MIN_MS = Number(args["min-ms"] || 50);
const MAX_MS = Number(args["max-ms"] || 500);
const HOME = args.home ? path.resolve(args.home) : os.homedir();
const LONG_TURNS = process.argv.includes("--long-turns");
const MIN_TURN_MS = 25000;
const HOOKS = path.join(__dirname, "..", "hooks");
const SB_DIR = path.join(HOME, ".claude", "statusbar");
const PROJECTS = ["alpha", "beta", "gamma", "alpha", "delta", "beta"]; // repeats test name numbering

if (!args.home) {
  const out = cp.execSync('tasklist /FI "IMAGENAME eq ClaudeStatusBar.exe" /NH', { encoding: "utf8" });
  if (!out.includes("ClaudeStatusBar.exe")) {
    console.error("The widget isn't running; start it first (or pass --home <dir> to simulate elsewhere).");
    process.exit(2);
  }
}

const env = { ...process.env, USERPROFILE: HOME, HOME, CLAUDE_STATUSBAR_DEBUG: "1" };
const stats = { sessionsStarted: 0, sessionsEnded: 0, events: 0, hookFailures: 0, byEvent: {} };
const ownIds = new Set(); // this run's sessions, so a concurrent run's files aren't counted as leftovers
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const rand = (lo, hi) => lo + Math.random() * (hi - lo);
const pick = (xs) => xs[Math.floor(Math.random() * xs.length)];

function hook(script, arg, payload) {
  return new Promise((resolve) => {
    const child = cp.spawn(process.execPath, [path.join(HOOKS, script), arg], { env, stdio: ["pipe", "ignore", "pipe"] });
    let err = "";
    child.stderr.on("data", (d) => (err += d));
    child.on("exit", (code) => {
      stats.events++;
      stats.byEvent[arg] = (stats.byEvent[arg] || 0) + 1;
      if (code !== 0) { stats.hookFailures++; console.error(`${script} ${arg} exited ${code}: ${err.trim()}`); }
      resolve();
    });
    child.stdin.end(JSON.stringify(payload));
  });
}

// The next hook event for a session in `state`, following Claude Code's real ordering.
// `turnMs` is how long the current turn has run; with --long-turns it can't stop early.
function nextEvent(state, turnMs) {
  switch (state) {
    case "idle": case "done": return "prompt";
    case "thinking":
      if (LONG_TURNS && turnMs < MIN_TURN_MS) return pick(["pre", "pre", "pre", "permreq"]);
      return pick(["pre", "pre", "pre", "stop", "permreq"]);
    case "tool": return "post";
    case "permission": return "post"; // approved; the tool runs
    default: return "prompt";
  }
}
const nextState = { prompt: "thinking", pre: "tool", post: "thinking", permreq: "permission", stop: "done" };
const TOOLS = ["Read", "Edit", "Bash", "Grep", "WebFetch", "Write"];

async function runSession(index, startDelay, lifetimeMs) {
  await sleep(startDelay);
  const id = `sim-${index}-${crypto.randomBytes(3).toString("hex")}`;
  ownIds.add(id);
  const base = { session_id: id, cwd: path.join("C:\\sim", PROJECTS[index % PROJECTS.length]), transcript_path: "" };
  await hook("lifecycle.js", "start", base);
  stats.sessionsStarted++;

  let state = "idle", turnStarted = Date.now();
  const end = Date.now() + lifetimeMs;
  while (Date.now() < end) {
    await sleep(state === "done" ? rand(MAX_MS, MAX_MS * 4) : rand(MIN_MS, MAX_MS)); // pause between turns
    const event = nextEvent(state, Date.now() - turnStarted);
    if (event === "prompt") turnStarted = Date.now();
    await hook("update.js", event, { ...base, tool_name: ["pre", "post", "permreq"].includes(event) ? pick(TOOLS) : undefined });
    state = nextState[event];
  }
  await hook("update.js", "stop", base);
  await hook("lifecycle.js", "end", base);
  stats.sessionsEnded++;
}

(async () => {
  const total = SECONDS * 1000;
  const logPath = path.join(SB_DIR, "hooks.log");
  const logStart = fs.existsSync(logPath) ? fs.statSync(logPath).size : 0;
  const started = Date.now();

  // N concurrent slots; each runs sessions back to back until time is up (churn: ~25% of
  // sessions end early and a new one takes the slot).
  await Promise.all(Array.from({ length: N }, async (_, slot) => {
    let generation = 0;
    let delay = rand(0, total * 0.2);
    while (Date.now() - started + delay < total - 2000) {
      const remaining = total - (Date.now() - started) - delay;
      const lifetime = Math.random() < 0.25 ? rand(remaining * 0.2, remaining * 0.6) : remaining;
      await runSession(slot + N * generation++, delay, lifetime);
      delay = rand(200, 1500);
    }
  }));

  let droppedWrites = 0;
  try {
    const log = fs.readFileSync(logPath, "utf8").slice(logStart);
    droppedWrites = (log.match(/write failed/g) || []).length;
  } catch {}
  const leftovers = (() => { try { return fs.readdirSync(path.join(SB_DIR, "sessions.d")).filter((f) => ownIds.has(f)); } catch { return []; } })();

  const summary = { ...stats, droppedWrites, leftoverSimSessions: leftovers.length, seconds: (Date.now() - started) / 1000 };
  console.log(JSON.stringify(summary, null, 2));
  process.exit(stats.hookFailures || droppedWrites || leftovers.length ? 1 : 0);
})();
