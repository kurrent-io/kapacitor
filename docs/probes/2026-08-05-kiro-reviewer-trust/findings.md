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

---

# Seeded-defect differential, and a blocking finding (2026-08-05, later)

`seeded_defect_probe.py` + `result_channel_server.py`. Three billable requests, all on `deepseek-3.2`.

## The reviewer genuinely reviews

Two arms over the same prompt, differing only in one planted off-by-one:

| arm | code | result delivered through the injected channel |
|---|---|---|
| A | `range(len(items) - 1)` | **`findings`** — *"it returns all items except the last one… The loop uses `range(len(items) - 1)` which excludes the last index."* |
| B | `range(len(items))` | **`clean`** |

Both halves are needed. A alone passes for a reviewer that always finds something; B alone for one
that always says clean. The pair is the oracle, and it is the only evidence in this feature that
distinguishes a working reviewer from an inert one — completion, zero human routing, reap and
channel-invoked are jointly satisfiable by a reviewer that ignores its input.

## ⛔ BLOCKING: namespaced trust is NOT deterministic

Arm B raised **one `session/request_permission`**, naming the tool that is *explicitly in the trust
list*:

```json
{"toolCall": {"title": "Running: @kcap-flow-result/submit_review_result"},
 "options": [{"optionId": "allow_once"}, {"optionId": "allow_always"}, {"optionId": "reject_once"}]}
```

Same `--trust-tools` value, same isolated `KIRO_HOME`, same injected server, a fresh session per arm:
**arm A raised zero frames, arm B raised one.** Re-running arm B alone reproduced it. So the earlier
measurement in this document — zero frames with the namespaced entry, one without — was correct but
*incomplete*: the entry does something, and it is not reliable.

**Why this blocks.** The shipped interaction policy is `Fail`: any server→client frame reaps the
reviewer, on the reasoning that with scoped trust a reviewer emits none on its expected path. That
reasoning is now falsified. A Kiro reviewer would fail an unpredictable fraction of otherwise-clean
rounds, on the very call that delivers its result.

This is almost certainly the upstream trust-flag prompt leak the original issue cited (#7398). The
spec anticipated it in the abstract — *"if upstream's prompt leaks fire, Kiro reviewer rounds fail
rather than silently auto-approving; that is the correct direction"* — but as a rare event. At
roughly one round in three on the simplest possible review, "a visibly flaky reviewer is a bug
report" no longer describes the situation.

**Options, none free; this is an owner decision:**

1. **`--trust-all-tools`** (Gemini's `yolo` equivalent). Removes the frame, and removes the scoped
   posture that made Kiro's reviewer tighter than the one already in production.
2. **A tool-aware auto-approve policy** — approve only the injected result channel's tools, fail on
   anything else. This is the allowlist-aware policy §2 of the spec deferred as foundation work
   rather than a Kiro detail, and it is the option that keeps the scoping.
3. **Ship with `Fail`** and accept intermittent round failures. Honest, and probably unusable.

Option 2 preserves what this design was for; it is also the largest.

## Note on cost

Three requests bought the functional proof AND a blocking behavioural finding that no unit test
would surface — every launch-shape assertion in this feature passes with this bug present.

---

# Verification of `AllowlistedAutoApprove` (2026-08-05, final)

Three billable requests on `deepseek-3.2`, all with a **production-shaped aliased** result channel
(`kcap-flow-result-<guid>`) rather than the canonical id — the alias is what the shipped admission
rule compares against, and a GUID-bearing name is exactly what a display title might truncate or
reformat.

## The functional half still holds

Seeded-defect differential re-run: planted off-by-one → `findings` naming it; defect removed →
`clean`.

## The admissibility question, and why the first run could not answer it

The first two-arm run passed with **zero permission frames observed** — which makes "every frame is
admitted" vacuously true. The frame is intermittent, so a run that sees none proves nothing about
the rule. Recorded rather than glossed: `frames_observed` is in the summary for exactly this reason.

Dropping the namespaced trust entry provokes a frame deterministically (measured: one, every time)
without changing its title shape, so one further request settled it:

```
Running: @kcap-flow-result-9f848b5c57dd405e896a3c1b92b8e137/submit_review_result
```

**The shipped rule admits it.** The alias round-trips byte-intact through Kiro's title — no
truncation, no reformatting, one `Running: ` prefix. That was the live risk in tightening from a
substring scan to a complete-title match: a stricter rule that real titles fail would reap the
reviewer on its own result call, which is worse than what it replaced. They do not fail.

## What this does NOT establish

One title shape, on one build (2.16.0), from one vendor code path. A future Kiro that decorates the
title differently matches nothing and reaps — visibly broken, fail-closed, and the version
affirmation is what makes a build change require a human look.

## Files

- `summary-policy-verify.json` — the two-arm differential (zero frames; admissibility unexercised).
- `summary-policy-provoked-frame.json` — the provoked frame and the admissibility check.
