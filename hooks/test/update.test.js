const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { tempHome, runHook, sbDir, sessDir, readJson, rmHome } = require("./helpers");

const payload = (sid, extra = {}) => ({ session_id: sid, cwd: "C:\\work\\my-project", transcript_path: "", ...extra });
const session = (home, sid) => readJson(path.join(sessDir(home), sid));

test("prompt writes the session's own file and state.json", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["prompt"], payload("s1"));

  const s = session(home, "s1");
  assert.equal(s.state, "thinking");
  assert.equal(s.project, "my-project");
  assert.equal(s.sessionId, "s1");
  assert.ok(s.startedAt > 0);
  assert.deepEqual(readJson(path.join(sbDir(home), "state.json")), s);
});

test("pre, post, permreq and stop move the session through its states", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["prompt"], payload("s1"));
  const started = session(home, "s1").startedAt;

  runHook(home, "update.js", ["pre"], payload("s1", { tool_name: "Edit" }));
  assert.equal(session(home, "s1").state, "tool");
  assert.equal(session(home, "s1").label, "Editing");
  assert.equal(session(home, "s1").startedAt, started, "a tool call keeps the turn's start time");

  runHook(home, "update.js", ["post"], payload("s1", { tool_name: "Edit" }));
  assert.equal(session(home, "s1").state, "thinking");

  runHook(home, "update.js", ["permreq"], payload("s1"));
  assert.equal(session(home, "s1").state, "permission");

  runHook(home, "update.js", ["stop"], payload("s1"));
  assert.equal(session(home, "s1").state, "done");
  assert.equal(session(home, "s1").startedAt, 0);
});

test("two sessions keep separate statuses and timers", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["prompt"], payload("a", { cwd: "C:\\work\\alpha" }));
  const aStarted = session(home, "a").startedAt;

  // b starts later and is mid-tool; a stop for b must not touch a.
  runHook(home, "update.js", ["pre"], payload("b", { cwd: "C:\\work\\beta", tool_name: "Bash" }));
  runHook(home, "update.js", ["stop"], payload("b", { cwd: "C:\\work\\beta" }));

  assert.equal(session(home, "a").state, "thinking");
  assert.equal(session(home, "a").startedAt, aStarted);
  assert.equal(session(home, "a").project, "alpha");
  assert.equal(session(home, "b").state, "done");
  assert.equal(session(home, "b").project, "beta");
  assert.equal(readJson(path.join(sbDir(home), "state.json")).sessionId, "b", "state.json holds the latest event");
});

test("a pre event for a fresh session doesn't inherit another session's start time", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  // Session a's old turn start sits in state.json.
  fs.mkdirSync(sbDir(home), { recursive: true });
  fs.writeFileSync(path.join(sbDir(home), "state.json"), JSON.stringify({ state: "thinking", sessionId: "a", startedAt: 1000, ts: 1000 }));
  fs.mkdirSync(sessDir(home), { recursive: true });
  fs.writeFileSync(path.join(sessDir(home), "b"), JSON.stringify({ state: "idle", startedAt: 0, ts: 1 }));

  runHook(home, "update.js", ["pre"], payload("b", { tool_name: "Read" }));
  assert.notEqual(session(home, "b").startedAt, 1000);
  assert.ok(session(home, "b").startedAt > 1000);
});

test("a non-permission notification keeps the status but registers the session", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["notify"], payload("s1", { message: "Claude is waiting for your input", notification_type: "idle_prompt" }));
  assert.equal(session(home, "s1").state, "idle");

  runHook(home, "update.js", ["prompt"], payload("s1"));
  runHook(home, "update.js", ["notify"], payload("s1", { message: "Claude is waiting for your input", notification_type: "idle_prompt" }));
  assert.equal(session(home, "s1").state, "thinking");

  runHook(home, "update.js", ["notify"], payload("s1", { message: "Claude needs your permission to use Bash", notification_type: "permission_prompt" }));
  assert.equal(session(home, "s1").state, "permission");
});

test("session ids are sanitized into safe file names", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["prompt"], payload("..\\..\\evil/../id"));
  assert.deepEqual(fs.readdirSync(sessDir(home)), ["....evil..id"]);
});

test("unknown events and bad input change nothing and never crash", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  assert.equal(runHook(home, "update.js", ["bogus"], payload("s1")).status, 0);
  assert.equal(runHook(home, "update.js", ["prompt"], "{not json").status, 0);
  assert.ok(!fs.existsSync(path.join(sessDir(home), "s1")) || session(home, "s1").state === "idle");
});

test("writes leave no temp files behind, in sessions.d or the status-bar dir", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  for (const e of ["prompt", "pre", "post", "stop"]) runHook(home, "update.js", [e], payload("s1", { tool_name: "Edit" }));
  assert.deepEqual(fs.readdirSync(sessDir(home)), ["s1"]);
  assert.deepEqual(fs.readdirSync(sbDir(home)).filter((f) => f.endsWith(".tmp")), []);
  const log = fs.readFileSync(path.join(sbDir(home), "hooks.log"), "utf8");
  assert.doesNotMatch(log, /write failed/);
});

test("records the parent process (the Claude Code session with exec-form hooks)", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["prompt"], payload("s1"));
  assert.equal(session(home, "s1").pid, process.pid, "the test runner is the hook's parent here");
});

test("a permission request records what it is for", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "update.js", ["permreq"], payload("s1", { tool_name: "Bash" }));
  assert.equal(session(home, "s1").detail, "Bash");

  runHook(home, "update.js", ["notify"], payload("s2", { message: "Claude needs your permission to use Write", notification_type: "permission_prompt" }));
  assert.equal(session(home, "s2").detail, "Claude needs your permission to use Write");

  runHook(home, "update.js", ["post"], payload("s1", { tool_name: "Bash" }));
  assert.equal(session(home, "s1").detail, "", "cleared once the session moves on");
});

test("stop records how long the turn took", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  fs.mkdirSync(sessDir(home), { recursive: true });
  const now = Math.floor(Date.now() / 1000);
  fs.writeFileSync(path.join(sessDir(home), "s1"), JSON.stringify({ state: "tool", startedAt: now - 95, ts: now }));

  runHook(home, "update.js", ["stop"], payload("s1"));
  const s = session(home, "s1");
  assert.ok(s.lastTurnSeconds >= 95 && s.lastTurnSeconds <= 97, `got ${s.lastTurnSeconds}`);
  assert.equal(s.startedAt, 0);

  runHook(home, "update.js", ["prompt"], payload("s1"));
  assert.equal(session(home, "s1").lastTurnSeconds, 0, "only the stop event carries it");
});

test("waiting for permission keeps the turn's start, so the whole turn is timed", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  fs.mkdirSync(sessDir(home), { recursive: true });
  const now = Math.floor(Date.now() / 1000);
  fs.writeFileSync(path.join(sessDir(home), "s1"), JSON.stringify({ state: "thinking", startedAt: now - 60, ts: now }));

  runHook(home, "update.js", ["permreq"], payload("s1", { tool_name: "Bash" }));
  assert.equal(session(home, "s1").startedAt, now - 60);
  runHook(home, "update.js", ["post"], payload("s1", { tool_name: "Bash" }));
  assert.equal(session(home, "s1").startedAt, now - 60, "approval doesn't restart the timer");
  runHook(home, "update.js", ["stop"], payload("s1"));
  assert.ok(session(home, "s1").lastTurnSeconds >= 60);
});
