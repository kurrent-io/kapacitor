# Kurrent Capacitor Desktop — daemon supervisor app (umbrella design)

**Date:** 2026-07-31
**Status:** Approved umbrella design. Each delivery slice (§9) gets its own spec → plan → PR cycle; this document is the shared frame. Per team convention, the durable home for this umbrella spec is a Linear document; this file is the working draft.

## 1. Problem

The `kcap` daemon runs coding agents on a user's machine with no local presence:

1. **Silent launches.** The daemon can start a harness (hosted agent, flow participant, reviewer) without asking the machine owner. Users have complained about this explicitly.
2. **No ambient visibility.** Running agents are only listable on demand via the CLI, or by watching the web UI. Nothing on the machine itself shows what is running.
3. **Settings friction.** Local configuration (profiles, recording rules, daemon capacity, consent) is file/CLI-only.
4. **Onboarding friction.** Machine setup is a terminal ritual: `npm i -g @kurrent/kcap`, `kcap setup`, `kcap login`, `kcap import`.

## 2. Decisions (settled in brainstorming, 2026-07-31)

| # | Decision |
|---|----------|
| 1 | **Headless stays first-class.** `kcap daemon` keeps working standalone (Linux servers, SSH, CI, `kcap daemon service install`). The app is the default desktop experience, never a requirement. |
| 2 | **macOS first; Windows/Linux later.** Tech choices must not foreclose cross-platform — hence Avalonia, not Mac Catalyst/AppKit. |
| 3 | **Hybrid consent.** Standing rules decide silently; only unmatched requests prompt; bounded timeout → deny (fail closed). |
| 4 | **Lean supervisor scope.** The app owns daemon-local concerns only. Session browsing stays in the web UI and MAUI Desktop; the app deep-links out. |
| 5 | **Menu-bar presence AND a full main window from day one.** |
| 6 | **Onboarding onto an existing tenant now**; a reserved wizard slot for future self-service workspace creation. No server-side provisioning work in this project. |
| 7 | **The app bundles the `kcap` binary** (Docker Desktop model). npm remains the headless/CI channel. |
| 8 | **Architecture: companion app + unchanged headless daemon over local IPC** (approach A below). |

### Approaches considered

- **A. Companion app + headless daemon over local IPC — chosen.** The daemon stays the AOT `kcap` binary; the Avalonia app bundles, launches, and supervises it. Consent is enforced daemon-side, so headless behavior is the same machinery, and a UI crash can never take down running agents.
- **B. One binary with an optional UI head** (`kcap daemon --ui`). Rejected: compiles Avalonia into the AOT CLI every user downloads (size/trimming discipline), a GUI event loop conflicts with service install (LaunchDaemons cannot own UI), and a UI fault kills live agents.
- **C. Grow the MAUI Desktop viewer into the supervisor.** Rejected: Mac Catalyst forecloses Windows/Linux (violates decision 2) and mixes local-machine concerns into a server-viewer client.

## 3. Scope

**Kurrent Capacitor Desktop** (working name): an Avalonia macOS app (Windows/Linux-ready) that bundles `kcap` and acts as the local supervisor.

**Owns:** running-agents visibility, launch consent UX, local settings (CLI + daemon config), first-run onboarding (join tenant, harness hook setup, historical import), daemon lifecycle management.

**Does not own:** session browsing, server-admin settings (deep-link to web admin), tenant provisioning.

**Code placement:** kcap-cli repo, new `src/Capacitor.App` project sharing `Capacitor.Cli.Core`. Server-side changes are small and additive: the coded launch-denial reason surfaces through the existing launch-failure lane, and — discovered during slice-1 planning — the server must stamp two trailing optional fields (`requester_user_id`, `requester_is_owner`) onto the `LaunchAgent` command, because today's launch payload carries no requester identity at all and the daemon stores no owner user id. Old servers send nothing; the daemon treats null as unknown and falls through to the (upgrade-safe `allow`) default.

## 4. Process architecture & local control channel

The daemon gains a **local control endpoint**: a Unix domain socket (named pipe on Windows later) in the kcap config dir, mode `0600`, same-uid peer-credential check — no tokens. Protocol: length-prefixed JSON frames with a versioned hello; contracts live in `Capacitor.Cli.Core` so app and daemon share types. This extends the existing `AgentOrchestrator.LocalIpc` seam rather than introducing a second mechanism.

v1 surface:

- `subscribe` → pushed daemon state (server/profile, connection health) + live agent list (id, kind [hosted / flow role / review], vendor, repo/worktree, requester, state, started-at).
- `stop-agent`.
- Consent: pending requests pushed to the app; `resolve-consent` (allow-once / allow-and-save-rule / deny).
- Consent-rules CRUD. The daemon is the single writer of its rules file; the app never edits it directly.

**Lifecycle:** the daemon runs as a launchd **LaunchAgent** installed by the app through the existing `kcap daemon service install` machinery (reusing the AI-1589 service investment). Agents survive the app quitting; the app attaches/detaches. If the daemon is down, the app starts it via the bundled CLI.

**Version skew:** the app ships a matching binary, so skew only occurs when attaching to an externally installed daemon. The hello handshake detects the mismatch and the app offers to take over management (replace the service unit with the bundled binary).

## 5. Consent engine (daemon-side)

Enforced entirely in the daemon so headless behavior is identical machinery, not a parallel code path.

- **Rules** match on requester user, launch kind (hosted agent / flow participant / review flow), repo, and vendor → allow/deny.
- **Built-ins:** the machine owner's own launches are always allowed; an explicit rule always beats the default.
- **Unmatched default:** `allow` | `deny` | `prompt`.
  - **Upgrade-safe default is `allow`** (today's behavior). A headless daemon on a shared box must not silently start failing flows on update. The app's onboarding flips the daemon it manages to `prompt`.
  - `prompt` with a UI attached: the request is offered over IPC with a bounded timeout (default 45 s — inside the server's 60 s launch-admission patience, `Flows:Settlement:LaunchAdmissionWaitSeconds`) → deny on timeout, fail closed.
  - `prompt` with no UI attached: resolves immediately as deny.
- **Denials** travel as a coded launch failure (`launch_denied_by_owner`), non-retryable on this daemon, through the same reporting lane as `daemon_at_capacity`, so flows fail fast with an honest reason instead of hanging.
- Every decision (rule-matched or human) is appended to a **local decision log** the app renders as the Activity feed.

## 6. The app

Avalonia + MVVM (CommunityToolkit.Mvvm); `Avalonia.Headless` for ViewModel/UI tests.

- **Menu bar:** icon encodes daemon state (stopped / connecting / idle / *n* agents running / attention-needed). Popover: running agents with per-agent Stop and "Open in web" deep links; quick toggles (pause new launches, open main window).
- **Main window:** three areas — **Agents** (richer list: kind, requester, repo, vendor, uptime, stop), **Activity** (consent/launch decision log), **Settings**. First run opens the **Onboarding wizard** instead.
- **Consent prompts:** system notification *plus* an auto-raised prompt window (Allow once / Always allow — saves a rule / Deny; shows requester, kind, repo, vendor). Native notifications from Avalonia on macOS are a known rough edge; the prompt window is the always-works path, notifications are enhancement.

## 7. Onboarding & distribution

Signed/notarized `.dmg`. App auto-update updates app + bundled binary atomically. npm remains the headless/CI channel.

Wizard steps — every step shells the bundled CLI (UX over existing, tested commands, not a reimplementation); each step individually retryable:

1. Install the CLI shim on PATH (symlink, one admin prompt — Docker Desktop pattern).
2. Connect: paste invitation/server URL, or pick an existing profile if `~/.config/kcap` exists. The reserved **"Create a workspace"** slot for future self-service sits immediately before this step.
3. Browser login via the CLI's existing PKCE/device flow; tokens land in the shared `tokens.json`.
4. Harness detection (Claude Code, Codex, Cursor, … found on the machine) → guided hook setup reusing `kcap setup`.
5. Optional historical import with progress (`kcap import` per harness). A failed import never blocks finishing onboarding.
6. Enable the daemon (LaunchAgent install) and set consent mode to `prompt`.

## 8. Settings surface

v1 edits **local** configuration only:

- Profiles / server.
- Recording: ignored repos/dirs, default visibility.
- Daemon: capacity, allowed vendors/models, borrowed-review token command (`KCAP_COPILOT_TOKEN_CMD`).
- Consent rules editor (front-end for §5).
- App preferences: launch at login, notifications.

Server-admin settings deep-link to the web admin — never duplicated.

## 9. Delivery slicing

Each slice is its own spec → plan → PR series:

1. **Daemon control IPC + consent engine** (kcap-cli). Headless-complete on its own: config rules, coded denials, decision log. Prerequisite for everything after it.
2. **App shell:** tray + main window, attach/supervise, agents list, stop, consent prompts.
3. **Distribution + onboarding:** bundling, signing/notarization, PATH shim, wizard.
4. **Settings surfaces.**

## 10. Error handling & testing

- IPC disconnect → tray shows "daemon unreachable" with retry/start; the app never blocks on the daemon.
- Consent paths are fail-closed and coded (§5).
- Consent engine + IPC protocol: unit/integration tests in the kcap-cli suite — the headless-default matrix (`allow`/`deny`/`prompt` × UI attached/absent), timeout → deny, rule precedence, owner-always-allowed.
- App: ViewModel tests via `Avalonia.Headless`.
- Onboarding e2e stays manual initially — signing/notarization makes CI e2e expensive (accepted risk).

## 11. Risks

- **Notarization/signing infrastructure** — new release-engineering surface for the team.
- **macOS notifications from Avalonia** — may need a small native interop layer; prompt window is the fallback.
- **PATH-shim installation privileges** — one admin prompt; needs a graceful denial path (app works without the shim, terminal features degrade).
- **Consent upgrade default `allow`** — deliberately preserves current behavior for existing headless daemons; the complaint is only fully answered on machines where the app (or an operator) flips the default to `prompt`.
