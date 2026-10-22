// Background work that outlives the turn that started it: run_in_background shell commands,
// Monitor watches and background subagents. Claude Code records each task's launch in the
// session transcript as a tool result, and its end as a <task-notification> carrying a <status>
// (or as a TaskStop result). A task launched but not yet ended is still running. When a task
// ends Claude is woken to handle it and Stop fires again, so rescanning on Stop keeps the
// count current.
//
// The transcript only grows, so each scan reads from where the previous one stopped; the
// pending tasks and that offset are kept in the session's status file between hook runs.

const fs = require("fs");

// Cheap pre-filter for launch and TaskStop results, matched on the raw line before parsing it.
// Inside a quoted tool output the key's quotes are escaped, so those lines don't match.
const RESULT_KEY = /"(backgroundTaskId|timeoutMs|agentId|task_id)":/;
const NOTIFICATION = /<task-notification>([\s\S]*?)<\/task-notification>/g;
const TASK_ID = /<task-id>([^<]+)<\/task-id>/g;
const STATUS = /<status>([^<]*)<\/status>/;

// Read size. A first scan of a long session can cover tens of MB; chunks keep memory near this
// plus the longest line (a transcript line can be over 1 MB).
const CHUNK = 1 << 20;

/**
 * The session's pending background tasks after reading what the transcript gained since `prev`.
 * Returns { offset, tasks }, where tasks maps a task id to when it expires (unix seconds; 0 when
 * it has no time limit). A transcript that shrank, or a different one, is read from the start.
 */
function scanBackground(transcript, prev, now = Date.now() / 1000) {
  if (!transcript) return { offset: 0, tasks: {} };
  // prev comes from the status file on disk; anything malformed starts over.
  let offset = Number.isSafeInteger(prev?.offset) && prev.offset > 0 ? prev.offset : 0;
  let tasks = prev?.tasks && typeof prev.tasks === "object" && !Array.isArray(prev.tasks) ? { ...prev.tasks } : {};

  let size;
  try { size = fs.statSync(transcript).size; } catch { return { offset, tasks }; }
  if (size < offset) { offset = 0; tasks = {}; }

  if (size > offset) {
    // Whole lines only: a line still being written is read next time. If a read fails partway,
    // the lines applied so far are kept with the offset after them, and the rest is read next stop.
    let fd;
    try {
      fd = fs.openSync(transcript, "r");
      let carry = null; // the unfinished line at the end of the previous chunk
      for (let pos = offset; pos < size;) {
        const chunk = Buffer.allocUnsafe(Math.min(CHUNK, size - pos));
        const n = fs.readSync(fd, chunk, 0, chunk.length, pos);
        if (n <= 0) break;
        pos += n;
        const data = carry ? Buffer.concat([carry, chunk.subarray(0, n)]) : chunk.subarray(0, n);
        const end = data.lastIndexOf(10);
        if (end < 0) { carry = data; continue; }
        for (const line of data.toString("utf8", 0, end).split("\n")) apply(line, tasks);
        offset += end + 1; // data always starts at offset
        carry = end + 1 < data.length ? data.subarray(end + 1) : null;
      }
    } catch {} // unreadable right now: retry next stop
    finally { if (fd !== undefined) fs.closeSync(fd); }
  }

  // A Monitor stops on its own at its timeout, even if that notice was missed.
  for (const [id, expires] of Object.entries(tasks)) if (expires && expires <= now) delete tasks[id];
  return { offset, tasks };
}

function apply(line, tasks) {
  if (RESULT_KEY.test(line)) launched(line, tasks);
  if (line.includes("<task-notification>")) ended(line, tasks);
}

function launched(line, tasks) {
  let o;
  try { o = JSON.parse(line); } catch { return; }
  if (!o || o.isSidechain) return; // a subagent's own tasks report back to the subagent
  const r = o.toolUseResult;
  if (!r || typeof r !== "object") return;

  if (typeof r.backgroundTaskId === "string") tasks[r.backgroundTaskId] = 0; // Bash/PowerShell
  else if (typeof r.taskId === "string" && "timeoutMs" in r) tasks[r.taskId] = r.persistent ? 0 : expiry(o.timestamp, r.timeoutMs); // Monitor
  else if (r.status === "async_launched" && typeof r.agentId === "string") tasks[r.agentId] = 0; // background subagent
  else if (typeof r.task_id === "string" && /^Successfully stopped/.test(r.message || "")) delete tasks[r.task_id]; // TaskStop
}

// A notification with any status other than "running" ends its tasks (completed, failed,
// killed, or stopped when a resumed session finds them orphaned). A Monitor's timeout arrives
// as an event without a status.
function ended(line, tasks) {
  for (const [, body] of line.matchAll(NOTIFICATION)) {
    const status = (body.match(STATUS) || [])[1];
    const done = status ? status !== "running" : body.includes("<event>[Monitor expired");
    if (!done) continue;
    for (const [, id] of body.matchAll(TASK_ID)) delete tasks[id];
  }
}

function expiry(timestamp, timeoutMs) {
  const ms = Number(timeoutMs);
  if (!Number.isFinite(ms) || ms <= 0) return 0;
  const at = Date.parse(timestamp);
  return Math.floor((Number.isFinite(at) ? at : Date.now()) / 1000 + ms / 1000);
}

module.exports = { scanBackground };
