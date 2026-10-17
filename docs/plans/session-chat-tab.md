# Open the session's chat tab, not just its window

**Status:** done (0.4.0) · **Tracked in:** [ROADMAP.md](../../ROADMAP.md)

## Context

Clicking a session (dot, panel row, pill, notification) brings its editor window forward. With
two or more Claude Code sessions in one window, the visible tab stays whatever it was.
[session-open-and-notifications.md](session-open-and-notifications.md) recorded this as a known
limit ("no API"). There is one: the Claude Code extension's URI route
`<scheme>://anthropic.claude-code/open?session=<id>`.

Goal: after a click, the session's own chat tab is active in every VS Code-based editor, with no
hard-coded editor list. When any step fails, the result is no worse than today (the window comes
forward).

**Rejected alternatives:**
- A companion extension listening on a named pipe. It's instant, but it adds a listener to
  secure and a second thing to install.
- Talking to the editor's private main-process pipe. It's undocumented and would break with
  editor updates.

## What the sources say

Read from the installed files and live processes, not from documentation:

| Fact | Source |
|---|---|
| A panel session is `<data folder>\extensions\anthropic.claude-code-<ver>-<platform>\resources\native-binary\claude.exe`. Its parent is the editor's extension host (the editor exe, `--type=utility`), whose parent is the editor's main process, which owns the window. **The session's exe path names the exact extension in use.** | `Win32_Process` chain of a live session |
| `product.json` is at `<editor exe dir>\resources\app\product.json`, with `urlProtocol` (`antigravity-ide`, `vscode`, …). | Antigravity IDE 1.107.0, VS Code 1.107.1 |
| The `/open` handler reads `session` and `prompt`, drops a session that fails its own schema check, and runs `claude-vscode.primaryEditor.open(session, prompt)`. | extension.js `handleUri` (2.1.296) |
| `primaryEditor.open` → `createPanel` calls `reveal()` on a panel that already holds the session (no duplicate); the same for the sidebar and remembered tabs. **Otherwise it creates a new panel that resumes the session in whichever window received the URI.** | extension.js `createPanel` |
| The editor routes a URI to the window named by a `windowId=<n>` query parameter (an internal Electron id, unknown outside the editor), else to the **last focused window** (`ActiveWindowManager`). | `resources/app/out/main.js`: `URLHandlerRouter.routeCall` |
| Each editor re-registers its protocol on every start, as `<exe> --open-url -- %1`. Old installs leave stale entries (here `antigravity` → a missing exe). | main.js `setAsDefaultProtocolClient`; `HKCR` |
| A panel session's environment has `ELECTRON_RUN_AS_NODE=1`, `VSCODE_*`, `CLAUDE_CODE_MESSAGING_TOKEN`, …, and `hooks/lifecycle.js` starts the widget with that environment. | `env` of a live session; lifecycle.js |
| The extension's "use terminal" mode runs Claude in an integrated terminal, through a shell. | extension.js `createTerminal` |

Consequences:
- **Launch the exe that owns the session's window**, `<exe> --open-url -- <uri>`, with a
  scrubbed environment, rather than going through the registry. This always reaches the install
  that runs the session, even when two installs share a scheme or the registry entry is stale.
  Only a direct launch (`UseShellExecute=false`) controls the environment. The trade-off is a
  dependency on the `--open-url --` command line, but that's exactly what each editor registers
  for itself.
- **The real risk is the URI reaching the wrong window**: it would start a second live copy of
  the session there. So the URI is sent only after the widget has brought the session's window
  to the front and confirmed it's the foreground window.

## Design

- **`ChatTabLink`** (`src/ChatTabLink.cs`): pure, unit-tested with fakes. It turns a session plus
  the walked process chain into either a link (editor exe + URI) or a skip reason:
  1. **Session id**: must parse as a GUID; it's re-formatted lowercase. Otherwise `bad-id`.
  2. **Panel session**: both of these must hold, otherwise `not-panel`:
     - the session exe sits in an `anthropic.claude-code-*\resources\native-binary\` folder;
     - its parent is the same exe as the window owner (the extension host).

     Terminal sessions have a shell as parent, whether started by hand, by "use terminal" mode
     or in Windows Terminal.
  3. **Scheme**: `urlProtocol` from the owner's `product.json`, checked against
     `^[a-z][a-z0-9+.-]{0,31}$`. Otherwise `no-protocol`.
  4. **Route**: the extension's `extension.js` contains `registerUriHandler`, `case"/open"` and
     `"claude-vscode.primaryEditor.open"`, cached per extension folder (the folder name carries
     the version). Otherwise `no-open-route`.
  5. **URI**: `<scheme>://anthropic.claude-code/open?session=<guid>`. Never `prompt`, never
     `windowId`.
- **`EditorLauncher`** (`src/EditorLauncher.cs`) starts the process:
  - `UseShellExecute=false`, with arguments passed as a list `--open-url`, `--`, `<uri>` (no
    shell);
  - the environment minus `ELECTRON_*`, `VSCODE_*`, `CLAUDE*`;
  - fire and forget, off the UI thread, failures logged;
  - one launch per session per 2 s.
- **Opening** (`OverlayWindow.OpenSession`): find and activate the window as before. Then, only
  if the setting is on and the window really is in front, plan the link and launch it.
- **`open.log`** next to the exe: one line per click, with the session id, the decision and the
  timing. No window titles.
- **Setting:** "Switch to the session's chat tab", on by default; off restores the 0.3.0
  behaviour.
- **Remote windows (WSL, SSH, containers) are not supported**: their hooks write to the remote
  `~/.claude`, so the widget never sees those sessions.
- **`lifecycle.js`** starts the widget without the session's `ELECTRON_*`, `VSCODE_*` and
  `CLAUDE_CODE_*` variables. The widget never needs them, and it shouldn't hold the messaging
  token.

## Evidence

- **Unit tests:** `ChatTabLinkTests`, `EditorLauncherTests`, and the route probe against a
  fixture.
- **Contract workflow** (`.github/workflows/extension-contract.yml`, weekly and manual): runs
  the route probe against the current Marketplace VSIX.
- **`tools/check-ui.ps1 -Target opentab`:** UI Automation confirms that the active editor tab is
  the session's tab, and times it.
- **Process Monitor:** the launched command line and its environment.
- **Manual matrix:**
  - editor: Antigravity, VS Code, plus one more;
  - install: user, system, portable;
  - session: panel, sidebar, editor terminal, Windows Terminal;
  - one or two windows;
  - editor in front, behind another app, or minimized;
  - local or remote.

## Spike

| # | Question | Experiment | Result |
|---|---|---|---|
| Q1 | With 2 windows of one editor, does focus-then-URI reach the focused one? | Session in the back window; focus it, launch; 10 runs | _pending (needs two windows)_ |
| Q2 | Are terminal sessions told apart? | Dump the process chain of a CLI session | Code: "use terminal" runs through a shell. Live dump _pending_ |
| Q3 | Does the inherited environment break `--open-url`? | Launch with and without `ELECTRON_RUN_AS_NODE=1` | **Yes.** With it, the exe runs as Node: `bad option: --open-url`, exit 9. Scrubbed: exit 0. |
| Q4 | Is `AllowSetForegroundWindow` needed? | Editor minimized or behind another app | _pending_ |
| Q5 | Where is `product.json` in other layouts? | Search the install folders | `<exe dir>\resources\app\product.json` in Antigravity IDE 1.107.0 and VS Code 1.107.1 (the only two installed). Other forks and portable installs _pending_ |
| Q6 | Does "Don't ask again" persist? | Restart the editor and click again | _pending_ |
| Q7 | Sidebar or remembered tab | Click a session shown in the sidebar | Code: `createPanel` reveals the sidebar or remembered tab. Live check _pending_ |
| Q8 | Does "use terminal" mode look like a panel session? | Read the extension's terminal code | **No.** It runs in an integrated terminal through a shell, so the parent check rules it out. |
| Q9 | A VSIX URL that works without an account | `curl` | `…/vsextensions/claude-code/latest/vspackage` → 404. Asking the gallery `extensionquery` API for the latest win32-x64 version, then `…/claude-code/<ver>/vspackage?targetPlatform=win32-x64`, → 200 (127 MB). |
| Q10 | The real delay | `check-ui.ps1 opentab` | The forwarding process lives ~3.9 s; time from click to tab _pending_ |

## Implementation notes

Built as designed, with these differences:

- **Files.** The launcher, debounce, log and the opener that ties them together are in
  `src/ChatTabOpener.cs`, not a separate `EditorLauncher.cs`. `usage.log` and `open.log` share a
  small `CappedLog` (`src/UsageLog.cs`).
- **The widget's environment** is filtered by a new `hooks/widgetenv.js`, installed next to
  `atomic.js`, so it can be unit-tested. `lifecycle.js` itself runs on load.
- **UI Automation can't see the editor's tabs** (Chromium exposes no tab items to it here). But
  VS Code-based editors put the active tab's name in the window title, so
  `check-ui.ps1 -Target opentab` checks the foreground window's title instead.
- **The contract test** can't skip itself in xUnit 2. It returns early when `CLAUDE_EXT_JS` is
  unset, and fails if it's set but the file is missing. The workflow always sets it.

### Checks run so far

| Check | Result |
|---|---|
| Release build, `--warnaserror` | clean |
| `dotnet test`, offline / online | 153 / 173 passed |
| `node --test hooks/test/*.test.js` | 43 passed |
| Contract test, installed 2.1.296 (Antigravity) and 2.1.144 (VS Code); Marketplace 2.1.301 | passed |
| Contract test against a file without the route | fails, as it should |
| Workflow download step, run locally (Windows PowerShell 5.1) | 2.1.301, `extension.js` 3.58 MB extracted in 22 s |
| `check-ui.ps1 -Target opentab`, Antigravity, session tab already active | PASS: `launched antigravity-ide anthropic.claude-code-2.1.296-win32-x64 (34 ms)`; the title showed the tab 62–76 ms after the click |
| Launched process, widget started with `ELECTRON_RUN_AS_NODE=1` | `Antigravity IDE.exe --open-url -- antigravity-ide://anthropic.claude-code/open?session=<id>`, lived ~2.8 s (the normal hand-off; the Node failure exits at once), so the scrub works |

### Manual tests (Antigravity IDE 1.107.0, extension 2.1.296)

The user ran these on 2026-10-17, with five sessions across two windows:

| Test | Result |
|---|---|
| Two sessions in one window, switching both ways | Works. After the first "Allow" with "Don't ask again", no prompt. |
| Q1: session in the back of two windows | Works: the right window comes forward on the right tab, and no copy of the session appears in the other window. |
| Q4: editor minimized | Works. A scripted run with the window minimized also passed (tab in 63 ms). `AllowSetForegroundWindow` isn't needed. |
| Q6: "Don't ask again" after restarting the editor | Kept. |
| Q2: a CLI session in the editor's terminal | Left alone: the window comes forward and the log says `skip:not-panel`. |
| VS Code 1.107.1, extension 2.1.301 | Works, the same as in Antigravity. |

`open.log` showed **17 launches (6–34 ms to decide) and 4 `skip:not-in-front`** in that
session. Two of the skipped sessions were opened again 4 s and 27 s later, which looks like a
second click after the tab didn't switch. The first build checked the foreground straight after
`SetForegroundWindow` returned, and didn't record why it failed. It now:
- logs `(refused)` when `SetForegroundWindow` fails;
- otherwise waits up to 500 ms, in the background, for the window to come forward;
- names what is in front when it still isn't ("another window of the editor" or "another app").

The next skips will show which case it is. The minimized case wasn't the cause (above).

With that build, in Antigravity and VS Code: 12 launches, the window in front within 0–1 ms each
time, and no `not-in-front` skip. The cause of the first 4 skips wasn't found. If it happens
again, the log line now says which case it was.
