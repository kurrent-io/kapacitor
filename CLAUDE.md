# Kurrent Capacitor CLI

**File paths:** CLI source at `src/Capacitor.Cli/`, shared core at `src/Capacitor.Cli.Core/`, daemon at `src/Capacitor.Cli.Daemon/`, npm packages at `npm/`, Claude Code plugin at `kcap/`.

**Harness layout:** vendor-specific code lives under `Harness/`. Vendors: Antigravity, Claude, Codex, Copilot, Cursor, Gemini, Kiro, OpenCode, Pi.

- `src/Capacitor.Cli.Core/Harness/<Vendor>/` — paths, hook parsers/installers, CLI runners.
- `src/Capacitor.Cli.Daemon/Harness/<Vendor>/` — launchers, runtimes, reviewer capabilities, posture policies; one directory per vendor covering what used to be split between `Services/` and `Acp/`.
- `src/Capacitor.Cli/Commands/Harness/` — command entry points, flat (`<Vendor>HookCommand` and friends).
- `src/Capacitor.Cli/Harness/<Vendor>/` — everything else per vendor: import sources, subagent teardowns, correlators, ledgers.

Namespaces follow the directory (`Capacitor.Cli.Core.Harness.Codex`, `Capacitor.Cli.Commands.Harness`, `Capacitor.Cli.Harness.Pi`), enforced by the compiler, so moving a file means fixing its namespace in the same change. Code shared across vendors stays outside `Harness/` — a directory named after a vendor holds only that vendor's code. Adding a harness should mean a new `Harness/<Vendor>/` directory plus one registration site per assembly, not edits to shared folders.

## What this project does

The `kcap` CLI records Claude Code sessions by forwarding hook payloads and transcript data to a Kurrent Capacitor server. It also hosts an agent daemon for remote Claude CLI management and provides PR review context via MCP tools.

Review flows use a vendor-neutral catalog-start v2 protocol: reserved `spec-review`/`code-review`
aliases select an explicit or server-default reviewer independently of the driver. Codex setup
registers `kcap-flows` without auto-approval and tracks only newly-created global TOML entries in
`mcp-ownership-v1.json`, so uninstall preserves manual/customized MCP configuration. Daemons retain
the string unattended-vendor list for compatibility and additionally advertise structured
per-vendor CLI/launcher-policy capabilities. Cursor serves borrowed review context from a
daemon-owned snapshot (dirty tracked and non-ignored untracked files, refreshed between rounds),
because its zero-interaction modes may write; Claude has no borrowed-review containment strategy
and therefore fails closed to an owned worktree.

Copilot also borrows from a daemon-owned snapshot, but its read boundary is an **OS sandbox**
(`BorrowedReviewSandbox`, `sandbox-exec`, `(deny default)`) rather than a tool clamp, because the
read tools it needs to see the snapshot are the same tools that could be pointed elsewhere. The
profile grants nothing under the user's home: a per-launch `HOME`/`TMPDIR` state directory replaces
the vendor's own config and cache grants, `BorrowedReviewAuthBroker` replaces the keychain grant with
a token from the daemon's own environment, and `BorrowedReviewRuntimeRoots` replaces whole-prefix
grants with software subdirectories derived from the vendor binary — never the prefix's `etc`/`var`. Support is
conjunctive: macOS/ARM64 **and** `sandbox-exec` **and** a brokerable token, or the capability is not
advertised and the server answers `vendor_containment_unreadable` with the `context-only` remedy.
Enforcement is asserted by tests that run a real process under the profile, because a model-layer
refusal is not containment evidence.

The daemon never *looks* for a credential: no keychain read, no prompt, no cache, no persistence, no
default command. It forwards a token the operator exported, or — for a supervised daemon, whose unit
file must not hold a secret — runs the single command the operator configured in
`KCAP_COPILOT_TOKEN_CMD`, and only when an actual borrowed launch needs one. Availability is
deliberately passive (configuration presence, never execution): probing by running the command at
startup would mint a credential nobody asked for, so a configured-but-broken command instead fails at
spawn with the coded `borrowed_review_auth_unavailable`. Service units are written owner-only, and
installation fails rather than leaving one readable.

Borrowed-review capability is **trust-by-default**: a vendor advertises it whenever its runtime
factory declares a containment strategy, for whatever build of the vendor CLI is installed and on
every platform. It is deliberately not gated on the installed binary matching a validated-build
record — a vendor auto-update would then silently withdraw the capability and reviewers would fall
back to a stale committed base. The daemon logs the CLI version it probed at startup (a startup
observation, not a launch-time fact) and does no automated drift detection; a defective vendor
release is handled by a human report and a corrected record. See
`docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md` in kcap-server.

A daemon-owned launch-consent gate (AI-1623, `LaunchConsent*` in `Capacitor.Cli.Daemon/Services`)
sits in front of every SERVER-driven launch: `LaunchConsentStore` owns `{stateDir}/consent.json` as
the single writer, degrading a missing/corrupt file to the upgrade-safe default (`allow`, so no
pre-existing daemon bricks on update) rather than failing closed. Every decision — rule-matched or
human — is appended to `consent-decisions.jsonl` (1MB rotation, 0600 from first byte) for the
`kcap daemon consent log` verb and the eventual desktop Activity feed, and a non-owner denial
surfaces to the server as the coded `launch_denied_by_owner` reason. The policy is queried/edited
live over the local control socket via the append-only `FrameType` values 11–14
(`ConsentSubscribe`/`ConsentResolve`/`ConsentRulesGet`/`ConsentRulesPut`) and 72–74
(`ConsentPending`/`ConsentRules`/`ConsentAck`); `kcap daemon consent {show,set-default,allow,deny,remove,log}`
is the CLI surface and never blocks a launch waiting on a terminal prompt.

**AI-1648** hardens the local control IPC ahead of the desktop supervisor app (spec:
`docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`). A versioned **hello**
frame pair (`HelloIpc.cs`) lets a client discover daemon capabilities before assuming any protocol
shape: `Hello = 15` (client→daemon, optional `ClientHelloDto` — diagnostics only, never trusted)
draws `HelloReply = 75` (`HelloReplyDto`: protocol/daemon version + a `capabilities` list) from
`LocalControlServer`, answered and closed like `List`. `LocalControlCapabilities.Current` sits next
to the routing switch so an entry can never be advertised without a live handler — this PR ships
`["consent/1"]` only, `"status/1"` is reserved for AI-1649's `StatusSubscribe` handler. A pre-hello
daemon can't decode frame 15 at all, so down-level discovery is hello-then-EOF, not an `Error` reply.
The `prompt_no_ui` instant-deny race is closed by a bounded **subscriber grace** in
`LaunchConsentGate`/`LaunchConsentBroker`: `min(5s, PromptTimeoutSeconds)` burned from one monotonic
absolute deadline (injectable `TimeProvider`) fixed at prompt-path entry — every later wait
recomputes `deadline − now` immediately before use rather than accumulating elapsed time, zero
remaining settles as the existing `prompt_timeout` denial (no special case), and a generational
subscriber-arrival waiter in the broker (one shared `TaskCompletionSource` per zero-subscriber
period) lets concurrent waiters converge with arrival winning ties. Cancellation (the launch's own
shutdown token) aborts the wait and the launch together — no consent decision is ever fabricated.

**AI-1655 Plan B** (spec: `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` §4/§6)
is the desktop app's mutation-safety substrate. Every daemon mutation the app performs routes through
ONE app-lifetime `DaemonMutationLane` (`Capacitor.App/Services/Mutation/`): per-action CLI pinning
(validated login-shell resolver, strict `0.12.0-beta.1` floor probe per mutation), an action-scoped
`KcapCli` executor overlaying `KCAP_CONSENT_SEED_DEFAULT`/`KCAP_EXPECT_SERVER_URL`/
`KCAP_APP_SPAWN_NO_TELEMETRY`/`KCAP_BOOT_ATTEMPT`, instance-bound evidence classification (one
shared predicate; misclassification-toward-success is the cardinal sin), boot-refusal-marker
attribution by attempt id, and a leased FIFO outcome channel whose single consumer owns ALL
actionable presentation (waiter results are state-only; requeue-exactly-once, second abandonment =
logged consume). `ConsentFlipClaims` (durable, `ConfigFileLock`-mutated, two-lock conditional clear,
quarantine-aside) + `ConsentFlipCoordinator` (factory guard → `ConsentRulesPutV2`) cover
pre-existing daemons. `OnboardingGate` (provider-aware, mirrors `TokenStore`'s real refresh rules,
shared URL validator with `App.ValidProfileName`) drives the decision-2 startup carve-out:
gate-incomplete machines build the graph with lifecycle auto-actions permanently closed and the
shim auto-offer suppressed (item stays visible). Wizard UI + the Core auth façade are Plan C.

**AI-1655 Plan C** (spec §5/§3) is the Core façade and the full wizard. `OnboardingFacade`
(`Capacitor.Cli.Core/Auth/`) drives login/discover/create through one ordered commit boundary —
claims (decision 7) → config + provider stamp → tokens — behind a totalized `AuthResult`:
`Committed`/`Cancelled`/`Failed(AuthFailureReason)`/`Retarget(ServerInput)`. `LoginAsync`'s
`adoptServer` flag separates `kcap login` (never repoints `server_url`) from `kcap setup`/the
wizard (adopts — the write that reaches gate-complete); `kcap setup`/`kcap login` re-plumb onto the
façade as thin Spectre adapters, behavior-preserving. `WizardComposition.BuildGraph`
(`Capacitor.App/Services/Onboarding/`) composes the 8-step wizard (Shim/Connect/Sign-in/Defaults/
Agents/Import/Daemon/Done) over that SAME façade via `WizardAuthService` and its decision-7
`ArmingHook`; `App.RunWizardModeAsync` runs it wizard-first on a gate-incomplete machine (no
daemon graph, no tray) and hands the outcome channel to the normal graph's consumer via
`OutcomeChannel.TransferConsumer` once the sign-in lane cancels/quiesces, closing auto-actions
permanently past the quiesce cap (decision 2/§6a). The §7 streaming `IProcessRunner` backs the
Import step's live, bounded-tail log pane.

The receive pump no longer awaits launch/stop EXECUTION for either command format: arrival order is
preserved by routing sequenced AND un-sequenced server-origin launch/stop traffic through the ONE
existing serial lane (`RunLaneAsync`). Un-sequenced commands commit via a typed, no-ack entry point
— `SequencedCommandProcessor.SubmitUnsequenced(UnsequencedItem)` — whose admissibility check,
active-launch-instance tracking, and lane commit all happen inside one critical section before the
call returns. Active launch instances are reference-counted per agent id, so a launch dequeued and
parked at the consent gate stays an admissible stop target; admissible targets are `_agents` ∪
durable PID records ∪ active instances — the PID-record arm is load-bearing (it's how the server's
registry-independent physical stop reclaims a prior incarnation's survivor), not belt-and-braces. A
per-boot publication barrier makes "no dual domain, ever" structural: one lock guards both handler
admission and the processor's single null→live transition. Stop coalescing is launch-aware and
identity-guarded (a launch commit clears all of its id's pending-stop keys; a same-payload retry
after a faulted stop always commits fresh), and the queued-stop count backs an edge-triggered,
hysteresis-gated alarm exposed via `QueuedStopDepth`/`QueuedStopHighWater` accessors — additive, with
no production consumer yet (AI-1649's supervision IPC is the natural one).

## Tech stack

- .NET 10, NativeAOT compiled
- SignalR client for real-time server communication
- TUnit for testing, WireMock.Net for HTTP mocking

## Building

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```

## Test conventions

**Layout:** one test project per prod project, each mirroring that project's directories — `test/Capacitor.Cli.Core.Tests.Unit/`, `test/Capacitor.Cli.Tests.Unit/`, `test/Capacitor.Cli.Daemon.Tests.Unit/`, plus `test/Capacitor.Cli.Tests.Integration/`. A test project references its own prod project and `test/Capacitor.Tests.Helpers/`, never another test project: anything shared across suites goes in Helpers, with a `public` surface (no `InternalsVisibleTo`). Helpers' `Guards/` holds the process-global pins every assembly needs, and Helpers is a global `using` everywhere, so its types need no import.

- Throwaway directories come from Helpers' `TempDir` — `using var tmp = new TempDir();` — never a per-class copy. Build paths under it with its own members — `tmp.PathTo(…)` for a path that must not exist yet, `tmp.CreateDir(…)`, `tmp.CreateFile(…)` — not `Path.Combine(tmp.Path, …)` + `Directory.CreateDirectory`/`File.WriteAllText`. When the code under test refuses a symlinked path component, hand it `tmp.GetResolvedPath(…)` instead: a Mac's temp root is under `/var`, a symlink into `/private`, so the plain path is rejected there and nowhere else — CI has no macOS leg to catch it. It is not the default because resolving costs 8 characters of the `sockaddr_un` budget.
- When every test in a class needs one, inject it instead of holding a field: `[TempDir] public required TempDir Tmp { get; init; }` (or `[TempDaemonPaths]` for a `TempDaemonPaths`; both take an optional hint, `[TempDir("short")]`). TUnit builds one per test and disposes it, so the class needs no `IDisposable` — which is also how CA1001 stops applying. `Shared` widens the lifetime (`SharedType.PerClass` and friends) but hands one directory to tests that run concurrently, so it fits a read-only fixture only. The property is set *after* construction, so a ctor or another field initializer cannot read it (make those members lazy) and a `static` helper cannot see it at all; a `[Before(Test)]` hook can, because injection runs first.
- Outside a test class — a nested harness type, a fixture object — keep the field and implement `IDisposable`: TUnit only injects into test classes. CA1001 is an error there, so an `[After(Test)]` hook will not build.
- A class whose time goes into real child processes (git, a vendor CLI, a PTY) can draw from `[ParallelLimiter<SubprocessLimit>]` — one pool of half the cores, shared by every class in the assembly that names it. TUnit's own cap is 4x the cores, sized for IO-bound tests; at that width these classes starve each other's timing assertions. Pool the CPU hogs, not the test that failed. Today the pool is the daemon suite's; elsewhere a whole-class `[NotInParallel(nameof(TheClass))]` is the cheaper tool when the point is to keep one class's own tests apart.
- Spawning the real `kcap` binary goes through Helpers' `KcapProcess`, which requires a `DaemonStore` and pins it for the child. The assembly-wide `KCAP_DAEMONS_DIR` pin is a path that cannot be created, so a hand-rolled spawn dies with a bare ENOTDIR `IOException` instead of quietly resolving the developer's own daemons directory.
- Capture console output with `ConsoleOutput.StartCapture()` / `StartErrorCapture()`, never a hand-rolled `Console.SetOut`/`SetError` save-restore — TUnit0055 is an error. Console is process-global, so every caller needs bare `[NotInParallel]`; a group key is not enough.
- The same bare `[NotInParallel]` applies to any test that reaps a child itself (a raw `waitpid`). Once a pid is reaped the OS may reassign the number, so a concurrent spawn can make the wait land on someone else's child — and stealing one that `System.Diagnostics.Process` owns leaves .NET's SIGCHLD handler with ECHILD, which it answers by FailFast-ing the test host. A whole suite then dies mid-run with no failing assertion to point at.
- **`Capacitor.Cli.Daemon.Tests.Unit` is not parallel-safe** — it spawns real processes, unix sockets and PTYs, and around 39 of its tests collide concurrently. Run it the way CI does, or it fails for reasons that have nothing to do with your change:
  ```bash
  dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --maximum-parallel-tests 1
  ```
  A TUnit assembly-level `[ParallelLimiter<T>]` is **not** a substitute: it serialises by wall-clock but still leaves `CodexLauncherTests` failing intermittently, where the command-line flag does not.
- Never assert that an environment variable is *absent* from a built `ProcessStartInfo`: its environment is seeded from the current process, and the repo's own `.envrc` exports several. Assert what the code under test contributed, by comparing against the inherited value.

## Running tests

Tests use TUnit on Microsoft Testing Platform.

```bash
dotnet test --solution Capacitor.slnx
```

A single suite still runs directly as an executable, which is the faster loop when iterating:

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
```

## Publishing

AOT publish for the current platform:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release
```

Always verify no IL3050/IL2026 AOT warnings after changes:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Issues and pull requests

This is a public repository — we develop in the open.

- **Open issues in GitHub Issues**, not Linear. Linear auto-imports GitHub issues, so there is no need to create the issue in Linear by hand.
- **PRs must reference both the Linear issue and the GitHub issue.** Put these references in the PR *description*, not the title (the title stays clean and human-readable). Reference the GitHub issue with a closing keyword (e.g. `Closes #123`) and include the Linear issue (e.g. `AI-774`) so Linear links the PR back to the imported issue.

## Dos and donts

- DO use `JsonElementExtensions` instead of checking JSON value kind.
- DO NOT use Linear issue numbers in comments. If you absolutely need an issue number, use the GitHub issue number.
- **Comments:** only where they add value, and short. Explain why — the trap, the non-obvious constraint, the decision someone would otherwise undo. Never restate what the code already says, never narrate a change ("moved from X", "hoisted from all 25 projects") — that is what the commit message and the issue are for. One or two lines is the norm; a longer block needs a reason a reader could not get anywhere else. This applies to `.editorconfig`, `.props` and YAML as much as to C#.

## Common mistakes to avoid

- **AOT warnings only show on publish** — `dotnet build` does NOT surface IL3050/IL2026 trimming warnings. Run `dotnet publish -c Release` after changes.
- **JsonArray collection expressions** — `[item1, item2]` compiles to `Add<T>()` which requires dynamic code. Use `new JsonArray(item1, item2)` constructor instead.
- **TUnit test filtering** — Use `--treenode-filter` with glob syntax, NOT `--filter`.
- **macOS AOT binary code signing** — After copying an AOT binary, run `codesign --force --sign -` to re-sign.
- **Never read an agent-owned file with a write-denying open** — `File.ReadAllText`/`ReadAllTextAsync` open `FileShare.Read`, which *denies Write to every other handle* for the duration. On Windows that sharing is mandatory, so it stops the agent writing to its own transcript/sidecar — worst on the shutdown final drain, when it is flushing its last records. Read via `WatchCommand.ReadAllTextShared`/`ReadAllTextSharedAsync` (or your own `FileStream(..., FileShare.ReadWrite)`) for anything the agent writes: transcripts and their `{id}.json` sidecars. Config/settings files we own are fine. **This is invisible on macOS/Linux** — Unix has no mandatory sharing, so a violation passes locally and only reddens the Windows CI leg (AI-1629 was exactly this, on the one read that missed the rule while seven siblings had it).
- **README sync on CLI changes** — Any change to user-facing CLI surface (new command, new/renamed/removed flag, changed default behavior, new prerequisite) must update `README.md` in the *same* PR. Check both the quick-start (`## Getting started`) and the per-command section under `## CLI commands`. Updating only `src/Capacitor.Cli.Core/Resources/help-*.txt` is not enough — the README is the public-facing docs. This has been missed repeatedly and has required follow-up doc-only PRs (#60, #61).
