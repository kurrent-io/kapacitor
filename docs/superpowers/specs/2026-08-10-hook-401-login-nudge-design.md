# Actionable re-login nudge when a hook POST is rejected with HTTP 401

**Date:** 2026-08-10
**Status:** design approved
**Issue:** #509 / AI-1835
**Siblings:** #510 (a 401'd lifecycle payload is dropped, not spooled), #511 (the daemon does not
reliably pick up the new credential once the user acts on this nudge)

## Problem

A user's credential lapsed mid-session. Claude Code showed only this:

```
⏺ Ran 2 stop hooks (ctrl+o to expand)
  ⎿  Stop hook error: Failed with non-blocking status code: HTTP 401
```

Nothing in that banner says recording has stopped, and nothing says `kcap login`. The user had to
run `kcap whoami` on a hunch to discover they were logged out. Recording was silently off until then.

### Root cause

There are two distinct auth lapses, and only one of them is handled.

| Lapse | How it is detected | Behaviour today |
|---|---|---|
| The local token store knows the credential is dead (`AuthStatus.Expired` / `NotAuthenticated` / `WrongServer`) | pre-flight, before any POST (`ClaudeHookCommand.cs:377`) | exit 0 plus a `systemMessage` nudge — but only on `session-start` |
| The store believes the credential is usable; the **server** rejects it (401) | only from the response itself | no nudge on any event |

The reported case is the second. `CreateClientWithAuthStatusAsync` reports `AuthStatus.Ok` whenever
`TokenStore.GetValidTokensForServerAsync` hands back a token that is locally valid, and the hook path
builds its client with `autoRetryUnauthorized: false`, so a 401 is final. It falls through to the
shared failure arm (`ClaudeHookCommand.cs:786`), which writes the bare string `HTTP 401` to stderr
and returns 1. Claude Code renders any non-zero, non-2 hook exit as the opaque
"non-blocking status code" banner above.

The other hook events are worse than opaque — they are silent. `session-start`, `session-end` and
`subagent-stop` each classify a 401 as *permanent* (`< 500 and not 408 and not 429`), drop the
payload, and return 0 without a word.

## Design

### Mechanism

`systemMessage` on stdout with exit 0. Verified against the installed Claude Code bundle
(2.1.226), whose own embedded documentation states `systemMessage` — "Display a message to the user
(all hooks)" — and gives the `Stop` event as a worked example:

> **Stop hook that displays message to user:** Command must output JSON with `systemMessage` field:
> `echo '{"systemMessage": "Session complete!"}'`

Deliberately **not** used: `decision: "block"` with a `reason`. That is the only way to hand text to
the *agent* from a `Stop` hook, but it makes Claude keep working instead of stopping — an extra model
turn per lapsed turn, and a loop hazard. The user is the one who has to run `kcap login`, so the
notice goes straight to the user.

### Claude hook

A 401 becomes a recognized outcome rather than a generic failure.

- **`stop` and `session-start`** — write the notice to stdout as `{"systemMessage": "…"}` and exit
  **0**. Exit 0 is what replaces the hook-error banner with a clean notice.
- **Every other event on the shared path** (`notification`, `subagent-start`, `pre-compact`) — exit 0
  silently on a 401. These fire more than once per turn (`notification` on every permission prompt,
  `subagent-stop` once per subagent), so nudging from them would stack several identical notices in a
  single turn. `stop` and `session-start` are the two once-per-turn, user-facing events; they are
  sufficient, and they need no throttle state on disk.
- **Non-401 failures** — unchanged: bare status on stderr, exit 1.
- **The `session-start` 401 arm** — currently returns 0 having silently dropped the payload; it gains
  the same notice.
- **`session-end` / `subagent-stop`** — unchanged. A notice at session end lands as the UI is going
  away, and `subagent-stop` is a per-subagent event.

### Notice text

The three strings live together in one Core type (`AuthLapseNotice`), so the pre-flight nudge and the
server-rejection nudge cannot drift apart in wording. Follows the `VersionNudgeEmitter` precedent:
vendor-neutral text in Core, the vendor's JSON envelope at the call site.

| Member | Text |
|---|---|
| `Expired` | `[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.` |
| `NotAuthenticated` | `[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.` |
| `Rejected` (new) | `[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.` |

`Expired` and `NotAuthenticated` are moved verbatim from their current inline site at
`ClaudeHookCommand.cs:380-382`; no wording change, and `WrongServer` keeps mapping to
`NotAuthenticated` exactly as it does today.

### Other vendors

Codex, Gemini, Copilot, Pi, Kiro, OpenCode and Antigravity funnel their recording POST through
`AgentHookPoster` (`:105` and `:313`). None can carry a `systemMessage` — their stdout is a strict
handshake contract that the vendor parses — so only the stderr text changes:

```
[kcap] codex-hook stop: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording
```

Outcome classification (`HookPostOutcome.Failed`) and exit codes are untouched. One edit covers every
vendor that shares the poster.

> **Correction (2026-08-10, after PR review).** This section originally listed Cursor among the
> vendors sharing the poster. It does not: `CursorHookCommand` POSTs directly
> (`TryPostHookAsync`, and its own spool-drain lambda), using `AgentHookPoster` only for the
> `IsAuthLapsed` predicate. A 401 there returned `false`/`DrainOutcome.Drop` in silence, so Cursor
> would have been the one vendor left with no explanation — the exact bug this change exists to fix.
> `TryPostHookAsync` (the live path) now emits the same stderr line. The drain lambda deliberately
> stays silent: it replays many entries per pass and would repeat the line for each.

### 401 only

A 403 is an authorization decision, not a dead credential, and `kcap login` would not fix it. Only
401 maps to the nudge.

## Explicitly out of scope

**A 401'd lifecycle payload is dropped, not spooled.** Because 401 is classed *permanent*, the
`session-start` / `session-end` event that hit the 401 is gone; logging in afterwards does not
retroactively record it. Making 401 retryable would mean changing the drop rule in `HookSpool` /
`LifecycleSpoolDrain` for every vendor, and it directly contradicts the current reasoning at
`AgentHookPoster.cs:208` ("a POST with no bearer token would 401, and the production poster treats a
non-timeout/non-5xx status as a permanent drop — which would silently discard the very backlog this
protects"). That is a separate change with real blast radius; it is not bundled in here — tracked as
#510.

**No token-recovery retry on 401.** The hook path could re-attempt via
`TokenStore.RecoverForServerAsync` (the `rejectedAccessToken` seam) before declaring rejection. It is
omitted: `GetValidTokensForServerAsync` already refreshes proactively, so a 401 after it means the
server rejected a *fresh* token — a revoked session or an org mismatch, neither of which a refresh
heals — and WorkOS refresh tokens are single-use, so a speculative retry on the per-turn hook path
risks burning one for nothing.

## Testing

Unit tests (`test/Capacitor.Cli.Tests.Unit`) over `ClaudeHookCommand.HandleCore` with a stubbed
401-returning client:

| Case | Assertion |
|---|---|
| `stop` → 401 | exit 0, stdout JSON carries `systemMessage` containing `kcap login` |
| `session-start` → 401 | exit 0, same notice |
| `notification` → 401 | exit 0, stdout empty — no notice |
| `stop` → 500 | exit 1, no notice (regression guard on the untouched path) |

Plus `AgentHookPoster`: a 401 writes a stderr line containing `kcap login` while still returning
`HookPostOutcome.Failed`.

The existing pre-flight-lapse tests must keep passing unchanged — the string move is verbatim.

## Documentation

`README.md`, in the `kcap whoami` paragraph under *Getting started*, where server-side token
rejection is already described: one sentence noting that a mid-session rejection now surfaces in the
agent as a "run `kcap login`" notice rather than an opaque hook error. No help-text change — no
command, flag or default behaviour changes.
