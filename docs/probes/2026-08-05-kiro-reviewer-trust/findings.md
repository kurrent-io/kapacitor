# Kiro unattended-reviewer probe — scoped trust and global-MCP suppression (2026-08-05)

**Environment:** `kiro-cli 2.16.0`, macOS/arm64, `kiro-cli acp` (the daemon's argv),
`initialize protocolVersion: 1`, `session/new {cwd, mcpServers: [<probe server>]}` — the same
handshake `AcpHostedAgentRuntime.StartAsync` performs. Harness: `probe.py` +
`mcp_probe_server.py` (a one-tool stdio server named `kcap-flow-result` exposing
`submit_review_result`, the production names), reusing the ACP child driver from
`../2026-08-04-acp-reconnect-c0/`.

**Cost: two billable requests**, both on `deepseek-3.2` (`rate_multiplier: 0.25`, the cheapest
tier). Everything else is control-plane only and free — Kiro bills per prompt, not per RPC.

## Q1 — does scoped `--trust-tools` cover the injected result tool on the ACP path?

**YES, and the namespaced entry is what does it.** Measured both ways.

| turn | `--trust-tools` | `session/request_permission` frames | `tools/call` at the server |
|---|---|---|---|
| test | `fs_read,thinking,@kcap-flow-result/submit_review_result` | **0** | yes |
| **control** | `fs_read,thinking` | **1**, naming the tool | yes (after approval) |

The control's frame, verbatim:

```json
{"toolCall": {"toolCallId": "tooluse_GzBUGmCj0fLCahJIGxf2uP",
              "title": "Running: @kcap-flow-result/submit_review_result"},
 "options": [{"optionId": "allow_once",   "kind": "allow_once"},
             {"optionId": "allow_always", "kind": "allow_always"},
             {"optionId": "reject_once",  "kind": "reject_once"}]}
```

**Why the control was mandatory.** "No permission frame" on its own is not evidence that the
namespaced entry did anything — MCP tools might have needed no approval at all, in which case the
trust list would be decorative and a spec built on it would claim a mechanism it does not have.
Dropping only that one entry discriminates. This is the same discipline the AI-1410 spec §1.4
already records after a containment test passed vacuously.

Both turns ended `stopReason: end_turn`, the server logged the full
`initialize` → `notifications/initialized` → `tools/list` → `tools/call` sequence, and the
server's nonce reached the transcript — so the result really was delivered, not merely permitted.

**Consequence: Kiro does not need `--trust-all-tools`.** A reviewer can run with `fs_read`,
`thinking` and its own result channel, with `fs_write` and `execute_bash` excluded. Under the
`Fail` interaction policy those exclusions are real — an excluded tool raises a frame, and `Fail`
ends the round rather than approving it.

## Q2 — does an isolated `KIRO_HOME` suppress the operator's global MCP servers on 2.16.0?

**YES, with a positive control.** Free phase, no billable request.

| phase | `KIRO_HOME` | servers Kiro reported starting |
|---|---|---|
| A | empty temp dir | `kcap-flow-result` (the injected one) — and nothing else |
| **B (control)** | unset → real `~/.kiro` | `kcap-flow-result`, `kcap-review`, `kcap-memory`, `kcap-sessions`, **`kcap-flows`** |

The control is what makes A mean anything: without it, "zero global servers" is unfalsifiable.
`kcap-flows` in B is the specific hazard — a reviewer holding it can start nested review flows.

This re-confirms on 2.16.0 what AI-1404 measured on 2.15.2, and the injected result channel still
starts, so suppression does not cost us the channel.

## Two notification shapes worth building on

```
_kiro.dev/mcp/server_initialized    {sessionId, serverName}
_kiro.dev/mcp/server_init_failure   {sessionId, serverName, error}
```

Both were observed. Together they support a **runtime containment assertion** that needs no
certified-version set: at launch, compare the reported `serverName`s against the set this launch
injected, and fail the round on any extra. `server_init_failure` additionally turns a dead result
channel into an immediate coded error rather than a silent round timeout — the failure mode the
spec's §5 was reaching for and could not otherwise reach.

## A harness trap, recorded because it mimics a vendor refusal

The first run reported `server_init_failure: "connection closed: initialize response"` for the
injected server, which reads exactly like Kiro rejecting it. The real cause was the probe's own:
the MCP log path was **relative**, and Kiro spawns the server with cwd set to the review worktree,
so the server died opening its log before its first write. Anything that kills the child before it
answers `initialize` produces this same generic message. Check the child before concluding
anything about the vendor.

## Files

- `probe.py` — the harness. Free phase by default; `--turn` spends exactly one prompt request;
  `--trust` overrides the trust list (that is how the negative control is run).
- `mcp_probe_server.py` — the one-tool stdio server, logging every JSON-RPC request it receives.
- `summary-free.json` / `summary-turn.json` / `summary-control.json` — the three runs, home
  directory rewritten to `~` and temp paths to `<tmp>`. Frames, stderr and MCP logs are not
  committed; the decisive lines are quoted above.
