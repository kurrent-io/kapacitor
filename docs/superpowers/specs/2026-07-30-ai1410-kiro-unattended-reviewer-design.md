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

**What that obliges the design to specify** — an isolated home is a filesystem boundary, so everything
the reviewer needs must be deliberately placed and everything else deliberately withheld:

* **Credentials.** Kiro must still authenticate. Determine what under `KIRO_HOME` carries auth and
  place exactly that, or confirm auth lives outside `KIRO_HOME` entirely. *Unresolved — must be probed
  before implementation, and it is the one item that could invalidate this mechanism.*
* **Deliberately excluded:** `settings/mcp.json` (the whole point), the user's agents, and anything
  granting write or flow-starting capability.
* **Placed if required:** only the injected `flow-result` server, which arrives via
  `session/new.mcpServers` rather than the home, so probably nothing.
* **Permissions and cleanup:** owner-only; removed when the reviewer is reaped, including on crash.
* **Concurrency:** one home per reviewer launch, so two concurrent reviews cannot share or race state.
* **Preflight:** `ComputeUnattendedVendors` must not advertise `kiro` until the daemon can create an
  isolated home AND authenticate inside it. A failure here refuses the launch with a coded error rather
  than wedging a round.

Because this touches daemon process launch rather than the installer, it remains the largest item in
the issue — but it is now a known quantity rather than an unknown one.

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
--trust-tools=fs_read,grep,shell     (exact tool names to be confirmed against `kiro-cli chat --help`)
```

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
UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.AutoApprove,
ReviewFlowMcpTransport: AcpReviewFlowMcpTransport.SessionNew,   // measured-honoured
```

Borrowed review stays **off**. It needs a containment-token decision
(`NativeToolClamp` vs `IndependentSnapshot`) grounded in what Kiro's tool clamp actually permits, and
the descriptor's own doc comment is explicit that guessing that token wrong is a wiring bug. Out of
scope here; its own issue once basic reviewing works.

### 5. Auth

`authMethods: []` shows no ACP-mediated auth flow, and a prompt completed with no `KIRO_API_KEY` set —
but on one machine, which may have had cached credentials. That proves **no pre-checkable signal**, not
"no auth requirement" and not "no tier gate".

So: build no pre-check, and fail on the real launch/prompt error with a coded diagnostic. And the
criterion "a tier/auth failure surfaces a coded error" needs a reproducible way to exercise it —
an isolated unauthenticated `KIRO_HOME` if that reproduces, otherwise a fake ACP peer returning the
measured auth-failure shape. Without one it cannot be closed.

## Verification

- [ ] `start_review_flow(kind="spec-review", vendor="kiro")` completes findings→clean unattended
- [ ] Same for `code-review`
- [ ] **Zero** human-routed interactions during the round
- [ ] `flow-result` is callable without approval (the §1 namespacing question)
- [ ] `kcap-flows` is NOT present in the reviewer's session (the §"blocker" suppression works)
- [ ] Reviewer session captured and reaped; no orphan
- [ ] Runs at the vendor default model — override is explicitly out of scope
- [ ] A tier/auth failure surfaces a coded error instead of a hung round, exercised by a defined setup
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
