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

So admission is **name equality and nothing else**: no prefix match, no glob, no merge-based admission.
That is what makes §3.2's per-launch GUID sufficient — and it also locates the entire defence in one
property, which the design must then protect deliberately:

* **the alias must be unpredictable**, so it comes from `Guid.NewGuid()` (v4, CSPRNG-backed on .NET) and
  never from a counter, a timestamp, the session id, or anything repository-derived;
* **the alias must not leak into anything the repository can read.** It appears in the launch argv (visible
  to the daemon user via `ps`, which is already trusted) and in the injected server spec. It must not be
  written into the worktree, into a file the reviewer can read, or into the reviewer's prompt.

**Deliberately NOT measured: duplicate `--allowed-mcp-server-names` handling and config precedence.** The
design does not depend on either, because §3.3a asserts the final argv contains **exactly one** allowlist
option with **exactly one** value. Depending on a vendor's duplicate-flag semantics would be a worse
design than not permitting duplicates in the first place, and an unmeasured dependency is exactly what
rev 1 was faulted for.

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
    public static ReviewChannelIdentity ForLaunch(string vendor) =>
        vendor == Vendors.Gemini
            ? new(KcapMcpRegistry.ReservedResultChannelId,
                  $"{KcapMcpRegistry.ReservedResultChannelId}-{Guid.NewGuid():N}")
            : new(KcapMcpRegistry.ReservedResultChannelId,
                  KcapMcpRegistry.ReservedResultChannelId);   // unchanged for every other vendor
}
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

```csharp
static void RequireApprovalModeInvariants(IReadOnlyList<string> argv, bool isReviewFlow, string vendor) {
    // Legacy spelling is refused everywhere, on any launch. It is not merely redundant with
    // --approval-mode: the two together were the failure mode the issue's original note named.
    if (argv.Contains("--yolo"))
        throw new InvalidOperationException($"...'{vendor}' launch carries the legacy --yolo flag...");

    var approvalModes = argv.Count(a => a == "--approval-mode");

    if (!isReviewFlow && approvalModes > 0)
        throw new InvalidOperationException(
            $"...interactive '{vendor}' launch carries --approval-mode; hosted sessions must behave as the "
          + "user's own session does...");

    if (isReviewFlow && vendor == Vendors.Gemini && approvalModes != 1)
        throw new InvalidOperationException($"...expected exactly one --approval-mode, found {approvalModes}...");
}
```

and the same boundary asserts the allowlist shape §2.6 relies on: **exactly one**
`--allowed-mcp-server-names` option with **exactly one** value, so the design never depends on the
vendor's duplicate-option semantics.

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
| 12 | `WireName_IsNotDerivedFromLaunchInputs` | the alias vs session id, agent id, worktree path, vendor, clock | the GUID is swapped for a counter, hash, or timestamp — the §2.6 unpredictability property |

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
