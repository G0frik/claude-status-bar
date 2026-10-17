# Security overview

What Claude Status Bar does on a machine, for a security review. Every item below maps to code in
this repository; paths are relative to the user profile (`%USERPROFILE%`) unless absolute. The
app is `ClaudeStatusBar.exe` (WPF, .NET 9); the hooks are the Node.js scripts in `hooks/`, which
Claude Code runs. Neither runs elevated.

## Build variants and network

| Variant | How to build | Network destinations |
|---|---|---|
| **Offline** (default; the only released one) | `.\build.ps1` | **None.** `src/UsageFetcher.cs` is not compiled in (`Directory.Build.props`, `src/ClaudeStatusBar.csproj`). |
| Online | `.\build.ps1 -Online` | `https://api.anthropic.com/api/oauth/usage` (TCP 443), only after the user turns on "Fetch usage online" (off by default). Redirects are refused, and any other URI is rejected before sending (`PinnedHostHandler`). Uses the system proxy. |

The hooks never use the network in either variant (no network module is loaded; enforced by
`hooks/test/no-network.test.js`).

How the offline build is enforced:
- `BuildVariantTests` (run in CI for both variants) reads the app assembly's metadata. The
  offline assembly must reference no `System.Net*` assembly or type, and must contain neither
  `.credentials.json` nor `api.anthropic.com`.
- `tools/verify-offline.ps1` checks a published exe the same way and runs on every CI build and
  release.

A self-contained exe bundles the whole .NET runtime, which includes networking libraries the app
doesn't use; the checks above are on the app's own code.

## Files read

| Path | By | Why |
|---|---|---|
| `.claude\statusbar\state.json`, `.claude\statusbar\sessions.d\*` | app | Session status written by the hooks. |
| `.claude\statusbar\usage.json` | app, `statusline.js` | Plan usage for the usage bars. |
| `.claude\statusbar\win-settings.json` | app | Its own settings. |
| `.claude.json` | app, every 15 s when the file changes | Claude Code's cached plan usage. The whole file is parsed; only the `cachedUsageUtilization` key is used. Nothing else is kept or logged. |
| The session transcript named by a session's status, normally `.claude\projects\…\*.jsonl` | app, while a session is busy | Last 8 KB only, to see whether the user interrupted the turn. |
| `<editor install>\resources\app\product.json` | app, on a click only | The `urlProtocol` of the editor that runs a clicked Claude Code panel session (e.g. `vscode`). Nothing else is used. |
| `<editor data folder>\extensions\anthropic.claude-code-*\extension.js` | app, once per extension version | Checked for the extension's `/open` link before using it; read-only, nothing kept but yes/no. |
| `.claude\statusbar\app-path.txt` | `lifecycle.js` | Path of the exe to launch. |
| `.claude\settings.json` | `install.js`, `uninstall.js` | To add or remove the hooks (below). |
| `.claude\.credentials.json` | **online build only**, and only while "Fetch usage online" is on | `claudeAiOauth.accessToken` and `expiresAt`, read right before each request; the token is never refreshed, stored, logged or written. |
| Hook input on stdin | hooks | Claude Code's event payload (session id, working folder, tool name, transcript path). |

## Files written

| Path | By | Notes |
|---|---|---|
| `.claude\statusbar\sessions.d\<session id>`, `state.json` | `update.js`, `lifecycle.js` | Status per session; deleted when the session ends. |
| `.claude\statusbar\usage.json` | app, `statusline.js` | Plan usage percentages and reset times. |
| `.claude\statusbar\win-settings.json` | app | Menu choices. |
| `usage.log` next to `ClaudeStatusBar.exe` | app | Where usage numbers came from (percentages, sources, HTTP status codes). Capped at 512 KB. Not written if the folder is read-only. |
| `open.log` next to `ClaudeStatusBar.exe` | app | One line per click that tried to switch to a session's chat tab: session id, the decision, the editor's URL scheme and extension folder name. No window titles. Capped at 512 KB. |
| `%TEMP%\ClaudeStatusBar.completion.mp3` | app | Completion chime, extracted from the exe on first use. |
| `.claude\statusbar\hooks.log` | app, hooks | Only when `CLAUDE_STATUSBAR_DEBUG=1` is set. |
| `.claude\statusbar\*.tmp` | app, hooks | Short-lived; atomic writes are a temp file renamed over the target. |
| `.claude\statusbar\{update,lifecycle,statusline,atomic,widgetenv}.js`, `app-path.txt`, `statusline-chain.json` | `install.js` | Installed hook scripts, the exe path, and the user's previous statusLine. |
| `.claude\settings.json`, `.claude\settings.json.bak-statusbar` | `install.js`, `uninstall.js` | Hook entries; a one-time backup before the first change. |

## Processes started

| Started by | Process | When |
|---|---|---|
| Claude Code | `node <…>\.claude\statusbar\update.js <event>`; `lifecycle.js start/end` | On each hook event. Exec form, no shell. |
| Claude Code | `node <…>\statusline.js` (or `… --tee \| <your previous statusLine>`) | Terminal status line refreshes; through Claude Code's shell. |
| `lifecycle.js` | `tasklist /FI "IMAGENAME eq ClaudeStatusBar.exe" /NH` (via `cmd`) | Session start: is the widget already running? |
| `lifecycle.js` | the exe named in `app-path.txt`, detached and hidden, without the session's `ELECTRON_*`, `VSCODE_*` and `CLAUDE_CODE_*` variables (`widgetenv.js`) | Session start, if not running. |
| `uninstall.js` | `taskkill /IM ClaudeStatusBar.exe /F` (via `cmd`) | Uninstall. |
| `setup.bat` / `uninstall.bat` | `where node`, `node --version`, `node hooks\install.js "<exe>"` / `where node`, `node hooks\uninstall.js` | When run by the user. |
| The app | `<editor exe> --open-url -- <scheme>://anthropic.claude-code/open?session=<uuid>` (e.g. `Code.exe`), no shell, no window, without `ELECTRON_*`, `VSCODE_*` and Claude Code session variables | On a click on a session that runs in an editor's Claude Code panel, after its window came to the front; at most once per session per 2 s. The exe is the one already running that session's window (its path read from the live process, not from config or the registry). The new process passes the link to the running editor and exits. Off with "Switch to the session's chat tab". |

The app runs only while a Claude Code session exists: the SessionStart hook launches it, and it
exits 30 seconds after the last session ends. There is no Run key, scheduled task or service. A
named mutex (`ClaudeStatusBar.SingleInstance`) keeps it to one instance.

`app-path.txt` is in the user's own profile. Pointing it at another exe needs the same rights as
editing the hooks in `settings.json` directly, so it is not an escalation path.

## Windows APIs with a security angle

All in `src/Native.cs`; none needs elevation.

| API | Used for |
|---|---|
| `CreateToolhelp32Snapshot`, `Process32FirstW`, `Process32NextW`, `CloseHandle` | On a click or a notification only: process names and parent IDs. The widget walks up from the session's Claude Code process (PID recorded by the hooks) to the process that owns its window (the editor or terminal). |
| `EnumWindows`, `GetWindowText`, `GetWindowTextLength`, `GetWindowThreadProcessId`, `IsWindowVisible`, `IsWindow`, `GetWindow` | Same moments: titles of visible top-level windows, to pick the window that names the session's project. Titles are compared in memory, never stored or logged. |
| `AttachConsole`, `GetConsoleWindow`, `FreeConsole` | Classic console sessions: attach to the session's console only to read its window handle, then detach at once. No console input or output. Only if the PID still belongs to a `claude*`/`node*` process. |
| `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `QueryFullProcessImageNameW` | On a click only: the exe paths of the session's process, its parent and its window's owner, to tell an editor panel session from a terminal one and find the editor's install. No other access to those processes. |
| `SetForegroundWindow`, `IsIconic`, `ShowWindow(SW_RESTORE)`, `FlashWindowEx` | Bring the session window the user clicked to the front (flash it if Windows refuses). |
| `GetForegroundWindow` | Skip a notification for the session the user is already looking at. |
| `SetWindowPos(HWND_TOPMOST)`, `GetWindowLong`/`SetWindowLong` (`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`) | Keep the widget above the taskbar and out of Alt-Tab; notifications never take focus. |
| `SHQueryUserNotificationState` | No notifications during fullscreen apps, presentations or quiet hours. |
| `FindWindow("Shell_TrayWnd")`, `FindWindowEx("TrayNotifyWnd")`, `GetWindowRect`, `EnumDisplayMonitors`, `GetMonitorInfo` | Position the widget next to the notification area. |

No hooks into other processes, no keyboard or mouse hooks, no uiAccess (`src/app.manifest`), no
screen capture, no clipboard access.

## Changes to `~/.claude/settings.json`

`setup.bat` runs `hooks\install.js`, which:
- backs the file up once to `settings.json.bak-statusbar`;
- copies the hook scripts to `.claude\statusbar\` and records the exe path in `app-path.txt`;
- adds one hook per event, in exec form (`{"type": "command", "command": "node", "args": [<script>, <event>]}`):
  - `UserPromptSubmit`, `Notification`, `Stop` run `update.js`;
  - `PreToolUse`, `PostToolUse`, `PermissionRequest` run `update.js`, matcher `*`;
  - `SessionStart`, `SessionEnd` run `lifecycle.js`;
- sets `statusLine` to `node "<…>\statusline.js"`. If one already existed, it is saved to
  `statusline-chain.json` and kept running: `node "<…>\statusline.js" --tee | <previous command>`.
- leaves other hooks untouched. Its own entries are the ones whose command or args point into
  `.claude\statusbar\`.

`uninstall.bat` runs `hooks\uninstall.js`, which stops the widget, removes those hook entries
(including the older shell-form entries from earlier versions), and restores the previous
statusLine from `statusline-chain.json`, or removes the statusLine if there was none. It does not
delete `.claude\statusbar\` or the `.bak-statusbar` backup; remove those by hand to clean up
completely.

## Verify a release

1. Download `ClaudeStatusBar-win-x64.zip` and `SHA256SUMS.txt` from the release, then compare:
   `(Get-FileHash ClaudeStatusBar-win-x64.zip -Algorithm SHA256).Hash`.
2. Check build provenance, which proves the zip was built by this repository's release workflow
   from the tagged commit:
   `gh attestation verify ClaudeStatusBar-win-x64.zip -R G0frik/claude-status-bar`.
3. Extract it, then from a checkout of the same tag run
   `.\tools\verify-offline.ps1 <path>\ClaudeStatusBar.exe`. It must print `PASS`.
4. File > Properties > Details shows "Claude Status Bar (offline build)", and the widget menu's
   Version line ends in "· offline".

## Known gaps

- The exe is not Authenticode-signed, so SmartScreen and some EDR products will treat it as
  unknown. Checksums plus build provenance are the compensating control.
- `usage.log` is written next to the exe; installed in a read-only folder, it simply isn't
  written.
- The hooks need Node.js on `PATH`.
