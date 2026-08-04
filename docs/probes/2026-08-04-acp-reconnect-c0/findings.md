# C0 re-probe: ACP reconnect/resume identity facts (2026-08-04)

Re-run of the AI-689 C0 gate probe against all four registered ACP vendors, triggered by ACP v1
adding the optional `messageId` MAY on message chunks. Feeds
`docs/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md`; per-vendor conclusions
are baked into `AcpVendorDescriptor.SupportsReconnectResume`.

## Method

`acp_c0_probe.py --vendor {cursor|copilot|kiro|gemini}` — spawns the vendor's ACP server with the
production descriptor argv in a throwaway cwd, then:

1. `initialize` (protocol 1, no client fs/terminal) → capture `agentCapabilities.loadSession`.
2. `session/new {cwd, mcpServers: []}`; for Cursor, model selection mirroring
   `ConfigOptionModelSelector` (`session/set_config_option {configId: "model"}`).
3. Turn A and turn B: byte-identical prompts (occurrence-identity test); turn C: a shell tool call
   (auto-approving `session/request_permission` with the least-privilege allow option).
4. Turn D: a long turn, SIGKILLed mid-flight once chunks are streaming.
5. Fresh child, `initialize`, `session/load {sessionId, cwd, mcpServers: []}`; every frame tagged
   pre/post the load *response* (the barrier edge), plus a 3s trailer window; then one post-load
   prompt.

Every frame is captured (per-vendor `frames.jsonl` retained in the originating session's scratchpad;
the committed `*-summary.json` files carry the analysis: update kinds, per-kind key unions, id-field
values, live↔replay overlaps, interrupted-turn shape, barrier violations).

Two variants (same script, small deltas):

- **copilot-sleepkill** — the plain kill raced Copilot's fast counting turn (the first run's turn D
  completed before SIGKILL landed; its `interrupted_turn_replay_shape` datum reflects a *completed*
  turn). The variant's turn D runs `sleep 20 && echo …` and the kill fires 3s after the first
  `tool_call`, guaranteeing a mid-turn death. Note its summary's `interrupted_shape: absent` is a
  classifier artifact (the classifier greps the original turn-D marker text); the raw
  `replay user_chunks` in the same summary show the sleep-turn's user message **present**, followed
  by agent updates — Copilot persists the interrupted turn.
- **kiro-retry** — after Kiro's `session/load` refusal, retries at +15s and +45s to test whether the
  stale-owner lock clears with time.

## Results

| Fact | cursor 2026.07.23 | copilot 1.0.78 | kiro 2.16.0 | gemini 0.53.0 |
|---|---|---|---|---|
| `loadSession` advertised | yes | yes | yes | yes |
| `session/load` after SIGKILL | works | works | refused: `Session is active in another process (PID <dead>)` — identical at 0/15/60s (durable lock, not a liveness check) | refused: `No previous sessions found for this project` |
| `messageId` (any id-ish field) on live/replayed chunks | none | none | none | not analyzed (load refused; frames not archived — see below) |
| Replay granularity | coalesced: 3 user + 4 thought + 3 message chunks replay 77 live chunks | coalesced; includes one **empty** user chunk matching no turn | — | — |
| `toolCallId` live↔replay | rewritten to synthetic `replay-2-1` (overlap 0) | stable (`toolu_…`, overlap 2/2) | — | — |
| Interrupted turn in replay | **absent** (3 chunks had streamed pre-kill) | **present** (user + partial agent output) | — | — |
| Conversation updates after the load response | 0 (one `available_commands_update` trailer) | 0 (two `available_commands_update` trailers) | — | — |
| Post-load prompt | works | works | — | — |

Gemini extras: it **self-re-execs** (a second `gemini` node process with identical argv per launch —
a sandbox wrapper), so SIGKILLing the spawned pid orphans the real agent, which keeps the stdio
pipes open; the probe's asyncio teardown wedged on exactly that, which is why gemini has a run log
but no `summary.json`. Design consequence recorded in the spec: for Gemini, "spawned pid exited" ≠
"agent dead", and any kill must be process-tree-wide (the daemon's `AcpChildProcess` already kills
the tree; the probe did not).

## Conclusions

1. No vendor implements the `messageId` MAY → the streaming-preserving per-envelope dedup (AI-689
   C3(a)) remains impossible; Cursor's synthetic replay tool ids close it doubly.
2. The `session/load` response is a reliable closed-world replay barrier for both capable vendors
   (spec MUST, measured to hold; only non-conversation trailers follow).
3. Cursor + Copilot are resume-capable across a hard child death; Kiro and Gemini are not, despite
   both advertising `loadSession` — capability gating must be probe-verified per vendor, never
   inferred from the advertisement.
4. An interrupted turn's replay shape differs by vendor (Cursor drops it; Copilot persists it) —
   consistent with the spec's rule that the interrupted-turn disposition keys on local send facts
   only, never on replay content.
