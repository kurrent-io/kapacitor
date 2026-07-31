# AI-1413 — Gemini CLI as an unattended review-flow reviewer

**Status:** rev 2, 2026-07-31, against `gemini 0.53.0` and `kcap-cli` main (`bfb7bb2`).
**Implementation-ready.** Every claim marked **measured** was observed against a live `gemini
--experimental-acp` process; the probes and their controls are in §2.

**Rev 2** answers a codex spec review that found six real defects in rev 1 — three of them High. The
substantive ones: rev 1 had no single non-bypassable source for the wire alias (§3.2a now defines one);
rev 1's central security claim outran its evidence (§2.6 now measures the allowlist's matching
semantics); and rev 1 relied on "the trust argv is only appended under `IsReviewFlow`" as if that
enforced a "must never" property, which it does not (§3.3a adds a final-argv boundary check). Rev 1's
test #7 also contradicted the code it was testing.
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

**What this run does NOT establish** — rev 1 wrote "a repository cannot match a per-launch GUID" on the
strength of it, which review correctly called out as a generalisation. One exact name matching and one
different literal being blocked says nothing about prefix matching, globbing, or settings merge. §2.6
measures those.

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

### 2.6 The allowlist is exclusive EXACT-match — which is what makes a GUID sufficient

Rev 1's claim needed its own measurement, so it got one. One workspace, four hostile servers declared in
its own `.gemini/settings.json`, each writing a separate marker; the allowlist held only our injected
name. Under inherited trust, `--approval-mode yolo`:

| hostile repository server name | process ran? |
|---|---|
| `kcap-flow-result` — a **prefix** of the allowed name | **no** |
| `kcap-flow-result-<a different guid>` — same shape | **no** |
| `kcap-flow-result-*` — wildcard attempt | **no** |
| **byte-identical to the allowlisted name** | **YES — spawned** |
| our injected server (positive control) | **YES — spawned + tool called** |

Those four samples show that a prefix, a wildcard literal, and a same-shaped different GUID are all
refused, and that a byte-identical name is admitted. **They do not by themselves license a universal
claim** — review said so, and rev 1 had already over-generalised once. §2.7 grounds the rule in Gemini's
own matcher instead, which is what actually makes it a rule.

Read together: admission is **name equality and nothing else**. That is what makes §3.2's per-launch GUID
sufficient — and it also locates the entire defence in one
property, which the design must then protect deliberately:

Review caught rev 2 stating this requirement in a way §2.8 contradicts — "must not leak into anything the
repository can read", when the reviewer demonstrably CAN read it from its own argv, and the model must
receive enough tool metadata to route the namespaced tool at all. Whole-launch secrecy was never the
property. The normative property is temporal:

* **unpredictable until repository MCP admission is frozen.** The repository's declarations are read at
  process start (§2.8); the alias must be unguessable *at that instant*, which is what makes a v4 GUID
  sufficient and a counter, timestamp, session id or any repository-derived value insufficient;
* **not reused by a later launch.** A reviewer can write a `settings.json` naming its own alias, and a
  *subsequent* launch in that worktree will read it. Freshness per launch is what makes that stale name
  harmless — so per-launch, never per-daemon or per-worktree, is load-bearing;
* **kcap itself must not persist the alias into the worktree** (or anywhere a later launch's repository
  read would find it), for the same reason.

What is explicitly NOT required: that the alias stay hidden from the reviewer process. It cannot, and
§2.9 explains why that does not matter — a reviewer able to act on it already has strictly more.

**Duplicate CLI options** are made irrelevant by §3.3a asserting exactly one option with exactly one
value — a design that does not depend on the vendor's duplicate handling beats one that measures it and
then depends on it. **Repository-config precedence is a different question and is NOT made irrelevant that
way** (review's correction): if Gemini unioned a repository-settable admission key with the CLI value, the
final argv could satisfy §3.3a perfectly while a repository server was still admitted. §2.7 settles it.

### 2.7 Grounded in the matcher, not in samples: exact equality, and the CLI value REPLACES settings

Read from the installed bundle (`gemini-cli` 0.53.0), which is stronger than any number of samples.

**The matcher is `Array.prototype.includes` on the raw name:**

```js
isBlockedBySettings(name3) {
  const allowedNames = this.cliConfig.getAllowedMcpServers();
  if (allowedNames && allowedNames.length > 0 && !allowedNames.includes(name3)) {
    return true;
  }
  const blockedNames = this.cliConfig.getBlockedMcpServers();
  if (blockedNames && blockedNames.length > 0 && blockedNames.includes(name3)) {
    return true;
  }
  return false;
}
```

`includes` is SameValueZero, so for the values delivered to `isBlockedBySettings` the comparison is
**exact and case-sensitive, with no glob syntax and no regex**. That is the claim the snippet supports, and
review was right that rev 2 said more: "no Unicode normalization, no vendor aliasing" would need the
producers of *both* operands traced, which this read does not do. Narrowed accordingly — the property
relied on is exact equality of the values as delivered, and §2.6's prefix/glob negatives are the live
evidence that nothing upstream re-writes them into patterns on the CLI path.

Two further facts from the option definitions, both of which change the validator (§3.3a):

```js
.option("allowed-mcp-server-names", { type: "array", string: true, nargs: 1, coerce: coerceCommaSeparated })
.option("approval-mode",            { type: "string", nargs: 1,
                                      choices: ["default", "auto_edit", "yolo", "plan"] })
```

* **the allowlist is comma-coerced.** One option token whose value contains a comma yields **several**
  allowed names. So "exactly one value" has to be asserted on the **coerced list**, not on the raw token —
  a GUID hex contains no comma, so we are safe by construction, but only if the assertion is written at the
  right level. It is also `type: "array"`, so repeated options accumulate, which is why "exactly one
  option" is a separate assertion from "exactly one name".
* **`approval-mode` is `choices`-constrained**, so an unrecognised value fails at Gemini's own parser. A
  *recognised but wrong* value does not — `--approval-mode default` is perfectly valid and silently
  restores prompting, which is precisely review's finding 2.

It also exposes a trap worth stating: **`length > 0`**. An **empty** allowlist is not "deny all", it is
"no allowlist" — every server is admitted. That is the same hazard AI-899 §3.1c recorded as "never the
empty string", now visible in the mechanism, and it is why §3.3a asserts exactly one *value* and not
merely at most one.

**The CLI value replaces the settings value — it does not union with it:**

```js
allowedMcpServers: mcpEnabled ? argv.allowedMcpServerNames ?? (loadedSettings ? loadedSettings.getConsolidatedAllowedMcpServers() : settings.mcp?.allowed) : void 0,
blockedMcpServers: mcpEnabled ? argv.allowedMcpServerNames ? void 0 : ... : void 0,
```

`??` is null-coalescing. When `--allowed-mcp-server-names` is present, `settings.mcp.allowed` — including a
repository-level one — is **never consulted**; the *excluded* list is dropped too. So a repository cannot
widen admission through its own settings while we pass the flag, which is exactly review's finding 1, and
the answer is negative. The dependency is now on one documented operator: that the flag is always present
on a review launch, which §3.3a asserts.

### 2.8 The alias only has to stay secret for the launch — and it does

Review's sharpest finding: the alias is unguessable but not *invisible*. It is in the reviewer's own
process argv, and a reviewer under `--approval-mode yolo` can run shell commands and write files. A
reviewer prompt-injected by the repository it is reviewing could therefore read its own argv, learn the
allowlisted name, write a `.gemini/settings.json` declaring a server with that exact name, and trigger the
byte-identical collision of §2.6.

Whether that works turns on a temporal property. **Measured, in one process:**

| stage | result |
|---|---|
| 1 — control: our channel injected and called | **reported** |
| 2a — hostile server written **mid-session** under the exact allowlisted name, then prompted again | **not loaded** |
| 2b — hostile server present, **new `session/new` in the same process** | **not loaded** |

**What that shows, stated no wider:** the repository's declarations are not re-read across the two
transitions tested — a subsequent prompt in the same session, and a fresh `session/new` in the same
process. Rev 3 wrote "evaluated once at process start … never hot-reloaded", and review was right that this
is a third instance of the same over-generalisation: the probe does not locate when the snapshot is taken,
and it does not exclude a delayed watcher, a debounce, a workspace-switch path, or another lifecycle
trigger. The bundle has **seven `scheduleMcpContextRefresh` call sites and a 300 ms coalescing delay**, so
I am not in a position to claim exhaustiveness and will not.

**So the design does not rest on it.** §2.9 is why: an actor able to *use* a reload — a reviewer running
repository-influenced instructions with tool access — already has strictly more than the reload would give
it. No-reload is therefore recorded here as **defence in depth against the case it does cover**, and the
security argument lives in §2.9 instead.

What the alias actually defends, precisely: a repository that can **declare** but not **execute** — its
`settings.json` is read before the model acts, so an exact-name collision is its only lever, and a fresh
GUID removes it. That is the case §2.3 measured, and it is a real one.

### 2.9 The security property of unattended review, stated rather than implied

Review's sharpest point, and rev 3 got the boundary wrong. §2.8 argued peer same-user processes are outside
the threat model because they can already read the token store — while simultaneously accepting that the
repository reaches the model's tool use. Under blanket `--approval-mode yolo` those are the same actor:
prompt-injected repository content can run shell commands, and therefore **create a daemon-user process,
read the token and config stores, inspect a concurrent reviewer's argv, persist, and exfiltrate.** A
repository-induced process is not cleanly outside the boundary; it is reachable through the in-scope path.

So this is recorded as an **accepted security property** rather than defended:

> Starting an unattended review in a daemon-owned worktree grants prompt-injected repository content
> **arbitrary code execution with the full authority of the daemon user**.

Rev 4 wrote "for the duration of the review". Review was right that this is false comfort, and the paragraph
above it already contradicted it by admitting persistence and exfiltration. **The effects are not bounded to
the review's lifetime** unless descendant-process cleanup, credential scoping and revocation are separately
guaranteed, and none of them is. Spelled out:

* **Confidentiality** — the token store and config directory are readable, so any credential the daemon holds
  (server token, vendor logins, integration credentials) can be copied. A copied token stays valid until it
  expires or is revoked; reaping the reviewer does not touch it.
* **Integrity** — daemon-user writes reach *other* worktrees, the daemon's own configuration and state, the
  installed CLI, and shell profiles. So one review can alter a later review, a later hosted agent, or the
  operator's own environment. This is the cross-review consequence, and it is why §2.6's "no reuse by a later
  launch" is necessary but nowhere near sufficient.
* **Availability / persistence** — a launched child, a scheduled job, or a service unit can outlive the
  review entirely. Nothing in this design reaps a process tree it did not create.

**And it grants no verdict integrity** (review's separate point, and an important one): once repository
content can steer the reviewer, it can simply ask the *legitimate* model to call `submit_review_result` with
a fabricated `clean`. It never needs to impersonate the channel. So the per-launch alias must not be
misread — it prevents a repository *declaration* from starting a process and capturing the channel before
the model acts; it does nothing about the model being talked into a false verdict afterwards. **A review
result is not authenticated review output**, and "the real channel won dispatch" says only that the bytes
came from our server, not that they reflect an honest review.

Three things follow, and none of them is new to this issue:

* **It is a property of unattended review as already shipped**, not something Gemini introduces. Cursor's
  reviewer passes `--force` (command approval suppressed) and Claude's and Codex's reviewers run with
  equivalent posture. Anything this spec claimed about Gemini being narrower would be false.
* **Sandboxing is the only thing that changes it**, which is exactly why Copilot's borrowed review exists —
  `BorrowedReviewSandbox` plus a brokered credential (AI-1584, AI-1589) is the one path that does not grant
  this, and it is the path §1 explains Gemini cannot take yet.
* **The alias defence is therefore not weakened by it, because it was never aimed at it.** §2.3's attacker
  declares a server and cannot execute; §2.9's attacker executes and does not need to declare one. Different
  actors, and conflating them is what made rev 3's boundary statement dishonest.

If that accepted property is not acceptable, the correct response is not a better alias — it is to withhold
Gemini's reviewer until it can be sandboxed, and to revisit the shipped reviewers on the same grounds. That
is a product call, not a design detail.

**Audit integrity is compromised too**, which rev 5 omitted: daemon-user writes reach the session captures,
the local logs, and the installed reviewer itself. So after a compromise neither the verdict nor its local
evidence is trustworthy — you cannot use the recorded transcript to establish what happened, because the
same authority that ran the code could edit the record.

Rev 5 then said the story "has to rest on server-side data", which review correctly called overselling: the
accepted compromise **includes theft of the daemon's server token**, so unless the server's records are
append-only or tamper-evident and that token carries no update, delete or impersonation authority, remote
evidence can be forged or erased too. Neither property is established here. The honest consequence is
therefore stronger and less comforting: **local audit is untrustworthy, and reliable post-incident
reconstruction may simply be unavailable** — how much survives depends on the server token's scope and on
server-side retention/immutability, neither of which this issue examines or changes.

**Who accepts it, and where.** Review's correction here is sharp and I had it wrong: the assets at risk —
the daemon's credentials, other worktrees, host persistence, integration tokens — belong to the **daemon
operator**, who is not necessarily the repository owner or the PR approver. A sign-off from me accepts the
risk only for deployments and credentials I control; it does not inform or authorise a downstream operator
who later selects Gemini as a reviewer, or inherits it as a default.

So the acceptance has two parts, and only the first is a sign-off:

1. **For this repository and its own daemons**, the repository owner accepts it explicitly on the issue and
   in the PR description. §7 keeps that as a merge gate: "CI is green" does not authorise enabling an
   unsandboxed unattended reviewer for a new vendor.
2. **For any other operator, the consent event is enabling it — and that has to be enforced, not documented.**
   Review's correction, and it is right: a non-Gemini default plus a docs paragraph is informed guidance, and
   an API caller can still ask for `vendor: "gemini"` explicitly while being a different person from the
   operator whose credentials and host are exposed. So this issue adds a **daemon-side capability gate**:

   ```
   Reviewers:Gemini:Unattended:Enabled   (daemon config, DEFAULT FALSE)
   ```

   * it lives in the **daemon's** configuration, because the daemon operator is the affected principal —
     not in the server's flow settings, which the requester controls;
   * **checked twice**: at availability advertisement, so a disabled daemon never offers Gemini as a reviewer
     and the server's vendor-capable-daemon selection simply does not see it; and again **pre-spawn**, so an
     explicit `vendor: "gemini"` request cannot bypass advertisement — a direct request at a disabled daemon
     is refused with a coded error before any process starts;
   * **enabling it is the consent event.** The config key's own documentation states §2.9's property, so the
     operator reads it at the moment of deciding rather than in a spec they will never open.

   **Fail-closed, and revocable** (review's finding 4): the flag reads false when the key is absent,
   malformed, or unreadable — anything but an explicit affirmative is off. It is read from the
   **daemon-local** configuration only; no server- or requester-supplied value can override it, because the
   requester is precisely the party the gate protects the operator from. And the **pre-spawn read uses
   current authoritative state, not a value captured at factory construction**, so disabling it stops new
   launches even while the server still holds a cached capability advertisement — a stale advertisement must
   not be able to spend consent that has been withdrawn. Tests: absent, false, malformed, true, plus a
   stale-advertisement direct request against a now-disabled daemon.

   **Placement, because "checked pre-spawn" is not a location** (review's finding 2, and they are right that
   rev 7 named a helper rather than establishing the property). `ValidateAndBuildReviewFlowMcp` is the wrong
   home: its name says result-channel, and a transport change or a path supplying prebuilt MCP state would
   route around it. The authoritative check goes in the **single factory operation immediately before the
   process is created** — the same place `CreateProcessStartInfo`'s output is handed to the spawn — and is
   keyed on the **resolved descriptor's** vendor identity, never on requester-supplied text, so a vendor
   alias or case variant cannot slip past it. The advertisement check stays, explicitly as an optimisation
   rather than a boundary. Tests call the lowest exposed spawn seam directly with the gate false and assert
   the process-start seam is never reached, across every accepted Gemini selector spelling, while interactive
   Gemini and other vendors are unaffected.

### 3.2b The certified matcher belongs to a Gemini VERSION, so the gate carries a version too

Review's sharpest finding this round, and it is the AI-1592 lesson in a new place: §2.7's exact-match and
no-union conclusions were read from the **0.53.0** bundle, and nothing in rev 7 tied them to the binary the
daemon actually launches. `GeminiPath` resolves whatever is installed. So a `npm -g update` could change the
matcher to prefix-matching, or make settings union with the CLI value, or change empty-list semantics —
while the capability flag, set months earlier, keeps advertising the reviewer and keeps carrying an
operator's consent to a mechanism that no longer holds.

So the capability decision includes the resolved binary's version:

* a **supported version range** is declared alongside the descriptor, and it means *"the §2.7 matcher
  behaviour has been certified for these versions"* — not "these versions are new enough";
* the version is resolved from the binary the launch will actually use, and checked **at availability and
  again pre-spawn**, on the same fail-closed footing as the flag;
* an **unknown or out-of-range version makes Gemini unavailable as a reviewer** until it is explicitly
  certified — it does not warn and proceed. An upgrade that invalidates the security mechanism should take
  the feature offline, which is the safe direction, and the operator's remedy is to re-run §6.1's gated
  certification against the new binary and widen the range.

This is deliberately stricter than the AI-899 hosting path, which happily runs any installed Gemini: hosting
degrades to a broken agent, whereas an unattended reviewer with a broken MCP gate degrades to
repository-controlled process execution. Different consequence, different posture.

That second part is a real scope addition and it is listed in §4 and §7 rather than assumed: without it,
this design would be accepting a risk on behalf of people who never saw it.

Worth being precise about what changes and what does not: this property is already live for Cursor, Claude
and Codex reviewers, so enabling Gemini widens the vendor surface without changing the class of exposure.
The decision is therefore "do we keep accepting this while adding a vendor", not "do we introduce it".

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

### 3.2a One launch-scoped identity, produced once and consumed by both sinks

Rev 1 said "derived once per launch" and then, in §4, assigned derivation to `AcpReviewFlowMcp` and argv
substitution to `AcpHostedAgentRuntimeFactory` **without saying what passes between them**. Review was
right that this is the defect, not a detail: with no shared value an implementation can generate two
GUIDs, or reconstruct one from the other, or rename the server while the allowlist keeps the deny value —
and tests #5/#6 would still pass at helper level while the emitted launch disagrees.

So the alias is an explicit launch-scoped value with both names on it:

```csharp
/// The result channel's identity for ONE launch. CanonicalId is what every reserved-name comparison
/// uses (KcapMcpRegistry's reservation check, Copilot's --available-tools builder); WireName is the
/// only string that ever reaches a vendor. Created once per launch and threaded — never re-derived,
/// because two derivations is the defect this type exists to make unrepresentable.
internal sealed record ReviewChannelIdentity(string CanonicalId, string WireName) {
    /// PRODUCTION entry point. Takes the vendor and NOTHING else — no launch context, and no delegate
    /// either. Rev 3 passed a Func&lt;Guid&gt;, which review correctly demolished: a delegate closes over its
    /// caller's scope, so `ForLaunch(vendor, () =&gt; Hash(ctx.SessionId))` was legal and the tests would
    /// still have passed with their own stub. The signature had moved the derivation boundary outward, not
    /// closed it. There is exactly one production overload and it reaches its own entropy.
    public static ReviewChannelIdentity ForLaunch(string vendor) => FromGuid(vendor, Guid.NewGuid());

    /// TEST-ONLY seam. Takes a concrete Guid, not a factory — a value cannot close over anything, so no
    /// caller can smuggle launch context through it. This is what gives §6 row 12 a deterministic oracle
    /// while leaving derivation unrepresentable on the production path.
    internal static ReviewChannelIdentity FromGuid(string vendor, Guid g) {
        if (vendor != Vendors.Gemini)
            return new(KcapMcpRegistry.ReservedResultChannelId,   // unchanged for every other vendor
                       KcapMcpRegistry.ReservedResultChannelId);

        // No fallback, predictable or otherwise: the security property of §2.6 is that this string is
        // unguessable at admission time, so a degraded value is worse than no reviewer.
        if (g == Guid.Empty)
            throw new InvalidOperationException(
                "Refusing to launch a Gemini reviewer: the result-channel alias would be predictable. The "
              + "MCP allowlist is an exact-name gate (spec §2.7), so a predictable name is a "
              + "repository-impersonation hole.");

        return new(KcapMcpRegistry.ReservedResultChannelId,
                   $"{KcapMcpRegistry.ReservedResultChannelId}-{g:N}");
    }
}
```

The factory calls `ForLaunch(vendor)`. **The mutant lives at that call site**, not only inside the type —
review's point: a test that only exercises `ForLaunch` cannot catch a caller that reaches for the internal
overload with a derived value. So §6 row 12 asserts the production call site uses the context-free overload,
and row 15 is the mutant that swaps it for `FromGuid(vendor, DeriveFrom(ctx))` and must go red.

:

```csharp
```

Rules the implementation must satisfy, each of them testable at the launch boundary:

1. **`ForLaunch` is called exactly once per launch**, in the factory, before either sink runs. The instance
   is passed to `AcpReviewFlowMcp` (which stamps `WireName` onto the injected server spec) and to the
   allowlist substitution (which writes the same `WireName` into the argv). Neither derives its own.
2. **`WireName` is the ONLY string handed to a vendor.** `CanonicalId` never appears in argv or in a
   `session/new` payload for Gemini.
3. **`CanonicalId` is what every reserved-name comparison sees**, so `KcapMcpRegistry`'s reservation check
   and Copilot's tool-id builder are untouched — and non-Gemini vendors get `WireName == CanonicalId`, so
   their behaviour is byte-identical to today.

**Acceptance is at the launch artifact, not at the helper** (review's point, and the fix for §6's
vacuity): one launch is built, and the assertions read the **final argv** and the **serialized
`session/new` request** from that same launch — exactly one allowed name, exactly one result-channel
server, those two strings equal, and the canonical paths still resolving on `CanonicalId`.

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

### 3.3a The invariants are enforced at the final argv, not by where the array is appended

Rev 1 justified §3.3 with "the trust argv is appended only under `ctx.IsReviewFlow`". Review was right
that this proves only what *that array* does — it does not reject `--approval-mode yolo` arriving from
`descriptor.Argv`, from a caller-supplied argument, or from a future vendor-specific branch, and it does
not reject legacy `--yolo` from anywhere. A convention is not an invariant.

So the composed argv is validated once, at the launch boundary, after every contributor has run:

**Three revisions of this section hardened a guard against an input that does not exist.** Rev 3 counted
tokens, rev 4 parsed values, rev 5 refused non-canonical spellings — and each time review correctly pointed
out the guard could not be complete while "arbitrary raw caller argv" was accepted. The thing none of us
checked, including me, is whether that surface exists. **It does not.** Read from the code:

`RuntimeStartContext` — the only launch input — has **no argv field at all**. Its members are typed:
`AgentId`, `Vendor`, `SourceRepoPath`, `Worktree`, `Prompt`, `Model`, `Effort`, `Tools`, `IsReview`,
`IsReviewFlow`, `ServerUrl`, `DaemonBridgeUrl`, `CapacitorPath`, `McpAllowlist`, `Work`. And the complete set
of contributors to the argv list is three lines:

```csharp
var argv = SubstituteUnmatchableNames([.. descriptor.Argv]);   // compile-time constant
if (ctx.IsReviewFlow) {
    argv.AddRange(descriptor.UnattendedTrustArgv);             // compile-time constant
    if (descriptor.ReviewFlowMcpTransport == AcpReviewFlowMcpTransport.CopilotAdditionalConfig) { … }
}                                                              // Copilot only — never Gemini
```

There is no config-supplied extra-args setting either. So for a Gemini launch the argv is **two compile-time
constants plus two GUIDs this code generates**. Nothing untrusted reaches it.

That changes what this section should be, and simplifies it:

* **No runtime input filter is warranted.** A validator whose job is to reject hostile tokens has nothing to
  reject; it would be dead code asserting a property the type system already gives. Rev 5's
  `IsGuardedToken` grammar list — which review rightly said could never be provably complete — is deleted
  rather than extended, because the question it answered was the wrong one.
* **The invariants become test-time assertions over the built argv**, which is where they belong: they are
  properties of two constants and one branch, so a unit test that builds a launch and reads the result
  proves them completely. §6's table already reads the launch artifact, so no test changes shape — only
  their justification does.
* **One runtime assertion is kept, for a different and stated reason.** Not to filter input, but so that a
  *future* contributor to this argv cannot silently break the invariant:

```csharp
/// NOT an input filter — nothing untrusted reaches this argv (see above). This asserts an invariant about
/// code, so that adding a fourth contributor to the list cannot silently produce a Gemini launch that
/// prompts for approval or widens the MCP gate. It should be unfalsifiable today; if it ever throws, a
/// contributor was added without reading this section.
static void AssertGuardedOptionsAreCanonical(
        IReadOnlyList<string> argv, bool isReviewFlow, string vendor, GeminiLaunchIdentity id) { … }
```

That is the AI-899 lesson restated: an invariant that holds at every interpolation is worth more than one
applied where it is currently needed, because the next value added inherits it. The difference from rev 5 is
honesty about which threat it addresses — a future edit, not an attacker.

**Consequently, several of review's findings on this section are answered by the input surface rather than by
the guard**, and I would rather say so than quietly keep a guard that implies otherwise: a caller cannot
append a second `--allowed-mcp-server-names`, cannot supply `--approvalMode`, cannot use dot-notation or an
alias, and cannot supply a byte-identical canonical pair to defeat provenance — because a caller cannot
supply argv tokens at all.

### 3.3b One launch identity carries every generated name

Review found `DenyAllNameEmittedForThisLaunch` used in rev 5's validator but never defined — a real gap, and
the same single-source discipline §3.2a applies to the channel alias applies here. Both generated names live
on one launch-scoped instance:

```csharp
/// Every per-launch generated name for one Gemini launch, created ONCE before any argv is composed and
/// threaded to every consumer. Two separate generators is the defect this type exists to prevent — the
/// deny-all name had exactly that shape in rev 5, where the validator referenced a value the factory
/// generated inline during substitution and never handed over.
internal sealed record GeminiLaunchIdentity(
    string CanonicalId,    // kcap-flow-result — what reserved-name comparisons see
    string WireName,       // kcap-flow-result-<guid> — the review channel, the only name a vendor sees
    string DenyAllName     // kcap-deny-<guid> — the interactive allowlist value
);
```

`SubstituteUnmatchableNames` takes `DenyAllName` rather than generating one, so the value in the argv and the
value the assertion expects are the same object — not two derivations that happen to agree.

**Construction, because the record shape is not the guarantee** (review's point, and the deny name is the
*pre-existing* barrier — a fixed, empty, reused, comma-bearing or derived `DenyAllName` reopens AI-899's
original repository-MCP hole even while every argv-equality test passes):

```csharp
internal sealed record GeminiLaunchIdentity {
    // Private ctor: production cannot construct an arbitrary identity, only ask for a fresh one. Rev 6
    // showed the record and left construction unspecified, which is where a degraded deny name would slip in.
    private GeminiLaunchIdentity(string canonicalId, string wireName, string denyAllName) { … }

    /// The ONLY production entry point. Two INDEPENDENT v4 GUIDs — the channel alias and the deny-all name
    /// must not be derived from each other, or learning one yields the other.
    public static GeminiLaunchIdentity ForLaunch() => FromGuids(Guid.NewGuid(), Guid.NewGuid());

    /// Test-only seam: concrete values, so no caller can smuggle context through a factory delegate.
    internal static GeminiLaunchIdentity FromGuids(Guid channel, Guid deny) {
        if (channel == Guid.Empty || deny == Guid.Empty || channel == deny)
            throw new InvalidOperationException(
                "Refusing to launch a Gemini reviewer: a generated launch name would be predictable or "
              + "reused. The MCP allowlist is an exact-name gate (§2.7), so either name being guessable is "
              + "a repository-impersonation hole.");

        return new(KcapMcpRegistry.ReservedResultChannelId,
                   $"{KcapMcpRegistry.ReservedResultChannelId}-{channel:N}",
                   $"kcap-deny-{deny:N}");
    }
}
```

`channel == deny` is refused too: independence is a property worth asserting rather than assuming, since a
lazy refactor sharing one GUID would make the deny name derivable from the alias the reviewer can read.

### 3.3c The closed input surface needs an oracle, not just a paragraph

Review's second finding, and it is fair: "two compile-time constants" is loose. `descriptor.Argv` and
`UnattendedTrustArgv` are `ImmutableArray<string>` — so immutability is a type guarantee rather than a
convention, and that is worth asserting — but an ordinary launch-output test proves *this example*, not the
architectural claim that no argv channel exists and that a future fourth contributor cannot add one.

So the premise gets tested as a premise, three ways:

1. **No extra-argv surface** — a test asserting `RuntimeStartContext` exposes no member of an argv/arguments
   shape, and that no daemon-config key supplies extra arguments. It fails the moment someone adds one, which
   is the point at which every conclusion in §3.3a would need revisiting.
2. **Descriptor argument collections are immutable and code-owned** — `ImmutableArray<string>` on both, and no
   public mutator or setter reachable from a launch path.
3. **Whole-vector template, not a guarded-key scan.** The strongest of the three, and review is right that it
   is better than what rev 5 had: a Gemini launch's **complete** final argv is compared against a structural
   template in which only the two per-launch names vary:

   ```
   interactive : ["--experimental-acp", "--skip-trust",
                  "--allowed-mcp-server-names", <DenyAllName>]
   review      : ["--experimental-acp", "--skip-trust",
                  "--allowed-mcp-server-names", <WireName>,
                  "--approval-mode", "yolo"]
   ```

   A template over the entire vector **fails on any new token whatever its spelling**, so it needs no model
   of the vendor's option grammar — which is exactly why it succeeds where three revisions of a guarded-key
   parser failed. It also makes the retained runtime assertion honest and complete: it compares the whole
   vector, so "a fourth contributor was added" is precisely what it detects.

The retained runtime assertion is therefore this template comparison, not a key scan — stated explicitly
because review asked whether it performs the complete-vector check. It does, and that is the only reason to
keep it.

**Three conditions make it actually work, and review was right that rev 7 stated none of them:**

1. **The expected template is a literal, independent structural sequence** — written out as shown above, with
   only the two launch-identity values substituted. It must **not** be derived from `descriptor.Argv`, because
   then a newly added token appears on both sides and the comparison passes while the launch has changed.
   That is the same defect class as an oracle derived from the thing under test.
2. **It runs after every contributor and every substitution** — last, not mid-composition, or it certifies a
   vector that is not the final one.
3. **The checked vector is the one the process receives.** The assertion freezes the list to an immutable
   value at the moment it checks it, and *that* value is what populates `ProcessStartInfo.ArgumentList` —
   with no later append, rewrite or transform. Otherwise the assertion is true of something the OS never
   sees, which is the more dangerous failure because it looks like coverage.

Mutants, one per condition: add a token to each of the three contributors; add a token *after* the assertion
runs; and change the descriptor constant. All must go red, and the fourth is the one that only fails if
condition 3 holds.

**`IsReviewFlow` ⇔ unattended.** Review asked whether every review-flow launch is necessarily unattended.
It is, and the code already enforces it rather than assuming it: `ValidateAndBuildReviewFlowMcp` throws
`"Vendor '{v}' cannot host an unattended (review-flow) agent"` when `ctx.IsReviewFlow &&
!descriptor.SupportsUnattended`. The two are the same condition, and this spec adds no third state. Named
here because rev 1 left it implicit.

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

| operator-facing docs for reviewer vendor selection | state §2.9's accepted property where an operator choosing a vendor will read it |

No projection change, and no server change to *routing* — reviewer vendor selection is already vendor-neutral
(AI-1488). The one server-side requirement is negative and must be checked rather than assumed: enabling
Gemini must not make it a default. `Flows:Review:DefaultVendor` stays unset/unchanged, so Gemini is only ever
reached by an explicit `vendor: "gemini"`.

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

Review found rev 1's plan partly vacuous, and it was right about each case: a test comparing a shared
alias property to itself cannot catch omission or later rewriting; a test asserting a constant is
unchanged does not prove the aliased launch traverses reservation logic; and two GUIDs being unequal
establishes neither unpredictability nor per-launch lifetime. Every assertion below therefore names the
**artifact** it reads and the **mutant** it must die to.

**All assertions read the launch artifact** — the final composed argv, and the serialized `session/new`
payload — from a single built launch. Not descriptor fields, not helper return values.

| # | test | reads | must fail when |
|---|---|---|---|
| 1 | `Gemini_ReviewLaunch_CarriesExactlyOneApprovalModeYolo` | final argv | the trust argv is dropped, or a second one is appended |
| 2 | `Gemini_InteractiveLaunch_CarriesNoApprovalMode` | final argv | `--approval-mode` is moved into `Argv`, or the review branch runs for an interactive launch |
| 3 | `AnyLaunch_CarryingLegacyYolo_IsRefused` | boundary throw | the `--yolo` check is removed — injected via `Argv`, via the trust argv, and via a caller argument, one case each |
| 4 | `ReviewLaunch_HasExactlyOneAllowlistOptionAndValue` | final argv | the review arm appends instead of replacing, or a duplicate option is emitted |
| 5 | `ReviewLaunch_SerializedChannelName_EqualsTheAllowlistValue` | **serialized `session/new` name vs parsed argv value** | either sink derives its own alias; the MCP serialization omits the name; the argv is rewritten after substitution |
| 6 | `ReviewLaunch_ReservationAndCopilotToolIds_ResolveOnCanonicalId` | `KcapMcpRegistry` reservation outcome + Copilot tool-id list, for a launch whose `WireName` differs | `CanonicalId` is replaced by `WireName` at either consumer |
| 7 | `InteractiveLaunch_HasExactlyOneDenyAllName_AndNoPlaceholder` | final argv | `SubstituteUnmatchableNames` stops running for interactive launches |
| 8 | `ReviewLaunch_HasNeitherThePlaceholderNorADenyName` | final argv | the review arm fails to replace the deny value |
| 9 | `DescriptorTemplate_StillHoldsThePlaceholder` | `descriptor.Argv` | someone "simplifies" the template to a literal |
| 10 | `NonGeminiVendors_WireNameEqualsCanonicalId` | identity for cursor/copilot/kiro | the alias is applied vendor-neutrally by accident, changing shipped behaviour |
| 11 | `ConcurrentReviewLaunches_DoNotShareAWireName` | two launches built concurrently | the alias is cached, made static, or hoisted to a field |
| 12 | `WireName_IsExactlyTheInjectedGuid` | the wire name, with a **stub** `Func<Guid>` | the entropy source is replaced by a counter, a hash of launch inputs, or a clock — the stub's value would no longer appear verbatim |
| 13 | `EmptyGuid_AbortsTheLaunch` | the boundary throw | the fallback-to-predictable arm is reintroduced |
| 14 | `ForLaunch_TakesNoLaunchContext` | the signature (a compile-time property, asserted by the absence of any context parameter) | someone adds a context parameter, re-opening derivation |

Row 12 replaces rev 2's black-box non-derivation test, which review correctly called unfalsifiable: a
UUID-shaped hash of the session id would have passed uniqueness and shape assertions. With the entropy
source injected, the assertion is exact equality against a value the test chose, and the mutant (any
derived source) cannot produce it. Row 14 is the structural half — the launch context is not in scope, so
derivation is unrepresentable rather than merely untested.

Rev 1's test #7 is gone: it asserted the placeholder survives into an interactive launch's argv, which
**contradicts the code** — `SubstituteUnmatchableNames` runs before the review branch on every launch, so
an interactive final argv holds a generated `kcap-deny-…`. Review caught that. It is now #7/#8/#9, split
by artifact so none of them can pass by reading the wrong object.

**Mutation-proof every row.** The "must fail when" column is the mutant, and a row whose mutant survives
is a row that proves nothing — #5 above all, whose failure mode is a reviewer that launches happily and
can never report.

### 6.1 Live certification (gated, `Skip.Unless` + opt-in env var)

The §2 probes promoted to tests, so the findings cannot silently rot when Gemini updates:

* **positive** — a real `gemini --experimental-acp` launch with an injected channel under a randomised
  name asserts the tool is invoked. **It must assert a bounded wait and fail on timeout**, never hang:
  "never reported" and "skipped/inconclusive" must be distinguishable, which is review's point and is
  exactly how a broken channel would otherwise look green.
* **negative** — the deny-all placeholder blocks the same injection (§2.2).
* **hostile repository** — the §2.6 run, reduced to its load-bearing rows: a workspace declaring servers
  named with a prefix of, a glob over, and a different suffix than the allowlisted name, asserting via
  marker files that **no repository MCP process starts** while the injected one does. This is the test that
  would catch Gemini changing the allowlist from exact-match to prefix-match in a future release, which is
  the single change that would silently reopen §2.3.

### 6.2 E2E (manual, recorded in the PR)

`start_review_flow(vendor="gemini")` completing a real round unattended, for **both flow kinds** —
`kind="spec-review"` and `kind="code-review"` — each taken from a `findings` result through a
`submit_review_round` to a `clean` result, so the multi-round path is exercised and not just the first
reply. Plus: zero human-routed interactions (any interaction frame reaps the reviewer under §3.4, so this
is observable rather than asserted by inspection), and **exactly one reviewer *session* recorded and
reaped** — which is not the same as one process: §2.5 measured Gemini spawning the MCP server twice per
session, so a process count is the wrong oracle here.

## 7. Definition of done

* `start_review_flow(vendor="gemini")` completes a real round unattended for both kinds (§6.2).
* A repository declaring MCP servers that prefix, glob, or near-miss the allowlisted name has **no process
  spawned** during a Gemini review, with the positive control that the real channel still loads (§6.1).
* Interactive hosted Gemini is unchanged: no `--approval-mode`, one deny-all name, no placeholder.
* Routing **refuses** Gemini for a borrowed snapshot before any process starts (§8, first bullet).
* Every row of §6's table mutation-proven.
* **Gemini is opt-in and never a default** — `Flows:Review:DefaultVendor` unchanged, verified, so no operator
  inherits an unsandboxed Gemini reviewer without selecting it (§2.9).
* **The operator-facing reviewer-selection documentation states §2.9's property**, since for any operator but
  this repository's owner, enabling *is* the consent event.
* **§2.9's accepted security property is signed off knowingly** — that an unattended review in an owned
  worktree grants prompt-injected repository content execution as, and the credentials of, the daemon user.
  This is a property of every shipped unattended reviewer, not of Gemini, and the alternative is to withhold
  the reviewer until it can be sandboxed. It is a product decision and belongs on the PR, not in a footnote.

## 8. What this issue does NOT do

* **Borrowed review for Gemini** — `SupportsBorrowedReviewFlow` stays false. Needs the sandbox credential
  question (§1), which is Kiro's AI-1410 blocker; a Gemini reviewer without it still reviews an owned
  worktree, which is what every shipped reviewer except Copilot does.

  Review's point stands, though: "we left the flag false" is only safe if routing *demonstrably* refuses
  Gemini for a borrowed snapshot **before a process starts**, or a fallback could run Gemini with the
  daemon's own `HOME` against borrowed content — the one shape the sandbox exists to prevent. The code
  already throws (`"requires an owned worktree, not a borrowed cwd"` when `ctx.Work !=
  WorkLocation.OwnedWorktree && !policy.Supported`), so this becomes a **required negative acceptance
  test**, listed in §7: build a borrowed-snapshot Gemini launch and assert it is refused pre-spawn. Cheap,
  and it converts an inherited guarantee into an asserted one.
* **Model override** — `NoOpModelSelector` stays. AI-899 §3.3: the write half is unverified and fails
  silently. A reviewer records `model=vendor-default` until AI-1417/AI-1612 resolve it.
* **AI-1612** — a Gemini reviewer reports `auto-gemini-2.5`, a pricing sentinel, so its cost is unpriceable.
  Known and accepted, not discovered later.
* **The vendor-neutral channel-name rename** — §3.2/§3.2a keep the canonical `ReservedResultChannelId` and
  alias only at the Gemini seam, so every other vendor's `WireName == CanonicalId` and their shipped
  behaviour is byte-identical (asserted, §6 row 10).

  Review objected that "generalise when they need it" leaves a known impersonation class unevaluated for the
  other `SessionNew` reviewer, and that §8 should **name the barrier** rather than defer. Naming it, with
  its verification status, because it is weaker than Gemini's:

  **Cursor** is the only other `SessionNew` reviewer, and it has no allowlist flag at all — its
  `UnattendedTrustArgv` carries `--approve-mcps`, which *suppresses* MCP-server approval rather than
  clamping it. So Cursor has no equivalent of the §2.6 gate. The barrier it relies on instead is a
  behavioural one recorded on **AI-1626**: Cursor's **ACP** path was measured not to load repo-authored MCP
  servers, which is why `--approve-mcps` is not an admission vector there. That measurement is load-bearing
  and it deserves scepticism — AI-1626 exists *because* an earlier reading of the same area was wrong in
  both directions (a false Urgent finding from measuring Cursor's `--prompt` path, then a retraction), and
  it was measured before the production worktree shape (a daemon worktree under a trusted parent) was
  understood.

  So: **not a blocker for Gemini**, whose gate is measured and exact (§2.6), but explicitly **not a clean
  bill of health for Cursor**. Re-verifying it under the production shape is **AI-1632**, already filed
  High, and this spec's §2.3/§2.6 method — hostile servers in a real workspace, marker files as evidence,
  under inherited trust — is the method that should be used there. If Cursor turns out to load them, the
  generalisation becomes urgent and `ReviewChannelIdentity.ForLaunch` is the seam it lands at: one branch,
  already vendor-keyed.

  **Copilot** is not exposed by this class: its transport is `CopilotAdditionalConfig`, and it clamps the
  visible surface with an exclusive `--available-tools` allowlist rather than admitting servers by name.
