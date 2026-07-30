# AI-1410 — Kiro CLI as an unattended review-flow reviewer

**Status:** re-specced 2026-07-30 against `kiro-cli 2.12.1` and current `origin/main` (`bc09eac`).
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1400 (reviewer choice in review flows)
**Depends on:** AI-1404 (Kiro hosting) · AI-1407 (ACP reviewer foundation) · AI-1402 (vendor selection)
**Companion spec:** `2026-07-30-ai1404-kiro-acp-hosted-agent-design.md` — all measured protocol facts
live there and are not repeated.

## The go/no-go, resolved: GO

The original design listed *"verify `mcpServers` honoring (go/no-go for the flow-result channel)"* as
an open question. It is now **measured and answered**: a stdio MCP server passed in
`session/new.mcpServers` (`command: kcap`, `args: [mcp, memory]`) produced
`_kiro.dev/mcp/server_initialized` naming that server.

So Kiro can carry the injected `flow-result` channel over `session/new`, meaning
`ReviewFlowMcpTransport: SessionNew` — the Cursor route, not Copilot's `--additional-mcp-config`
workaround. **Without this the whole issue would have been blocked**, because results arrive *only*
via the `flow-result` tool (markers are inert since AI-1190) and Kiro has no Copilot-style
config-preload flag.

Worth stating because it looks contradictory: Kiro advertises `mcpCapabilities: {http:true, sse:false}`
and the Copilot descriptor reads that same shape as *"stdio servers stay disabled."* That is an
empirical finding about Copilot, not a rule about ACP. Kiro honours stdio without advertising it.

## The blocker the original design did not know about

**Kiro auto-loads its agent config's MCP servers into every ACP session.** Measured: with
`mcpServers: []`, Kiro still initialized `kcap-flows`, `kcap-review`, `kcap-sessions` and
`kcap-memory` from `~/.kiro/agents/kcap.json`.

For an unattended reviewer that is not cosmetic — **`kcap-flows` would let a reviewer start nested
review flows.** The `review-flows` skill already tells a hosted reviewer not to, and the platform
strips MCP servers from hosted reviewers precisely so it *cannot*. Copilot handles this with
`--disable-builtin-mcps` in its unattended trust argv. **Kiro has no equivalent flag.**

Options, in preference order:

1. **`--agent <name>` with a purpose-built minimal reviewer agent.** Kiro's MCP servers come from the
   agent config, and `session/new` confirmed `availableModes` are agents. A `kcap-reviewer` agent
   carrying no MCP servers is the mechanism Kiro actually gives us. Cost: `kcap plugin install` must
   write a second agent file, and the reviewer launch must pin it.
2. **ACP `session/setMode`** to switch to a leaner mode after `session/new` — worse: the servers are
   already initialized by then.
3. **Ship without suppression.** Rejected. It hands a reviewer the tool that starts reviewers.

**This decision must be made before implementation**, and option 1 changes the installer, so it is
the largest single piece of work in this issue — larger than the descriptor change.

## Acceptance criterion that must be rewritten

The issue currently says:

> `start_review_flow(kind="spec-review", vendor="kiro", model="<priced-model>")` completes a real
> round unattended end-to-end with the model honored.

**"priced-model" is unsatisfiable.** Kiro's ACP reports no token usage at all (measured — see the
companion spec), so nothing prices a Kiro reviewer round and the AI-1402 pricing clamp has no input.
The criterion should assert the model is *honoured*, and state explicitly that cost is not observable
for Kiro. Leaving "priced" in makes the issue unclosable for a reason unrelated to the work.

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

### 2. Interaction policy

`UnattendedInteractionPolicy: AutoApprove` — the Copilot posture, not Cursor's `Fail`.

Cursor earns `Fail` because its own flags are contractually sufficient to suppress interaction frames,
so a frame means regression. Kiro has *known upstream prompt leaks* (#7398), so a frame is expected
rather than exceptional, and failing the round on one would make Kiro reviewers flaky by design.
Revisit once the leaks are fixed upstream.

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

`authMethods: []` and a real prompt completed with no `KIRO_API_KEY` (measured), so there is no auth
handshake to satisfy and no tier pre-check to build. A launch-time failure must still fail fast with a
coded diagnostic rather than wedge a round — but that is the generic path, not Kiro-specific.

## Verification

- [ ] `start_review_flow(kind="spec-review", vendor="kiro")` completes findings→clean unattended
- [ ] Same for `code-review`
- [ ] **Zero** human-routed interactions during the round
- [ ] `flow-result` is callable without approval (the §1 namespacing question)
- [ ] `kcap-flows` is NOT present in the reviewer's session (the §"blocker" suppression works)
- [ ] Reviewer session captured and reaped; no orphan
- [ ] Model override honoured — asserted as honoured, not as priced
- [ ] A tier/auth failure surfaces a coded error instead of a hung round

## Out of scope

- Borrowed review for Kiro (containment-token decision).
- Kiro cost/token reporting — absent upstream; AI-888 stands.
- `--agent-engine` pinning — deliberately unpinned by AI-1404.
- Fixing upstream trust-flag prompt leaks.
