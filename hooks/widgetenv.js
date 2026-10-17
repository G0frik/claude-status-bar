// The environment lifecycle.js starts the widget with: its own, minus what belongs to the Claude
// Code session that ran the hook. A session in an editor panel carries ELECTRON_RUN_AS_NODE,
// VSCODE_* (that editor's internals) and CLAUDE_CODE_* (including the session's messaging token).
// The widget needs none of them, and it outlives the session that started it.

const SESSION_PREFIXES = ["ELECTRON_", "VSCODE_", "CLAUDE_CODE_", "CLAUDE_AGENT_"];
const SESSION_NAMES = ["CLAUDECODE", "CLAUDE_PID"];

function isSessionVariable(name) {
  const upper = name.toUpperCase();
  return SESSION_PREFIXES.some((p) => upper.startsWith(p)) || SESSION_NAMES.includes(upper);
}

function widgetEnv(env) {
  const out = {};
  for (const [name, value] of Object.entries(env)) if (!isSessionVariable(name)) out[name] = value;
  return out;
}

module.exports = { widgetEnv, isSessionVariable };
