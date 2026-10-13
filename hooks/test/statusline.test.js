const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { tempHome, runHook, sbDir, readJson, rmHome } = require("./helpers");

const limits = (five, seven) => ({
  rate_limits: {
    ...(five != null && { five_hour: { used_percentage: five, resets_at: 1900000000 } }),
    ...(seven != null && { seven_day: { used_percentage: seven, resets_at: 1900500000 } }),
  },
});
const usagePath = (home) => path.join(sbDir(home), "usage.json");

test("records both windows, prints nothing, tags the source", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  const res = runHook(home, "statusline.js", [], limits(23.5, 41));
  assert.equal(res.stdout, "");
  const u = readJson(usagePath(home));
  assert.deepEqual(u.fiveHour, { pct: 23.5, resetsAt: 1900000000 });
  assert.deepEqual(u.sevenDay, { pct: 41, resetsAt: 1900500000 });
  assert.equal(u.source, "statusline");
});

test("absent rate_limits leaves the previous values alone", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "statusline.js", [], limits(10, 20));
  const before = fs.readFileSync(usagePath(home), "utf8");
  runHook(home, "statusline.js", [], { model: { display_name: "Opus" } });
  assert.equal(fs.readFileSync(usagePath(home), "utf8"), before);
});

test("unchanged values are not rewritten", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "statusline.js", [], limits(10, 20));
  const old = new Date(Date.now() - 60000);
  fs.utimesSync(usagePath(home), old, old);
  runHook(home, "statusline.js", [], limits(10, 20));
  assert.equal(fs.statSync(usagePath(home)).mtimeMs, old.getTime());
});

test("clamps percentages and accepts a single window", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "statusline.js", [], limits(150, null));
  const u = readJson(usagePath(home));
  assert.equal(u.fiveHour.pct, 100);
  assert.equal(u.sevenDay, null);
});

test("tolerates a UTF-8 BOM and garbage input", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  runHook(home, "statusline.js", [], "﻿" + JSON.stringify(limits(5, 6)));
  assert.equal(readJson(usagePath(home)).fiveHour.pct, 5);
  assert.equal(runHook(home, "statusline.js", [], "not json").status, 0);
});

test("--tee echoes the payload for a chained status line", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  const input = JSON.stringify(limits(1, 2));
  assert.equal(runHook(home, "statusline.js", ["--tee"], input).stdout, input);
});
