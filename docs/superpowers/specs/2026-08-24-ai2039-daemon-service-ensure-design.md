# AI-2039 — `kcap daemon service ensure`: the flow's daemon-install ladder

## Problem

The first-run flow's Done detour ("reach this machine from anywhere") needs one action that makes
the daemon service-installed and running. Today the ladder exists only in `Capacitor.App` (the
Avalonia wizard being retired by AI-2053): `DaemonStepViewModel` classifies a fresh
`service status --json`, `DaemonMutationLane` dispatches and classifies the mutation, and
`ReasonRouting` maps `start_gate_reason=` tokens to a recovery surface. The CLI has all the
primitives — `service status --json`, `service install --verify`, `service start --verify`, the
`ServiceVerify` transaction engine with coded exits — but nothing that composes them into the
ladder the flow needs.

## The ladder

From a fresh `service status --json`:

| Fresh state | Action | Notes |
| --- | --- | --- |
| no unit | `service install --verify` | bakes `KCAP_CONSENT_SEED_DEFAULT=prompt` — the app-installed daemon is born `prompt` |
| unit present, stopped | `service start --verify` | gated when the invoking launcher carries the seed directive |
| running (validated daemon pid) | none | already enabled — reachable is the flow's done state; service-vs-manual ownership is the detour's visibility question, not the ladder's |
| anything ambiguous | none — fail closed | unknown probe, txn marker/active, orphan label, running-unconfirmed — attention, never guessed |

Gate failures surface as coded exits (`verify_start_gate` = 28, `verify_start_gate_drift` = 29)
with one machine-readable `start_gate_reason=` line, mapped by `ReasonRouting` to **takeover**,
**reinstall** or **fail-closed attention** — never derived from prose.

## Changes

### 1. `ReasonRouting` / `RecoverySurface` move to Core

Both `Capacitor.Cli` and `Capacitor.App` reference `Capacitor.Cli.Core`; the CLI cannot reference
the app. The pinned token→surface table therefore moves from
`Capacitor.App/Services/Mutation/MutationModel.cs` to Core (namespace `Capacitor.Cli.Core`), and
the app's existing references (its `DaemonMutationLane`, `App.axaml.cs` presentation switch,
`MutationRequestFactory`) pick it up via `using`. The app's tests for the table move with it. This
is the same rescue shape as AI-2167 (classes leaving the app before AI-2053 deletes it).

### 2. `kcap daemon service ensure`

New verb in `DaemonServiceCommands`:

- fresh status query + the same lifecycle evidence `status --json` reads (probe/state/unit
  presence, txn marker/active, validated daemon pid);
- pure classification (unit absent → install; unit present + stopped → start; running → already
  enabled; ambiguous → attention), mirroring `ServiceStatusRender`'s "unknown never masquerades";
- install path builds the spec env via `ServiceEnvironment.Capture` and **force-bakes**
  `KCAP_CONSENT_SEED_DEFAULT=prompt` plus the `KCAP_EXPECT_SERVER_URL` pin (the identity half of
  the start gate re-reads it) and the `KCAP_DAEMON_SUPERVISED` pin — the app's `MutationEnv`
  equivalent in-process;
- launchd runs the verified transaction with a `gateEnv` carrying the seed directive (so the
  start gate fires — this is the app-managed start contract); other platforms run the plain
  install/start (the degraded end state whose copy the flow reflects);
- machine-readable result: coded exit + `start_gate_reason=` (already emitted by the engine) + a
  `recovery_surface=` line and a `--json` payload the flow can act on without parsing prose —
  including on the pre-flight refusals (no server configured, daemon binary missing), which emit
  a `refused` row rather than an undocumented empty stdout.

### 3. README + `help-daemon.txt`

Document `ensure` in the service command list and help text, noting the macOS/launchd `--verify`
scope and the plain install on Windows/Linux.

## Established: the Windows answer

AI-2039's open question asked what the daemon actually does on Windows, because the answer changes
copy on two screens (the install ladder's and the Done detour's). Established while wiring the
install:

- The daemon itself is **fully cross-platform**: ConPTY (`Pty/Windows/ConPtyProcess`), a Windows
  Scheduled Task service manager, job objects, a win-x64 npm package, and Windows CI legs. Hosted
  agents (ACP-over-stdio runtimes + ConPTY for PTY vendors) and the server→daemon SignalR path
  (agent launches, permission prompts) work there. So the detour's pitch — "reach this machine
  from anywhere, runs happen here" — holds on Windows.
- What is Windows-gated: the **local `kcap agent` terminal drive** ("not supported on Windows
  yet"), and the **verified transaction** (`install/start --verify`) which is **launchd-only** — on
  Windows/Linux the ladder degrades to plain install/start with no gates, no `start_gate_reason=`,
  no takeover/reinstall classification, and no rollback.

So the flow shows the detour everywhere; the copy reflects plain install off-macOS (`--json`
reports `verified:false`). The Avalonia wizard being macOS-only and the MAUI host being
maccatalyst+windows are both irrelevant to this decision — the browser flow is the first surface
that can give "which platforms does first run actually support" a single answer.

## Out of scope

- Flow/screen wiring (AI-2048), the flow's CLI create+poll half (AI-2156), and anything server-side.
- The Avalonia wizard itself (AI-2053) — only the shared mapping moves now.
- Takeover as an ensure *action*: ensure classifies and reports the surface; performing
  `install --replace --verify` stays a separate, consent-bearing step the flow offers.
