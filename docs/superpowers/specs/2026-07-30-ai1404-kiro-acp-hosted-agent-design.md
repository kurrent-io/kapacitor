# AI-1404 — Kiro CLI as an ACP hosted agent

**Status:** re-specced 2026-07-30 against `kiro-cli 2.12.1` and current `origin/main` (`bc09eac`).
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1399 (multi-vendor ACP hosted agents)
**Template:** the shipped Copilot child (AI-1403) and the Cursor descriptor — read those, not the
2026-07-19 sketch on the issue, which predates both.

## Why this was re-specced

The issue's original design was written before AI-1401, AI-1403 and AI-1407 shipped, and it carried
three unverified premises. Two were wrong. Everything below marked **measured** was observed directly
against a live `kiro-cli acp` process, not inferred.

## Measured facts

`kiro-cli acp` exists — `Start Agent Client Protocol (ACP) agent`. Its flags:

```
--agent <AGENT>            agent to use for the first session
--model <MODEL>            model ID for the first session
--effort <EFFORT>          low | medium | high | xhigh | max
-a, --trust-all-tools      auto-approve all tool permission requests
--trust-tools <NAMES>      trust only this set
--agent-engine <ENGINE>    v1 | v2 (default) | v3
```

`initialize` response (**measured**):

```json
{"protocolVersion":1,
 "agentCapabilities":{
   "loadSession":true,
   "promptCapabilities":{"image":true,"audio":false,"embeddedContext":false},
   "mcpCapabilities":{"http":true,"sse":false},
   "sessionCapabilities":{},
   "auth":{}},
 "authMethods":[],
 "agentInfo":{"name":"Kiro CLI Agent","version":"2.15.2"}}
```

A full `initialize` → `session/new` → `session/prompt` loop completed with `stopReason: end_turn`.

### Premise 1 — "headless requires a paid-tier `KIRO_API_KEY`". NOT REPRODUCED.

`authMethods` is empty, no auth method is advertised, and a real prompt completed with **no
`KIRO_API_KEY` set in the environment**. So there is no auth handshake and no tier gate at
initialize/new/prompt on the account this was measured with.

**Hedge this consistently — an earlier draft of this spec did not.** `authMethods: []` shows there is
no ACP-mediated auth flow. A prompt succeeding without the variable on ONE machine may simply be using
cached credentials, so it proves neither "no auth requirement" nor "no tier gate".

The justified conclusion is only this: **there is no reliable protocol or API-key signal to build a
pre-check on**, so fail on the actual launch/prompt error instead. Anywhere this spec or AI-1410 says
"no gate exists", read it as "no pre-checkable gate signal exists".

### Premise 2 — "ACP usage reporting may close Kiro's token gap". IT DOES NOT.

Across a complete prompt turn, **zero** frames carried any of `usage`, `tokens`, `inputTokens`,
`outputTokens`, `tokenUsage` or `totalTokens`. The notification surface was:

```
  5  _kiro.dev/commands/available
  4  _kiro.dev/mcp/server_initialized
  3  _kiro.dev/metadata
  1  _kiro.dev/subagent/list_update
  3  session/update
```

So ACP adds no **canonical token counters**, and AI-888's finding stands for tokens.

**Do not over-read this into "Kiro cost is unobservable" — that would be wrong.** `KiroUsage.cs`
already reads billing `metering_usage` credits from Kiro's on-disk session metadata. What is measured
here is narrower: no canonical token fields appeared in the frames inspected, for this version, on this
turn. It does not establish absence from `session/load`, other extensions, or the on-disk sidecar.

The honest statement is: ACP gives no token counts, credits remain available via the existing sidecar
path, and any downstream requirement should say which of the two it actually needs.

### Premise 3 — model selection. PARTLY CONFIRMED; my first reading was wrong.

`--model` is a real spawn flag, but that is **not** the mechanism `ConfigOptionModelSelector` uses.
The selector parses `session/new`'s `models.availableModels` and then sends
`session/set_config_option` — a spawn flag proves nothing about it.

**Measured:** `session/new` returns BOTH `modes` and `models`, so the selector's read half has the
shape it needs. Its write half (`session/set_config_option` actually taking effect) is **still
unverified** and must be measured before relying on it.

> **Resolved 2026-08-05** (the follow-up this premise deferred to;
> `docs/probes/2026-08-05-kiro-model-override/`, kiro-cli 2.16.0): `session/set_config_option`
> **does not exist on Kiro** — it answers `-32601 Method not found`, so `ConfigOptionModelSelector`
> can never work there and the silent-failure risk this premise guarded against was real. The
> stabilized `session/set_model {sessionId, modelId}` succeeds instead **and takes effect at the
> turn level**: the next turn's backend inference request carried the requested `modelId` verbatim,
> the reply self-identified as it (a different vendor family from the account's default), and
> Kiro's persisted session state recorded it with model-specific parameters. Kiro now carries
> `SetModelSelector` + `DaemonConfig.KiroModel` (`KCAP_KIRO_MODEL`, default null so
> zero-configuration behaviour is unchanged). The rest of this premise — `NoOpModelSelector`
> mechanics, `ResolveDefaultModel: null` being insufficient alone — is kept as written for the
> historical record, and the reviewer-model paragraph below still stands: `ReviewerModelResolver`
> stays `null`, so review-flow model override remains a separate follow-up.

**Decision: Kiro ships with `NoOpModelSelector` and no model override in either issue.** An earlier draft
kept `ConfigOptionModelSelector` while AI-1410 deferred override — two implementers could then ship
opposite behaviour, each following part of the spec. Note that `ResolveDefaultModel: null` is NOT
sufficient on its own: `ResolveRequestedModel` prioritises `RuntimeStartContext.Model`, so a
dashboard-supplied model would still reach a live selector. The selector itself has to be the no-op.

Also,
`AcpHostedAgentRuntimeFactory.ReviewerModelResolver` is `null` at this base, so the daemon advertises
no reviewer-model-resolution capability and the server refuses an ACP review-flow model override
today. Reviewer model override is therefore an AI-1410 dependency, not a free inheritance.

## New facts the original design did not have

**`--agent-engine v1|v2|v3`, default `v2`.** A spawn-time behavioural switch nobody had accounted
for. **Decision: do not pass it.** Pinning a version means owning an upgrade treadmill and diverging
from what a user gets interactively; the default is what Kiro tests. Revisit only if a measured
behavioural difference forces it.

**`session/new` returns BOTH `modes` and `models`.** ACP "modes" are Kiro *agents*: `availableModes`
was `[kcap, kiro_default, kiro_planner]` with `currentModeId: kcap` — our own installed agent. A
`models` object is present alongside it, which is what `ConfigOptionModelSelector` reads (see Premise 3).
An earlier probe of mine printed a truncated response and I wrongly concluded `models` was absent;
stated here so the correction is on the record.

**Kiro inherits pre-configured MCP servers into an ACP session.** With `mcpServers: []` in
`session/new`, Kiro still initialized `kcap-flows`, `kcap-review`, `kcap-sessions` and `kcap-memory`.

**Where from matters, and an earlier draft of this spec guessed wrong.** It attributed them to
`~/.kiro/agents/kcap.json`. They are in fact registered by `PluginCommand.InstallKiro` into GLOBAL
`~/.kiro/settings/mcp.json` — verified by inspecting that file, which contains exactly those four
names. Global settings are documented as independent of the agent file, so *switching agents does not
suppress them*. Harmless for interactive hosting; a hard blocker for the unattended reviewer, and
AI-1410 now owns finding a mechanism that demonstrably excludes global servers.

**`promptCapabilities.embeddedContext: false`** (Copilot: `true`). Kiro cannot take embedded context
resources in a prompt; context must be plain text. Interactive hosting does not care. AI-1410 does.

**`sessionCapabilities: {}`** — no `list` capability (Copilot advertises `{list:{}}`). Nothing today
depends on session listing; noted so its absence is not later mistaken for a regression.

**`agentInfo.version` is `2.15.2` while `kiro-cli --version` reports `2.12.1`.** The ACP agent reports
a different version line from the CLI wrapper. Log the ACP-reported version when diagnosing, and do
not assume the two match.

## Design

One descriptor plus a factory registration, exactly the Cursor/Copilot shape:

```csharp
public static readonly AcpVendorDescriptor Kiro = new(
    Vendor:              "kiro",
    ResolveBinaryPath:   cfg => cfg.KiroPath,
    ResolveDefaultModel: _ => null,     // no KiroModel until model override is in scope
    Argv:                ["acp"],
    UnattendedTrustArgv: [],            // AI-1410 owns this; empty keeps unattended off
    SupportsUnattended:  false,         // flipped by AI-1410
    ModelSelector:       NoOpModelSelector.Instance,   // see Premise 3 — override deferred
    SupportsMcpServers:  true,          // measured: stdio servers in session/new ARE honoured
    SupportsBorrowedReviewFlow: false   // AI-1410 decides; default off is the safe direction
);
```

`SupportsMcpServers: true` is the one line that deserves scrutiny, because the Copilot descriptor
says the opposite for itself on what looks like the same evidence: *"ACP itself advertises MCP over
http/sse only, so interactive `session/new` stdio servers stay disabled."* Kiro advertises the same
`{http, sse}` shape — but a **measured** probe passing a stdio server (`command: kcap`,
`args: [mcp, memory]`) produced `_kiro.dev/mcp/server_initialized` naming that server. Kiro honours
stdio despite not advertising it. Copilot's line is an empirical finding about Copilot, not a rule
about ACP, and must not be generalised.

**And `server_initialized` alone would NOT have been sufficient evidence** — it proves a server
started, not that its tools are discoverable or callable. A tool can be omitted from `tools/list`,
refused by trust policy, mis-namespaced, or fail at `tools/call`. So the claim was re-probed properly:
a purpose-built stdio server exposing one uniquely named tool was injected, Kiro was asked to call it,
and the server's own log recorded `initialize` → `tools/list` → **`tools/call`**, with the tool's nonce
reaching the model and the turn ending `end_turn`. That is what justifies `SupportsMcpServers: true`.

By the same standard, Copilot's `SupportsMcpServers: false` should only change if an equivalent
call-level probe against Copilot succeeds. Do not flip it on the strength of Kiro's result.

Config surface — **corrected**: `DaemonConfig.KiroPath` **already exists** with default `"kiro"`, not
`"kiro-cli"`. The spec previously said to add it, which would have produced duplicate plumbing or a
silent availability change. **Decision — CORRECTED: default to `"kiro-cli"`.** An earlier draft kept `"kiro"` on a compatibility
argument that does not hold: **measured**, `kiro` is absent from PATH while `kiro-cli` is present, and
`PluginCommand.KiroBinary` is `"kiro-cli"`. No descriptor consumed `KiroPath` before this issue, so there
is no compatibility to preserve — and keeping `"kiro"` would have silently never advertised Kiro on a
standard install until a user discovered `KCAP_KIRO_PATH`.

Two consequences, and an earlier draft of this section contradicted itself by keeping the old
argument's tail after reversing the decision:

* **Do not probe both names.** A dual-name probe makes advertised availability depend on install
  layout in a way that is hard to reason about and hard to test. One default, one override.
* **Acceptance needs a zero-configuration availability test** against the supported install — Kiro is
  advertised with no `KCAP_KIRO_PATH` set — *in addition to* an env-precedence test. The precedence
  test alone is what let the wrong default survive review: it passes identically whichever name the
  default holds.

`KiroModel` is not added either, since model override is out of scope for AI-1410; it arrives with the
follow-up that needs it. Availability stays `CliResolver.Exists(KiroPath)`; advertise `kiro` in
`SupportedVendors` only when it resolves.

## Verification checklist

Mirrors the Copilot child, minus what has already been measured:

- [ ] Dashboard launch → live events; vendor label casing correct
- [ ] Permission and elicitation round-trips through `AcpInteractionBridge` (Kiro's shapes unverified)
- [ ] Clean stop; no orphaned child
- [ ] `SupportedVendors` advertises `kiro` only when the binary resolves
- [ ] End-to-end capture: the hosted session appears with vendor `kiro`
- [ ] No model override is attempted — `NoOpModelSelector` is wired, and a dashboard-supplied model is
      NOT silently honoured (see Premise 3)
- [ ] Confirm the ACP-reported agent version is logged

Explicitly **not** in this checklist: canonical token counts (absent from ACP; credits remain via
`KiroUsage`), an auth/tier pre-check (no pre-checkable protocol signal exists — the gate itself is
unproven either way), `--agent-engine` (deliberately unpinned), and model override (deferred by AI-1410
to its own follow-up, since `set_config_option` is unproven and the selector fails silently).

## Out of scope

- `SupportsUnattended` stays false; AI-1410 flips it after its own loop verifies.
- Borrowed review: AI-1410's call, and it needs a containment-token decision, not a default.
- Kiro's **global** `settings/mcp.json` servers leaking into a session — an unattended-only hazard,
  resolved by AI-1410 with a daemon-owned isolated `KIRO_HOME` (measured to inherit nothing).
- Kiro's `--effort` flag: no kcap surface exposes effort today; adding one is its own issue.
