# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-10-13

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

[0.2.0]: https://github.com/G0frik/claude-status-bar/releases/tag/v0.2.0
[0.1.0]: https://github.com/BankkRoll/claude-status-bar/releases/tag/v0.1.0
