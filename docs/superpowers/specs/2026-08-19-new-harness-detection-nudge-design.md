# New-harness detection and setup nudges

**Date:** 2026-08-19
**Status:** Draft
**Repos:** kcap-cli (surfaces 1–3 client side), kcap-server (surface 3 server side, surface 4)

## Problem

`kcap setup` detects installed harnesses once, at setup time, and wires kcap (hooks, skills,
instructions, MCP) into the ones the user consents to. Nothing ever looks again. A user who sets up
kcap while using Claude and later installs Antigravity gets no recording, no prompt, and no signal
that kcap could be wired in — unless they happen to re-run `kcap setup` by hand. Worse, if they
*switch* harnesses entirely, kcap goes silent: the new harness has no hooks, so no kcap code runs in
their sessions at all, and from the server's perspective the machine just stops recording.

## Goals

- Detect, after initial setup, that a supported harness is installed but kcap is not wired into it.
- Prompt the user to install kcap's integration for that harness, on surfaces that still work when
  the user has stopped using every wired harness.
- Ask once per harness per machine, remember declines, and stay silent after one.

## Non-goals

- Instant (filesystem-watcher) detection. All surfaces are check-on-occasion; the freshest is the
  next session start or daemon tick.
- Auto-installing anything without consent. Every surface ends in the existing consented install
  commands (`kcap plugin install --<vendor>` / `kcap setup`).
- Desktop-app UI. The app's Agents step already detects on wizard entry; a resident app-side offer
  coordinator (ShimOfferCoordinator-style) can reuse the ledger later but is out of scope.
- Version-drift refresh of already-wired harnesses (`--if-installed` and `refresh.js` own that).

## Design overview

One shared substrate — a vendor catalog in Core, the existing `AgentDetection` probes, a small
per-machine **offer ledger** — feeds four surfaces:

| # | Surface | Trigger | Reaches the user who… |
|---|---------|---------|----------------------|
| 1 | SessionStart nudge | every session in an already-wired harness (throttled) | still uses a wired harness |
| 2 | CLI stderr notice | any interactive `kcap` command (throttled) | still runs kcap commands |
| 3 | Capacitor UI notification | harness inventory reported on daemon heartbeat + hook ingest | visits the Capacitor UI |
| 4 | Machine-went-quiet backstop | server notices a machine stopped recording | switched harnesses and went fully dark client-side |

All four share the same predicate and the same ledger, so acting on (or declining) the offer on any
surface silences every surface.

### The predicate

A vendor V is **nudgeable** when all of:

1. `AgentDetection.Detect(...)` reports V detected (existing probes, unchanged).
2. kcap's integration is not installed for V — the vendor's existing installer `IsInstalled(path)`
   returns false (same probes `kcap status` uses today).
3. The offer ledger does not record V as declined, and any prior offer is older than the re-offer
   interval (below).

The check is pure filesystem/PATH probing — no process spawns, no network.

## Shared substrate

### S1. Vendor catalog hoisted to Core

Today the only table binding vendor label + install flag + detection selector is
`AgentVendors.All` in **Capacitor.App** (`ViewModels/Onboarding/AgentsStepViewModel.cs`), which the
CLI cannot reference. Hoist it:

- New `Capacitor.Cli.Core/Setup/HarnessCatalog.cs`:
  `record KnownHarness(string VendorId, string Label, string InstallFlag, Func<AgentDetectionResult, DetectedAgent> Select)`
  with `HarnessCatalog.All` (9 entries; `VendorId` matches the `VendorSelection.KnownVendorFlags`
  token, `InstallFlag` is the `kcap plugin install` flag, empty for Claude which is flagless).
- `AgentVendors.All` in the App re-derives from `HarnessCatalog.All` (label + flag come from the
  catalog; the App keeps only its own view concerns).
- The driver-schema conformance suite that today pins `VendorSelection.KnownVendorFlags` gains a pin
  that `HarnessCatalog.All` covers every known vendor flag, so adding a tenth harness fails a test
  rather than silently missing every nudge surface. This also serves the standing problem of new
  vendors being left off user-facing surfaces.

The "is kcap wired in?" half of the predicate is a new
`HarnessIntegrationProbe.IsWired(vendorId, home, env)` in **Core**: the per-vendor installers and
`*Paths` classes it delegates to already live in Core, and it must be callable from both the CLI
(surfaces 1–2, `kcap status`) and the Daemon (surface 3's heartbeat inventory), which cannot
reference the CLI assembly. It derives each installer's config path from home + the existing env
overrides the same way `PluginEnvironment.FromProcess()` does. `StatusCommand` refactors onto it,
**behavior-preserving**: each installer must keep receiving exactly the path argument it gets from
`BuildHooksStatusLine` today, guarded by keeping/adding the status line unit test as a regression
pin.

### S2. Offer ledger

New file `~/.config/kcap/harness-offers-v1.json` (under `PathHelpers.ConfigPath`, atomic
temp+rename like `AppState`):

```json
{
  "version": 1,
  "vendors": {
    "antigravity": {
      "first_seen": "2026-08-19T10:00:00Z",
      "last_offered": "2026-08-19T10:00:00Z",
      "declined": false
    }
  }
}
```

- Missing or corrupt file reads as empty (worst case: one repeat offer; never a crash, never a
  blocked hook).
- **Setup stamps it:** at the end of `kcap setup` Step 4, every vendor that was *detected and
  offered* gets `last_offered` = now — regardless of whether the user answered yes or no to the
  unified install prompt. Setup **never writes `declined: true`**: a "not now" at setup is a soft
  skip that resurfaces after the re-offer floor; permanent silence is only ever the explicit
  `kcap harness dismiss`. Vendors excluded from the prompt by a `--skip-*` flag were not offered
  and are not stamped. Stamping never overwrites an existing `declined: true` entry.
- **Install clears the need silently:** a successful `kcap plugin install --<vendor>` makes
  predicate clause 2 false; the ledger entry becomes inert (no cleanup needed).
- **Decline:** new command `kcap harness dismiss <vendor>` sets `declined: true`. This is what the
  SessionStart nudge tells the agent to run when the user says no, and what surface 2's notice
  mentions ("… or `kcap harness dismiss antigravity` to stop asking"). `kcap harness dismiss --all`
  declines exactly the vendors currently detected-and-unwired — deliberately NOT all 9: a harness
  installed *after* the dismiss is a new event and nudges once. "Never ask about any harness ever"
  is the profile opt-out, not `--all`; the command's help text states this.
- **Re-offer floor:** predicate clause 3 requires `last_offered` older than 7 days, so a given
  vendor nudges **at most once per 7 days even on a fully active machine** — the 6h throttle
  (S3 below) governs only how often the *evaluation* runs, never how often a vendor re-nudges.
  Emitting a nudge stamps that vendor's `last_offered`, which is what starts its 7-day quiet
  period. An ignored nudge therefore resurfaces weekly instead of dying after one ignored session
  or spamming every session.
- **Opt-out:** `Profile.DisableHarnessNudge` (bool, default false) alongside the existing four
  `Disable*` profile bools; when set, surfaces 1 and 2 emit nothing (surface 3/4 dismissal is
  server-side, below).

### S3. Throttled evaluation

Detection runs are throttled with an on-disk mtime stamp,
`~/.config/kcap/harness-offers.last-check`, exactly the `AgentHookPoster.TryClaimDrainAttempt`
pattern (every hook is a fresh AOT process, so the guard must be cross-process; mtime is the clock;
I/O errors fail open to "don't check"). Throttle: 6 hours. Within the window, surfaces 1 and 2 do
nothing — not even the probe. The read-mtime-then-write claim is **not atomic** and this is
accepted: two hook processes starting within the same instant can both claim it, and the worst
case is the same nudge fragment appearing in two simultaneously-started sessions once per 6h
window — benign, and the per-vendor 7-day floor still bounds real repetition. No lock file. The evaluation itself (9 dir/PATH probes + one small JSON read) is
well under a millisecond of filesystem work; the throttle exists to keep hook latency identical on
the common path, not because the probe is expensive.

### Known detection constraints (inherited, documented here)

- **Claude and Codex are PATH-only detectable** (`AgentDetection.cs` deliberately has no config-dir
  probe for them). A newly installed Claude/Codex is noticed only once its binary is on the PATH
  the probing process sees. For hooks that PATH is the harness's session PATH; for the daemon it is
  the daemon's environment. Acceptable: both vendors are overwhelmingly installed onto PATH.
- **`~/.gemini` is shared by Gemini CLI and Antigravity.** The existing narrowed probes already
  disambiguate, asymmetrically: Gemini requires **file/content evidence** inside `~/.gemini`
  (`settings.json`, `projects.json`, or a `tmp/` dir — a bare `~/.gemini` is NOT Gemini), while
  Antigravity requires only **directory existence** of `~/.gemini/antigravity` or
  `~/.gemini/antigravity-cli` (an empty such dir IS Antigravity). The nudge adds no new probing and
  inherits this. Tests pin both directions of the pair, honoring that asymmetry.

## Surface 1 — SessionStart nudge (wired harnesses)

Where: the existing SessionStart `additionalContext` envelope
(`SessionStartAdditionalContext.BuildEnvelope`), emitted by all 9 hook commands — the same channel
as `VersionNudgeEmitter` ("offer the user to run `kcap update`") and `WorkItemsNudgeEmitter`.

New `HarnessNudgeEmitter.Resolve(...)` (CLI assembly, pure over injected inputs) returns a fragment
when the throttle stamp is claimable and at least one vendor is nudgeable:

> The user appears to have installed Antigravity, and Kurrent Capacitor is not set up for it —
> sessions there are not being recorded. Offer to run `kcap plugin install --antigravity` to wire it
> in (hooks, skills, MCP). If the user declines, run `kcap harness dismiss antigravity` so they are
> not asked again.

Multiple nudgeable vendors are folded into one fragment. Emitting stamps `last_offered` for the
vendors named. The emitter is wired into all 9 hook commands next to the existing
`WorkItemsNudgeEmitter.Resolve` call sites, so any wired harness can announce any new one.

Failure posture: identical to the other emitters — any exception degrades to "no fragment"; a nudge
must never break a hook.

## Surface 2 — CLI stderr notice (interactive commands)

Where: one call in `Program.cs` command dispatch, after argument parsing, before command execution.

Gating (all required):

- stderr is a TTY (`!Console.IsErrorRedirected`), so scripts and pipelines never see it;
- the command is interactive-user-facing — an allowlist anchored on the top-level verb, explicitly
  excluding `hook`, `mcp`, `daemon run` (the daemon process itself), `watch`, `completion`, the
  `harness` group itself (dismissing must never print a fresh nudge; independent of ordering, the
  verb is simply excluded), and any command with `--json`-style machine output;
- throttle stamp claimable (shared with surface 1 — one check per 6h across both surfaces);
- profile opt-out unset.

Output, one line per nudgeable vendor set, to stderr:

```
kcap: Antigravity detected but not set up for recording — run `kcap plugin install --antigravity` (or `kcap harness dismiss antigravity` to stop asking).
```

Same `last_offered` stamping as surface 1. This mirrors the update-available notice pattern
(`update-check-{channel}.json`): informational, one line, never blocks, never prompts inline.

## Surface 3 — Harness inventory → Capacitor UI notification

Client side (this repo):

- New payload fragment `harness_inventory`: `{ vendor_id: { detected: bool, wired: bool } }` for
  all 9 vendors, plus `declined: [vendor_id]` from the ledger so the server can suppress declined
  vendors without a second round-trip.
- Carriers, both cheap and already flowing:
  - **Daemon heartbeat** (the existing periodic heartbeat in `AgentOrchestrator`): the daemon
    evaluates the inventory once at startup and then re-evaluates every 6h from its own in-memory
    timestamp — it must NOT claim the shared on-disk stamp, or a resident daemon would perpetually
    starve surfaces 1–2 of their claim. Every heartbeat attaches the **last cached** inventory;
    heartbeats never trigger a probe themselves. No install-event signal: a `plugin install` is
    picked up by the next 6h re-evaluation (and immediately by the hook-ingest carrier). This is the resident carrier — it works
    with zero sessions and zero manual kcap use. Note: this is pure filesystem probing; the
    trust-by-default precedent against daemon-side probing rejected *spawning vendor binaries* for
    version checks, which this does not do.
  - **Hook ingest metadata**: attach the same fragment to the SessionStart hook post, so machines
    without a daemon still report whenever any wired harness is used. PR 2 must verify the
    SessionStart post already carries `machine_id` (expected — `MachineId` is a config-dir
    fixture) and add it to the fragment's envelope if not, or the server cannot key the inventory.
- Skew, both directions: an older **server** ignores the unknown fragment (additive, no protocol
  gate); an older **client** simply never sends it, and the server treats an absent fragment as
  "inventory unknown" for that machine — never as "nothing detected" — so no notification is
  raised or cleared from silence.

Server side (kcap-server, same issue, separate PR):

- Persist latest inventory per machine (`machine_id` already flows with both carriers).
- Raise a user-facing notification — same surface and lifecycle as the existing "CLI update
  available" notification — when a vendor is `detected && !wired && !declined`:
  *"Antigravity was installed on ‹machine› but Kurrent Capacitor isn't set up for it. Run
  `kcap plugin install --antigravity` on that machine."*
- UI dismissal is server-side state per (machine, vendor) and independent of the client ledger:
  dismissing in the UI silences the UI only; `kcap harness dismiss` silences everything (the
  `declined` list suppresses the server notification on the next report).
- Re-notify only if the vendor disappears and reappears (fresh `first_seen`).

## Surface 4 — Machine-went-quiet backstop (server-only)

Covers the fully-dark case: the user switched to an unwired harness, runs no kcap commands, and has
no daemon — nothing client-side executes, so surfaces 1–3 are all dead. The server still holds one
signal: sessions from that machine stopped arriving.

- kcap-server tracks last-session-recorded per machine (already derivable from ingest).
- When a machine that recorded ≥ N sessions (proposal: 5, to skip drive-by trials) records nothing
  for 14 days, raise a one-time notification:
  *"‹machine› hasn't recorded a session since ‹date›. If you've switched coding tools, run
  `kcap setup` there to wire the new one in."*
- One-shot per quiet period: re-arms only after the machine records again. Dismissible in the UI.
- Entirely kcap-server work; no client change. The 14-day/5-session thresholds are server config.

## What this does NOT change

- `kcap setup` remains the full-consent install path; the nudges funnel into `plugin install`.
- `kcap status` gains the passive line — "‹Vendor› installed, kcap not configured — run
  `kcap plugin install --<flag>`" — via the shared predicate (ledger ignored: status always tells
  the truth, even for declined vendors).
- No change to `--if-installed` refresh semantics or `refresh.js` (its missing gemini/kiro/
  antigravity entries are a separate bug, fixed independently).

## Delivery slicing (one issue, ordered PRs)

1. **kcap-cli:** catalog hoist (S1) + ledger (S2) + throttle (S3) + `kcap harness dismiss` +
   surface 1 + surface 2 + the `kcap status` line + setup stamping. New user-facing surface
   (`kcap harness dismiss`, the status line, the stderr notice) means README + help-text updates in
   the same PR.
2. **kcap-cli:** surface 3 client fragment on heartbeat + hook ingest.
3. **kcap-server:** surface 3 notification + surface 4 backstop.

PR 1 is independently shippable and already solves the "still uses any wired harness or kcap at
all" majority; PRs 2–3 add the resident and fully-dark coverage.

## Testing

- `HarnessCatalog` conformance pins: every `KnownVendorFlags` flag appears exactly once; every
  `AgentDetectionResult` field is selected by exactly one entry.
- Ledger: corrupt/missing file → empty; setup stamping (declined vs offered); dismiss command;
  atomic write (partial-write torn file reads as empty).
- Predicate unit tests per vendor over injected `AgentDetectionInputs` + fake installer state,
  including the Gemini/Antigravity shared-dir disambiguation pair and Claude/Codex PATH-only cases.
- Emitter tests mirror `VersionNudgeEmitter`'s: fragment text, throttle respected, multi-vendor
  fold, exception → null fragment.
- Surface 2: allowlist gating (hook/mcp/daemon-run never print), TTY gating, stderr only.
- Surface 3 fragment: shape-pinned serialization test; heartbeat attachment cadence (6h cache).
- Server-side tests live in kcap-server with its PR.
