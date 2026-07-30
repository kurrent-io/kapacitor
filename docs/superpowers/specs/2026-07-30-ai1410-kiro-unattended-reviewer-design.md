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

### Required before implementation: a source-isolation matrix

The mechanism cannot be chosen from what is currently known. Run a matrix against a temporary
`KIRO_HOME`:

| global `settings/mcp.json` | selected agent declares MCP | expected |
|---|---|---|
| present | none | does `kcap-flows` still initialize? |
| present | minimal agent | does agent selection suppress global? |
| absent | none | baseline — nothing inherited |
| absent | minimal agent | agent-only inheritance |

Then pick a mechanism that **demonstrably excludes global servers**. Candidates, none yet validated:
an isolated reviewer `KIRO_HOME`; a launch-time config boundary; or upstream support for suppression.

Acceptance must inspect the **effective callable tool surface** — what the reviewer can actually
invoke — not merely which mode was selected. `server_initialized` absence is necessary but not
sufficient, for the same reason its presence was not sufficient above.

**Estimate correction.** An earlier draft called this "a second agent file + descriptor change" and
the largest piece of work. It is larger and less certain than that: the mechanism is unknown, an
isolated `KIRO_HOME` would touch daemon process launch rather than just the installer, and the matrix
itself is real investigation. This is the item that should gate scheduling.

## Acceptance criterion that must be rewritten

The issue currently says:

> `start_review_flow(kind="spec-review", vendor="kiro", model="<priced-model>")` completes a real
> round unattended end-to-end with the model honored.

**"priced-model" is very likely unsatisfiable, but state the reason precisely.** ACP surfaced no
canonical token counters (measured). It does **not** follow that Kiro cost is unobservable:
`KiroUsage.cs` already reads billing `metering_usage` credits from Kiro's on-disk session metadata.

So the rewrite must say which input the AI-1402 pricing clamp actually needs. If it requires canonical
token counts against a priced model, Kiro cannot satisfy it and the criterion should assert the model is
*honoured* while recording that token-based cost is unavailable. If credits suffice, the criterion may
be satisfiable through the existing sidecar. **Resolve this against the clamp's real requirement before
rewriting the issue** — do not assert "no cost data" when a credits path exists.

## Design

### 1. Trust at spawn

`UnattendedTrustArgv` on the Kiro descriptor. Start scoped rather than blanket:

```
--trust-tools=fs_read,grep,shell     (exact tool names to be confirmed against `kiro-cli chat --help`)
```

A reviewer reads and reports; it does not need write. If the scoped set proves insufficient — or if
upstream's trust-flag prompt leaks (issues #7398, #7483) fire anyway — fall back to
`--trust-all-tools`, and rely on the interaction policy below rather than on the flag alone.

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

**Requirement:** either an allowlist-aware unattended policy that approves only frames matching the
scoped trust set, or `Fail` on any non-matching frame. Falling back to `--trust-all-tools` makes the
gap wider, not narrower, and is not an acceptable resolution.

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
- [ ] Model override honoured — via `session/set_config_option`, and only once an ACP
      `ReviewerModelResolver` exists
- [ ] A tier/auth failure surfaces a coded error instead of a hung round, exercised by a defined setup
- [ ] **Negative:** an untrusted / write-capable / out-of-worktree request is denied or reaps the
      reviewer (see §2) — not merely absent
- [ ] The effective callable tool surface excludes global servers, inspected directly
- [ ] A missing reviewer configuration refuses the launch with a coded error rather than wedging

## Out of scope

- Borrowed review for Kiro (containment-token decision).
- Kiro cost/token reporting — absent upstream; AI-888 stands.
- `--agent-engine` pinning — deliberately unpinned by AI-1404.
- Fixing upstream trust-flag prompt leaks.
