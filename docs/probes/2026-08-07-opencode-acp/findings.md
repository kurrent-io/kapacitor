# OpenCode ACP hosting + reviewer probe — 2026-08-07

**Subject:** `opencode` 1.18.9 (Homebrew, `opencode-ai/bin/opencode.exe`), macOS 25.5.0 / arm64.
**Driver:** `probe.py` (reuses the `2026-08-04-acp-reconnect-c0` `AcpClient`).
**Cost:** free phase issues zero model requests. The turn phase issues one request per arm; the
model-effect arm deliberately targets a `-free` model.

Every claim below is an effect-level measurement. The weaker signals available here are all known to
lie: the advertised `mcpCapabilities` shape predicts nothing about stdio support (Kiro and Gemini
advertise `{http,sse}` and honour stdio; Copilot advertises the same and does not), a server that
merely *starts* proves nothing about whether its tools are callable, and a `set`-style RPC that
echoes success can still not take effect — that last one is exactly why Gemini is still on
`NoOpModelSelector`.

---

## 1. ACP surface — `opencode acp`

`initialize` (protocolVersion 1) answers:

```json
{ "protocolVersion": 1,
  "agentCapabilities": {
    "loadSession": true,
    "mcpCapabilities": { "http": true, "sse": true },
    "promptCapabilities": { "embeddedContext": true, "image": true },
    "sessionCapabilities": { "close": {}, "fork": {}, "list": {}, "resume": {} } },
  "authMethods": [ { "id": "opencode-login", "name": "Login with opencode" } ],
  "agentInfo": { "name": "OpenCode", "version": "1.18.9" } }
```

`session/new {cwd, mcpServers}` answers `{sessionId, configOptions}` — note **`configOptions`, not a
`models` object**. `opencode acp` accepts `--cwd` but the daemon does not need it: `session/new`
carries the cwd.

## 2. Model selection — `ConfigOptionModelSelector`, verified at effect level

The read half is `configOptions[id == "model"]`: `currentValue` plus an `options[{value,name}]` list
of `provider/model` ids. The write half is
`session/set_config_option {sessionId, configId:"model", value:"provider/model"}`, which answers with
the **full updated option list**, `currentValue` set to the requested id.

That echo is still only a self-report, so the turn arm asked the model: with `currentValue` moved
from `opencode/big-pickle` to `opencode/deepseek-v4-flash-free`, the model answered exactly
`opencode/deepseek-v4-flash-free` and never named the previous model. OpenCode's own system prompt
tells the model its exact id ("You are powered by the model named …"), so this reads the *running*
model, not the configured one.

`session/set_model {sessionId, modelId}` also exists (answers `{}`, so not `-32601`) and validates
ids. It is redundant — `set_config_option` is the one with a read half on the same surface.

> **Trap this probe walked into.** Its first revision looked for a `models` object, found none, and
> fell back to a hard-coded `anthropic/claude-sonnet-4-5` the account cannot reach. *Both* selectors
> then answered `-32602 model not found`, which reads exactly like an unsupported method and would
> have argued for `NoOpModelSelector` on a measurement that never named a real model.

## 3. stdio `mcpServers` — honoured at CALL level

A purpose-built stdio server passed in `session/new.mcpServers` was driven all the way through:

```
spawned → initialize → notifications/initialized → tools/list → tools/call → end_turn
```

with the tool's nonce reaching the model's own frames. OpenCode honours stdio despite advertising
only `{http, sse}` — the Kiro result, not the Copilot one. So the reviewer's result channel can ride
`session/new`, and no `--additional-mcp-config`-style preload is needed.

The free-phase `mcp_admission` arm reaches `tools/list` without any model request, which makes it a
cheap regression canary; it is **not** sufficient on its own, and `--turn` is what proves callability.

## 4. Dual capture is REAL, and `OPENCODE_PURE=1` is the lever

OpenCode sessions are normally captured by kcap's own plugin
(`~/.config/opencode/plugins/kcap.ts`). That plugin loads **inside the `opencode acp` process too**,
and its `session.created` handler starts a top-level capture for the very session the ACP mapper is
already capturing — a POST plus a spawned watcher, per hosted agent.

Controlled arms, identical 10s dwell, differing only in the lever (plugin confirmed installed, so
neither arm is vacuous):

| arm | env | `~/.cache/kcap/opencode/<sessionId>.jsonl` created |
|---|---|---|
| `plugin_default` | — | **yes** |
| `plugin_pure` | `OPENCODE_PURE=1` | no |

An earlier uncontrolled pass had unequal dwell between arms and would have "shown" prevention that
was really just a shutdown race — which is also the honest description of the bug being prevented:
whether a hosted session gets double-ingested is otherwise **timing-dependent**, not reliably
present, which is worse than a deterministic duplicate.

`--pure` and `OPENCODE_PURE=1` are equivalent (the argv middleware just sets the env var).

## 5. Config isolation does not break the launch

`OPENCODE_CONFIG_DIR` pointed at an empty daemon-owned directory (plus `OPENCODE_PURE=1` and
`OPENCODE_DISABLE_PROJECT_CONFIG=1`) still completes `initialize` and `session/new` with all 22
account models listed. Credentials therefore live **outside** the config dir, so the Kiro-style
"empty vendor home" containment — which is what keeps an operator's global MCP servers, `kcap-flows`
among them, out of a reviewer — is available here without costing the reviewer its ability to run.

## 6. Permission posture — default is silent; the trust lever is proven non-inert

Four arms, because the obvious two are a vacuous pair. Each arm asks for a file write in a temp git
repo; the driver auto-approves any frame, so the discriminator is the **frame count**, not whether
the write happened (it always did).

| arm | env | `session/request_permission` frames | file written |
|---|---|---|---|
| `perm_default` | — | 0 | yes |
| `perm_trusted` | `OPENCODE_PERMISSION={"*":"allow"}` | 0 | yes |
| `perm_asking` *(positive control)* | operator config `permission:{"*":"ask"}` | **1** (`toolCall.kind:"edit"`) | yes |
| `perm_asking_trusted` | that config **+** `OPENCODE_PERMISSION` | **0** | yes |

OpenCode's native permission default already contains `"*": "allow"`, which is why the first two arms
are indistinguishable — on their own they measure nothing, and an entirely inert trust lever would
have scored identically. `perm_asking` proves a frame is reachable at all; `perm_asking_trusted` is
the only arm that measures the lever, and it shows `OPENCODE_PERMISSION` overriding an operator's
asking configuration.

Consequence for the reviewer: a correctly-configured unattended OpenCode launch emits **no**
interaction frame, so `Fail` is the honest interaction policy (the Cursor/Gemini contract) — a frame
means the launch contract regressed. The trust env is still load-bearing rather than decorative,
because without it an operator whose own config says `"*": "ask"` would wedge every reviewer.

## 6b. A SCOPED reviewer posture is expressible, and every part of it is load-bearing

`{"*":"allow"}` would make a reviewer able to run shell commands and write files — broader than the
Kiro reviewer's read-only trust list, and not something to ship because it happens to be the shortest
env value that produces zero frames. OpenCode's permission config expresses the narrow posture
instead: deny everything, then allow the read family plus the injected result channel.

The channel is nameable because OpenCode presents an injected MCP tool to the model **flattened** as
`{serverName}_{toolName}` — observed as `kcap-probe_probe_nonce` in the §3 arm — so a
`{serverName}_*` glob addresses it.

Three arms, each asking the model to (1) read a file holding a sentinel, (2) call the injected tool,
(3) run `echo ok > shell-ran.txt`. Shell success is judged on **the file existing**, never on the
answer text: an earlier revision searched the model's prose for the sentinel and scored the DENIED arm
as having run the shell, because the model quoted the command it had been asked to run while
explaining that it could not.

| arm | permission | read | result channel | shell actually ran | frames |
|---|---|---|---|---|---|
| `reviewer_scoped` | deny-all + read family + `{server}_*` | ✅ | ✅ | ❌ | 0 |
| `reviewer_bash_allowed` | same, but `bash: allow` | ✅ | ✅ | **✅** | 0 |
| `reviewer_mcp_unlisted` | same as scoped, `{server}_*` **removed** | ✅ | **❌** | ❌ | 0 |

Both controls were necessary and both changed what can honestly be claimed:

* **`reviewer_bash_allowed`** — "the model did not run bash" is indistinguishable from "bash was
  denied"; a model that simply chose not to shell out would have scored as containment. Flipping the
  one rule makes the shell run, so the denial is attributable to the rule.
* **`reviewer_mcp_unlisted`** — the alternative explanation for the scoped arm was that MCP tools
  bypass the permission system entirely, which would make the allowlist entry decorative while reading
  as load-bearing. Removing it takes the tool out of the model's toolset altogether ("no such
  tool… I only have `glob`, `grep`, and `read`"). MCP tools go through the permission system, and
  without that entry a reviewer cannot reach its own result channel.

Note the MECHANISM, which is stronger than a refusal: a denied tool is absent from the model's
surface rather than refused at call time. That is why every arm reports zero frames, and it is what
makes `Fail` the right interaction policy — on this posture a frame means the launch contract
regressed, not that a reviewer asked for something.

## 7. Not probed

**Reconnect/resume.** OpenCode advertises `loadSession: true` and `sessionCapabilities.resume`, and
§1 records that, but nothing here measures `session/load` across a SIGKILLed owner or the
response-after-replay barrier. `SupportsReconnectResume` therefore stays `false` as
*unprobed* — which is a different claim from Kiro's and Gemini's `false`, both of which are
measured-ineligible. Flipping it needs a run of `2026-08-04-acp-reconnect-c0`.

## 8. Artifacts

`out/` and `*.stderr.log` are gitignored. The non-pure arms of §4 left five zero-byte files in
`~/.cache/kcap/opencode/` and created the matching empty sessions server-side; both are inert (an
empty session carries no `meaningful_activity` and is swept) and are left in place as evidence.
