# Claude Status Bar

<img width="800" height="450" alt="claude-status-bar-promo" src="https://github.com/user-attachments/assets/3cae9a4d-2331-4bfe-a62c-12f057e21db7" />

A small Windows taskbar widget that shows Claude Code's live status: an animated icon while it
is thinking or running a tool, rotating activity words, a yellow dot when it is awaiting your
permission, the elapsed time of the current turn, and two small bars for your plan usage limits. It docks into the empty space of the
taskbar and resizes itself so it never overlaps the notification tray.

<a href="https://github.com/G0frik/claude-status-bar/releases/latest"><img width="250" alt="Download for Windows" src="https://github.com/user-attachments/assets/69b3f2e8-5c20-4dfa-b7d7-2341639ab734"></a>

```
…  ✦ Frolicking…  ▮▮ 0m 43s   |   ^  🖥 🔊  7:11 PM
```

> Inspired by [m1ckc3s/claude-status-bar](https://github.com/m1ckc3s/claude-status-bar) (a macOS
> menu-bar app). This is a full rewrite to native C and Windows — though the hook idea
> and some pieces of the `state.json` contract were drawn from the original.
>
> This repository is a fork of [BankkRoll/claude-status-bar](https://github.com/BankkRoll/claude-status-bar)
> that adds multi-session support, usage bars, notifications, and an offline build for managed
> machines (see [Security](#security)).

## What it shows

- **Thinking** — the icon animates with a rotating activity word and a live `1m 1s` timer.
- **Running a tool** — a label such as `Editing`, `Reading`, `Running command`, or `Searching`.
- **Awaiting permission** — a paused yellow dot.
- **Background tasks** — a blue dot and `2 background tasks` when a turn ends but work it started
  (background shell commands, Monitor watches, background subagents) is still running. The timer
  keeps going, and the session counts as finished only when the last task ends.
- **Idle / done** — rests on the Claude mark.
- **Usage limits** — two thin bars at the right edge, after the timer: your 5-hour (left) and weekly (right)
  claude.ai plan usage. A bar turns orange at 80%; hover for exact percentages and reset times.
  Shown for Pro and Max subscribers once a session has had its first response; hidden otherwise.
- **Several sessions** — with two or more Claude Code sessions (terminals or VS Code chat tabs),
  a small dot per session appears after the timer: orange (pulsing) while working, yellow while
  awaiting permission, blue while background tasks run, grey when idle. The icon, label and timer follow one session, ringed among
  the dots: the one awaiting permission first, otherwise the one already shown, so a session that
  starts working later doesn't take over. Beyond five sessions the dots end in `+N`. Rest the mouse
  on the dots to open a panel listing every session (project, status, timer); the row of the dot
  under the mouse is highlighted.
- **Open a session** — left-click a dot or a panel row to bring that session's window (VS Code,
  Antigravity, Windows Terminal, console) to the front; left-click anywhere else on the widget
  opens the session the label shows. For a session in the Claude Code panel of a VS Code-based
  editor (VS Code, Cursor, Antigravity, …), the widget also switches that window to the
  session's own chat tab. See [Switching to the chat tab](#switching-to-the-chat-tab).
- **Notifications** — a small popup above the widget when a session needs permission (it stays
  until the session moves on) or finishes a turn of 20 seconds or more. It never takes focus, is
  skipped for the session whose window is already in front and while a fullscreen app,
  presentation or Focus Assist is on, and opens the session when clicked.

The widget tracks the taskbar height and DPI, and anchors itself just before the tray, reflowing
when tray icons appear or disappear.

### Switching to the chat tab

When several Claude Code sessions share one editor window, clicking a session brings the window
forward and then shows that session's tab. The widget does this through the Claude Code
extension's own link, `<editor>://anthropic.claude-code/open?session=<id>`, handed to the editor
that runs the session.

- **The first time** in each editor, the editor asks *"Allow 'Claude Code for VS Code' extension
  to open this URI?"*. Tick **Don't ask again for this extension** and choose **Open**.
- **It takes about a second.** Each switch starts the editor's exe briefly to pass the link to the
  running window, so the tab changes a moment after the window comes forward.
- **Only Claude Code panel sessions.** A session started with `claude` in a terminal (the
  editor's terminal, Windows Terminal, a console) gets its window brought forward, but not its
  terminal tab. Sending the link would open a second copy of the session in a panel. Sessions in
  remote windows (WSL, SSH, containers) don't show in the widget at all, because their hooks run
  on the remote machine.
- **Only when the window really came forward.** The editor delivers the link to its last
  focused window. If Windows refused to bring the session's window forward, the widget doesn't
  send the link, so it never opens the session in another window.
- Turn it off with **Switch to the session's chat tab** in the menu. Each click is logged in
  `open.log` (see [Debugging](#debugging)).

## Menu

Right-click the widget to open its menu:

- **Show timer** — toggle the elapsed-time readout.
- **Show usage limits** — toggle the usage bars.
- **Fetch usage online** and **Refresh usage every** — online build only (see
  [How it works](#how-it-works)): turn on the usage API (off by default), and choose 2, 5
  (default), 10, or 15 minutes between fetches.
- **Play Completion Sound** — a chime when a turn finishes.
- **Switch to the session's chat tab** — when opening a session in an editor's Claude Code panel,
  also show its tab (on by default; see [Switching to the chat tab](#switching-to-the-chat-tab)).
- **Notifications** — turn notifications off, or choose which: when a session needs permission,
  when a turn finishes.
- **Animation** — Claude Spark, Claude Code, or Crab Walking.
- **Show on** — choose which monitors display the widget (defaults to the primary monitor).
- **Version** — the version and build variant (`offline` or `online`).
- **Quit** — close the widget; it returns on your next Claude Code session.

## Install

1. Download the latest `ClaudeStatusBar-win-x64.zip` (the offline build) from
   [Releases](https://github.com/G0frik/claude-status-bar/releases/latest) and extract it. To
   check it first, see [Verify a release](docs/SECURITY-OVERVIEW.md#verify-a-release).
2. Double-click **setup.bat**. A small window opens, registers the hooks for the current user,
   prints a confirmation, and waits for a key press.
3. Start a new Claude Code session — the widget appears whenever Claude Code is running, and
   hides itself when no session is active.

To remove it, run **uninstall.bat** (or the command under [Uninstall](#uninstall)), then delete
the folder.

## Requirements

- Windows 10 or 11
- [Claude Code](https://claude.com/claude-code) (CLI)
- [Node.js](https://nodejs.org) — runs the hooks

The release build bundles the .NET runtime, so no separate install is needed.

## How it works

The Claude Code hooks write each session's status to its own file under
`~/.claude/statusbar/sessions.d/`; the widget polls them and renders the icon, label and dots.
The hooks also write the latest event of any session to `state.json`, which the widget falls back
to when hooks from an older install haven't written per-session status yet.

```
Claude Code  ->  hooks       ->  ~/.claude/statusbar/sessions.d/<session>  ->  widget (polls 0.4s)
usage API / cache / statusLine  ->  ~/.claude/statusbar/usage.json  ->  widget (usage bars)
```

Plan usage comes from these sources, which write the same file:

- **Claude Code's cache** (both builds). Claude Code keeps a copy of its usage response in
  `~/.claude.json`, and the widget uses it whenever it is newer than what it has, with no
  network. It only updates when Claude Code fetches usage itself, so it can be hours old.
- **The usage API** (online build only, off until you turn on **Fetch usage online**). The
  widget asks `api.anthropic.com` for your plan usage, using the sign-in Claude Code keeps in
  `~/.claude/.credentials.json`. It only reads that file: the token is never stored, logged,
  refreshed, or sent anywhere but that one endpoint (redirects are refused). This is what keeps
  the bars current from the VS Code extension, which never runs status line commands. The
  endpoint is the one behind `/usage`; it is undocumented, so it could change, and it
  rate-limits frequent callers. The widget therefore fetches once at startup, then every
  **Refresh usage every** minutes only while Claude is working and for one interval after the
  turn ends, and not at all while idle. After a rate-limit or error response it waits twice as
  long each time, up to 30 minutes.
- **The status line** (both builds). In the terminal, Claude Code passes plan usage to
  [status line](https://code.claude.com/docs/en/statusline) commands, so setup also registers
  `statusline.js` as your `statusLine`. It prints nothing. If you already had a status line, it is
  saved to `~/.claude/statusbar/statusline-chain.json` and chained after ours, so your own status
  line keeps working; uninstalling restores it.

`SessionStart` launches the widget and `SessionEnd` removes the session; the widget exits on its
own once no sessions remain (each active session is a file under `~/.claude/statusbar/sessions.d`).

The widget is a borderless, top-most overlay. It locates the tray via
`Shell_TrayWnd → TrayNotifyWnd` and anchors itself just before it, re-asserting its z-order each
poll so it stays on top. One overlay window is created per selected monitor. No Explorer injection
or shell extensions are used.

## Build from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
.\build.ps1 -SelfContained -Zip   # -> dist\ClaudeStatusBar-win-x64.zip
.\build.ps1 -Install              # build and register the hooks in one step
.\build.ps1 -Online               # the online variant (adds the opt-in usage API)
```

There are two build variants. The **offline** build (the default, and the one released)
compiles in no network code at all and never reads `~/.claude/.credentials.json`. The **online**
build (`-Online`, or `-p:Offline=false` for `dotnet build`) adds the opt-in usage API described
above. See [Security](#security).

The hook registration done by `setup.bat` runs `hooks\install.js`, which merges the hooks into
`~/.claude/settings.json` (backing it up once to `settings.json.bak-statusbar` and leaving other
hooks intact), sets the status line as described above, copies the hook scripts to
`~/.claude/statusbar`, and records the executable path. It is safe to run repeatedly; run it
again after updating, so the installed hook scripts match the widget.

### Tests

```powershell
dotnet test tests/ClaudeStatusBar.Tests                    # app, offline build
dotnet test tests/ClaudeStatusBar.Tests -p:Offline=false   # app, online build
node --test (Get-ChildItem hooks/test/*.test.js).FullName  # hooks: per-session status, install, ...
```

`BuildVariantTests` checks that the offline build references no network code, and
`.\tools\verify-offline.ps1 dist\ClaudeStatusBar.exe` checks a published exe.

Two tools help check the widget itself while it runs:

- `node tools/simulate-sessions.js --sessions 8 --seconds 60` drives the real hook scripts for
  several fake sessions with random events, so the dots can be watched under load. It reports any
  failed hook or dropped write.
- `.\tools\check-ui.ps1 -Target usage|panel|menu|open|notify` hovers or clicks the widget and checks
  that the popup stays fully visible above it.

## Uninstall

Run `uninstall.bat` from the extracted folder, or:

```powershell
node "<extracted folder>\hooks\uninstall.js"
```

Removes the status-bar hooks and stops the running widget, leaving other hooks intact, and
restores the status line you had before installing (if any). Then delete the application
folder. To remove everything, also delete `%USERPROFILE%\.claude\statusbar` and
`%USERPROFILE%\.claude\settings.json.bak-statusbar`.

## Debugging

Set `CLAUDE_STATUSBAR_DEBUG=1` before starting a session to append hook activity to
`~/.claude/statusbar/hooks.log`.

The widget always records where the usage bars' numbers come from in `usage.log`, next to
`ClaudeStatusBar.exe`: usage API results (only when they change) and each update it displays,
tagged with its source (`api`, `cache` or `statusline`).

`open.log`, in the same folder, has one line per click that tried to switch to a session's chat
tab. A line shows either `launched <scheme> <extension version>`, or the reason it was skipped:

| Reason | Meaning |
|---|---|
| `not-panel` | The session runs in a terminal. |
| `not-in-front` | Windows refused to bring the window forward (`refused`), or another window was still in front 500 ms later. |
| `no-open-route` | The installed Claude Code extension has no `/open` link. |
| `no-protocol` | The editor's `product.json` names no URL scheme. |
| `bad-id`, `no-chain`, `no-process` | The session's id or processes couldn't be read. |
| `repeat` | A second click within 2 s. |

## Security

- [SECURITY.md](.github/SECURITY.md): how to report a vulnerability, and the scope of each build
  variant.
- [docs/SECURITY-OVERVIEW.md](docs/SECURITY-OVERVIEW.md): every file read and written, process
  started, Windows API with a security angle and network destination, and how to verify a
  release (checksums, build provenance, `tools/verify-offline.ps1`).

## Trademark

Unofficial and not affiliated with, endorsed by, or sponsored by Anthropic. "Claude" and the
Claude mark are trademarks of Anthropic, used nominatively. The MIT license covers the source code
only.

Please don't contact Anthropic about this project. To report a bug, request a feature, or raise any
other issue, open an issue at
[github.com/G0frik/claude-status-bar/issues](https://github.com/G0frik/claude-status-bar/issues).

## License

MIT
