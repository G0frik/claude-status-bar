const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { tempHome, rmHome } = require("./helpers");
const { scanBackground } = require("../background");
const T = require("./transcript");

const ids = (r) => Object.keys(r.tasks).sort();

function transcript(t) {
  const home = tempHome(); t.after(() => rmHome(home));
  const file = path.join(home, "t.jsonl");
  fs.writeFileSync(file, "");
  return { file, add: (...lines) => fs.appendFileSync(file, lines.map((l) => l + "\n").join("")) };
}

test("background shells, monitors and subagents are pending until their notification", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("b1"), T.monitorLaunch("m1"), T.agentLaunch("a1"), T.plainToolResult());
  let r = scanBackground(tr.file, null);
  assert.deepEqual(ids(r), ["a1", "b1", "m1"]);

  tr.add(T.notification("b1", "completed"), T.notification("a1", "failed"));
  r = scanBackground(tr.file, r);
  assert.deepEqual(ids(r), ["m1"]);

  tr.add(T.notification("m1", "killed"));
  assert.deepEqual(ids(scanBackground(tr.file, r)), []);
});

test("a Monitor event without a status keeps it pending; its expiry notice ends it", (t) => {
  const tr = transcript(t);
  tr.add(T.monitorLaunch("m1"), T.monitorEvent("m1", "build finished"));
  let r = scanBackground(tr.file, null);
  assert.deepEqual(ids(r), ["m1"]);

  tr.add(T.monitorEvent("m1", "[Monitor expired after 30m with 1 event delivered.]"));
  assert.deepEqual(ids(scanBackground(tr.file, r)), []);
});

test("a Monitor past its timeout is dropped even if the notice was missed", (t) => {
  const tr = transcript(t);
  tr.add(T.monitorLaunch("m1", { timeoutMs: 60_000, timestamp: "2026-01-01T00:00:00Z" }));
  const launched = Date.parse("2026-01-01T00:00:00Z") / 1000;
  assert.deepEqual(ids(scanBackground(tr.file, null, launched + 30)), ["m1"]);
  assert.deepEqual(ids(scanBackground(tr.file, null, launched + 61)), []);
});

test("a persistent Monitor has no timeout", (t) => {
  const tr = transcript(t);
  tr.add(T.monitorLaunch("m1", { persistent: true, timestamp: "2026-01-01T00:00:00Z" }));
  assert.deepEqual(ids(scanBackground(tr.file, null, Date.now() / 1000)), ["m1"]);
});

test("TaskStop ends a task; orphan notices on resume end several at once", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("b1"), T.bashLaunch("b2"), T.bashLaunch("b3"), T.taskStop("b1"));
  let r = scanBackground(tr.file, null);
  assert.deepEqual(ids(r), ["b2", "b3"]);

  tr.add(T.orphanNotice(["b2", "b3"]));
  assert.deepEqual(ids(scanBackground(tr.file, r)), []);
});

test("subagent (sidechain) launches and launch text inside tool output are ignored", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("side", { isSidechain: true }), T.toolOutputQuoting("quoted"));
  assert.deepEqual(ids(scanBackground(tr.file, null)), []);
});

test("scans resume where they stopped, and a partial last line waits for the next scan", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("b1"));
  const half = T.notification("b1", "completed");
  fs.appendFileSync(tr.file, half.slice(0, 40));

  let r = scanBackground(tr.file, null);
  assert.deepEqual(ids(r), ["b1"]);
  assert.equal(r.offset, Buffer.byteLength(T.bashLaunch("b1")) + 1, "offset (in bytes) stops at the last whole line");

  fs.appendFileSync(tr.file, half.slice(40) + "\n");
  r = scanBackground(tr.file, r);
  assert.deepEqual(ids(r), []);
  assert.equal(r.offset, fs.statSync(tr.file).size);
});

test("a transcript that shrank is read again from the start", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("b1"), T.plainToolResult(), T.plainToolResult());
  const r = scanBackground(tr.file, null);
  fs.writeFileSync(tr.file, T.bashLaunch("b2") + "\n");
  assert.deepEqual(ids(scanBackground(tr.file, r)), ["b2"]);
});

test("no transcript, or a missing one, never throws", () => {
  assert.deepEqual(scanBackground("", null), { offset: 0, tasks: {} });
  const prev = { offset: 10, tasks: { b1: 0 } };
  assert.deepEqual(scanBackground(path.join(__dirname, "no-such.jsonl"), prev), prev);
});

test("lines longer than a read chunk (1 MB) are still read whole", (t) => {
  const tr = transcript(t);
  const huge = JSON.stringify({ type: "user", message: { role: "user", content: "x".repeat(2_500_000) } });
  tr.add(T.bashLaunch("b1"), huge, T.bashLaunch("b2"), huge, T.notification("b1", "completed"));
  const r = scanBackground(tr.file, null);
  assert.deepEqual(ids(r), ["b2"]);
  assert.equal(r.offset, fs.statSync(tr.file).size);
});

test("a malformed saved state is ignored rather than trusted", (t) => {
  const tr = transcript(t);
  tr.add(T.bashLaunch("b1"));
  for (const prev of [{ offset: "12", tasks: "ab" }, { offset: -5, tasks: [1] }, { offset: 1.5 }, "junk"])
    assert.deepEqual(ids(scanBackground(tr.file, prev)), ["b1"], JSON.stringify(prev));
});
