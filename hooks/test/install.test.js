const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { tempHome, runHook, sbDir, readJson, rmHome } = require("./helpers");

const settingsPath = (home) => path.join(home, ".claude", "settings.json");
const writeSettings = (home, s) => fs.writeFileSync(settingsPath(home), JSON.stringify(s, null, 2));
const settings = (home) => readJson(settingsPath(home));

// uninstall.js taskkills ClaudeStatusBar.exe; keep it away from a real running widget.
const PATH_WITHOUT_TASKKILL = { PATH: path.dirname(process.execPath) };

test("install copies every hook script, including the shared helper", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  writeSettings(home, {});
  assert.equal(runHook(home, "install.js").status, 0);
  for (const f of ["update.js", "lifecycle.js", "statusline.js", "atomic.js", "widgetenv.js", "background.js"])
    assert.ok(fs.existsSync(path.join(sbDir(home), f)), `${f} copied`);
});

test("install registers hooks and a plain status line; uninstall removes both", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  writeSettings(home, { hooks: { Stop: [{ hooks: [{ type: "command", command: "echo mine" }] }] } });

  runHook(home, "install.js");
  const s = settings(home);
  assert.match(s.statusLine.command, /statusline\.js"$/);
  assert.ok(s.hooks.UserPromptSubmit && s.hooks.SessionStart && s.hooks.SessionEnd);
  assert.equal(s.hooks.Stop.length, 2, "user's own Stop hook kept");

  runHook(home, "uninstall.js", [], {}, PATH_WITHOUT_TASKKILL);
  const after = settings(home);
  assert.equal(after.statusLine, undefined);
  assert.deepEqual(after.hooks, { Stop: [{ hooks: [{ type: "command", command: "echo mine" }] }] });
});

test("an existing status line is chained, survives re-install, and is restored exactly", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  const mine = { type: "command", command: "node my-line.js", padding: 1, refreshInterval: 5 };
  writeSettings(home, { statusLine: mine });

  runHook(home, "install.js");
  runHook(home, "install.js"); // idempotent
  const s = settings(home).statusLine;
  assert.match(s.command, /statusline\.js" --tee \| node my-line\.js$/);
  assert.equal(s.padding, 1);
  assert.equal(s.refreshInterval, 5);
  assert.equal((s.command.match(/statusline\.js/g) || []).length, 1, "not chained twice");

  runHook(home, "uninstall.js", [], {}, PATH_WITHOUT_TASKKILL);
  assert.deepEqual(settings(home).statusLine, mine);
  assert.ok(!fs.existsSync(path.join(sbDir(home), "statusline-chain.json")));
});

test("hooks are registered in exec form, so a hook's parent is the Claude Code process", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  writeSettings(home, {});
  runHook(home, "install.js");
  const [entry] = settings(home).hooks.UserPromptSubmit;
  assert.deepEqual(entry.hooks[0], { type: "command", command: "node", args: [path.join(sbDir(home), "update.js"), "prompt"] });
  assert.equal(settings(home).hooks.SessionStart[0].hooks[0].args[1], "start");
});

test("re-installing over an older shell-form install replaces it without duplicates", (t) => {
  const home = tempHome(); t.after(() => rmHome(home));
  const old = (script, evt) => ({ type: "command", command: `node "${path.join(sbDir(home), script)}" ${evt}` });
  writeSettings(home, {
    hooks: {
      UserPromptSubmit: [{ hooks: [old("update.js", "prompt")] }],
      PreToolUse: [{ matcher: "*", hooks: [old("update.js", "pre")] }, { matcher: "Bash", hooks: [{ type: "command", command: "echo mine" }] }],
      SessionStart: [{ hooks: [old("lifecycle.js", "start")] }],
    },
  });

  runHook(home, "install.js");
  const s = settings(home);
  for (const evt of ["UserPromptSubmit", "PreToolUse", "SessionStart"]) {
    const ours = s.hooks[evt].flatMap((e) => e.hooks).filter((h) => (h.args || []).some((a) => a.includes(sbDir(home))) || (h.command || "").includes(sbDir(home)));
    assert.equal(ours.length, 1, `${evt}: exactly one status-bar hook`);
    assert.equal(ours[0].command, "node");
  }
  assert.ok(s.hooks.PreToolUse.some((e) => e.hooks[0].command === "echo mine"), "user's own hook kept");

  runHook(home, "uninstall.js", [], {}, PATH_WITHOUT_TASKKILL);
  assert.deepEqual(settings(home).hooks, { PreToolUse: [{ matcher: "Bash", hooks: [{ type: "command", command: "echo mine" }] }] });
});
