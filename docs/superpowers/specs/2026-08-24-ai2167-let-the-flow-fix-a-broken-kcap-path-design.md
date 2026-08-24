# AI-2167 — Let the flow fix a broken kcap PATH

## Problem

The Agents screen's PATH warning (AI-2032) is the one justified use of coral in the whole flow: if
the login shell cannot find `kcap`, hooks run and record nothing, silently. The design offers two
actions — **"fix it for me"** and **"show me the line"** — and AI-2032 ships the second only,
because the two things "fix it for me" needs both live in `Capacitor.App`, the Avalonia supervisor
being retired by AI-2053:

- **`LoginShellProbe`** — the only login-shell probe implementation (interactive-login `$SHELL
  -lic/-lc`, sentinel-parsed, fail-closed path validation, independent per-question caches).
- **`PathShimInstaller`** — the shim writer (osascript admin prompt, non-forcing `ln -s`,
  lstat preflight taxonomy, post-install re-probe that never reports success on the symlink alone).

This ticket is the **rescue** of those two classes into `Capacitor.Cli.Core` (the same rescue shape
used for the app's daemon-mutation mapping — types leave the app before AI-2053 deletes it, the app
picks them up via `using`), plus **the capability that drives the shim writer** — because the flow
cannot simply ask for it either.

## The lane rule (retirement spec §6.1)

The server→CLI lane is a **configuration push whose payload is effectively executable** — `kcap
setup` writes Claude Code hooks, and a hook entry is a command string Claude Code runs. So the
enumeration must be of **values, not paths**: a closed set, with the CLI composing file contents
itself, and unknown members rejected rather than passed through. **The CLI must never accept a
server-supplied command string, file body or path.**

Consequence for the shim: the flow cannot send `"/usr/local/bin/kcap"` or `"sudo ln -s ..."`. It can
only name the capability; the CLI resolves the target (its own binary path) and composes the whole
operation. That named capability is a new CLI verb.

## Changes

### 1. The process seam moves to Core

`LoginShellProbe` and `PathShimInstaller` both depend on `IProcessRunner` (the app's process seam:
records + interface, and the production `ProcessRunner` implementation nested in
`DaemonClientService`). For the classes to live in Core, the seam must come with them — one copy,
shared by app and CLI (the same one-copy rule as `ReasonRouting`):

- `IProcessRunner` + `ProcessResult`/`RunOptions`/`CancelMode`/`TimeoutKillScope`/
  `ProcessStreamKind`/`StreamedLine`/`StreamingResult` move to `Capacitor.Cli.Core`
  (namespace `Capacitor.Cli.Core`).
- The production `ProcessRunner` implementation moves to Core as `Capacitor.Cli.Core.ProcessRunner`
  (public), so the CLI's new verb has a real runner — no second implementation.
- The app keeps its `DaemonClientService` etc. working via `using Capacitor.Cli.Core;`.

### 2. `LoginShellProbe` / `PathShimInstaller` move to Core

- `ILoginShellProbe` + `LoginShellProbe` → `Capacitor.Cli.Core.Setup` (namespace
  `Capacitor.Cli.Core.Setup`, beside `AgentDetection`/`HarnessInventory` — the probe feeds the
  harness inventory the flow already reads).
- `PathShimInstaller` + `ShimPreflight`/`ShimOutcome`/`ShimResult` → same namespace.
- The app's consumers (`ShimOfferCoordinator`, `DaemonLifecycleController`, `AgentsStepViewModel`,
  `App.axaml.cs` composition, the wizard) pick them up via `using Capacitor.Cli.Core.Setup;`.
- The installer's test seam (`InstallAsync(target, destination, ct)` and `Preflight`) becomes public
  — Core has no `InternalsVisibleTo` for the app, and the app's shim coordinator drives preflight
  against a temp destination in tests.

### 3. `kcap daemon shim ensure` — the named capability

New verb in the `kcap daemon` group (the same group as `service`, where the flow's other
machine-setup ladder verb lives; the shim is the CLI-visibility half of the same story):

- **Resolves the target itself**: the running CLI's own binary path (`Environment.ProcessPath`) —
  never a server-supplied path. No target → coded refusal.
- **Probes**: fresh `LoginShellProbe` against the real login shell → `KcapOnPathAsync`.
- **Decides** (pure classifier, mirroring the wizard's step matrix):
  - probe positive → **already_on_path** — nothing to do (the flow's done state).
  - probe unknown → **fail closed** — `probe_unknown`, never guessed, nothing touched.
  - probe negative → macOS only: preflight (installable → install; already-installed → re-probe;
    conflict → coded refusal); off-macOS → `unsupported_platform` refusal (the shim is
    osascript-based; copy reflects plain "show me the line" off-macOS).
  - a **null re-probe after install** fails closed to `failed` ("could not re-verify") — the link
    exists, but the PATH's contents were never positively re-read, so they are not asserted.
- **Reports machine-readably**: `--json` payload (outcome token, never prose; refusal rows carry a
  coded reason), following the shape of the service group's existing `status --json` contract. Human
  output without `--json`.

### 4. README + `help-daemon.txt`

Document `kcap daemon shim ensure` in the daemon section and help text, noting the macOS scope and
the off-macOS degraded shape.

## Out of scope

- **Server/flow wiring** (AI-2048, AI-2156) — the verb exists; who calls it and when rides those.
- **The probe result field in the create-flow body** — that field is one line in a body AI-2156 is
  already writing, so it belongs there, not here. This ticket moves the probe to Core so AI-2156 can
  report it; it does not add the field itself.
- **AI-2032's warning rendering** — conditional on the probe result, and it stays informational
  ("show me the line") until the flow wiring lands.
- Anything else in `Capacitor.App` (AI-2053).
