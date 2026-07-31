# AI-1413 — Gemini CLI as an unattended review-flow reviewer

**Status:** rev 1, 2026-07-31, against `gemini 0.53.0` and `kcap-cli` main (`bfb7bb2`).
**Implementation-ready.** Every claim marked **measured** was observed against a live `gemini
--experimental-acp` process; the probes and their controls are in §2.
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1400 (reviewer choice in review flows)
**Template:** the shipped Cursor reviewer (AI-1408) and Copilot reviewer (AI-1409). Gemini hosting is
AI-899, merged today.

Its three blockers — AI-899 hosting, AI-1407 reviewer foundation, AI-1402 vendor selection — are all Done,
so this is unblocked. It is **not** blocked on the credential problem that blocks Kiro's AI-1410: see §1.

## 1. Why this is not blocked on a credential broker (unlike Kiro's AI-1410)

Worth stating first, because the parent epic's other open reviewer *is* blocked on exactly this and the two
look alike from outside.

A borrowed reviewer needs a brokered credential because `BorrowedReviewSandbox` redirects `HOME` and grants
nothing under it, so the vendor cannot see its own login. That is a property of **borrowed review**, not of
unattended review. Read from the code (`AcpHostedAgentRuntimeFactory`):

```csharp
// The read boundary. Only a borrowed snapshot is wrapped: every other launch either has no
// borrowed content to protect or is already confined by the owned worktree it runs in.
if (ctx.IsBorrowedSnapshot && resolved.RequiresProcessSandbox) {
```

So an unattended reviewer in a daemon-**owned** worktree runs with the daemon's own `HOME`, sees its own
login, and needs no broker. That is the Cursor path, and it is the path this issue takes.

**This issue therefore ships `SupportsUnattended: true` and leaves `SupportsBorrowedReviewFlow` false.**
Borrowed review for Gemini is a separate question with its own credential work; scoping it in here would
import Kiro's blocker for no benefit. §8 records that boundary.

## 2. Measured facts, with their controls

Four probe runs against `gemini 0.53.0`, driving the real ACP argv. Evidence is a marker file written by the
MCP server itself — the ACP client cannot see whether Gemini spawned a server, so the client's transcript is
not evidence.

| # | launch | result-channel server | tool invoked |
|---|---|---|---|
| A | `--allowed-mcp-server-names kcap-probe`, server injected as `kcap-probe` | spawned, `tools/list` | **YES** `{"verdict":"reached"}` |
| B | `--allowed-mcp-server-names __kcap_unmatchable_mcp_name__` (today's descriptor), server injected | **never spawned** | no — and the model replied `TOOL_NOT_VISIBLE` |
| C | `--allowed-mcp-server-names kcap-flow-result-<guid>`, server injected under that name | spawned, `tools/list` | **no** — `session/request_permission` was emitted first |
| D | run C **plus `--approval-mode yolo`** | spawned, `tools/list` | **YES** `{"verdict":"reached"}` |

### 2.1 Gemini honours `session/new.mcpServers` — the flow-result channel is viable

Run A. Gemini spawned the stdio server, completed the MCP handshake, called `tools/list`, then `tools/call`
with the right arguments. **`SupportsMcpServers: false` on today's descriptor is stale** — AI-899 §3.4
deferred exactly this call-level probe and the answer is GO.

This is the whole feasibility question: a reviewer delivers its result **only** through the injected
`flow-result` MCP tool (markers are inert — AI-1190). No channel, no feature.

### 2.2 The MCP allowlist gates INJECTED servers too — AI-899 §3.1c's coupling is load-bearing

Run B is the control, and it does more than validate run A. With the deny-all placeholder in place the
injected server is **never spawned**, and Gemini itself reports `TOOL_NOT_VISIBLE`. So
`--allowed-mcp-server-names` is not only a repo-authored-server clamp: it is a hard gate on *every* MCP
server including ours.

**Consequence:** flipping `SupportsMcpServers: true` without putting the injected name in the allowlist ships
a reviewer that can never report. AI-899's `Gemini_McpAllowlist_IsCoupledToSupportsMcpServers` test asserts
that coupling in both directions; it was written as a guess and is now a measured requirement.

### 2.3 A FIXED injected name is repo-matchable — the same defect class as AI-899 §3.1d

The obvious way to satisfy §2.2 is to put the literal `KcapMcpRegistry.ReservedResultChannelId`
(`"kcap-flow-result"`) in the allowlist. **Measured: that re-opens repo-controlled process execution.**

A workspace whose own `.gemini/settings.json` declares an MCP server named `kcap-flow-result`:

* **loads under INHERITED trust with no `--skip-trust`** — measured in a fresh directory under a trusted
  parent, which is the production shape (a daemon worktree lives inside the operator's repo);
* **matches an allowlist containing `kcap-flow-result`**, because the name is a fixed literal in a public
  repository;
* **with nothing injected, its `submit_review_result` is CALLED** — the repo owns the channel outright;
* **with our server also injected under the same name, ours wins the tool dispatch — but the repo's impostor
  is still SPAWNED.** Its process runs as the daemon user, completes an MCP handshake and lists tools.

Ours winning dispatch is not a defence. `AI-899 §3.1`'s rule is that *any* repository-controlled facility
that can cause process execution before an explicit user-approved action must be clamped, and an impostor
that merely loses a race has already executed. The severity if it wins is worse still: it sits on the channel
the verdict arrives on, able to submit a fabricated `clean`, suppress real findings, or read the round token.

This is AI-899 §3.1d's collision class at a new sink. Same fix shape, and it is the reason §3.2 exists.

### 2.4 `--approval-mode yolo` is REQUIRED, not a nicety

Runs C and D differ by that flag alone. Without it Gemini emits `session/request_permission` before invoking
the **injected result-channel tool**, and with no human to answer, the reviewer cannot report at all. With
it, no interaction frame is emitted and the tool call lands.

The issue's design already prescribed the flag; this measures *why*, and it upgrades the flag from
"trust-at-spawn convenience" to a correctness requirement of the result channel.

It also settles the interaction policy — see §3.4.

### 2.5 Incidental facts worth recording

* **`mcpServers` is a REQUIRED array in `session/new`**, even when empty. Omitting it returns
  `-32603 Internal error` with `"expected array, received undefined"`. A schema error, not a capability
  signal — easy to misread as "this vendor rejects MCP".
* **A randomised server name does not break tool resolution.** Runs C and D used
  `kcap-flow-result-<32 hex>`; the prompt named the bare tool `submit_review_result` and the model built the
  namespaced call itself (`toolCallId: mcp_kcap-flow-result-<guid>_submit_review_result`). So §3.2's
  randomisation costs nothing at the prompt contract.
* **Usage arrives on the prompt result** as `_meta.quota.model_usage`, naming both `gemini-2.5-flash` and
  **`auto-gemini-2.5`** — a pricing sentinel. That is AI-1612, not this issue, but a Gemini reviewer will
  report an unpriceable model until it lands.
* Gemini spawns an MCP server **twice** per session (distinct pids, both handshaking; only the second
  receives `tools/call`). Any test asserting "spawned once" would be wrong.

## 3. Design

### 3.1 Descriptor changes

```csharp
public static readonly AcpVendorDescriptor Gemini = new(
    Vendor:              "gemini",
    ResolveBinaryPath:   cfg => cfg.GeminiPath,
    ResolveDefaultModel: _ => null,
    Argv:                ["--experimental-acp", "--skip-trust",
                          "--allowed-mcp-server-names", UnmatchableMcpNamePlaceholder],
    UnattendedTrustArgv: ["--approval-mode", "yolo"],          // §2.4 — required for the channel
    SupportsUnattended:  true,                                  // §5
    ModelSelector:       NoOpModelSelector.Instance,            // unchanged; AI-1612/AI-1417 own the model
    SupportsMcpServers:  true                                   // §2.1
);
```

`Argv` keeps the deny-all placeholder: it is correct for interactive hosting, where nothing is injected. The
review-flow launch replaces it — §3.2.

**`ReviewFlowMcpTransport` is deliberately not passed.** The constructor resolves it:

```csharp
Default when SupportsMcpServers => SessionNew,
Default                         => Unsupported,
```

so `SupportsMcpServers: true` already yields `SessionNew`, which is what line 130 of the factory tests before
sending `session/new.mcpServers`. Declaring it explicitly would be redundant and would add a second place to
keep in sync.

**These four flags cannot be changed independently — the constructor enforces it:**

* `UnattendedTrustArgv` must be empty while `SupportsUnattended` is false;
* `UnattendedInteractionPolicy` must be `Disabled` while `SupportsUnattended` is false, and **must be
  explicit** once it is true;
* `SessionNew` requires `SupportsMcpServers`.

So the descriptor edit is one atomic change, not a sequence. That is a better invariant than the one this spec
first assumed (§5) — it makes a half-configured reviewer unconstructable rather than merely untested.

`ModelSelector` stays `NoOpModelSelector`. AI-899 §3.3 measured that the write half of
`ConfigOptionModelSelector` is unverified and fails **silently** for Gemini; a reviewer that silently ran on
the wrong model would be worse than one that records `model=vendor-default`. Out of scope, named in §8.

### 3.2 The injected result-channel server name becomes per-launch unguessable

The core of this change, and the part §2.3 forces.

Today `AcpReviewFlowMcp` injects the channel under the fixed `KcapMcpRegistry.ReservedResultChannelId`. For
Gemini the *server name* must be unguessable, so the allowlist can name it without also naming anything a
repository can declare:

* the review-flow launch derives `kcap-flow-result-{Guid.NewGuid():N}` **once per launch** (not per process —
  two concurrent reviewers must not share a name, so nothing observed in one run helps another);
* the injected server carries that name;
* `--allowed-mcp-server-names` carries **exactly** that name. Note the mechanism: `SubstituteUnmatchableNames`
  runs on `descriptor.Argv` *before* the `if (ctx.IsReviewFlow)` block, so by the time the review arm runs the
  allowlist already holds a `kcap-deny-{guid}` value. The review arm therefore **replaces that value**, it does
  not append — appending would leave two allowlist entries and the deny-all one is harmless but misleading.
  Fail-closed is the useful property here: deny-all is what a launch gets by default, and only the review arm
  widens it;
* the *tool* names inside the server are unchanged (`submit_review_result`, `send_flow_message`), so the
  folded reviewer prompt and the `review-flows` skill need no edit — measured in §2.5.

A repository cannot match a per-launch GUID, so its own server is refused by the allowlist and never spawned:
the §2.3 impostor is closed at the same gate that admits ours.

**Scope of the rename.** `ReservedResultChannelId` is load-bearing elsewhere — `KcapMcpRegistry`'s
reservation check and Copilot's `--available-tools` builder both compare against it. So the randomisation is
introduced as a **per-launch alias resolved at the Gemini launch seam**, not by changing the constant. The
reserved-name comparisons keep working on the canonical id; only the string handed to Gemini and its
allowlist varies. A vendor-neutral rename is a bigger change and is not needed to fix Gemini.

### 3.3 `UnattendedTrustArgv` composition, and what must never happen

`--approval-mode yolo` is added on ReviewFlow launches only, via the existing `UnattendedTrustArgv` seam. Two
invariants:

* it must **never** appear on an interactive hosted launch. AI-899 §3.5 deliberately omits `--approval-mode`
  from `Argv` so hosted Gemini behaves as the user's own session does; `yolo` there would silently grant
  blanket approval to an interactive agent.
* it must **never** be combined with the legacy `--yolo` flag (the issue's original note). Only
  `--approval-mode yolo` is passed.

### 3.4 `UnattendedInteractionPolicy.Fail`

`Fail` — Cursor's strict contract — rather than Copilot's `AutoApprove`. §2.4 measured that with
`--approval-mode yolo` Gemini emits **no** interaction frame at all, so receiving one means the launch
contract has regressed (a flag dropped, a vendor behaviour change), and the honest response is to reap the
reviewer rather than paper over it by auto-approving whatever was asked.

The issue's design worried that Gemini is the harness most prone to asking. That is a property of the
*default* approval mode, which this launch does not use — and if it turns out to ask anyway, `Fail` surfaces
it as a failed round with a reason instead of an unattended agent quietly approving its own requests.

### 3.5 Reviewer prompt: no new wording needed

Item 4 of the issue asks whether the `review-flows` skill's hosted-reviewer branch routes a Gemini reviewer
to `submit_review_result`. It does, and it needs no Gemini branch: the skill already instructs a reviewer with
no flow tools to deliver via `submit_review_result`, and §2.5 measured that the bare tool name resolves under
a randomised server name. Verified by test rather than assumed — §6.

## 4. What changes, file by file

| file | change |
|---|---|
| `src/Capacitor.Cli.Daemon/Acp/AcpVendorDescriptor.cs` | Gemini: `SupportsUnattended: true`, `SupportsMcpServers: true`, `UnattendedTrustArgv: ["--approval-mode","yolo"]`, `UnattendedInteractionPolicy: Fail` |
| `src/Capacitor.Cli.Daemon/Services/AcpReviewFlowMcp.cs` | derive the per-launch channel alias; inject the channel under it |
| `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` | substitute the alias into `--allowed-mcp-server-names` for a review-flow launch, replacing the deny-all placeholder |
| `test/…/Acp/AcpVendorDescriptorTests.cs` | descriptor assertions + the allowlist coupling, extended for the review-flow arm |
| `test/…/Services/AcpReviewFlowMcpTests.cs` | alias uniqueness, canonical-id preservation, allowlist agreement |
| `docs/HOSTED_AGENTS.md` (or nearest) | Gemini reviewer row |

No projection or server change: reviewer vendor routing is already vendor-neutral (AI-1488).

## 5. Ordering — the descriptor flip is atomic, so verification comes before it

An earlier draft of this section had `SupportsUnattended: true` land last, after E2E. **The constructor makes
that impossible** (§3.1): the capability flags, the trust argv and the interaction policy must all be
consistent or construction throws. So the ordering is about what is *proven* before the one descriptor commit,
not about staging the flags:

1. the per-launch channel alias and the allowlist replacement, with unit tests + mutants — these are testable
   with no vendor and no descriptor change, because they operate on the argv the factory builds;
2. the descriptor edit, atomic: `SupportsUnattended` + `SupportsMcpServers` + `UnattendedTrustArgv` +
   `UnattendedInteractionPolicy`;
3. the gated live certification (§6) and the E2E round.

The risk the old ordering was trying to manage — the server advertising Gemini as a reviewer before the
channel works — is real, and step 1 is what retires it: the allowlist/alias agreement is asserted before
anything advertises the vendor.

## 6. Test plan

**Unit (deterministic, no vendor):**

1. `Gemini_UnattendedTrustArgv_IsApprovalModeYolo` — exact argv, and that it is absent from `Argv`.
2. `Gemini_InteractiveLaunch_HasNoApprovalMode` — §3.3's first invariant, asserted on a non-review launch.
3. `Gemini_NeverPassesLegacyYolo` — `--yolo` appears nowhere.
4. `ReviewFlowChannelAlias_IsUniquePerLaunch` — two launches, two aliases.
5. `ReviewFlowChannelAlias_MatchesTheAllowlistExactly` — the injected server name and the single allowlist
   value are the same string. This is §2.2's coupling: if they can disagree, the reviewer cannot report.
6. `ReviewFlowChannelAlias_PreservesTheCanonicalReservedId` — `KcapMcpRegistry`'s reservation check and
   Copilot's tool-id builder still see `kcap-flow-result`.
7. `ReviewFlow_ReplacesTheDenyAllPlaceholder` — the placeholder is gone from a review launch's argv, and
   still present on an interactive one.
8. `Gemini_AllowlistCoupling` (extend AI-899's) — with `SupportsMcpServers: true`, the review-flow allowlist
   must be the injected names; the interactive allowlist must still be unmatchable.

**Mutation-proof each guard.** Every assertion above must fail when its guard is removed — in particular #5,
whose failure mode is a reviewer that launches happily and can never report.

**Live certification (gated, `Skip.Unless`, opt-in env var):** a real `gemini --experimental-acp` launch that
injects a channel under a randomised name and asserts the tool is invoked, plus the negative control that the
deny-all placeholder blocks it. Same shape as AI-1463's memory cert. These are the §2 probes, promoted to
tests so the finding cannot silently rot.

**E2E (manual, recorded in the PR):** `start_review_flow(kind="spec-review", vendor="gemini")` completing a
real round unattended — findings→clean on both kinds, zero human-routed interactions, reviewer session
captured once and reaped.

## 7. Definition of done

* `start_review_flow(kind="spec-review", vendor="gemini")` completes a real round unattended, end to end.
* A repository declaring its own `kcap-flow-result` MCP server is **not spawned** during a Gemini review —
  asserted, with the positive control that the real channel still loads.
* Interactive hosted Gemini is unchanged: no `--approval-mode`, deny-all allowlist intact.
* Every guard mutation-proven.

## 8. What this issue does NOT do

* **Borrowed review for Gemini** — `SupportsBorrowedReviewFlow` stays false. Needs the sandbox credential
  question (§1), which is Kiro's AI-1410 blocker; a Gemini reviewer without it still reviews an owned
  worktree, which is what every shipped reviewer except Copilot does.
* **Model override** — `NoOpModelSelector` stays. AI-899 §3.3: the write half is unverified and fails
  silently. A reviewer records `model=vendor-default` until AI-1417/AI-1612 resolve it.
* **AI-1612** — a Gemini reviewer reports `auto-gemini-2.5`, a pricing sentinel, so its cost is unpriceable.
  Known and accepted, not discovered later.
* **The vendor-neutral channel-name rename** — §3.2 keeps the canonical `ReservedResultChannelId` and aliases
  only at the Gemini seam. If Cursor or Kiro need the same protection, that is the moment to generalise, and
  AI-1632's trust probe is what would establish whether they do.
