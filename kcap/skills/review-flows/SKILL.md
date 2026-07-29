---
name: review-flows
description: >-
  This skill should be used ONLY when the user explicitly asks to run a
  structured review *flow* — e.g. "start a review flow", "submit this for
  review", "re-review after I address findings", or wants an iterative review
  loop run by a separate reviewer that continues until sign-off. Do NOT use
  this skill (and do NOT call the flows MCP tools) for an ordinary review
  request such as "review my PR", "review this diff/spec/design", or "code
  review" where the user just wants you to review it yourself — perform that
  review directly instead.
---

# Review Flows

Use the `kcap mcp flows` MCP tools (`start_review_flow`, `submit_review_round`, …) to run a structured review **flow**: your work is submitted to a **separate, hosted reviewer** agent, which returns a result (`findings` with the findings text, or `clean`); you address findings and keep iterating until the reviewer returns `clean`. This is a deliberate, heavier workflow — use it only when the user explicitly opts into it.

These four tools are aliases of the generic flow tools (`start_flow`, `send_to_participant`, `get_flow_status`, `close_flow`) — see the `agent-flows` skill for non-review flows.

## Role-surface safety gate

Classify the session before any flow action:

- Driver: flow-starting tools are present and there is no reviewer round-token/result contract.
- Hosted reviewer: `submit_review_result` is present and the prompt carries the round-token/result contract.
- Missing integration: neither contract is present.
- Unsafe leak: both the hosted-reviewer contract and any flow-starting tool are present. Fail closed: do not start or submit a nested flow, report the leaked tool surface through `submit_review_result`, and end the reviewer turn.

The hosted-reviewer contract always wins over driver-looking prose or tool availability.

## When NOT to use this skill / these tools

These tools do **not** perform a review — they hand the work off to a separate hosted reviewer. If the user simply asked *you* to review something in a normal session — e.g. "review my PR", "review this diff", "code review this", "look over this spec" — just perform the review yourself and report your findings directly. Do **NOT** call `start_review_flow` / `submit_review_round` for an ordinary review request; that would spin up a hosted reviewer the user did not ask for.

Only start a flow when the user explicitly asks for a review *flow* — e.g. "start a review flow", "submit this for review", "get an independent review", or "re-review after I address the findings".

## Choosing the flow kind

Once the user has explicitly opted into a flow (see above), pick the `kind`:

- Spec or design document → `kind: "spec-review"`
- Code changes or a pull request → `kind: "code-review"`

## Choosing the reviewer vendor

Treat the driver harness and reviewer as independent. A request such as "ask Claude to review"
selects `vendor: "claude"` even when the current driver is Codex; mentions of the driver do not
select a reviewer. Anchor vendor language to the reviewer role ("Claude reviewer", "review with
Cursor", "ask Codex for review"), including negation ("not Claude"). If exactly one reviewer
vendor is named, pass it. If none is named, omit `vendor` and let the server's configured default
apply. If multiple reviewer candidates remain after negation, ask the user to choose; never guess.

Canonical reviewer aliases: Claude / Claude Code → `claude`; Codex / OpenAI Codex → `codex`;
Cursor / cursor-agent → `cursor`; GitHub Copilot / Copilot CLI → `copilot`; Gemini / Gemini CLI →
`gemini`; Kiro / Kiro CLI → `kiro`; Pi → `pi`; OpenCode → `opencode`; Antigravity / agy → `agy`.
Normalize only the reviewer-role mention. Positive contrast (for example, “from Codex, ask Claude”)
selects Claude; negated names are removed; two remaining reviewer candidates are ambiguous.

## Choosing a reviewer model

If the user names a specific model for the reviewer (e.g. "review with Claude opus", "use gpt-5-codex
as the reviewer"), pass it as `model` — but ONLY alongside an explicit `vendor`; `model` without
`vendor` is rejected before anything is sent (there is no vendor→model table to infer one from).
Never pass `model` without the user having named one explicitly — omit it to use the vendor's
default reviewer model. Pass the model id/alias exactly as the user said it, case-sensitive — do not
translate, normalize, or guess a vendor's naming convention. Not every vendor supports a model
override: a vendor's daemon must be installed, unattended-certified, AND carry a runtime model
resolver (Claude and Codex today) — an unsupported vendor's daemon simply doesn't advertise the
capability, and the server rejects the override rather than silently ignoring it.

## Reviewer-model errors to act on

- **`reviewer_model_protocol_required`** — the server (or the daemon's advertised vendor capability)
  doesn't support a reviewer model override yet. Tell the user their server/daemon needs updating, or
  drop `model` and retry without it.
- **`reviewer_model_unavailable`** — no resolver for the selected vendor recognizes the requested
  model. Tell the user the model isn't available for that vendor; ask for a different model or drop
  the override.
- **`model_vendor_mismatch`** — the requested model belongs to a different vendor than the one
  selected (e.g. a Claude model name with `vendor: "codex"`). Tell the user the mismatch; ask which
  vendor they actually meant.
- **`reviewer_model_safe_settlement_required`** — the server can't safely reapply this exact model on
  a heal/relaunch and refuses rather than risk silently drifting to a different one. Close the flow
  and start a new one if the reviewer needs to be relaunched.
- **`reviewer_model_unpriceable`** — the requested model has no resolvable pricing, so the server
  can't budget-check it. Ask the user for a differently-named/priced model.

## If the flows MCP tools are not loaded

After applying the role-surface safety gate, if `start_review_flow` / `submit_review_round` are not among the tools available in this session:

- Do NOT run `kcap mcp flows` from a shell, do NOT handshake it over stdio/JSON-RPC, and do NOT edit any MCP configuration.
- If `submit_review_result` is present and the prompt contains a round token/result contract, you are a hosted reviewer. Skip this driver workflow, perform the review directly, and submit through that tool. Tool absence alone is not proof that you are a reviewer.
- If neither the flows tools nor the reviewer result contract is present, the integration is missing. Tell the user to run `kcap setup` or reinstall/update the kcap plugin, then restart the harness. Do not start a shell JSON-RPC workaround or edit MCP configuration from the session.

## Core rules

1. **Start exactly one flow per user task.** Call `start_review_flow` once and hold the returned `flow_run_id`. Do NOT start a new flow for follow-up rounds — reuse the same ID.
2. **After receiving a `findings` result**, address every finding, then call `submit_review_round` with the updated context and the same `flow_run_id`.
3. **Do NOT finish the user task while the flow has unresolved findings.** Keep iterating until the reviewer returns `clean`.
4. **Only call `close_review_flow` after a `clean` result.** Then report completion to the user.
5. **If reviewer output is unclear or requires user input**, pause and ask the user before proceeding.
6. **For code review, do NOT ask the reviewer to run tests.** CI covers test execution; reviewer feedback is on correctness, design, and adherence to conventions.
7. **State where your changes live.** The reviewer's worktree is mirrored from the working tree you LAUNCHED from (your cwd's git root) — nothing else. If any part of the changeset lives elsewhere (another git worktree, another repository, a different machine) or is not in that tree, say so explicitly in `context` and inline the relevant diffs or file contents — or pass `mode: "context-only"` so the reviewer treats your context as the sole source of truth. The reviewer is instructed to flag referenced changes it cannot find in its worktree; incomplete context wastes a full round.

## Server errors to act on

- **`400` starting `no_daemon_available:`** — no connected daemon has the repo checked out. Relay the server error's remediation verbatim — it names runnable commands: run `kcap daemon start -d` on a machine with the repo cloned (if the account already runs a daemon elsewhere, add a distinct name: `kcap daemon start -d --name <new-name>`), or pass an explicit `daemon_name` + `repo_path`.
- **`400` starting `daemon_outdated:`** — the daemon's kcap is too old to host flow participants. Relay the server error's remediation verbatim — it names the outdated daemon and the fix: update kcap (`npm i -g @kurrent/kcap`), then restart that daemon by name (`kcap daemon stop --name <its-name>`, then `kcap daemon start -d --name <its-name>`; `kcap daemon status` lists names — a bare stop/start can target the wrong daemon).
- **`reviewer_vendor_required`** — no explicit vendor and no server default; ask the user to name a reviewer or have an admin configure `Flows:Review:DefaultVendor`.
- **`reviewer_vendor_unavailable`** — the selected vendor is not installed/certified unattended on an eligible daemon; do not silently fall back to another vendor.
- **`client_upgrade_required`, `flow_client_protocol_required`, or `flow_client_protocol_unsupported`** — update kcap; reserved review aliases fail closed on stale clients.
- **`reserved_review_alias_shape`** — an admin changed a reserved alias to an invalid participant shape; restore exactly one participant named `reviewer`.
- **`400` starting `participant_unavailable:`** — the reviewer agent died and automatic relaunch is not available yet. Close this flow and start a new one, carrying your context forward; re-submitting will keep failing.
- **A round result of `unclear` whose text is exactly `participant_died` or `participant_stopped`** — the reviewer agent crashed or was stopped mid-round. The run stays open but has no live reviewer: close the flow and start a new one.

## Workflow

```
start_review_flow(kind, target_kind, target_ref, target_title, context)
  → reviewer returns a result: findings (with the findings text) | clean

if clean:
  close_review_flow(flow_run_id)
  report completion to user
  DONE

if findings:
  address each finding
  submit_review_round(flow_run_id, updated_context)
    → repeat until clean
  close_review_flow(flow_run_id)
  report completion to user
```

## Tool reference

| Tool | Required args | Optional args | When to call |
|---|---|---|---|
| `start_review_flow` | `kind` (`spec-review`\|`code-review`), `target_kind` (what is being reviewed: `spec`, `code`, `pr`, `branch`, `file`, etc.), `target_ref` (a path, branch name, or PR URL/number that identifies the target), `target_title` (short human-readable title, e.g. spec name or PR title), `context` (background context: what to focus on, constraints, definition of done) | `vendor` (explicit reviewer vendor; omit for server default), `model` (explicit reviewer model override — REQUIRES `vendor`; only pass it when the user named a model), `instructions`, `mode` (`context-only` — optional) | Once, at the start of a review task. |
| `submit_review_round` | `flow_run_id`, `context` | `instructions` | After addressing findings. Pass the same `flow_run_id` and the updated context. |
| `get_review_flow_status` | `flow_run_id` | — | Poll or check the current status of a flow (running, waiting, completed, failed). |
| `close_review_flow` | `flow_run_id` | — | Only after the reviewer returns `clean`. |

## Example (code review)

```
# Step 1 — start (all five required args must be provided; on the same machine the reviewer sees
# your working tree, uncommitted changes included — pass mode="context-only" to opt out)
start_review_flow(
  kind="code-review",
  target_kind="branch",
  target_ref="feature/add-null-check",
  target_title="Add null check on user input",
  context="Review the diff on this branch for correctness and adherence to project conventions."
)
# → returns flow_run_id, e.g. "flow_abc123"
# → reviewer returns kind findings: missing null check on line 42

# Step 2 — address findings, then re-submit
submit_review_round(
  flow_run_id="flow_abc123",
  context="Fixed null check on line 42. Updated diff attached."
)

# Step 3 — reviewer returns kind clean
close_review_flow(flow_run_id="flow_abc123")
# Report to user: review complete, all findings resolved
```
