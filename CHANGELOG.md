# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Opening a session that runs in an editor's Claude Code panel now also switches that window to
  the session's own chat tab, in any VS Code-based editor, through the extension's
  `/open?session=<id>` link. Terminal sessions still get only their window brought forward.
  The menu item "Switch to the session's chat tab" turns it off, and `open.log` records each
  attempt.
- A weekly workflow (`extension-contract.yml`) checks that the current Marketplace build of the
  Claude Code extension still has that link.

### Changed

- `lifecycle.js` starts the widget without the session's `ELECTRON_*`, `VSCODE_*` and
  `CLAUDE_CODE_*` environment variables (new `widgetenv.js`, installed with the other hooks).

## [0.3.0] - 2026-10-17

### Added

- Offline build, the default: `.\build.ps1` (or `-p:Offline=true`) compiles in no network code
  and never reads `~/.claude/.credentials.json`; `.\build.ps1 -Online` builds the variant with
  the opt-in usage API. The menu's Version line and the exe's product name show the variant.
- `BuildVariantTests`, `tools/verify-offline.ps1` and a hook test prove the offline build and
  the hooks contain no network code; CI runs them for both variants.
- `.github/SECURITY.md` rewritten for this fork, and `docs/SECURITY-OVERVIEW.md` for security
  reviews.
- CodeQL for C# and JavaScript, Dependabot for actions and NuGet, and release builds with
  `SHA256SUMS.txt` and build provenance attestation.

- Open a session from the widget: left-click a session's dot, a row of the new sessions panel, or
  the widget itself to bring that session's window to the front. The hooks now run in exec form
  and record each session's Claude Code process, from which the widget finds the window.
- Sessions panel: resting the mouse on the dots lists every session, with the hovered dot's row
  highlighted. Replaces the dots tooltip.
- Notifications in the widget's style when a session needs permission or finishes a turn of
  20 s or more, with a Notifications submenu. They never take focus and respect fullscreen apps,
  presentation mode and Focus Assist.
- `tools/measure.ps1` for memory and handle leak checks.

- Several sessions at once: one dot per Claude Code session (working, awaiting permission, idle),
  with a tooltip listing each session's project, status and timer. The icon, label and timer follow
  the session that needs attention and no longer jump to whichever session started last.
- Unit tests for the app (xUnit) and the hooks (`node:test`), run in CI, plus
  `tools/simulate-sessions.js` and `tools/check-ui.ps1` for load and popup checks.

### Changed

- Right-click opens the menu; left-click now opens a session.
- The usage API is off by default, under a new setting: earlier builds saved "Fetch usage
  online" as on for everyone, so it has to be turned on again in the online build.
- Releases publish the offline build only.

### Security

- The usage API request can only go to `https://api.anthropic.com/api/oauth/usage`: redirects
  are refused, any other destination is rejected before sending, and responses are capped at
  64 KB.
- The sign-in token is read just before each request and set on that request only; it is never
  logged, stored or written.

### Fixed

- After you approved a permission request, the timer (and now "finished after") restarted
  from the approval instead of covering the whole turn.
- Status icons were re-rendered on every poll and animation frame (a new bitmap each time); they
  are now rendered once and reused, which keeps GDI objects and peak memory down under load.

- Tooltips and the menu were partly hidden behind the widget, which re-asserted its top-most
  position every poll; it now waits while a popup is open.
- A session's timer could start from another session's turn.
- Status writes could be lost when the widget was reading the file at the same moment (Windows
  rejects replacing an open file); writes are now retried, and the widget no longer blocks hooks
  from deleting an ended session's file.
- The completion chime played once per monitor; it now plays once.

## [0.2.0] - 2026-10-13

Not published as a GitHub release; the version is in the source history only.

### Added

- Usage-limit bars: two thin vertical bars at the right edge of the widget showing the 5-hour
  and weekly claude.ai plan usage, turning orange at 80%, with a tooltip giving exact percentages
  and reset times. Toggle with **Show usage limits** in the menu.
- Plan usage is fetched from Anthropic's usage endpoint using Claude Code's existing sign-in, so
  the bars work from the VS Code extension too. It refreshes every 5 minutes by default (**Refresh
  usage every** in the menu) only while Claude is working and shortly after, and backs off after
  rate-limit responses. **Fetch usage online** turns it off, falling back to the usage Claude Code
  caches in `~/.claude.json`.
- `usage.log` next to the executable records where the displayed usage came from.
- `statusline.js`, registered as the Claude Code `statusLine` to record plan usage to
  `usage.json` from the CLI. An existing status line is preserved by chaining it, and restored on
  uninstall.

## [0.1.0] - 2026-06-25

Initial release. A Windows port of the macOS
[claude-status-bar](https://github.com/m1ckc3s/claude-status-bar).

### Added

- Taskbar overlay that shows Claude Code's live status, docked before the notification tray.
- Status states driven by Claude Code hooks: thinking, running a tool, awaiting permission,
  done, and idle.
- Animated Claude icon with three styles: Claude Spark, Claude Code, and Crab Walking.
- Rotating activity words while thinking, with a live elapsed-time readout.
- Tool labels for common tools (Reading, Editing, Running command, Searching, and more).
- Right-click menu: toggle the timer, toggle a completion sound, choose the animation style,
  and select which monitors display the widget.
- Multi-monitor support, with one overlay per selected display (primary by default).
- Completion chime played when a turn finishes.
- One-click `setup.bat` / `uninstall.bat`, plus `build.ps1` for building and packaging.
- Self-contained release build that bundles the .NET runtime.

[Unreleased]: https://github.com/G0frik/claude-status-bar/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/G0frik/claude-status-bar/releases/tag/v0.3.0
[0.2.0]: https://github.com/G0frik/claude-status-bar/tree/0de4b0a
[0.1.0]: https://github.com/BankkRoll/claude-status-bar/releases/tag/v0.1.0
