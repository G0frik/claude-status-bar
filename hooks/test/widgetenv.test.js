const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { widgetEnv } = require("../widgetenv");

test("the widget doesn't inherit the session's editor and Claude Code variables", () => {
  const env = widgetEnv({
    PATH: "C:\\Windows",
    SystemRoot: "C:\\Windows",
    CLAUDE_CONFIG_DIR: "D:\\claude",
    ELECTRON_RUN_AS_NODE: "1",
    VSCODE_IPC_HOOK: "\\\\.\\pipe\\x-main-sock",
    VSCODE_PID: "4242",
    CLAUDE_CODE_MESSAGING_TOKEN: "secret",
    CLAUDE_CODE_SESSION_ID: "3f2b8c1e-7a4d-4e9b-9c6a-1d5e0f2a8b47",
    CLAUDE_AGENT_SDK_VERSION: "0.3.296",
    CLAUDECODE: "1",
    CLAUDE_PID: "5151",
    electron_run_as_node: "1",
  });
  assert.deepEqual(env, { PATH: "C:\\Windows", SystemRoot: "C:\\Windows", CLAUDE_CONFIG_DIR: "D:\\claude" });
});

test("lifecycle.js starts the widget with the filtered environment", () => {
  const source = fs.readFileSync(path.join(__dirname, "..", "lifecycle.js"), "utf8");
  assert.match(source, /cp\.spawn\(exe, \[\], \{[^}]*env: widgetEnv\(process\.env\)/);
});
