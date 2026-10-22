const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { tempHome, runHook, sbDir, sessDir, readJson, rmHome } = require("./helpers");

const payload = (sid, extra = {}) => ({ session_id: sid, cwd: "C:\\work\\my-project", ...extra });

test("start registers the session as an idle JSON entry with its project", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("s1"));

  const s = readJson(path.join(sessDir(home), "s1"));
  assert.equal(s.state, "idle");
  assert.equal(s.project, "my-project");
  assert.ok(s.ts > 0);
  assert.deepEqual(fs.readdirSync(sessDir(home)), ["s1"], "no temp file left in sessions.d");
});

// Without System32 on PATH, lifecycle.js can't run tasklist and sees no widget running, which is
// what a CI runner (or a quit widget) looks like. Tests that depend on it must not hinge on
// whether a real ClaudeStatusBar.exe happens to run on the machine.
const NO_WIDGET = { PATH: path.dirname(process.execPath) };

test("start records the transcript path, so the widget can tell a session that has no conversation yet", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("s1", { transcript_path: "C:\\x\\s1.jsonl" }));
  assert.equal(readJson(path.join(sessDir(home), "s1")).transcript, "C:\\x\\s1.jsonl");
});

test("start on resume or compaction keeps the session's current status", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("s1"), NO_WIDGET);
  runHook(home, "update.js", ["prompt"], payload("s1"));
  runHook(home, "lifecycle.js", ["start"], payload("s1", { source: "compact" }), NO_WIDGET);
  assert.equal(readJson(path.join(sessDir(home), "s1")).state, "thinking");
});

test("a resumed session no longer waits on the previous process's background tasks; compaction still does", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  fs.mkdirSync(sessDir(home), { recursive: true });
  const file = path.join(sessDir(home), "s1");
  const waiting = { state: "background", label: "1 background task", startedAt: 100, ts: 200, bg: { offset: 5000, tasks: { b1: 0 } } };

  fs.writeFileSync(file, JSON.stringify(waiting));
  runHook(home, "lifecycle.js", ["start"], payload("s1", { source: "compact" }), NO_WIDGET);
  assert.equal(readJson(file).state, "background");

  runHook(home, "lifecycle.js", ["start"], payload("s1", { source: "resume" }), NO_WIDGET);
  const s = readJson(file);
  assert.equal(s.state, "idle");
  assert.equal(s.startedAt, 0);
  assert.deepEqual(s.bg, { offset: 5000, tasks: {} }, "the transcript isn't read again from the start");
});

test("start with no widget running clears other sessions' leftover files", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("crashed"), NO_WIDGET);
  runHook(home, "lifecycle.js", ["start"], payload("s1"), NO_WIDGET);
  assert.deepEqual(fs.readdirSync(sessDir(home)), ["s1"]);
});

test("end removes the session and idles state.json only if it was that session's", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("a"));
  runHook(home, "lifecycle.js", ["start"], payload("b"));
  runHook(home, "update.js", ["prompt"], payload("b"));

  runHook(home, "lifecycle.js", ["end"], payload("a"));
  assert.deepEqual(fs.readdirSync(sessDir(home)), ["b"]);
  assert.equal(readJson(path.join(sbDir(home), "state.json")).state, "thinking", "b's turn untouched");

  runHook(home, "lifecycle.js", ["end"], payload("b"));
  assert.deepEqual(fs.readdirSync(sessDir(home)), []);
  assert.equal(readJson(path.join(sbDir(home), "state.json")).state, "idle");
});

test("start without an installed app path doesn't fail", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  assert.equal(runHook(home, "lifecycle.js", ["start"], payload("s1")).status, 0);
});

test("start records the session's process", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "lifecycle.js", ["start"], payload("s1"));
  assert.equal(readJson(path.join(sessDir(home), "s1")).pid, process.pid);
});
