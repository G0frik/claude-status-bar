# Phone notifications through ntfy

**Status:** planned · **Tracked in:** [ROADMAP.md](../../ROADMAP.md)

## Context

Claude Code's own mobile push (Remote Control → Claude app) is unreliable: the app shows the
session and its approvals, but the push often never arrives
([anthropics/claude-code#87003](https://github.com/anthropics/claude-code/issues/87003), open, no
fix as of 2.1.302). Reproduced here on Windows 10 + VS Code extension.

This adds an opt-in push to the phone through [ntfy](https://ntfy.sh) when a session needs
permission or finishes a long turn and **nobody has dealt with it at the PC**. ntfy does the
alerting; the Claude app (Remote Control) stays the place to approve.

**Decisions:**
- **Escalation, not a mirror.** A push goes out only if the event is still current after a
  delay (default 60 s) and there was no keyboard or mouse input since it happened. At the desk
  you answer first, so nothing reaches the phone; away, it does. A permission approved at the
  PC is never pushed, so there are no stale pushes to retract.
- **Secure by default.** Off by default, online build only, unguessable topic, a fixed message
  that carries nothing about the session, HTTPS to one host only. Anything that loosens this
  waits for a request (see *Not in this version*).

## 1. When to push

- **Pure policy** in `src/SessionTransitions.cs`, next to `NotificationPolicy`:
  `PhonePolicy.ShouldPush(kind, stillCurrent, secondsSinceEvent, secondsSinceInput, settings)`.
  - **Needs permission:** the session is still in `permission` after the delay.
  - **Finished:** `lastTurnSeconds ≥ 20` (the popup's rule) and the session is still `done`
    after the delay (no new turn started).
  - Respects the existing `NotifyPermission` / `NotifyFinished` toggles.
  - Not gated on `userBusy` / foreground window: those mean "at the PC", which the input
    check already covers.
- **Pending queue** in `HandleTransitions` (`src/OverlayWindow.xaml.cs`, controller window only,
  so several monitors never push twice): `Dictionary<sessionId, (kind, timeUtc)>`, checked on
  each existing poll, so no timers.
  - `PermissionResolved` and `Ended` drop the session's entry; a new event replaces it.
  - At most one push per session per event.
  - Turning the feature off clears the queue.
- **Input check:** `GetLastInputInfo` in `src/Native.cs`. Compare with unsigned 32-bit
  subtraction against `Environment.TickCount`, so the 49.7-day tick wrap can't break it.
- **Global cap:** at most 20 pushes per hour; extra ones are dropped and logged. It guards
  against a loop of sessions spamming the phone and hitting ntfy.sh's rate limits.

## 2. Sending (`src/NtfyPublisher.cs`, `#if !OFFLINE`)

- **Request:** `POST {server}/` with a JSON body
  `{ topic, title, message, priority, tags, click }`.
  - JSON, not headers, so the text is never limited to ASCII.
  - Built by a pure function, unit-tested.
- **Content: fixed text from the event kind only.**

  | Event | title | message | priority | tags |
  |---|---|---|---|---|
  | Needs permission | Claude Code | A session needs your permission | 4 (high) | `warning` |
  | Finished | Claude Code | A session finished | 3 (default) | `white_check_mark` |

  - No project name, path, tool, command, prompt or duration ever leaves the machine. A leaked
    topic then tells a stranger only *that* you use Claude Code and when.
  - `click` defaults to the constant `https://claude.ai/code`. Tested on a phone: it opens a
    new Claude Code session in the Claude app, and one tap back reaches the session list where
    you approve.
- **Open the exact session** (opt-in, `PhoneOpenSession`, off by default): `click` becomes
  `https://claude.ai/code/<bridgeSessionId>`, which opens that session straight away.
  - **Where the ID comes from:** while Remote Control is on, Claude Code writes
    `bridgeSessionId` (e.g. `session_01PS…`) to `~/.claude/sessions/<pid>.json`. The hooks
    already record each session's pid (`ClaudeState.Pid`).
  - **Verified** on a phone (Claude Code 2.1.308, VS Code extension): the URL opens that session
    in the Claude app.
  - **Off by default** because it puts a session ID in the message. The ID alone opens nothing
    without your claude.ai login, but it lets someone with the topic tell sessions apart.
  - **The file is a trust boundary and undocumented.** It is read only at send time, after the
    delay, and the ID is used only if every check passes. Otherwise `click` falls back to
    `https://claude.ai/code`, and the push still goes out.
    - Size cap of 16 KB.
    - The file's `sessionId` equals the session's, so a reused pid can't point at another
      session.
    - `bridgeSessionId` matches `^session_[A-Za-z0-9]{1,64}$`.
  - Lives in `src/RemoteSessionLink.cs` as a pure parse function plus a thin file read, so it can
    be unit-tested.
- **Transport**, reusing the pattern of `src/UsageFetcher.cs`:
  - One `HttpClient`.
  - `PinnedHostHandler` generalised to take the allowed origin, so only the configured server
    (scheme, host and port) is allowed.
  - No redirects, no cookies, 10 s timeout, response body not read beyond the status.
  - Runs off the UI thread. One attempt, no retries: a late "needs permission" is worse than
    none, and the next event pushes again.
  - Failures never surface beyond the log, and never affect the widget.
- **Logging** (`UsageLog`): event kind and HTTP status only. Never the topic, the URL or the
  body.

## 3. Topic and server (`src/Settings.cs`, `#if !OFFLINE`)

- **New settings:**
  - `PhonePush` (default `false`).
  - `NtfyServer` (default `https://ntfy.sh`).
  - `NtfyTopic` (empty until the first opt-in).
  - `PhoneDelaySeconds` (default 60, clamped to 15–600).
  - `PhoneOpenSession` (default `false`).
- **Topic** generated on first opt-in with `RandomNumberGenerator.GetString`: 32 characters of
  `[A-Za-z0-9]`, about 190 bits.
  - No prefix, so nothing marks it as this app's.
  - Not editable in the menu.
- **Validation before every send:** if any rule fails, the send is refused and logged. It never
  falls back to ntfy.sh, so a typo can't send to a server you didn't choose.
  - `NtfyServer` is an absolute `https` URI with no user info, query or fragment, and an empty
    path.
  - `NtfyTopic` matches `^[A-Za-z0-9_-]{24,64}$`, so a hand-edited JSON can't weaken it.
- **Self-hosted servers:** set `NtfyServer` in `win-settings.json`. That edit is deliberate and
  has no menu UI.
- **Storage:** the topic sits in plain JSON in the user's profile, which only that Windows user
  can read. DPAPI would add nothing against malware running as the same user, so it waits for
  tokens (see below).

## 4. Menu

```
Notifications             ▸   ✓ Show notifications
                              ✓ When a session needs permission
                              ✓ When a turn finishes
                              ─────────────────────
                                Send to phone
                                Open the session from the phone
                                Copy phone link…
                                Send test notification
                                New phone link…
```

- **Send to phone:** the first time, a short dialog explains:
  - what is sent (the fixed text above);
  - where it goes (the server);
  - that the link is the password;
  - how to subscribe in the ntfy app.

  Then it generates the topic.
- **Copy phone link…:** copies `{server}/{topic}`. The confirmation reminds you that Windows
  clipboard history (Win+V) keeps it, and that the link shouldn't be shared or screenshotted.
- **Send test notification:** sends "Test from Claude Status Bar" right away. It reports success
  or the HTTP status in a popup, so setup is verifiable without waiting for a real event.
- **Open the session from the phone:** toggles `PhoneOpenSession`. Its tooltip says it needs
  Remote Control on for the session and puts the session ID in the push.
- **New phone link…:** rotates the topic after a confirmation. The old subscription stops
  receiving anything, which is the recovery if the link ever leaks.
- The four items below "Send to phone" are disabled while it's off.

## 5. Tests

- **`NotificationTests`** (policy):
  - pushed after the delay with no input;
  - not pushed if resolved before the delay;
  - not pushed with input after the event;
  - not pushed for a session that ended;
  - finished turns under 20 s never pushed;
  - one push per event;
  - global cap;
  - tick wrap.
- **`NtfyPublisherTests`:**
  - payload has only the fixed fields and text;
  - server/topic validation (http, user info, query, short topic, bad characters all refused);
  - pinned handler refuses other hosts and redirects;
  - topic generator length and alphabet;
  - `click` is the plain URL when `PhoneOpenSession` is off, even if an ID is available.
- **`RemoteSessionLinkTests`:**
  - valid file → session URL;
  - fallback to the plain URL for a missing file, missing field, oversized file, malformed
    JSON, mismatched `sessionId`, and an ID with characters outside the pattern (e.g. `/`, `?`,
    `#`).
- **`BuildVariantTests`:** add `ntfy.sh` to `NetworkStrings`, so the offline build is proven not
  to contain it.
- **`tools/verify-offline.ps1`:** add the same needle.
- **Manual:**
  - Locked PC → push arrives (Android and iOS if available).
  - At the desk → none.
  - Approve at the PC within the delay → none.
  - Rotate link → old subscription silent.
  - `PhoneOpenSession` on with Remote Control on → the tap opens that session. With Remote
    Control off → the tap opens `claude.ai/code`.

## Order of work (one commit each)

1. ✅ `ROADMAP.md` + this document.
2. `PhonePolicy` + pending queue + `GetLastInputInfo`, with tests (no network yet; logs what it
   would send).
3. `NtfyPublisher` + generalised `PinnedHostHandler` + settings validation, with tests; offline
   checks updated.
4. Menu, opt-in dialog, test notification, rotation.
5. `RemoteSessionLink` + `PhoneOpenSession` toggle, with tests.
6. README: Notifications section, plus a Security section on what leaves the machine and why
   the link is a secret. CHANGELOG (Unreleased). ROADMAP to Done.

## Files

- **New:**
  - `src/NtfyPublisher.cs`, `src/RemoteSessionLink.cs`
  - `tests/ClaudeStatusBar.Tests/NtfyPublisherTests.cs`, `RemoteSessionLinkTests.cs`
- **Modified:**
  - `src/SessionTransitions.cs`, `src/OverlayWindow.xaml(.cs)`, `src/Settings.cs`,
    `src/Native.cs`, `src/UsageFetcher.cs` (`PinnedHostHandler`)
  - `tests/ClaudeStatusBar.Tests/NotificationTests.cs`, `BuildVariantTests.cs`
  - `tools/verify-offline.ps1`, `README.md`, `CHANGELOG.md`, `ROADMAP.md`

## Done when

1. Release build with `--warnaserror` is clean; `dotnet test` (offline) and
   `dotnet test -p:Offline=false` are green.
2. `verify-offline.ps1` passes on the offline build.
3. With `PhonePush` off (the default), the online build makes no request to any ntfy server.
   Check with a proxy, or by the absence of log lines.
4. The manual checks in section 5 pass.

## Not in this version (on request or later)

- **Access tokens** for reserved ntfy.sh topics or self-hosted servers with access control.
  - An `Authorization: Bearer` header plus DPAPI storage.
  - About 2–3 hours, no design change.
- **Project name in the message**, as an opt-in toggle.
- **`http://` or LAN-only servers.**
- **QR code** for subscribing (avoids the clipboard).
- **Retries**, and updating or deleting a sent push.
- **Action buttons** on the push.
- **End-to-end encryption:** ntfy doesn't offer it; the fixed text is the mitigation.

## Implementation notes

_Measurements and any deviations from this plan go here as work proceeds._
