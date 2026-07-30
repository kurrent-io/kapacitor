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

This does **not** prove no account tier can ever be blocked — only that the gate the original design
feared does not exist at the protocol level. Keep a clear diagnostic for a launch-time auth failure,
but do not build a tier pre-check: there is nothing to check.

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

So AI-888's "Kiro emits no token counts" stands, and hosting Kiro over ACP does **not** improve its
cost story. Anything downstream that prices a Kiro session still has nothing to price. Say so rather
than leaving the hope in the issue.

### Premise 3 — model selection works. CONFIRMED.

`--model` is a real flag, so `ConfigOptionModelSelector` (the same selector Cursor and Copilot use)
applies. AI-1401's model pass-through needs no Kiro-specific work.

## New facts the original design did not have

**`--agent-engine v1|v2|v3`, default `v2`.** A spawn-time behavioural switch nobody had accounted
for. **Decision: do not pass it.** Pinning a version means owning an upgrade treadmill and diverging
from what a user gets interactively; the default is what Kiro tests. Revisit only if a measured
behavioural difference forces it.

**`session/new` returns `modes`.** ACP "modes" are Kiro *agents*: `availableModes` was
`[kcap, kiro_default, kiro_planner]` with `currentModeId: kcap` — our own installed agent, selected by
default because `kcap plugin install` wrote `~/.kiro/agents/kcap.json`.

**Kiro auto-loads its agent config's MCP servers into an ACP session.** With `mcpServers: []` in
`session/new`, Kiro still initialized `kcap-flows`, `kcap-review`, `kcap-sessions` and `kcap-memory`.
Harmless for an interactive hosted agent — it is what the user would get anyway — but it is a hazard
for the unattended reviewer, so AI-1410 owns it, not this issue.

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
    ResolveDefaultModel: cfg => cfg.KiroModel,
    Argv:                ["acp"],
    UnattendedTrustArgv: [],            // AI-1410 owns this; empty keeps unattended off
    SupportsUnattended:  false,         // flipped by AI-1410
    ModelSelector:       ConfigOptionModelSelector.Instance,
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

Config surface: add `KiroPath` (default `kiro-cli`) and `KiroModel` to `DaemonConfig`, mirroring
`CursorPath`/`CursorModel`. Availability is `CliResolver.Exists(KiroPath)`; advertise `kiro` in
`SupportedVendors` only when it resolves.

## Verification checklist

Mirrors the Copilot child, minus what has already been measured:

- [ ] Dashboard launch → live events; vendor label casing correct
- [ ] Permission and elicitation round-trips through `AcpInteractionBridge` (Kiro's shapes unverified)
- [ ] Clean stop; no orphaned child
- [ ] `SupportedVendors` advertises `kiro` only when the binary resolves
- [ ] End-to-end capture: the hosted session appears with vendor `kiro`
- [ ] Model override reaches the agent (`--model` observable in the launch argv)
- [ ] Confirm the ACP-reported agent version is logged

Explicitly **not** in this checklist: token counts (measured absent), auth/tier pre-check (no gate
exists), `--agent-engine` (deliberately unpinned).

## Out of scope

- `SupportsUnattended` stays false; AI-1410 flips it after its own loop verifies.
- Borrowed review: AI-1410's call, and it needs a containment-token decision, not a default.
- Kiro's own agent-config MCP servers leaking into a session — an unattended-only hazard.
- Kiro's `--effort` flag: no kcap surface exposes effort today; adding one is its own issue.
