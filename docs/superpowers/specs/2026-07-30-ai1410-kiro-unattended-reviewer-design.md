# AI-1410 — Kiro CLI as an unattended review-flow reviewer

**Status:** re-specced 2026-07-30 against `kiro-cli 2.12.1` and current `origin/main` (`bc09eac`).
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1400 (reviewer choice in review flows)
**Depends on:** AI-1404 (Kiro hosting) · AI-1407 (ACP reviewer foundation) · AI-1402 (vendor selection)
**Companion spec:** `2026-07-30-ai1404-kiro-acp-hosted-agent-design.md` — all measured protocol facts
live there and are not repeated.

## The go/no-go, resolved: GO

The original design listed *"verify `mcpServers` honoring (go/no-go for the flow-result channel)"* as
an open question. It is now measured and answered — but the FIRST answer was on insufficient evidence
and is recorded here as a caution.

An initial probe showed `_kiro.dev/mcp/server_initialized` naming an injected stdio server, and an
earlier draft of this spec called GO on that. **That was premature**: `server_initialized` proves a
server process started, not that its tools are discoverable or invocable — a tool can be missing from
`tools/list`, refused by trust policy, mis-namespaced, or fail at `tools/call`.

Re-probed at the call level: a purpose-built stdio server exposing one uniquely named tool was passed
in `session/new.mcpServers`, Kiro was asked to call it, and **the server's own log recorded
`initialize` → `tools/list` → `tools/call`**, with the tool's nonce reaching the model and the turn
ending `end_turn` under `--trust-all-tools`. That is a real GO.

So Kiro can carry the injected `flow-result` channel over `session/new`, meaning
`ReviewFlowMcpTransport: SessionNew` — the Cursor route, not Copilot's `--additional-mcp-config`
workaround. **Without this the whole issue would have been blocked**, because results arrive *only*
via the `flow-result` tool (markers are inert since AI-1190) and Kiro has no Copilot-style
config-preload flag.

Worth stating because it looks contradictory: Kiro advertises `mcpCapabilities: {http:true, sse:false}`
and the Copilot descriptor reads that same shape as *"stdio servers stay disabled."* That is an
empirical finding about Copilot, not a rule about ACP. Kiro honours stdio without advertising it.

## The blocker — and my first proposed mechanism was wrong

**Kiro inherits pre-configured MCP servers into every ACP session.** Measured: with `mcpServers: []`,
Kiro still initialized `kcap-flows`, `kcap-review`, `kcap-sessions` and `kcap-memory`.

For an unattended reviewer this is not cosmetic — **`kcap-flows` would let a reviewer start nested
review flows.** The `review-flows` skill tells hosted reviewers not to, and the platform strips MCP
servers from hosted reviewers precisely so it *cannot*. Copilot handles this with
`--disable-builtin-mcps`; Kiro has no equivalent flag.

### Why the obvious fix does not work

An earlier draft proposed a purpose-built minimal agent selected via `--agent kcap-reviewer`, reasoning
that Kiro's MCP servers come from the agent config. **That reasoning was wrong.**
`PluginCommand.InstallKiro` registers those four servers in GLOBAL `~/.kiro/settings/mcp.json` — and
inspecting that file confirms it contains exactly `kcap-review`, `kcap-sessions`, `kcap-flows`,
`kcap-memory`. Both `PluginCommand.cs` and `KiroPaths.cs` document that file as independent of
`~/.kiro/agents/kcap.json`. So **selecting a different agent may leave `kcap-flows` fully exposed.**

`session/setMode` remains too late (servers are already initialized by `session/new`), but that only
rules out one alternative; it does not rescue the agent approach.

### Decision: a daemon-owned reviewer `KIRO_HOME` (measured)

**Measured:** launching `kiro-cli acp` with `KIRO_HOME` pointed at an empty temporary directory
initializes **zero** MCP servers — `kcap-flows` absent — while `initialize` and `session/new` both
still succeed. So the containment mechanism is settled, and it is not agent selection: it is an
isolated `KIRO_HOME` owned by the daemon for reviewer launches.

This is a decision, not a candidate list. The prior draft's matrix is collapsed to the one cell that
mattered.

**Validated for the operation this issue actually performs** — not just handshake. In an isolated empty
home, with the probe MCP server injected via `session/new.mcpServers`:

* `session/new` succeeded;
* `session/prompt` completed `end_turn`;
* the probe server logged a real **`tools/call`** and its nonce reached the model;
* **no authentication error of any kind** — nothing on stderr, no auth frame, no failure.

**Therefore Kiro's credentials do NOT live under `KIRO_HOME`,** for the auth configuration measured.
That collapses the *inbound* half of this section: nothing secret has to be copied INTO a reviewer
home, so the design needs no credential source, no refresh/expiry handling, no atomic secret
materialization, and no symlink validation of copied secrets.

**But an earlier draft then concluded "a reviewer home contains nothing sensitive", and that is
wrong.** The inbound direction is not the only one. `KiroPaths.ConfigRoot` reads `KIRO_HOME` first, so
`SessionsDir()` resolves to `{KIRO_HOME}/sessions/cli` — meaning **Kiro writes the reviewer's own
conversation JSONL into the isolated home**, and that transcript contains the review context it was
given: the caller's diff, source excerpts, and the reviewer's findings. A reviewer home is therefore
**write-sensitive even though it is read-empty**, and it is created on a shared multi-tenant daemon
host.

The practical difference: permissions and cleanup are **security requirements with a stated threat**,
not tidiness. Concretely —

* **Contents at creation:** nothing. Create it empty. `settings/mcp.json` and the user's agents are
  excluded by simply not being there, which is why the mechanism works.
* **Permissions:** `0700`, owner-only, set **at creation** rather than after — a world-readable window
  between `mkdir` and `chmod` is exactly long enough to leak a transcript on a shared host. Do not
  place the home inside a world-writable shared temp path that another user could pre-create or
  substitute; root it under a daemon-owned directory.
* **Concurrency:** one home per reviewer launch, so concurrent reviews cannot race shared state — and,
  now that the home is transcript-bearing, so one caller's review context is never readable from
  another caller's reviewer home.
* **Cleanup, including after a daemon crash** — see §4b. This is a transcript-disposal requirement, not
  merely a disk-hygiene one, which is why §4b specifies reaping the process before deleting the tree
  rather than deleting opportunistically.

Because this touches daemon process launch rather than the installer it is still the largest item here,
but it is now a small, measured one.

## Acceptance criterion that must be rewritten

The issue currently says:

> `start_review_flow(kind="spec-review", vendor="kiro", model="<priced-model>")` completes a real
> round unattended end-to-end with the model honored.

**"priced-model" is very likely unsatisfiable, but state the reason precisely.** ACP surfaced no
canonical token counters (measured). It does **not** follow that Kiro cost is unobservable:
`KiroUsage.cs` already reads billing `metering_usage` credits from Kiro's on-disk session metadata.

**Decision: drop the model clause from the acceptance criterion entirely.** Model override is out of
scope (see §4a), so the criterion becomes:

> `start_review_flow(kind="spec-review", vendor="kiro")` completes a real round unattended end-to-end.

That is closable today. It removes the pricing-clamp question from this issue's critical path rather
than resolving it by assertion: whether the clamp needs canonical tokens or accepts billing credits is a
real question, but it belongs to the model-override follow-up, and `KiroUsage.cs`'s existing credits path
means the answer is not simply "no data".

## Design

### 1. Trust at spawn

`UnattendedTrustArgv` on the Kiro descriptor. Start scoped rather than blanket:

```
--trust-tools=<read-only set>        (exact Kiro tool names UNMEASURED — see below)
```

**`shell` must NOT be in this list**, and an earlier draft had it. Trusting `shell` means a write or an
outside-home command executes WITHOUT emitting a permission frame — so `Fail` never fires and the
"read-only reviewer" boundary is fiction. That also means the negative acceptance criterion must verify
that a forbidden **effect** is prevented (ask the reviewer to actually write a file and to read outside
the worktree, then assert neither happened), not merely that a frame was handled.

If the remaining read-only tools cannot review a repository, the answer is a real command/filesystem
sandbox or a command-level read allowlist — not re-adding `shell`. **The exact Kiro tool names and trust
semantics are UNMEASURED and must be probed before this section is implementable.**

A reviewer reads and reports; it does not need write. **There is no `--trust-all-tools` fallback.** An
earlier draft offered one, which contradicted §2 and would have widened the hole it describes: if the
scoped set proves insufficient, the correct outcome is a failing reviewer to investigate, not a
blanket-trusted one.

Note Kiro's warning observed during the memory-cert work:
`--trust-tools arg for custom tool ... needs to be prepended with @{MCPSERVERNAME}/`. So MCP-provided
tools are namespaced in the trust list, which matters the moment `flow-result` is injected: the
reviewer must be able to *call* `flow-result` without a prompt, and that likely needs
`@kcap-flow-result/...` in the trust set. **Verify this explicitly** — a reviewer that cannot call
`flow-result` without approval cannot deliver a result at all, and would present as a silent timeout.

### 2. Interaction policy — an earlier draft of this spec had a security hole here

The earlier proposal was `AutoApprove` (the Copilot posture) on the grounds that Kiro's known upstream
prompt leaks (#7398) make a frame expected rather than exceptional, so `Fail` would be flaky.

**That combination is unsafe and it undoes §1.** `AcpInteractionBridge` documents that `AutoApprove`
selects an allow option and **does not inspect the tool**. So on exactly the fallback path this design
expects to exercise, a leaked request for a tool deliberately excluded from `--trust-tools` — a
write-capable shell command, an out-of-worktree path, anything — is auto-approved. Scoped trust would
provide no protection whatsoever, and "zero human-routed interactions" would not detect it: the frames
were handled, just not safely.

**Decision: `Fail`** — the Cursor posture. An allowlist-aware policy would need an authoritative tool-ID
allowlist, Kiro/MCP namespace normalisation, shell-command and path handling, and unknown-frame
treatment; that is a foundation feature, not a Kiro detail, and inventing it here would leave a matcher
nobody has specified.

`Fail` accepts the cost the earlier draft was avoiding: if upstream's prompt leaks (#7398) fire, Kiro
reviewer rounds fail rather than silently auto-approving. **That is the correct direction** — a visibly
flaky reviewer is a bug report; a tool-blind auto-approver is a security hole that passes its own tests.
If the leaks prove frequent enough to make Kiro unusable, the answer is an allowlist-aware policy
specified as its own piece of work, not `AutoApprove`.

Acceptance needs **negative** criteria, which the earlier draft lacked entirely: an untrusted,
write-capable, or out-of-bounds request must be denied or must terminate the reviewer, while the known
safe read and `flow-result` requests still succeed. Without those, this policy is untested by
construction.

### 3. Prompt shape

`promptCapabilities.embeddedContext: false` (measured). Kiro cannot take embedded context resources,
so AI-1407's prompt folding must deliver review context as **plain text** for Kiro. Confirm the
foundation does not assume embedded context for any vendor; if it does, that is a foundation fix, not
a Kiro one.

### 4. Descriptor changes

Flip on the AI-1404 descriptor:

```csharp
UnattendedTrustArgv: [/* §1 */],
SupportsUnattended:  true,
UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,   // §2 — NOT AutoApprove
ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,   // measured-honoured
```

Borrowed review stays **off**. It needs a containment-token decision
(`NativeToolClamp` vs `IndependentSnapshot`) grounded in what Kiro's tool clamp actually permits, and
the descriptor's own doc comment is explicit that guessing that token wrong is a wiring bug. Out of
scope here; its own issue once basic reviewing works.

### 4a. Model override, and what the daemon may advertise

Model override is **out of scope for this issue** — AI-1404 ships `NoOpModelSelector.Instance` because
`session/set_config_option` is unproven on Kiro and the selector fails silently when it does not take.
This section exists because two earlier references pointed at a "§4a" that was never written, so the
decision had no home and could be re-litigated by whoever implemented it.

The load-bearing part is not the deferral, it is **refusing rather than ignoring**:

* Do **not** wire `AcpHostedAgentRuntimeFactory.ReviewerModelResolver` for Kiro. It is `null` at this
  base, which is why the server already refuses an ACP review-flow model override. That refusal is the
  correct behaviour and must be preserved deliberately, not inherited by accident.
* A Kiro reviewer launched with a caller-supplied model must **fail with a coded error**, never accept
  the request and run the vendor default. A silently-ignored `model=` is the worst outcome available
  here: the round completes, the result looks authoritative, and nothing anywhere records that the
  requested model was not the model that reviewed the code.
* Correspondingly, `ResolveDefaultModel: _ => null` and `NoOpModelSelector` must both hold. Per AI-1404
  Premise 3, `ResolveDefaultModel: null` alone is **not** sufficient — `ResolveRequestedModel`
  prioritises `RuntimeStartContext.Model`, so a dashboard-supplied model still reaches a live selector.

**Availability advertisement is static, and must be gated on resolution, not on the descriptor
existing.** `SupportsUnattended: true` is what makes `vendor="kiro"` selectable as a reviewer, and a
daemon that advertises Kiro on a host where `kiro-cli` is absent converts a clean
`no_daemon_available` into a launch failure mid-round. Advertise the reviewer capability only when
`CliResolver.Exists(KiroPath)` — the same gate AI-1404 applies to interactive hosting.

### 4b. Reviewer-home lifecycle and crash cleanup

Because the home is transcript-bearing (see the isolated-`KIRO_HOME` decision above), disposal is a
security requirement. This is the one item that touches daemon process launch, so specify it exactly:

* **Naming:** `kcap-kiro-reviewer-<daemonEpoch>-<launchId>` under a **daemon-owned** root, not directly
  in the shared system temp dir. `daemonEpoch` is fixed once per daemon process start; `launchId` is
  per reviewer launch.
* **Startup sweep, epoch-keyed:** on daemon start, delete every `kcap-kiro-reviewer-*` directory whose
  epoch is **not** the current epoch. This is what recovers from a crash or `SIGKILL`, and the epoch key
  is what makes it safe for a second daemon on the same host — a sweep that deleted every matching
  directory would delete a *live* peer's reviewer home mid-review.
* **Reap before delete:** terminate the reviewer process and confirm exit before deleting its tree.
  Deleting under a live Kiro leaves it writing transcript lines into an unlinked or recreated path, and
  on a crash-recovery pass the owning process may still be alive.
* **No symlink following** when deleting, and **assert the resolved path is still inside the daemon root**
  before recursing. Both are the standard recursive-delete hazards; the transcript content is the reason
  they matter here rather than being theoretical.
* **Failure handling:** log and continue. A home that cannot be deleted must not fail the review round
  or block daemon startup — but it must be logged at warning with the path, because a persistent
  failure is undisposed review context accumulating on disk.
* **The installer is not involved, and must not be changed.** It is tempting to "fix" the blocker by
  having `PluginCommand.InstallKiro` stop registering the four servers in global
  `~/.kiro/settings/mcp.json`, or by removing them at reviewer launch. Both are wrong: those servers
  are what make kcap work for the user's own interactive Kiro sessions, and mutating a user's global
  config as a side effect of someone else's review flow is a far worse failure than the one being
  solved. Containment stays entirely inside the daemon-owned home.

### 5. Auth

`authMethods: []` shows no ACP-mediated auth flow, and a prompt completed with no `KIRO_API_KEY` set —
but on one machine, which may have had cached credentials. That proves **no pre-checkable signal**, not
"no auth requirement" and not "no tier gate".

So: build no pre-check, and fail on the real launch/prompt error with a coded diagnostic.

**The acceptance criterion "a tier/auth failure surfaces a coded error" cannot be closed as written, and
an earlier draft of this section proposed two ways to exercise it that do not exist.** Both are now
ruled out on evidence:

* *"An isolated unauthenticated `KIRO_HOME` if that reproduces"* — **measured not to reproduce.** The
  isolated-empty-home probe completed `initialize`, `session/new` and a full `session/prompt` to
  `end_turn` with no auth error at all. That is the same measurement that proved credentials live
  outside `KIRO_HOME`; it necessarily also means an empty home does not produce an unauthenticated Kiro.
* *"Otherwise a fake ACP peer returning the measured auth-failure shape"* — **no auth-failure shape was
  ever measured.** Not one auth error was observed in any probe. Specifying a fake peer would mean
  inventing a frame shape and then asserting our handling of our own invention: a test that passes by
  construction and proves nothing about Kiro.

**Decision: drop the auth-specific criterion from this issue** and replace it with the vendor-agnostic
property that is actually testable — *a reviewer whose launch or first prompt fails surfaces a coded
error rather than hanging the round* — exercised with a synthetic non-auth failure (an unresolvable
binary path, and a peer that exits before responding to `initialize`). That covers the real risk the
criterion was reaching for, which is a **wedged round**, and it does so without a fabricated fixture.

If a genuine auth/tier failure is ever observed in the field, capture its shape then and add the
specific assertion. Until it is observed, there is nothing to assert against.

## Verification

- [ ] `start_review_flow(kind="spec-review", vendor="kiro")` completes findings→clean unattended
- [ ] Same for `code-review`
- [ ] **Zero** human-routed interactions during the round
- [ ] `flow-result` is callable without approval (the §1 namespacing question)
- [ ] `kcap-flows` is NOT present in the reviewer's session (the §"blocker" suppression works)
- [ ] Reviewer session captured and reaped; no orphan
- [ ] Runs at the vendor default model — override is explicitly out of scope
- [ ] A caller-supplied model is **refused with a coded error**, not silently ignored (§4a)
- [ ] A failed launch / failed first prompt surfaces a coded error instead of a hung round, exercised by
      a synthetic non-auth failure — unresolvable binary, and a peer that exits before `initialize`
      (§5; the auth-specific criterion is deliberately dropped as unclosable)
- [ ] The reviewer home is created `0700` at creation time, under a daemon-owned root (§4b)
- [ ] A stale reviewer home from a *previous* daemon epoch is swept at startup, and a *current*-epoch
      home belonging to a live peer is NOT (§4b)
- [ ] The reviewer process is reaped before its home is deleted (§4b)
- [ ] The user's global `~/.kiro/settings/mcp.json` is byte-identical after a reviewer round (§4b —
      containment never mutates user config)
- [ ] **Negative:** an untrusted / write-capable / out-of-worktree request is denied or reaps the
      reviewer (see §2) — not merely absent
- [ ] The effective callable tool surface excludes global servers, inspected directly
- [ ] A missing reviewer configuration refuses the launch with a coded error rather than wedging

## Out of scope

- Borrowed review for Kiro (containment-token decision).
- Model override (see §4a) — its own follow-up.
- Kiro **canonical token** reporting — absent from ACP; billing credits remain available via `KiroUsage`.
- `--agent-engine` pinning — deliberately unpinned by AI-1404.
- Fixing upstream trust-flag prompt leaks.
