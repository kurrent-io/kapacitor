# Kiro model-override probe — `session/set_config_option` vs `session/set_model` (2026-08-05)

**Question** (the follow-up the Kiro hosting work deferred): does Kiro's ACP surface let a client
select the session model in a way that **takes effect**, at the standard the hosting work set —
not "the RPC returned success" but "the turn demonstrably ran on the requested model"?

**Environment:** `kiro-cli 2.16.0`, macOS/arm64, spawned as `kiro-cli acp` (the daemon's exact
argv), `initialize protocolVersion: 1`, `session/new {cwd, mcpServers: []}` — the same handshake
`AcpHostedAgentRuntime.StartAsync` performs, with the selector RPC sent after `session/new` and
before the first `session/prompt`, mirroring `IAcpModelSelector`'s call point. Harness:
`probe.py` (reuses the ACP child driver from `../2026-08-04-acp-reconnect-c0/acp_c0_probe.py`).

## Measurements

### Free phase (control-plane only, no billable request)

1. `session/new` returns a `models` object: `currentModelId` (the account's configured default —
   `minimax-m2.5` on the probe account) plus nine `availableModels`, each `{modelId, name,
   description}` with **bare** ids (`auto`, `claude-sonnet-4.5`, `claude-sonnet-4`,
   `claude-haiku-4.5`, `deepseek-3.2`, `minimax-m2.5`, `minimax-m2.1`, `glm-5`,
   `qwen3-coder-next`) — no parameterized variants, unlike Cursor. There is no `configOptions`
   object in the result.
2. `session/set_config_option {sessionId, configId: "model", value: "<exact id>"}` — the exact
   frame `ConfigOptionModelSelector` sends — answers:

   ```json
   {"error": {"code": -32601, "message": "Method not found", "data": "session/set_config_option"}}
   ```

   **The method does not exist on Kiro.** `ConfigOptionModelSelector` can never work there, and
   the hosting work's fear was structurally justified: that selector swallows this error and
   continues on the vendor default, which would have been a silent no-op on every launch.
3. `session/set_model {sessionId, modelId: "<exact id>"}` (the stabilized ACP model-selection
   method) answers `{"result": {}}` — accepted at the RPC level. By the standard above this alone
   proves nothing; hence the paid phase.
4. The session sidecar (`~/.kiro/sessions/cli/{sessionId}.json`) shows
   `session_state.rts_model_state.model_info: null` before any turn — the setting is not yet
   observable there, so persistence evidence requires the turn.

### Effect phase (exactly one billable request)

`session/set_model {modelId: "deepseek-3.2"}` (chosen: a different vendor **family** from the
account default `minimax-m2.5`, so no identity confusion is possible, and the cheapest tier —
`rate_multiplier: 0.25`), then one no-tools prompt asking the model to identify itself. Turn ended
`stopReason: end_turn`. Three independent channels agree:

1. **The wire request** (client trace log, `KIRO_LOG_LEVEL=trace`): the turn's actual backend
   inference request — `CodeWhispererStreaming.GenerateAssistantResponse`, "transmitting request"
   — carries the requested id verbatim in its body:

   ```
   ...,"origin":"KIRO_CLI","modelId":"deepseek-3.2"}},"chatTriggerType":"MANUAL",...
   ```

2. **Self-identification:** the reply text was exactly `deepseek-3.2` — an id that appears nowhere
   in the prompt, from a session whose default was a MiniMax model. (Kiro evidently tells the
   model its platform id, i.e. the runtime itself believes the turn's model is the requested one.)
3. **Kiro's persisted session state:** `rts_model_state.model_info` flipped from `null` to
   `{"model_name": "deepseek-3.2", "model_id": "deepseek-3.2", "context_window_tokens": 164000,
   "rate_multiplier": 0.25, "rate_unit": "Credit"}` — model-specific runtime parameters (DeepSeek's
   164k context window, vs 200k observed for `auto`), not an echo of the requested string.

## Verdict

- `session/set_config_option`: **conclusively absent** on Kiro (`-32601`). Closed as "won't work",
  with the measurement recorded here and in the hosting design doc.
- `session/set_model`: **works and takes effect at the turn level.** This clears the bar for
  carrying a live selector: Kiro now ships `SetModelSelector` (same resolution half as
  `ConfigOptionModelSelector` via `AcpModelResolver`, `session/set_model` write half) plus
  `DaemonConfig.KiroModel` / `KCAP_KIRO_MODEL` (default **null** — zero-configuration launches keep
  Kiro's own default model with nothing reported, unchanged).

## Boundaries this probe does NOT move

- **Gemini** stays `NoOpModelSelector`: nothing here measures Gemini. This probe is the template —
  determine which write method Gemini implements, then verify it at effect level.
- **Reviewer (review-flow) model override** stays refused for Kiro:
  `AcpHostedAgentRuntimeFactory.ReviewerModelResolver` remains `null`, and Kiro is not
  unattended-certified anyway. The unattended reviewer issue owns that decision.
- An unresolvable requested model still resolves to null before any RPC (no `session/set_model`
  sent), so with the handshake-confirmed-model registration fix in place a launch with a bad model
  runs and reports Kiro's default rather than claiming the requested one.

## Files

- `probe.py` — the harness (free phase by default; `--turn` spends exactly one prompt request).
- `kiro-free-phase-summary.json` / `kiro-turn-summary.json` — captured summaries of the two runs,
  with raw client-log excerpt fields stripped (they can embed injected session context; the
  decisive lines are quoted above).
