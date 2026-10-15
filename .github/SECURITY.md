# Security Policy

## Reporting a vulnerability

Report security issues privately through GitHub's
[private vulnerability reporting](https://github.com/G0frik/claude-status-bar/security/advisories/new)
for this repository, not in a public issue.

Include a description, steps to reproduce, the affected version, and which build variant
(offline or online, shown in the widget menu's Version line). You can expect an initial response
within a few days.

## Supported versions

Fixes go into `main` and the next release. Only the latest release is supported.

## Scope

Claude Status Bar is a local desktop utility: a WPF taskbar widget plus Node.js scripts that
Claude Code runs as hooks. It never runs elevated, installs no service or autostart entry beyond
the Claude Code hooks, injects into no other process, and sends no telemetry.

It is built in two variants:

- **Offline (the default, and the only one released):** no network code is compiled in. The app
  never reads `~/.claude/.credentials.json` and makes no network connections. Tests and a release
  check enforce this (see the overview below).
- **Online (build from source with `.\build.ps1 -Online`):** adds one opt-in feature, off by
  default. When the user turns it on, the app reads the Claude Code sign-in token from
  `~/.claude/.credentials.json` and sends it to `https://api.anthropic.com/api/oauth/usage` and
  nowhere else (redirects are refused). The token is never stored, logged or written to disk.

Its only change outside its own data folder is the hook and statusLine entries it adds to
`~/.claude/settings.json`, which `uninstall.bat` removes.

Every file read and written, process started, Windows API with a security angle, and network
destination is listed in [docs/SECURITY-OVERVIEW.md](../docs/SECURITY-OVERVIEW.md), with how to
verify a release.
