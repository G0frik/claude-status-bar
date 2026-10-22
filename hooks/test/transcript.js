// Transcript lines shaped like the ones Claude Code writes, for the background-task tests.
// Only the fields the scanner reads are kept, plus enough around them to look real.

const toolResult = (result, extra = {}) => JSON.stringify({
  type: "user", isSidechain: false, timestamp: new Date().toISOString(),
  message: { role: "user", content: [{ type: "tool_result", tool_use_id: "toolu_1", content: "…" }] },
  toolUseResult: result, ...extra,
});

const bashLaunch = (id, extra) =>
  toolResult({ stdout: "", stderr: "", interrupted: false, backgroundTaskId: id }, extra);

const monitorLaunch = (id, { timeoutMs = 1_800_000, persistent = false, timestamp = new Date().toISOString() } = {}) =>
  toolResult({ taskId: id, timeoutMs, persistent }, { timestamp });

const agentLaunch = (id) =>
  toolResult({ isAsync: true, status: "async_launched", agentId: id, description: "Explore" });

const taskStop = (id) =>
  toolResult({ message: `Successfully stopped task: ${id} (sleep 600)`, task_id: id, task_type: "local_bash" });

const plainToolResult = () => toolResult({ stdout: "ok", stderr: "", interrupted: false });

// Tool output that happens to print a launch result: the keys sit inside a quoted string.
const toolOutputQuoting = (id) =>
  toolResult({ stdout: JSON.stringify({ backgroundTaskId: id, taskId: id, timeoutMs: 1 }), stderr: "" });

const userText = (text) => JSON.stringify({ type: "user", isSidechain: false, timestamp: new Date().toISOString(), message: { role: "user", content: text } });

const notification = (id, status) => userText(
  `<task-notification>\n<task-id>${id}</task-id>\n<tool-use-id>toolu_1</tool-use-id>\n` +
  `<status>${status}</status>\n<summary>Background command "x" ${status}</summary>\n</task-notification>`);

const monitorEvent = (id, event) => userText(
  `<task-notification>\n<task-id>${id}</task-id>\n<summary>Monitor event: "watch"</summary>\n` +
  `<event>${event}</event>\n</task-notification>`);

const orphanNotice = (ids) => userText(
  `<task-notification>\n${ids.map((i) => `<task-id>${i}</task-id>\n`).join("")}` +
  `<task-id>__orphan_summary__:shell</task-id>\n<status>stopped</status>\n` +
  `<summary>${ids.length} background shell command tasks didn't finish before the previous session ended.</summary>\n</task-notification>`);

module.exports = { bashLaunch, monitorLaunch, agentLaunch, taskStop, plainToolResult, toolOutputQuoting, notification, monitorEvent, orphanNotice };
