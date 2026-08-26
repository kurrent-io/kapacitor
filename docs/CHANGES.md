# Change notes

Why each feature is the shape it is — the reasoning a reader would otherwise reconstruct from a
diff. `CLAUDE.md` holds the invariants; `docs/superpowers/specs/` holds the full designs.

Not release notes. Each entry is written as of the change that produced it and is not revised as the
code moves on; where an entry disagrees with the code, the code wins.


## The Agents screen's visibility answer reaches the profile

The flow asked who may read future sessions, recorded it on `FirstRunAgentsDecidedEvent`, served it on
the poll as `default_visibility` — and no CLI read it. The field was absent from the wire models
entirely, so it was dropped at deserialisation and `kcap setup`'s step 3 prompted unconditionally and
wrote its own answer over it. The one place in the flow that asked a question and discarded the answer.

It rides the Agents decision, so it is read off the same answer and gated the same way, and it is
validated against `AppConfig.ValidVisibilities` rather than forwarded: the value lands in profile
config and is stamped on every session afterwards, so a stop a newer server invented would be written
to a file this build owns and read back by something that may not mean the same by it. A dropped value
degrades to null, which leaves the profile as it was — the same outcome as never having asked.

Null is not a value here, and covers both "unanswered" and "declined everything". Declining every
harness while still choosing an audience is coherent, so `IsDecline` says nothing about the visibility.
No precedence question against `--default-visibility` arises: that flag is read only under
`--no-prompt`, where the browser leg never runs.

## `--private` stamps a value

An omitted `default_visibility` is not "no default": the server's generated column reads
`COALESCE(default_visibility,'org_public') = 'org_public'`, so a session-start that says nothing
lands as `default:org` — a class two `VisibilitySql` arms admit, one of them provider-independent.
Six of the nine import sources omitted the field under `--private` and left privacy to the closing
`SetVisibilityNoneForAll` pass, which meant minutes of org-visibility on a large import and
permanent exposure for any session whose PUT failed, since those failures are swallowed by design.
The other three stamped `"private"` in their own payload builder, which is why checking one source
found it correct.

`ImportContext.VisibilityStampFor(status)` is now the only place that decides the stamp, and the
chain path resolves the same rule into `chainDefaultVisibility`. The Step-3 default lands on `New`
alone, while `private` is sent on every status because it costs nothing.

**A stamp only decides visibility at creation.** The read model's import-overlap branch — the one a
re-import of an already-closed session takes — omits `default_visibility` from its update, so
re-asserting `private` on a session that already exists is discarded. For anything a run merely
revisits, the closing `visibility=none` pass is the only mechanism, which is why membership in it is
now the in-scope classification set rather than whatever the import concluded: `importedSessionIds`
gains a session only where new work happened, and `privateScopeSessionIds` excludes Copilot, Kiro, Pi
and OpenCode, so a failed routed replay or a chain resume whose session-end POST failed was
privatised by nothing. The bound is status — the scope filter runs before classification and an
excluded source has its status flipped — so `New | Partial | AlreadyLoaded` is the selected-and-
present set and a too-short session is left alone.

**And it happens before the content, not after.** A closing pass guarantees a revisited session ends
up owner-only; it does not stop what this run uploads into it being readable meanwhile, which is the
window the defect is named after. So the in-scope `Partial` and `AlreadyLoaded` sessions are narrowed
ahead of both import phases — `New` is excluded, having nothing to narrow and no row to name — and the
closing pass becomes recovery for a session created during the run.

That pass is fail-closed per session: the write logs and swallows its failures, so a session it could
not narrow is dropped from `chains` and `routed` and counted as a failure, rather than replayed into
while still carrying the audience the user just excluded.

The 2026-07-20 unified-import spec scoped this expansion out while already arguing that post-hoc
privatisation is unsafe for a session that fails mid-stream; this is that argument applied to the
eight other paths.

## The first-run flow's import lane

`kcap setup`'s browser leg now feeds and reads the Import screen. Discovery reports per repository AND
per window, because "how many sessions will this selection import" is a cell and neither margin of a
table gives you one; `ImportDiscoverySummary` buckets both from one pass, and windows are keyed off the
same constant the report travels under, so `--discover`'s own windows and the screen's picker are one
list.

The vendor filter is applied to the sources scanned rather than to the counts afterwards, which is what
makes every reported figure already scoped. **Only an explicit refusal drops a vendor:** the server
normalises an untouched harness out of the decision, so refused and never-offered look identical on the
wire, but this machine knows what it reported — `FirstRunMachineReport.Detected` is the set the screen
could offer, and anything outside it was never offered to refuse.

The scan is gated on the Agents step settling, since its answer is the filter. It runs once; the POST
is retried until the server takes it. The decision then runs two passes, because `--private` is per
invocation, with the shared one followed by an explicit `visibility=org` write — the profile default
produces `default:org`, which is admitted only where the repository owner matches the configured org,
so the default route promises a team can read this and delivers owner-only nearly everywhere.

Polling stops while the import runs, because two live Spectre renderables cannot share a terminal, and
both lanes add their elapsed time back to the poll budget: that budget catches a terminal nobody is
sitting at, and a scan or an upload is work. The decision's timestamp is a cursor rather than a flag,
so widening the window on a second answer runs the wider import while re-confirming runs nothing.
`FirstRunImportAnswer.NoReadableVendors` covers the one otherwise-silent failure — repositories chosen
but no vendor this build can read, where running would report success for history that never moved.

Whether a pass succeeded is read off `ImportRunOutcome`, not the exit code: `HandleImport` returns 0
for a run whose sessions failed, because import is best-effort and the Done grid is where that is
reported. The outcome carries the run's counts plus lost explicit-visibility writes, since a session
the user chose an audience for that still carries the old one is a failure of what they asked for.

## Claude SessionEnd hand-off

Claude Code computes the grace it gives SessionEnd hooks from `settings.json` timeouts only; a
plugin's `hooks.json` timeout is used for matching but never for that computation, so kcap's
SessionEnd hook gets the 1.5 s floor and is killed — after it has already killed the watcher whose
parent-exit watchdog would otherwise have ended the session. The hook therefore reads its payload,
re-invokes itself with `--detached`, pipes the payload to that child and exits, all before the
server-URL git probes and the global spool drain that `Program.cs` runs ahead of every hook. The
continuation runs the unchanged session-end path — spool fallback and `ended_at` idempotency
included — under the 15 s `HookBudget` that used to be the hook's, with its output in the session
log and its own session so neither Claude's abort nor a closing terminal can reach it. Only
SessionEnd is handed off: SubagentStop is already `async` in `hooks.json`, and the others honour
their timeouts.

## Review flows and reviewer selection

Review flows use a vendor-neutral catalog-start v2 protocol: reserved `spec-review`/`code-review`
aliases select an explicit or server-default reviewer independently of the driver. Codex setup
registers `kcap-flows` without auto-approval and tracks only newly-created global TOML entries in
`mcp-ownership-v1.json`, so uninstall preserves manual/customized MCP configuration. Daemons retain
the string unattended-vendor list for compatibility and additionally advertise structured
per-vendor CLI/launcher-policy capabilities. Cursor serves borrowed review context from a
daemon-owned snapshot (dirty tracked and non-ignored untracked files, refreshed between rounds),
because its zero-interaction modes may write; Claude has no borrowed-review containment strategy
and therefore fails closed to an owned worktree.

## Borrowed review: containment

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

## Borrowed review: credentials

The daemon never *looks* for a credential: no keychain read, no prompt, no cache, no persistence, no
default command. It forwards a token the operator exported, or — for a supervised daemon, whose unit
file must not hold a secret — runs the single command the operator configured in
`KCAP_COPILOT_TOKEN_CMD`, and only when an actual borrowed launch needs one. Availability is
deliberately passive (configuration presence, never execution): probing by running the command at
startup would mint a credential nobody asked for, so a configured-but-broken command instead fails at
spawn with the coded `borrowed_review_auth_unavailable`. Service units are written owner-only, and
installation fails rather than leaving one readable.

## Borrowed review: capability advertisement

Borrowed-review capability is **trust-by-default**: a vendor advertises it whenever its runtime
factory declares a containment strategy, for whatever build of the vendor CLI is installed and on
every platform. It is deliberately not gated on the installed binary matching a validated-build
record — a vendor auto-update would then silently withdraw the capability and reviewers would fall
back to a stale committed base. The daemon logs the CLI version it probed at startup (a startup
observation, not a launch-time fact) and does no automated drift detection; a defective vendor
release is handled by a human report and a corrected record. See
`docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md` in kcap-server.

## Launch consent

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

## Local control IPC

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

## Desktop onboarding: mutation safety

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

## Desktop onboarding: wizard and auth façade

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

## Session workspace terminal

**AI-2195** (spec: `docs/superpowers/specs/2026-08-24-ai2195-session-workspace-terminal-design.md`)
attaches a live terminal to the session workspace. `TerminalTabViewModel` opens every workspace in
`Resolving` and **never constructs an attach client until the session's first matching
`AgentStatusDto` arrives**: attaching optimistically would race the has_terminal gate and show a
spurious "no such agent" flash before a genuinely no-terminal session's note could render. `has_terminal`
is authoritative when the daemon sends it; `HostedHarnessCatalog`'s vendor-transport map is only the
fallback for an older daemon that sent null. `AgentAttachClient` linearizes every termination race
through one atomic cause slot, and **detach intent is recorded, never itself a cause**: a terminal
frame the pump already read wins even with a detach pending, so a daemon `Exited` racing a client
`Detach` still resolves `Exited`, not `Detached`; only EOF with detach intent pending settles
`Detached`. Teardown spends at most its first second on the (best-effort, unacknowledged) `Detach`
write, then force-closes the socket regardless of whether that write landed — the tmux-style PTY
dimension clamp is guaranteed to release by roughly that one-second mark on every exit path, not
contingent on graceful pump completion. `WorkspaceTeardownTracker` seals atomically at the shutdown
drain (registration and seal cannot race past the final snapshot); a post-seal `Track` is executed
and observed rather than refused, so a workspace a coordinator builds between the two shutdown passes
still cannot hold a socket open past the drain. The companion guard lives in `NavigationGate`: its
first shutdown pass latches (which also bumps the generation), so `OpenSession` — card click or launch
auto-open alike — rejects from then on in every window, current or later-built.

## Launch and stop command routing

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
