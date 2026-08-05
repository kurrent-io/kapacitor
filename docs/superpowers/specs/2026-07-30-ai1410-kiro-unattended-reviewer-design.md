# AI-1410 — Kiro CLI as an unattended review-flow reviewer

**Status:** IMPLEMENTATION-READY (rev 2, after spec review). Re-specced **2026-08-05** against
`kiro-cli 2.16.0` and `origin/main` (`2ed9d91`). The 2026-07-30 revision was **BLOCKED**; this
revision unblocks it by reversing that revision's containment decision, on the owner's direction
that Kiro is user-installed and user-authenticated.

**Rev 2 changes, all from spec-review findings that were verified against the code before being
accepted.** Three were defects this spec would otherwise have shipped: no bounded launch/prompt
deadline exists in `AcpHostedAgentRuntime` today, so operator-managed auth still wedges on
*expiry* (§6); a **fixed** trust list cannot admit the allowlist servers `McpAllowlist` really
injects, so any review with an allowlist would die under `Fail` (§3.4); and the epoch sweep as
written deletes a *live* peer daemon's home (§7). Rev 2 also reframes §0/§2 — what dissolved is the
**authentication** blocker, not the need for a read boundary — and closes §9's embedded-context
question on evidence (`embeddedContext` has zero consumers in the tree).

**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1400 (reviewer choice in review flows)
**Depends on:** AI-1404 (Kiro hosting, shipped) · AI-1407 (ACP reviewer foundation) ·
AI-1402 (vendor selection) — all landed.
**Companion specs:** `2026-07-30-ai1404-kiro-acp-hosted-agent-design.md` (Kiro protocol facts) ·
`2026-07-31-ai899-gemini-acp-hosted-agent-design.md` (the reviewer shape this follows).
**Probe record:** `docs/probes/2026-08-05-kiro-reviewer-trust/` — two billable requests, both on
`deepseek-3.2` (`rate_multiplier: 0.25`).

---

## 0. What changed, and why the blocker dissolved

The 2026-07-30 revision chose an **OS sandbox** (`BorrowedReviewSandbox`) as Kiro's read boundary,
then measured that the choice was unimplementable: the sandbox redirects `HOME` to a per-launch
state root and grants nothing under the user's home, but Kiro's credential lives at
`~/Library/Application Support/kiro-cli/data.sqlite3`. Deprived of it, `kiro-cli` does not emit a
coded auth error — it opens an **interactive browser login** and never returns, which is precisely
the wedge that design existed to prevent. The recommended way out was brokering a `KIRO_API_KEY`
we could not obtain.

**The owner's direction removes the premise.** Kiro is installed and authenticated by the operator,
by whatever mechanism they choose, exactly as Gemini is; we require only an authenticated CLI that
can launch on the user's machine. A daemon that never redirects `HOME` never separates Kiro from
its credential, and the blocker does not arise. There is nothing to broker and nothing to grant
file-granularly. (The credential still rotates; what changes is that the daemon is no longer
interposed on that rotation, so it is not our exposure to manage. §6 covers the case where it
expires anyway.)

This is not a novel posture. It is the one **already shipped** for Gemini
(`GeminiReviewerCapability`, merged 2026-08-01): an unattended reviewer runs in a daemon-owned
worktree with the daemon's own `HOME`, and the security decision is a daemon-local operator consent
flag rather than a containment mechanism.

| Item | 2026-07-30 | This revision |
|---|---|---|
| Containment mechanism | OS sandbox | **No sandbox** — operator consent, Gemini's shape |
| Kiro auth | ⛔ blocker (browser-login wedge) | Operator-managed; daemon never touches it. **Expiry still wedges** → bounded deadline (§6) |
| Platform gate | Required (macOS + `sandbox-exec`) | **Re-shaped**: POSIX-only, for §7 not for a sandbox (§0.2) |
| §1.2 whole-filesystem `fs_read` | Reason the sandbox was chosen | **Explicitly accepted** (§2) |
| §1.5 namespaced `@kcap-flow-result/…` trust | ❓ unmeasured, blocking | ✅ **GO**, with negative control (§3) |
| Global-MCP suppression | measured on 2.15.2 | re-measured on **2.16.0**, positive control (§4.1) |
| Branch-authored `.kiro/settings/mcp.json` | open | ✅ closed by AI-1632, merged (§4.2) |
| Version certification | n/a | **Runtime tripwire**, no certified-version set (§5) |
| Model override | "out of scope — Kiro can't select a model" | Out of scope, **reason corrected** (§8) |
| Reviewer-home lifecycle | "subsumed by the sandbox state root" | **Back in scope** — we own it (§7) |

### 0.1 What dissolved, and what did NOT

Precision matters here, because an earlier draft of this section blurred the two and read as though
the security problem had gone away.

**Dissolved: the authentication blocker.** It existed only because the sandbox redirected `HOME`.
No redirection, no wedge, nothing to broker.

**NOT dissolved: the need for a read boundary.** The 2026-07-30 finding stands unaltered as a
*fact* — a trusted `fs_read` is a whole-filesystem read primitive under the daemon's uid, and
`--trust-tools` scopes the tool surface, not the filesystem. This revision does not remove that
requirement; it **declines to meet it**, and §2 records the acceptance and its consequences. What
changed is the response to a measurement, not the measurement.

Nor does user-managed auth *prove* no boundary is achievable — it establishes that the one
mechanism we have (`BorrowedReviewSandbox`, which grants nothing under the user's home) is
incompatible with a credential that lives under the user's home. A narrower profile, a Kiro sandbox
mode, or a vendor non-interactive credential could each reopen the question. None is measured, so
none is claimed either way.

### 0.2 Supported platforms, and why this is not "nothing to gate on"

An earlier draft dropped the platform gate entirely on the grounds that the sandbox was gone. That
was too broad: the gate was `sandbox-exec`-shaped, but §7's disposal requirement is
**POSIX-shaped**.

* **Measured:** macOS / arm64, `kiro-cli 2.16.0`. Every probe result in this spec is from there.
* **Supported:** POSIX hosts (`!OperatingSystem.IsWindows()`). The four load-bearing mechanisms —
  the `KIRO_HOME` environment variable, `--trust-tools` argv, ACP notifications, and injected
  `session/new` MCP servers — are platform-independent by construction, so Linux is supported on
  structural grounds with the probe unrepeated. Say so rather than implying it was measured.
* **Excluded: Windows.** §7 requires `0700` at creation and a symlink-safe recursive delete;
  `UnixFileMode` is a no-op there, and the codebase already branches on `OperatingSystem.IsWindows()`
  wherever it uses it. A transcript-bearing directory whose permissions we cannot set is a
  requirement we cannot meet, so the reviewer is **not advertised** there — fail closed, as with
  every other capability in this area.

---

## 1. Threat model, stated before the mechanisms

An unattended Kiro reviewer runs in a daemon-owned worktree with the daemon user's full authority.
Repository content reaching the model's tool use can therefore read anything that user can read.
**That risk lands on the daemon operator, who is not necessarily the person requesting the review** —
a caller can ask for `vendor: "kiro"` without owning the exposed host.

So the decision belongs in daemon-local configuration, and **enabling it is the operator's consent
event**. A non-default plus documentation would be informed guidance, not consent. This is
`GeminiReviewerCapability`'s reasoning verbatim, and it applies to Kiro for the same reason.

What the design still owes, *given* that consent:

1. The reviewer must not be handed capabilities the operator did not intend for a reviewer —
   specifically `kcap-flows`, which would let it start nested review flows (§4.1).
2. Repository-authored configuration must not execute (§4.2).
3. The reviewer must be able to deliver a result without a human (§3).
4. Failures must be coded errors, never wedged rounds (§6).
5. Review context written to disk must be disposed of (§7).

---

## 2. Accepted risk, recorded explicitly

**A Kiro reviewer can read any file the daemon user can read.** On a daemon host that includes
`~/.config/kcap/tokens.json`, `~/.aws/credentials`, SSH keys, and other concurrent reviews'
worktrees. Scoped trust (§3) excludes `fs_write` and `execute_bash` but does **not** bound `fs_read`
to the worktree; only an OS sandbox would, and an OS sandbox is incompatible with a
user-authenticated Kiro (§0).

This is a **risk acceptance**, not a solved problem, and the consequences below are part of what is
being accepted. Precedent is not justification: Gemini's reviewer already ships this surface, and on
a *narrower tool list* Kiro's is tighter (Gemini's `--approval-mode yolo` also permits writes and
shell). That is a tool-surface comparison only — it is **not** a claim that either is contained
end-to-end.

### 2.1 The exfiltration paths, named

A read tool's output does not stay on the host:

1. **To the model provider.** Anything `fs_read` returns enters the prompt of the operator's own
   Kiro account, i.e. leaves the machine.
2. **To the review requester.** The reviewer's findings text is returned to whoever started the
   flow. A reviewer that reads `~/.config/kcap/tokens.json` can put it in a finding. **The requester
   need not be the daemon operator**, so this crosses from "the operator exposed their own host" to
   "the operator exposed their host to a third party".
3. **Across concurrent reviews.** Other callers' worktrees are readable, so one caller's review can
   surface another's source.

### 2.2 The supported trust boundary

Given (2) and (3), the honest statement is: **this reviewer is supported only on a daemon whose
operator and whose review requesters are inside one trust domain.** Daemon-operator consent is
consent to expose the operator's host; it is not authorization from every other data owner whose
content is reachable. A daemon serving mutually-untrusting callers must not enable it, and the
opt-in text has to say so rather than leaving the reader to infer it.

Proposed refusal/consent text, which is the acceptance artifact and should ship verbatim:

> `kiro_unattended_reviewer_disabled`: this daemon has not enabled Kiro as an unattended
> review-flow reviewer. Enabling it grants a review read access to every file this daemon user can
> read — including its own credentials — with no filesystem boundary, and a reviewer can return
> what it read to whoever requested the review. Enable it only on a daemon whose operator and
> review requesters are in one trust domain: set `KiroUnattendedReviewerEnabled` on the daemon (not
> on the server).

The mitigation is therefore consent plus scope plus deployment boundary — not containment.

---

## 3. Trust at spawn — measured, both directions

**Decision: scoped `--trust-tools`, not `--trust-all-tools` — and built PER LAUNCH, not fixed.**

```
--trust-tools fs_read,thinking,@<result-channel-wire-name>/submit_review_result[,@<server>/<tool>…]
```

The probe measured the shape with the canonical name; §3.4 explains why the shipped value must be
derived from the launch's own injected MCP set rather than frozen in the descriptor.

The open question from the 2026-07-30 revision (§1.5) was whether a **namespaced** MCP tool can be
trusted at all on the **ACP** path — the prior GO used `--trust-all-tools`, which that revision
forbade, so it did not transfer. A reviewer that cannot call `flow-result` without approval cannot
deliver a result at all, and presents as a silent round timeout. Measured on `kiro-cli 2.16.0`:

| turn | `--trust-tools` | `session/request_permission` | `tools/call` reached the server |
|---|---|---|---|
| test | `fs_read,thinking,@kcap-flow-result/submit_review_result` | **0 frames** | yes |
| **control** | `fs_read,thinking` | **1 frame**, naming the tool | yes, after approval |

Both ended `stopReason: end_turn`; the server logged the full
`initialize → notifications/initialized → tools/list → tools/call` sequence and its nonce reached
the transcript, so the result was really delivered rather than merely permitted.

**The control is load-bearing and its absence would have been a defect.** "No permission frame" on
its own does not show the namespaced entry did anything — if MCP tools needed no approval at all,
the trust list would be decorative and this spec would claim a mechanism it does not have. Dropping
only that entry discriminates. This is the discipline the 2026-07-30 revision §1.4 records after a
containment test passed vacuously: phrased with `PWNED`/"Do it now", Kiro refused on
prompt-injection heuristics and never called the tool, so the effect's absence proved nothing about
trust.

### 3.1 Why scoped rather than trust-all, given §2 already concedes reads

Because the exclusions are real under the `Fail` policy (§3.3): `fs_write` and `execute_bash` raise
a frame, and `Fail` ends the round rather than approving it. `--trust-all-tools` would exclude
nothing and would leave the reviewer's write and shell access to the model's discretion. Consent to
a read surface is not consent to unattended writes and shell execution.

### 3.2 Two spelling traps that must be asserted, not eyeballed

Both were measured in the 2026-07-30 revision and remain true:

* **A typo is a WARNING, not an error.** `--trust-tools=fs_reed` warns and continues, trusting
  nothing — a reviewer that mysteriously cannot read. The shipped list must be asserted against the
  real names by a test, and a launch whose trust list draws a warning must fail rather than proceed.
* **The trust-flag name and the displayed name differ** (`fs_write` trusts; the transcript says
  `using tool: write`). Do not derive one from the other.

Native names, for reference (enumeration oracle: an unknown name warns, a valid one is silent):
`fs_read`, `fs_write`, `execute_bash`, `use_aws`, `knowledge`, `thinking`, `introspect`,
`todo_list`, `gh_issue`, `web_search`.

### 3.3 Interaction policy: `Fail`

Not `AutoApprove`. `AcpInteractionBridge`'s `AutoApprove` selects an allow option and **does not
inspect the tool**, so it would auto-approve exactly the excluded request §3's scoping exists to
reject, and "zero human-routed interactions" would not detect it — the frames were handled, just not
safely.

With scoped trust the reviewer emits **no** frame on its expected path (measured, §3). A frame
therefore means something outside the intended surface was attempted, and ending the round is the
correct response. This mirrors Gemini's reasoning for the same policy.

### 3.4 The trust list must be derived from the launch's injected MCP set

**A fixed list is a defect, and this one would have shipped.** `LaunchAgentCommand.McpAllowlist`
is real and plumbed: `AgentOrchestrator` resolves it through
`KcapMcpRegistry.TryResolveReviewFlowAllowlist`, and `AcpReviewFlowMcp.Build` injects each resolved
server into `session/new` alongside the result channel. A review started with, say,
`kcap-review` in its allowlist therefore gets a server whose tools appear in **no** fixed trust
list — so every call raises a frame, and `Fail` (§3.3) kills the round. The reviewer would be
unusable for exactly the reviews that need repository context most, and it would look like a
vendor bug.

**Gemini never had this problem** because `--approval-mode yolo` trusts whatever is injected.
Scoping the surface is what creates the obligation to enumerate it, and this is the second place
(§9's aliasing is the first) where Kiro's tighter posture costs a per-launch derivation Gemini
could skip.

**Contract.** One builder produces the trust list from the **same** `AcpMcpServerSpec` list handed
to `session/new`, in the same launch, from the same `LaunchIdentity`:

* fixed native entries: `fs_read`, `thinking` — never `fs_write`, never `execute_bash`;
* `@{wireName}/submit_review_result` for the result channel;
* `@{wireName}/{tool}` for every tool of every injected allowlist server, taken from
  `KcapMcpRegistry.ReviewFlowUnattendedSafeTools` — the same authoritative table
  `TryResolveReviewFlowAllowlist` validates against, never a second list.

**Two derivations of this is the failure this contract exists to prevent**, and it is silent: a
trust list built from a different identity than the injected specs yields a reviewer that starts
normally and cannot call its own channel. `LaunchIdentity`'s doc comment already names this exact
hazard for Gemini's allowlist argv; the same rule applies here.

An injected server with no entry in `ReviewFlowUnattendedSafeTools` must **fail the launch**, not
be injected untrusted — `TryResolveReviewFlowAllowlist` already rejects non-auto-approvable
servers, so this is an assertion on an existing invariant rather than new policy.

---

## 4. MCP containment: three sources, three answers

The reviewer's callable MCP surface must be exactly what the launch injects — the
`kcap-flow-result` channel plus any resolved allowlist servers.

### 4.1 Global `~/.kiro/settings/mcp.json` → isolated `KIRO_HOME`

`PluginCommand.InstallKiro` registers `kcap-review`, `kcap-sessions`, `kcap-flows` and
`kcap-memory` in the operator's **global** settings, and Kiro inherits them into every ACP session.
`kcap-flows` is the hazard: a reviewer holding it can start nested review flows. Selecting a
different agent does **not** suppress them — global settings are documented as independent of
`~/.kiro/agents/kcap.json` — and `session/setMode` is too late, since servers are initialized by
`session/new`.

**Decision: a daemon-owned, empty `KIRO_HOME` per reviewer launch.** Measured on 2.16.0, with a
positive control:

| phase | `KIRO_HOME` | servers Kiro reported starting |
|---|---|---|
| A | empty temp dir | `kcap-flow-result` (the injected one) — nothing else |
| **B (control)** | unset → real `~/.kiro` | `kcap-flow-result` + `kcap-review`, `kcap-memory`, `kcap-sessions`, **`kcap-flows`** |

The control is what makes A mean anything; without it "zero global servers" is unfalsifiable.

**In the shipped test the control must be a CONSTRUCTED populated home, not the real `~/.kiro`.**
The probe used the real one because it was the fastest way to get a truthful reading on this
machine, but as a regression test that is environment-dependent — it passes or fails on operator
state a test has no business depending on, and on a CI host with no kcap-registered Kiro it would
report "no global servers" and thereby certify suppression that was never exercised. Point
`KIRO_HOME` at a fixture directory containing a known `settings/mcp.json`, and keep the empty-home
case as the negative.

**This is also why the design needs no credential handling at all.** Kiro's credential is at
`~/Library/Application Support/kiro-cli/`, not under `KIRO_HOME` — which is why the AI-1404 probe
completed a full turn in an empty home with no auth error. Relocating `KIRO_HOME` suppresses
configuration without touching authentication. That separation is the whole reason this design
works where the sandbox did not.

### 4.2 Branch-authored `.kiro/settings/mcp.json` → already closed

Kiro spawns the server declared in a worktree's `.kiro/settings/mcp.json` at session setup — no
prompt, no model involvement. On a contributor-authored branch that is repository-controlled
process execution as the daemon user.

**Nothing to build:** AI-1632 (merged) removes every workspace-scoped vendor MCP config at worktree
creation, `.kiro/settings/mcp.json` included, unlinking the first symlinked component rather than
resolving paths, and failing closed if removal is impossible. This spec depends on that behaviour
and must not re-implement it.

### 4.3 The installer must not be touched

It is tempting to "fix" §4.1 by having `PluginCommand.InstallKiro` stop registering those four
servers, or by editing the user's global config at reviewer launch. Both are wrong: those servers
are what make kcap work in the operator's own interactive Kiro sessions, and mutating a user's
global config as a side effect of someone else's review flow is a worse failure than the one being
solved. **Containment stays entirely inside the daemon-owned home.** A verification item asserts
the file is byte-identical after a round.

---

## 5. Runtime containment tripwire, instead of a certified-version set

**First, what this is and is not.** An earlier draft called this "the containment assertion" and
said it "verifies the property directly". That over-read it. **Containment is source suppression**
— the empty `KIRO_HOME` of §4.1 and AI-1632's worktree deletion of §4.2. What follows is a
**tripwire** that detects suppression having failed. The distinction decides how much its residuals
matter, and the earlier framing made them look fatal when they are not.

Gemini's reviewer is gated on a **certified-version set** because its containment *is* a
version-fragile matcher (`--allowed-mcp-server-names`): there, the gate is the boundary. Kiro's
boundary is two mechanisms that do not depend on a Kiro build's matching semantics — an environment
variable and a file deletion we perform ourselves — so a version gate would buy a re-certification
treadmill (2.12.1 → 2.15.2 → 2.16.0 inside a week during this issue's life) for a property it does
not actually guard.

Kiro reports its own MCP outcomes (both observed in the probe):

```
_kiro.dev/mcp/server_initialized    {sessionId, serverName}
_kiro.dev/mcp/server_init_failure   {sessionId, serverName, error}
```

### 5.1 Enforce continuously, not on a sample

**Decision: evaluate on every notification, for the whole session — not once after a settle.**

The earlier draft said "bounded wait after `session/new`, re-checked before a result is accepted".
That is a sampling scheme, and sampling has a gap by construction: a server can initialize, be used,
and be missed between two samples. Because these are asynchronous notifications with no
initialization-complete event in the protocol (checked: Kiro emits none), there is **no barrier to
wait for**, so the only sound shape is to treat each `server_initialized` as an event to judge on
arrival:

* on arrival, if `serverName` is not in this launch's injected set → fail the round immediately with
  `kiro_reviewer_mcp_surface_unexpected`;
* enforce from `session/new` until the session ends, so a late initialization is caught whenever it
  happens rather than only if it lands inside a window;
* additionally require the result channel's own name to have arrived before a result is accepted —
  that is a readiness check, not the containment check, and §5.3 explains why it is not sufficient
  on its own.

### 5.2 Compare identity, not a public name

A bare `serverName` string proves neither provenance nor uniqueness: an unintended server named
`kcap-flow-result` would pass, and a duplicate name would be indistinguishable from the real one.

**This is why §9 now adopts per-launch aliasing for Kiro** (reversing an earlier "no aliasing"
decision). With `LaunchIdentity` supplying an unguessable per-launch wire name, the injected set is
a set of names **no other source can hold**, so membership is an identity check rather than a
string match, and a collision is impossible rather than merely unlikely. The machinery already
exists and its own doc comment describes it as vendor-neutral by shape; Gemini reaches for it for a
different reason (allowlist impersonation), Kiro for this one.

### 5.3 Residual, stated rather than mitigated away

**A Kiro build that stops emitting `server_initialized` for extra servers while still emitting it
for injected ones defeats this tripwire, and nothing here detects that.** The earlier draft claimed
the residual was closed by requiring the result channel to appear — that only catches *total*
silence, not selective omission, and the claim was wrong.

It is accepted, on these grounds and no others:

* the tripwire is not the boundary (see the top of §5), so its failure degrades detection, not
  containment;
* both suppression mechanisms would have to fail *and* notification behaviour change, in the same
  build, for exposure to occur undetected;
* the exposure it would hide — the reviewer holding `kcap-flows` — is bounded by §2's already-accepted
  surface rather than being a new class of risk.

**Escalation trigger, so this is a decision and not a shrug:** if suppression is ever observed to
regress in the field, or Kiro gains a second global-config source, add the certified-version gate
then. Until one of those happens there is nothing a version number would tell us that the tripwire
does not.

## 6. Failure surfaces: coded errors, never a wedged round

The 2026-07-30 revision's auth-specific criterion — "a tier/auth failure surfaces a coded error" —
is **dropped as unclosable**, for that revision's reasons and now an additional one: under
operator-managed auth there is no auth path we own to fail. An isolated empty `KIRO_HOME` was
measured NOT to produce an unauthenticated Kiro (credentials live elsewhere), and no auth-failure
shape has ever been observed, so a fake peer would assert our handling of our own invention.

Replaced by the testable, vendor-agnostic property: **a reviewer whose launch or first prompt fails
surfaces a coded error rather than hanging the round**, exercised with synthetic non-auth failures —
an unresolvable binary path, and a peer that exits before responding to `initialize`. Plus, newly
available from §5, a real vendor-reported failure: `server_init_failure` naming the result channel.

### 6.1 Operator-managed auth removes the blocker but NOT the wedge

An earlier draft treated "the operator authenticated Kiro" as closing this. It does not, and the
distinction is the difference between a precondition and an invariant.

Authentication is checked by the vendor **at launch**, not held for the daemon's lifetime. A
credential can **expire, be revoked, or have its store become unreadable** between the operator's
`kiro-cli login` and the review that runs three weeks later. The 2026-07-30 measurement is what
makes this concrete rather than theoretical: an unauthenticated `kiro-cli` does not fail — it prints
`Opening browser… | Press ^C to cancel` and **stays alive forever**. Operator-managed auth changes
who fixes it; it does not stop it happening.

**Both synthetic failures above terminate**, so neither exercises this. An unresolvable binary
never starts; a peer that exits closes the pipe and the read loop ends. The failure that actually
occurs in production is a peer that is *alive and silent*, which is the one shape a
termination-based test cannot produce.

**Verified against the code: there is no deadline today.** `AcpHostedAgentRuntime` bounds only its
settlement wait (`support.SettlementWait`); nothing bounds spawn → `initialize` → `session/new` →
first `session/prompt`. A server-side round timeout eventually fails the *round*, but it does not
reap the daemon-side process, and §7's home is then never disposed — so the transcript-bearing
directory outlives the review.

**Requirement.** An unattended Kiro launch carries **one bounded, daemon-owned deadline** covering
spawn through first-prompt completion. On expiry: terminate the child, confirm exit, delete its
home (§7), and surface `kiro_reviewer_launch_timeout`. Configurable, defaulted, and — like every
other bound in this area — computed once as an absolute deadline rather than re-derived per stage,
so a slow sequence cannot approach a multiple of the budget.

**Scope note, honestly:** this bound is not Kiro-specific and every ACP vendor arguably wants it.
It is specified here because Kiro is where it is load-bearing (a measured infinite-hang shape),
and because shipping this reviewer without it re-creates the exact wedge the 2026-07-30 revision
refused to ship. If the ACP foundation later grows a general deadline, this requirement should be
satisfied by that rather than duplicated.

Coded errors this design introduces:

| Code | Condition |
|---|---|
| `kiro_unattended_reviewer_disabled` | Operator has not enabled the reviewer on this daemon |
| `kiro_reviewer_mcp_surface_unexpected` | §5 assertion saw a server outside the injected set |
| `kiro_reviewer_result_channel_unavailable` | `server_init_failure` named the result channel |
| `kiro_reviewer_trust_list_rejected` | Kiro warned on a `--trust-tools` entry (§3.2) |
| `kiro_reviewer_launch_timeout` | The §6.1 deadline expired (covers the alive-but-silent auth wedge) |

Advertisement is gated on `CliResolver.Exists(KiroPath)`, as AI-1404 already does for interactive
hosting: a daemon advertising Kiro on a host with no `kiro-cli` converts a clean
`no_daemon_available` into a mid-round launch failure.

---

## 7. Reviewer-home lifecycle — back in scope, and a disposal requirement

The 2026-07-30 revision delegated this to the sandbox's per-launch state root. **With no sandbox we
own it again**, and it is a security requirement rather than tidiness:

`KiroPaths.ConfigRoot` reads `KIRO_HOME` first, so `SessionsDir()` resolves to
`{KIRO_HOME}/sessions/cli` — **Kiro writes the reviewer's own conversation JSONL into the isolated
home**, and that transcript contains the review context: the caller's diff, source excerpts, and the
findings. The home is read-empty but **write-sensitive**, and it is created on a host that may serve
several callers.

* **Contents at creation:** nothing. Empty is what makes §4.1 work.
* **Permissions:** `0700`, set **at creation**, not after — a world-readable window between `mkdir`
  and `chmod` is long enough to leak a transcript. Root it under a daemon-owned directory, never
  directly in a world-writable shared temp path another user could pre-create or substitute.
* **Naming:** `kcap-kiro-reviewer-<daemonEpoch>-<launchId>`. `daemonEpoch` is fixed once per daemon
  process start; `launchId` is per launch.
* **Concurrency:** one home per launch, so concurrent reviews neither race shared state nor read
  each other's transcripts.
* **Root is PER DAEMON, and this is load-bearing.** `{stateDir}/kiro-reviewers/` under the daemon's
  own state directory — never a root shared between daemons.
* **Startup sweep:** delete every `kcap-kiro-reviewer-*` in **this daemon's own root** whose epoch is
  not the current one. Because the root is per daemon, every directory in it belongs to a previous
  incarnation of *this* daemon, which by definition is dead.

  **The previous revision had this exactly backwards and it was a live bug.** It specified one
  shared root and claimed "the epoch key is what makes it safe for a second daemon on the same
  host". The opposite is true: with a shared root, daemon A's rule "delete every home whose epoch is
  not *mine*" selects daemon B's **current, live** home, because B's epoch is not A's. The
  reap-before-delete requirement below cannot rescue it either — A must not terminate a process
  owned by an unrelated live daemon. Per-daemon roots remove the question instead of adjudicating
  it; if a shared root is ever forced, the sweep needs a liveness/ownership lease and must delete
  only homes whose owner is provably dead.
* **Reap before delete:** terminate the reviewer and confirm exit first. Deleting under a live Kiro
  leaves it writing into an unlinked path, and on a crash-recovery pass the owner may still be alive.
  A process this daemon does not own is never signalled — with per-daemon roots that case should be
  unreachable, and if it is reached the sweep logs and skips rather than reaching across.
* **No symlink following** on delete, and assert the resolved path is still inside the daemon root
  before recursing.
* **Failure handling:** log at warning with the path and continue. An undeletable home must not fail
  a round or block startup, but persistent failure is undisposed review context accumulating.

---

## 8. Model override — out of scope, with the reason corrected

The 2026-07-30 revision deferred this because `session/set_config_option` was unproven on Kiro and
its selector fails silently. **That reason is now stale.** AI-1613's probe
(`docs/probes/2026-08-05-kiro-model-override/`) measured `session/set_config_option` as conclusively
absent (`-32601`) but `session/set_model` as working at effect level, and Kiro now ships
`SetModelSelector` plus `DaemonConfig.KiroModel` / `KCAP_KIRO_MODEL`.

**The deferral survives on a different, stronger reason:**
`AcpHostedAgentRuntimeFactory.ReviewerModelResolver` is `null` for **every** ACP vendor, so all of
them advertise `SupportsReviewerModelResolution: false` and the server already refuses a v3 reviewer
model override with no silent fallback. That is a foundation gap, not a Kiro one, and closing it
here would make Kiro the first ACP vendor with a reviewer-model resolver as a side effect of an
unrelated issue.

So: no `ReviewerModelResolver` wiring, and a caller-supplied reviewer model stays **refused with a
coded error** — never accepted-and-ignored, which is the worst outcome available (the round
completes, the result looks authoritative, and nothing records that the requested model did not
review the code).

**One consequence to state rather than discover.** `ResolveDefaultModel: cfg => cfg.KiroModel` means
a review launch inherits the daemon-wide `KCAP_KIRO_MODEL` if the operator set one. That is
desirable — an operator can pin a cheap reviewer model — but it must be **reported** on the launch
attempt, not silently applied, so the audit trail names the model that actually reviewed.

---

## 9. Descriptor and configuration changes

```csharp
// AcpVendorDescriptors.Kiro
// NOT a fixed array: §3.4 — built per launch from the same injected MCP specs and the same
// LaunchIdentity that session/new uses. The descriptor carries the native half only.
UnattendedTrustArgvBuilder: KiroReviewerTrustList.Build,   // fs_read, thinking + @wire/tool entries
SupportsUnattended:  true,
UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,      // §3.3 — NOT AutoApprove
ReviewFlowMcpTransport:      AcpReviewFlowMcpTransport.SessionNew,     // measured GO
// unchanged:
ModelSelector:           SetModelSelector.Instance,                    // §8 — interactive path only
SupportsReconnectResume: false,                                        // durable stale-owner lock
```

**Aliasing: ON for Kiro** — reversing an earlier "no aliasing" decision in this spec. That decision
reasoned that aliasing exists to stop allowlist impersonation, which source suppression already
prevents, so Kiro did not need it. True as far as it went, but it missed a second use: §5.2 needs
the injected set to be a set of names **no other source can hold**, so the tripwire compares
identity instead of a public string. Per-launch names give that for free from machinery
`LaunchIdentity` already provides and documents as vendor-neutral by shape. The cost is that the
trust list must then carry the wire name — which §3.4 requires independently, so the two changes
share one derivation rather than compounding.

**Borrowed review stays off.** It needs a containment-token decision (`NativeToolClamp` vs
`IndependentSnapshot`) grounded in what Kiro's clamp actually permits, and §2's finding — a trusted
`fs_read` is not path-scoped — says `NativeToolClamp` would be the wrong token. Its own issue.

**Consent flag: generalize, do not copy.** Kiro is the second vendor to need one, and a third copy
of the pattern is how they drift. `GeminiUnattendedReviewerEnabled` becomes a per-vendor lookup
(adding `KiroUnattendedReviewerEnabled` / `KCAP_KIRO_UNATTENDED_REVIEWER`), while
`GeminiReviewerCapability`'s **version** half stays Gemini-specific — Kiro's equivalent is §5's
runtime assertion, and conflating the two would either impose a version treadmill on Kiro or drop
Gemini's certification. The gate must run in `StartAsync` **before any connection source**, with
`BuildProcessStartInfo` keeping its own check as defence in depth, exactly as the Gemini gate does;
a gate only in the builder is bypassable by a supplied connection source.

**Prompt shape — question closed, not deferred.** `promptCapabilities.embeddedContext: false`
(measured), so AI-1407's prompt folding must deliver review context to Kiro as **plain text**. An
earlier draft left "confirm the foundation does not assume embedded context" as an open item while
claiming the spec was implementation-ready, which was a contradiction. It is now answered:
`embeddedContext` has **zero** consumers anywhere in the tree — nothing reads the capability, so
nothing branches on it and no folding path can be assuming it. Plain text is already what every
vendor gets. No foundation fix is required, and no work is deferred.

**Skill derail (the AI-1135 analogue) does not apply.** Kiro does not read `~/.agents/skills/`, so
there is no review-flows skill to route a Kiro reviewer away from.

---

## 10. Verification

Each negative assertion is paired with a positive control — §3's own history is why. **And each
functional assertion must be content-sensitive**: a reviewer that immediately returns `clean`
without reading anything would satisfy "round completed", "zero interactions", "session reaped" and
"result channel invoked" all at once. Those four together do not distinguish a working reviewer from
an inert one.

**Containment**
- [ ] A reviewer launch sets a fresh, empty, `0700` `KIRO_HOME` under this daemon's own root
- [ ] With it, `kcap-flows` is absent from the reviewer's session; **control:** the same handshake
      against a **constructed fixture home** containing a known `settings/mcp.json` shows it present
      (§4.1 — not the operator's real `~/.kiro`, which makes the test depend on machine state and
      would silently certify suppression on a CI host that never had those servers)
- [ ] The §5 tripwire fails the round when a server outside the injected set initializes;
      **control:** an ordinary launch, whose injected set is exactly what appears, passes
- [ ] It fails on a **late** initialization arriving after the first prompt has begun — the sampling
      gap §5.1 exists to close
- [ ] It fails on a server initializing under a **duplicate** of an injected wire name (§5.2 —
      unreachable once aliasing is on, so this asserts the aliasing, not the comparison)
- [ ] **Known-uncovered, asserted as such:** selective omission of `server_initialized` for extra
      servers is NOT detected (§5.3). Pin it with a test that documents the gap rather than
      pretending coverage — a fake peer that reports only the injected channel while starting
      another server passes the tripwire, and the test says so
- [ ] `server_init_failure` naming the result channel yields
      `kiro_reviewer_result_channel_unavailable`, not a round timeout
- [ ] The operator's global `~/.kiro/settings/mcp.json` is byte-identical after a round (§4.3)
- [ ] A worktree carrying `.kiro/settings/mcp.json` has it removed before launch (AI-1632 regression)

**Trust**
- [ ] `flow-result` is callable with **zero** `session/request_permission` frames;
      **control:** dropping the namespaced entry produces exactly one frame naming the tool
- [ ] A review launched **with a non-empty `McpAllowlist`** completes: every injected allowlist
      server's tools are in the trust list and none raises a frame (§3.4). **This is the case a
      fixed trust list fails**, so it must be a distinct test, not a variant of the empty-allowlist
      round
- [ ] The trust list and the `session/new` specs are built from **one** `LaunchIdentity`: mutate the
      identity used by one and the launch fails rather than silently producing a reviewer that
      cannot call its own channel
- [ ] An injected server absent from `ReviewFlowUnattendedSafeTools` fails the launch
- [ ] An `fs_write` or `execute_bash` attempt raises a frame and `Fail` ends the round;
      **control:** the same request succeeds when that tool is trusted
- [ ] A trust-list entry Kiro warns about fails the launch (`kiro_reviewer_trust_list_rejected`)
      rather than silently trusting nothing (§3.2)

**Consent, platform and availability**
- [ ] A daemon without the Kiro flag refuses with `kiro_unattended_reviewer_disabled` and does not
      advertise the vendor; asserted with the operator flag alone, so binary resolution cannot make
      the test pass for the wrong reason
- [ ] The refusal text is the §2.2 wording, including the trust-domain sentence — it is the consent
      artifact, so its content is the assertion, not just its presence
- [ ] The gate runs before any connection source (a supplied source cannot bypass it)
- [ ] A host without `kiro-cli` does not advertise the reviewer
- [ ] A Windows host does not advertise the reviewer (§0.2)

**Round behaviour — content-sensitive**
- [ ] **Seeded-defect round.** A spec/diff carrying one unique, unambiguous planted defect yields a
      `findings` result whose text names that defect; removing only the defect yields `clean`. This
      is what "findings→clean" means — without naming the observable that must drive it, the pair is
      satisfiable by a reviewer that ignores its input
- [ ] The submitted context demonstrably reaches Kiro (assert on the prompt actually sent, not on
      the round's outcome)
- [ ] The injected result endpoint receives the accepted result (assert at the MCP server, as the
      probe did — a `clean` status alone does not prove the channel carried it)
- [ ] Same for `code-review`
- [ ] **Zero** human-routed interactions across the round
- [ ] Reviewer session captured and reaped; no orphan
- [ ] A caller-supplied reviewer model is refused with a coded error, not ignored (§8)
- [ ] The applied model is reported on the launch attempt, including the `KCAP_KIRO_MODEL` case (§8)

**Failure bounding (§6.1)**
- [ ] **A peer that is alive and never responds** — to `initialize`, to `session/new`, or to the
      first `session/prompt` — hits the deadline, is terminated, is reaped, has its home deleted,
      and surfaces `kiro_reviewer_launch_timeout`. This is the production shape (an expired
      credential opening a browser), and it is the one case the two terminating fixtures below
      cannot produce
- [ ] An unresolvable binary, and a peer that exits before `initialize`, both surface coded errors
      rather than hanging
- [ ] The deadline is one absolute budget across all stages, not a fresh timeout per stage

**Home lifecycle**
- [ ] Created `0700` at creation time, under this daemon's own root (§7)
- [ ] A stale home from a *previous* epoch of **this** daemon is swept at startup
- [ ] **A live peer daemon's home is not deleted** — asserted with the peer's epoch *different* from
      the sweeper's, which is the case the previous revision's shared-root rule got wrong. A
      same-epoch-only test passes while the bug is present
- [ ] The reviewer process is reaped before its home is deleted, and a process this daemon does not
      own is never signalled
- [ ] Deletion does not follow symlinks and refuses a path resolving outside the daemon root

## 11. Acceptance

The issue's original criterion named a `model="<priced-model>"` clause. Model override is out of
scope (§8), so the criterion is:

> `start_review_flow(kind="spec-review", vendor="kiro")` completes a real round unattended
> end-to-end, on a POSIX daemon whose operator has enabled the Kiro reviewer — with the round
> driven by content (§10's seeded-defect case), not merely reaching `clean`.

---

## 12. Out of scope

- Borrowed review for Kiro (containment-token decision — §9).
- Reviewer model override (§8) — a foundation gap owned by whoever gives ACP a
  `ReviewerModelResolver`.
- Kiro **canonical token** reporting: absent from ACP. Billing credits remain available via
  `KiroUsage`, so cost is observable even though tokens are not.
- `--agent-engine` pinning — deliberately unpinned by AI-1404.
- Reconnect/resume for a crashed reviewer: `SupportsReconnectResume: false` (durable stale-owner
  lock, measured). A crashed reviewer fails its round and heals by relaunch.
- A general ACP launch deadline. §6.1 specifies a Kiro-owned one because Kiro is where the
  alive-but-silent hang is measured; generalizing it across vendors is separate work, and this
  requirement should be *satisfied by* that rather than duplicated if it lands first.
- A read boundary for the Kiro reviewer. §2 accepts its absence; re-opening it needs a Kiro sandbox
  mode, a vendor non-interactive credential, or a measured narrower profile — none available today.
- Upstream trust-flag prompt leaks (#7398). Under `Fail` these surface as failed rounds — a bug
  report, not a silent auto-approval. If they prove frequent, the answer is an allowlist-aware
  interaction policy specified as its own foundation work, never `AutoApprove`.
