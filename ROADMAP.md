# Roadmap

Ideas for Claude Status Bar, roughly in priority order. Sizes are rough: **S** is a few hours,
**M** a day or two. When an idea is picked up it gets a design doc under `docs/plans/`; bigger
or community-requested items can also become a GitHub issue (use the feature request template).

## Planned

Nothing in progress; pick from the ideas below.

## Ideas

- **Context-window fill per session** (S). Session transcripts record token usage for every
  response, so the panel could show "context 71%" per session, plus a warning before
  auto-compaction. Works offline and in VS Code.
- **Usage forecast** (S). Project the current pace of the 5-hour window ("at this rate you'll
  hit the limit at 22:40, 30 min before it resets") and warn only when that will actually
  happen, instead of a fixed 80% threshold.
- **Limit-reset notice** (S). After you were above about 80%, show a quiet notification when
  the 5-hour window resets. Builds on the notifications work.
- **Daily stats** (S). "Today: Claude worked 2h 14m across 3 projects, 41 turns" in the menu,
  from the turn start/stop times the hooks already record.
- **Light taskbar theme** (S). The pill is tuned for a dark taskbar; follow the Windows light
  theme too.
- **Claude Code plugin packaging** (M). Install the hooks as a Claude Code plugin instead of
  `setup.bat` editing `settings.json`. First check which parts of the setup (e.g. the status
  line) a plugin can provide.
- **Update check** (S). A menu line such as "Version 0.3.0 available", from the GitHub
  releases the release workflow already publishes.

## Done

- **Open a session from the widget** (0.3.0): click a dot, a panel row, or the widget to bring
  that session's window to the front; the sessions panel lists every session.
  Design: [docs/plans/session-open-and-notifications.md](docs/plans/session-open-and-notifications.md)
- **Notifications** (0.3.0): when a session needs permission or finishes a long turn, in the
  widget's style, never taking focus. Same design doc.
- **Several sessions at once** (0.3.0): one dot per session, sticky focus, live tooltip.
- **Usage-limit bars** (0.2.0): 5-hour and weekly plan usage, with smart refresh.
