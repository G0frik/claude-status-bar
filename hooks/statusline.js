#!/usr/bin/env node
// Claude Code statusLine command. Records the claude.ai plan usage limits (the 5-hour and 7-day
// windows) from the status-line payload to ~/.claude/statusbar/usage.json for the widget's bars.
// Usage: node statusline.js [--tee]   (status-line JSON arrives on stdin)
//   --tee  echo the payload to stdout so it can be piped into the user's own statusLine command;
//          install.js chains it that way. Without it nothing is printed.
// Set CLAUDE_STATUSBAR_DEBUG=1 to append activity to ~/.claude/statusbar/hooks.log.

const fs = require("fs");
const os = require("os");
const path = require("path");

const dir = path.join(os.homedir(), ".claude", "statusbar");
const usagePath = path.join(dir, "usage.json");
const tee = process.argv.includes("--tee");

function debugLog(line) {
  if (process.env.CLAUDE_STATUSBAR_DEBUG !== "1") return;
  try { fs.appendFileSync(path.join(dir, "hooks.log"), `${new Date().toISOString()} [statusline] ${line}\n`); } catch {}
}

// A rate-limit window as { pct, resetsAt }, or null when absent or malformed.
function readWindow(w) {
  const pct = Number(w && w.used_percentage);
  if (!w || !Number.isFinite(pct)) return null;
  const resetsAt = Number(w.resets_at);
  return { pct: Math.min(100, Math.max(0, pct)), resetsAt: Number.isFinite(resetsAt) ? resetsAt : 0 };
}

// Write usage.json when the limits changed. rate_limits is absent for non-subscribers and before
// a session's first API response; the previous values are kept rather than cleared in that case.
function record(p) {
  const limits = p && p.rate_limits;
  if (!limits) return;
  const fiveHour = readWindow(limits.five_hour);
  const sevenDay = readWindow(limits.seven_day);
  if (!fiveHour && !sevenDay) return;

  try {
    const prev = JSON.parse(fs.readFileSync(usagePath, "utf8"));
    if (JSON.stringify([prev.fiveHour, prev.sevenDay]) === JSON.stringify([fiveHour, sevenDay])) return;
  } catch {}

  try {
    fs.mkdirSync(dir, { recursive: true });
    const tmp = usagePath + "." + process.pid + ".tmp";
    fs.writeFileSync(tmp, JSON.stringify({ fiveHour, sevenDay, source: "statusline", ts: Math.floor(Date.now() / 1000) }));
    fs.renameSync(tmp, usagePath);
    debugLog(`5h=${fiveHour ? fiveHour.pct : "-"} 7d=${sevenDay ? sevenDay.pct : "-"}`);
  } catch {}
}

let input = "", done = false;
process.stdin.on("data", (d) => (input += d));
process.stdin.on("end", run);
process.stdin.on("error", run);
setTimeout(run, 1000); // fall back if stdin never closes, so the status line is never blocked

function run() {
  if (done) return; done = true;
  let p = {};
  try { p = JSON.parse(input.replace(/^﻿/, "") || "{}"); } catch {}
  record(p);
  if (tee) process.stdout.write(input, () => process.exit(0));
  else process.exit(0);
}
