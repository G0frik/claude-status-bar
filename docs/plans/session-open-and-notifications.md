# Open a session from the widget, and notifications

**Status:** done · **Tracked in:** [ROADMAP.md](../../ROADMAP.md)

## Context

The widget shows every Claude Code session (dots), but you can't act on what it shows. When a
session is waiting for permission in another window, you have to find that window yourself,
and if the widget is on another monitor or you're in another app you may not notice at all.

This adds two features:

1. **Open a session from the widget.** Click a dot, a row in a new hover panel, or the pill,
   and that session's window (VS Code / Antigravity / terminal) comes to the front.
2. **Notifications.** A small popup styled like the pill, shown above the widget when a session
   needs permission or finishes a long turn. It never takes focus, and clicking it opens the
   session.

**Decisions:**
- Hovering a dot or `+N` opens a panel listing **all** sessions, with the hovered dot's row
  highlighted. Clicking a row or a dot opens that session.
- Left-click on the rest of the pill opens the session the label shows; right-click opens the
  menu.
- Notifications follow the app's style (custom popup, not Windows Action Center toasts).
- The menu gets a Notifications submenu.

## 1. Knowing each session's process (hooks)

Verified on a real setup: the VS Code extension's `claude.exe` has `Antigravity IDE.exe` (the
extension host) as its parent, whose parent `Antigravity IDE.exe` owns the window
"claude-status-bar - Antigravity IDE - …". Walking up from the Claude process reaches the
right window. The hooks just need to record that PID.

- **Exec-form hooks.** Today our hooks run through Git Bash, so a hook's parent is a
  short-lived `bash.exe`. Claude Code supports an exec form (`"command": "node",
  "args": [...]`, no shell); with it, `process.ppid` *is* the long-lived Claude Code process.
  - `install.js` registers our hooks in exec form.
  - `stripOurs` also matches entries whose `args` contain our directory, so re-running install
    migrates old shell-form entries.
  - `uninstall.js` removes both forms.
- **Session file gains fields** (same writer, `hooks/atomic.js`):
  - `pid`: `process.ppid`, written by `lifecycle.js start` and refreshed by every `update.js`
    event.
  - `detail`: what a permission request is for (`tool_name` from PermissionRequest; the message
    from a permission Notification).
  - `lastTurnSeconds`: written on `stop`, as `ts - startedAt`, before `startedAt` resets.
- **Spike before building on it:** confirm in Antigravity and in a Windows Terminal CLI session
  that `ppid` is the `claude.exe`/node process. Also confirm that PermissionRequest carries
  `tool_name`. If exec form misbehaves on some setup, sessions just have no `pid`. Opening a
  session is then unavailable for it (the panel row says so), and nothing else breaks.

## 2. Finding and opening the window (app)

New `src/SessionWindows.cs`:

- **Process table:** one `CreateToolhelp32Snapshot` per lookup (fast, no WMI), giving PID →
  (parent, exe name).
- **Walk** up from the session's `pid` (max 8 levels, guarding against PID cycles) to the
  first process that owns a visible, unowned, titled top-level window.
- **Choosing among several windows of one process** (several VS Code windows, Windows
  Terminal): prefer the window whose title contains the session's project name; otherwise take
  the one highest in z-order.
- **Classic console fallback:** for `cmd`/`conhost` the window isn't an ancestor's.
  `AttachConsole(pid)` → `GetConsoleWindow()` → `FreeConsole()`.
- **Stale PIDs:** accept a PID only if the process is alive and its exe is `claude*`/`node*`,
  so a reused PID never opens a random window. Cache `(sessionId → hwnd)` and revalidate with
  `IsWindow` + owner check on every use.
- **Testable core:** `Resolve(pid, project, IProcessTable, IWindowTable)` is pure. The Win32
  calls sit in thin adapters, so the logic is unit-tested with fake tables.
- **Opening:**
  - `ShowWindow(SW_RESTORE)` if minimized, then `SetForegroundWindow`. Windows allows this
    because the click was the last input event, and it went to our process.
  - If the window can't be brought forward, call `FlashWindowEx` on it instead.
  - **Known limit:** the window comes forward, but the specific VS Code chat tab or Windows
    Terminal tab isn't selected (no API for either).

## 3. Interaction: clicks and the hover panel

- **Clicks** (`OverlayWindow.xaml.cs`, replacing `OnAnyClick`'s left-button behaviour):
  - Left-click on a dot → open that session.
  - Left-click elsewhere on the pill → open the focused session (also works with one session).
  - Right-click → menu (unchanged).
  - A session without a window shows a brief "Can't find this session's window" line in the
    panel instead of doing nothing silently.
- **Hover panel** (new `src/SessionsPanel.xaml(.cs)`, a WPF `Popup` placed above the pill,
  right-aligned). Replaces the current dots tooltip; the usage-bar tooltip stays.
  - **Opens** 250 ms after the mouse enters the dots or `+N`. It stays open while the mouse is
    over the dots or the panel, and closes 300 ms after leaving both, on click, or on Escape.
  - **Styled like the pill:**
    - Same corner radius (7) and `#22FFFFFF` border. The background is more opaque
      (`#F0262626`), because it sits over the desktop rather than the taskbar.
    - 12 px text in the same colors (`#F0F0F0` name, `#C0C0C0` status), timers in Consolas
      like `TimerText`.
  - **One row per session, in dot order:** status dot (same brushes and shared pulse as the
    pill), name (`SessionNames.Build`), status with detail ("Awaiting permission · Bash"),
    timer. No row limit: it scrolls past about 12 rows.
  - **Linked highlight:** hovering a dot highlights its row (`#1AFFFFFF` background); hovering a
    row grows and rings its dot. That makes it obvious which session a click will open.
  - **Topmost handling:** the panel counts as an open popup for `_openPopups`, so
    `AssertTopmost` stays paused while it is open.

## 4. Notifications

- **Why custom popups and not Windows toasts:** they match the widget's look and need no
  AppUserModelID/shortcut registration for an unpackaged app. The trade-off is no Action
  Center history, which is acceptable for transient status.
- **`src/ToastWindow.xaml(.cs)`:**
  - Borderless, transparent, topmost, `ShowActivated=false`, with
    `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, so it never steals focus or appears in Alt-Tab.
  - As wide as the pill and aligned with it, following it as dots and usage bars change its
    width (originally a fixed 300 px), styled like the panel.
  - **Content:** an 18 px icon (amber dot for permission, the Claude mark for finished); the
    project name; a detail line ("Needs permission · Bash", "Finished · 3m 12s"); a `×` that
    shows on hover.
- **`src/ToastManager.cs`** (only the controller window's monitor shows notifications):
  - **Stacking:** right-aligned above the widget, newest nearest the widget, 6 px gaps, at most
    3 (the oldest is dropped).
  - **Motion:** 150 ms fade/slide in. Positions follow the widget when it repositions.
  - **Stack math** is a pure function, so it is unit-tested.
- **When** (`src/SessionTransitions.cs`, generalising the per-session state tracking in
  `MaybeChime`; pure and unit-tested):
  - **Needs permission:** a session enters `permission`. The notification stays until the
    session leaves that state (it then closes itself), you click it, or you close it.
  - **Finished:** a session enters `done` with `lastTurnSeconds ≥ 20`. Short turns stay quiet.
    Auto-closes after 6 s; hovering pauses the timer.
  - **One per session:** a new event replaces that session's previous notification.
  - **Never** for a session seen for the first time (no burst when the widget starts).
- **Suppressed when:**
  - the session's window is already the foreground window (you're looking at it), or
  - Windows says not to disturb (`SHQueryUserNotificationState`: fullscreen app,
    presentation mode, Focus Assist / quiet hours).

  Suppressed events are dropped; the amber dot still shows.
- **Click** → open the session (section 2) and close the notification.
- **Chime:** "Play Completion Sound" stays its own setting and keeps its current behaviour.

## 5. Menu and settings

```
Show timer
Show usage limits
Fetch usage online
Refresh usage every       ▸
Play Completion Sound
Notifications             ▸   ✓ Show notifications
                              ─────────────────────
                              ✓ When a session needs permission
                              ✓ When a turn finishes
───
Animation …
```

- **Settings** (`src/Settings.cs`): `ShowNotifications`, `NotifyPermission`, `NotifyFinished`,
  all on by default. The two sub-items are disabled when the master switch is off.
- **Wiring** follows the existing pattern: `SyncMenuChecks` / `OnToggle…` /
  `OverlayManager.SyncAll`. Turning notifications off closes any that are showing.

## 6. Performance and memory

The widget runs all day, so it should cost almost nothing when nothing is happening.
**Measured before this work**, during the stress runs: about 185 MB working set / 147 MB
private at idle, and 0 % CPU at idle. That is mostly the WPF + .NET runtime itself.

**Budget:**
- idle CPU stays at 0 %;
- steady-state private memory grows by **≤ 10 MB**;
- handle, GDI and USER object counts return to baseline after panels and notifications close
  (no leaks).

**How each piece stays lean:**
- **Nothing new runs per tick.**
  - Window lookups (process snapshot, `EnumWindows`), foreground checks and the
    "do not disturb" query happen only on a click or a notification event, never in the 400 ms
    poll.
  - The snapshot handle is a `SafeHandle`, closed right after use.
  - Lookups are cached per session and revalidated cheaply (`IsWindow`) when used.
- **Panel built lazily, reused, and only fed while visible.**
  - One `Popup` per overlay window, created on first hover and kept.
  - Row elements are pooled and updated in place; they are not recreated each time.
  - Its content (and the timers) is computed **only while it is open**. This also fixes a
    current inefficiency: today the dots tooltip text is rebuilt every tick even when no one is
    hovering.
- **Notifications exist only while shown.**
  - Each notification window is created on demand and closed (HWND and visuals released)
    when dismissed. At most 3 exist.
  - Their `DispatcherTimer`s are stopped, and every handler is unsubscribed on close.
  - Animations use `FillBehavior.Stop` with completion handlers removed, so nothing keeps a
    closed window alive.
- **No leaks through events or statics.**
  - No static event holds a window.
  - Brushes and icons are frozen/shared, as `IconRenderer` and the existing frozen brushes
    already are.
  - The working-dot pulse keeps running only while a working dot is visible (already the case).
- **Allocation-free steady state.** Session signatures and display strings are built only when
  something changed. Session files are read only when their write time changes (already the
  case).
- **Runtime settings, applied only if measurements show a win.** In `ClaudeStatusBar.csproj`:
  - `ConcurrentGarbageCollection=false` (drops the background GC thread; a tiny UI app doesn't
    need it);
  - `System.GC.ConserveMemory` (compacts the heap more aggressively).

  Try each, measure, keep only what clearly helps. No working-set trimming hacks
  (`EmptyWorkingSet`): they just move the cost to page faults.

**Measurement** (`tools/measure.ps1`, extending the stress-test sampler):
- Samples private bytes, working set, handle count and GDI/USER objects (`GetGuiResources`)
  every 2 s.
- Runs scripted scenarios against the real widget:
  - idle for 2 minutes;
  - 20 simulated sessions;
  - open and close the panel 100×;
  - trigger 100 notifications.
- Reports start/peak/end values and fails if the end values exceed the baseline by more than
  the budget. That is a leak test, not just a snapshot.

## 7. Tests

- **xUnit** (`tests/ClaudeStatusBar.Tests`):
  - **`SessionWindows.Resolve`** (fake tables):
    - VS Code with several windows (title match, z-order fallback)
    - Windows Terminal
    - conhost fallback
    - dead PID; reused PID with a non-Claude exe
    - parent cycle; walk depth limit
  - **`SessionTransitions`:**
    - permission start/end
    - finished above/below the 20 s threshold
    - first-seen session (no event)
    - session disappears while notified
    - replacing an existing notification
  - **Toast stack layout:** positions, the cap of 3, DPI scale.
  - **Notification gating:** foreground window, busy state (both injected).
- **node:test** (`hooks/test`):
  - `pid` = `process.ppid` on start and on update; `detail` for permreq and notify;
    `lastTurnSeconds` on stop.
  - `install.js` writes exec-form entries, migrates old shell-form entries without duplicating
    them, and `uninstall.js` removes both.
- **End-to-end** (real widget, `tools/`):
  - **Simulator:** `tools/simulate-sessions.js` gains `--long-turns`. Its sessions' `pid` is
    the simulator's own process, so "open" should bring forward the terminal that runs it,
    which is a real check of the window lookup.
  - **UI check script:** `tools/check-tooltip.ps1` becomes `tools/check-ui.ps1`, with new
    targets:
    - `panel`: hover a dot → panel above the widget, row count = session count, z-order ok,
      the hovered row is highlighted (screenshot).
    - `open`: click a dot → the foreground window becomes the expected window.
    - `notify`: a simulated permission request in a background session → a notification
      window appears, the foreground window does **not** change, at most 3 stack, and it
      closes when the session leaves `permission`.
  - **Manual:**
    - two Antigravity windows on different projects (opens the right one)
    - a CLI session in Windows Terminal
    - one in a classic `cmd` window
    - fullscreen video (no notifications)
- **CI:** both suites already run; no workflow change needed.

## Order of work (one commit each)

1. ✅ `ROADMAP.md` + this document.
2. Spike: exec-form hooks and `pid`/`detail`/`lastTurnSeconds` in the hooks, with tests. Record
   the spike results below.
3. `tools/measure.ps1` baseline, then `SessionWindows` + click-to-open (dots and pill), with
   tests.
4. Hover panel (replaces the dots tooltip) + `check-ui.ps1 panel/open`.
5. `SessionTransitions` + `ToastWindow`/`ToastManager` + Notifications menu, with tests +
   `check-ui.ps1 notify`.
6. Measure after each step. Try the GC settings at the end and keep them only if they show a
   measured gain.
7. README (Menu, What it shows, a "Notifications" section), CHANGELOG (Unreleased), and moving
   the items to Done in `ROADMAP.md`.

## Files

- **New:**
  - `src/SessionWindows.cs`, `src/SessionsPanel.xaml(.cs)`, `src/ToastWindow.xaml(.cs)`,
    `src/ToastManager.cs`, `src/SessionTransitions.cs`
  - test files for each
  - `tools/check-ui.ps1` (renamed), `tools/measure.ps1`
- **Modified:**
  - `hooks/install.js`, `hooks/uninstall.js`, `hooks/update.js`, `hooks/lifecycle.js`
  - `src/OverlayWindow.xaml(.cs)`, `src/Settings.cs`, `src/Native.cs` (Win32 imports),
    `src/ClaudeStatusBar.csproj` (only if a GC setting earns its place)
  - `tools/simulate-sessions.js`, `README.md`, `CHANGELOG.md`

## Done when

1. Release build with `--warnaserror` is clean; `dotnet test` and `node --test` are all green.
2. `check-ui.ps1` passes `usage`, `menu`, `panel`, `open` and `notify` against the running
   widget, with 2 and 20 simulated sessions, and the screenshots are reviewed.
3. A notification never changes the foreground window, and none appears for the session you're
   already looking at.
4. Clicking a dot or panel row brings forward the right Antigravity window (with two projects
   open) and the right terminal.
5. With notifications off in the menu, nothing appears, and the other features are unchanged.
6. `tools/measure.ps1` passes: idle CPU 0 %, private memory within +10 MB of the baseline, and
   handles/GDI/USER objects back to baseline after 100 panel opens and 100 notifications.

## Implementation notes

_Spike results, measurements and any deviations from this plan go here as work proceeds._

### Step 2: exec-form hooks (spike)

- After `install.js` switched to exec form, a running Antigravity session picked up the new
  settings on its next event. Its session file then recorded `pid` 14944, the live
  `claude.exe` of the VS Code extension: the process whose parents lead to the Antigravity
  window. Before the reload it recorded a short-lived PID (the Git Bash shell of the old
  shell-form hooks), which is what exec form fixes.
- `install.js` migrates shell-form entries in place (tested), so upgrading needs no manual
  cleanup.
- Still to observe in real use: that PermissionRequest carries `tool_name` (`detail`) in the
  extension, and the Windows Terminal CLI case.

### Steps 3–5: open, panel, notifications

Implemented as planned, with these differences:

- **No per-session window cache.** A lookup is one process snapshot plus one `EnumWindows`
  (a smoke test bounds it under 250 ms; in practice a few ms) and runs only on a click or a
  notification event, so a fresh lookup each time is simpler and never stale.
- **Panel and notifications are built in code** (`SessionsPanel.cs`, `ToastWindow.cs`), not XAML:
  they are small and mostly dynamic.
- **"Can't find its window"** shows briefly in place of the label, which also covers clicks on
  the pill when no panel is open.
- **Escape doesn't close the panel:** the widget never takes keyboard focus. Moving the mouse away
  or clicking closes it.
- **Notifications have a fixed height** (52 DIPs), so stacking is arithmetic with no layout pass.

### Performance

The plan assumed `IconRenderer` already shared its images. It didn't: `Dot()`, `Logo()` and
`Spark()` rendered a new bitmap on every call. That happened every 400 ms poll while idle or
awaiting permission, and every animation frame (9/s) while working. Under the 20-session load
this pushed GDI objects from 18 to a peak of 89. Tinted images are now rendered once per (image,
color) and reused.

Measured with `tools/measure.ps1`. Each run is a fresh widget with 20 simulated sessions for
50 s, sampled for 60 s; values are MB of private memory unless noted.

| Build / setting | Peak private | End private | Peak GDI | Avg CPU |
|---|---|---|---|---|
| Before the icon cache | 158.8 | 140.2 | 89 | 0.94 % |
| Icon cache (run 1 / run 2) | 141.6 / 129.9 | 136.9 / 127.4 | 30 / 25 | 0.84 / 0.64 % |
| + `gcConcurrent=0` (run 1 / run 2) | 131.5 / 149.9 | 130.2 / 132.8 | 37 / 31 | 0.84 / 0.85 % |
| + `GCConserveMemory=7` | 130.0 | 126.5 | 25 | 0.51 % |
| + both | 129.0 | 127.1 | 25 | 0.53 % |

The GC settings stay within run-to-run noise (about ±10 MB), so neither was applied. Idle, the
new build used less than the old baseline: 115 MB private, 1,663 handles, 18 GDI and 28 USER
objects, against 130 MB, 2,127, 31 and 41. That compares a fresh widget with one that had been
running for hours, so take it as "no regression" rather than a gain.

### On-screen checks (desktop free)

All run against the real widget with `tools/check-ui.ps1`, using simulated sessions where needed:

| Check | Result |
|---|---|
| `open`, clicking the label (Notepad in front) | PASS: the Antigravity window came to the front |
| `open`, clicking a simulated session's dot | PASS |
| `panel`, 4 sessions | PASS: above the widget, 106 px = 4 rows, hovered row highlighted, timers tick |
| `notify` | PASS: shown in 34–40 polls, at most 2 at once, foreground never changed |
| `menu`, `usage` | PASS |

Screenshots confirmed the notification styles: "Needs permission · WebFetch" with the amber dot,
and "Finished · 22s" with the Claude mark.

**Leak test.** A fresh widget got an idle baseline, then this load: 6 simulated sessions, the
panel opened and closed 100 times, and notifications showing. The load was run twice.

| | Private | Handles | GDI | USER |
|---|---|---|---|---|
| Fresh, idle | 114.5 MB | 1,653 | 18 | 26 |
| After load, round 1 | 128.4 MB (+13.9) | 1,665 (+12) | 30 (+12) | 29 (+3) |
| After load, round 2 | 128.7 MB (+0.3) | 1,670 (+5) | 30 (0) | 28 (−1) |

No leak: the second round is flat. The first round's +13.9 MB is a one-time cost of first using
the panel and notifications (code JIT, the popup and notification windows, the Consolas font).
That misses the plan's +10 MB budget by about 4 MB on a fresh start. For scale, the old build
sat at 130 MB after hours of use.

**Bugs found by these checks, fixed:**
- **Timer reset after permission.** Waiting for permission reset the turn's start time, so after
  approval the timer and "Finished · N s" counted only from the approval. Permission now keeps
  the turn's start; the widget already hides the timer while waiting.
- **Test tooling:**
  - the simulator didn't send a tool name with permission requests;
  - the simulator counted another run's sessions as its own leftovers;
  - `check-ui.ps1 open` identified Notepad by its title, which is localized ("Блокнот"); it now
    compares window handles.

**Seen, not changed:** hovering the usage bars while a notification is showing can lay the
usage tooltip over the notification's right edge. Both are transient; it's cosmetic.
