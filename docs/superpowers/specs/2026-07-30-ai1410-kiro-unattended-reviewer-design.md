# AI-1410 — Kiro CLI as an unattended review-flow reviewer

**Status:** IMPLEMENTATION-READY. Re-specced **2026-08-05** against `kiro-cli 2.16.0` and
`origin/main` (`2ed9d91`). The 2026-07-30 revision was **BLOCKED**; this revision unblocks it by
reversing that revision's containment decision, on the owner's direction that Kiro is
user-installed and user-authenticated. Every previously-open measurement is now closed — see
"What changed" and the probe record.

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
its credential, and the blocker does not arise. There is nothing to broker, nothing to grant
file-granularly, and no rotating-credential exposure.

This is not a novel posture. It is the one **already shipped** for Gemini
(`GeminiReviewerCapability`, merged 2026-08-01): an unattended reviewer runs in a daemon-owned
worktree with the daemon's own `HOME`, and the security decision is a daemon-local operator consent
flag rather than a containment mechanism.

| Item | 2026-07-30 | This revision |
|---|---|---|
| Containment mechanism | OS sandbox | **No sandbox** — operator consent, Gemini's shape |
| Kiro auth | ⛔ blocker (browser-login wedge) | Non-issue; operator-managed, daemon never touches it |
| Platform gate | Required (macOS + `sandbox-exec`) | **Dropped** — nothing to gate on |
| §1.2 whole-filesystem `fs_read` | Reason the sandbox was chosen | **Explicitly accepted** (§2) |
| §1.5 namespaced `@kcap-flow-result/…` trust | ❓ unmeasured, blocking | ✅ **GO**, with negative control (§3) |
| Global-MCP suppression | measured on 2.15.2 | re-measured on **2.16.0**, positive control (§4.1) |
| Branch-authored `.kiro/settings/mcp.json` | open | ✅ closed by AI-1632, merged (§4.2) |
| Version certification | n/a | **Runtime assertion**, no certified-version set (§5) |
| Model override | "out of scope — Kiro can't select a model" | Out of scope, **reason corrected** (§8) |
| Reviewer-home lifecycle | "subsumed by the sandbox state root" | **Back in scope** — we own it (§7) |

### 0.1 What is deliberately NOT reversed

The 2026-07-30 finding that produced the sandbox decision stands unaltered as a **fact**: a trusted
`fs_read` is a whole-filesystem read primitive under the daemon's uid, and `--trust-tools` scopes
the tool surface, not the filesystem. What changed is not the measurement but the **response** to
it. §2 accepts that surface in writing; it does not claim it was mismeasured.

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

This is accepted, not overlooked, and it is the same surface already accepted for Gemini — whose
`--approval-mode yolo` additionally permits writes and shell execution, so **Kiro's reviewer is
strictly more contained than the reviewer already in production**.

The mitigation is consent plus scope, not containment: the operator opts in per daemon, and the
opt-in text says what is being accepted. Anything stronger requires either a vendor-supplied
non-interactive credential (which reopens the 2026-07-30 sandbox route) or a Kiro sandbox mode.
Both are out of scope and neither is available today.

---

## 3. Trust at spawn — measured, both directions

**Decision: scoped `--trust-tools`, not `--trust-all-tools`.**

```
--trust-tools fs_read,thinking,@kcap-flow-result/submit_review_result
```

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

## 5. Runtime containment assertion, instead of a certified-version set

Gemini's reviewer is gated on a **certified-version set** because its containment *is* a
version-fragile matcher (`--allowed-mcp-server-names`), and a build that changed matching semantics
would silently carry consent across the change.

**Kiro takes a different route, and it is a better one where it is available.** Kiro reports its own
MCP outcomes (both observed in the probe):

```
_kiro.dev/mcp/server_initialized    {sessionId, serverName}
_kiro.dev/mcp/server_init_failure   {sessionId, serverName, error}
```

**Decision: assert at runtime, per launch.** Collect `server_initialized` names after `session/new`;
if any name is outside the set this launch injected, fail the round with a coded error. This
verifies the containment property **directly** rather than through a version proxy, needs no
re-certification PR, and cannot go stale — which matters because `kiro-cli` auto-updates fast
(2.12.1 → 2.15.2 → 2.16.0 inside a week during this issue's life). A certified-version set would
take the reviewer offline repeatedly for a property the assertion proves outright.

**`server_init_failure` is the second half and closes §6's hardest case.** A result channel that
fails to start is otherwise invisible until the round times out with no result. Treating a failure
naming the result channel as an immediate coded error converts the worst failure mode — a silent
wedge — into a diagnosable one.

**Ordering caveat, and it is load-bearing.** These are asynchronous notifications; the probe needed
a short settle before the tally was complete. The assertion must therefore be evaluated on a bounded
wait after `session/new` and **re-checked before a result is accepted**, not sampled once at an
arbitrary instant. A single early sample would pass while a late server was still starting.

**Residual, accepted:** a Kiro build that stopped emitting these notifications would make the
assertion vacuous — it would observe an empty set and conclude "nothing extra". Mitigated by the
same launch requiring its injected result channel to appear in that set: an assertion that sees no
notifications at all fails, because it never saw its own channel. That makes silence a failure
rather than a pass, which is the direction that matters.

---

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

Coded errors this design introduces:

| Code | Condition |
|---|---|
| `kiro_unattended_reviewer_disabled` | Operator has not enabled the reviewer on this daemon |
| `kiro_reviewer_mcp_surface_unexpected` | §5 assertion saw a server outside the injected set |
| `kiro_reviewer_result_channel_unavailable` | `server_init_failure` named the result channel |
| `kiro_reviewer_trust_list_rejected` | Kiro warned on a `--trust-tools` entry (§3.2) |

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
* **Startup sweep, epoch-keyed:** delete every `kcap-kiro-reviewer-*` whose epoch is **not** the
  current one. This is the crash/`SIGKILL` recovery, and the epoch key is what stops a second daemon
  on the same host deleting a live peer's home mid-review.
* **Reap before delete:** terminate the reviewer and confirm exit first. Deleting under a live Kiro
  leaves it writing into an unlinked path, and on a crash-recovery pass the owner may still be alive.
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
UnattendedTrustArgv: ["--trust-tools", "fs_read,thinking,@kcap-flow-result/submit_review_result"],
SupportsUnattended:  true,
UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,      // §3.3 — NOT AutoApprove
ReviewFlowMcpTransport:      AcpReviewFlowMcpTransport.SessionNew,     // measured GO
// unchanged:
ModelSelector:           SetModelSelector.Instance,                    // §8 — interactive path only
SupportsReconnectResume: false,                                        // durable stale-owner lock
```

**No aliasing.** `AliasesResultChannel` stays Gemini-only. Aliasing exists because Gemini's MCP gate
is an exact-name allowlist a repository could impersonate; Kiro's containment is *source
suppression* (§4.1 + §4.2), so there is no competing source to impersonate and the result channel
keeps its canonical `kcap-flow-result` id.

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

**Prompt shape.** `promptCapabilities.embeddedContext: false` (measured): AI-1407's prompt folding
must deliver review context to Kiro as **plain text**. Confirm the foundation does not assume
embedded context for any vendor; if it does, that is a foundation fix, not a Kiro one.

**Skill derail (the AI-1135 analogue) does not apply.** Kiro does not read `~/.agents/skills/`, so
there is no review-flows skill to route a Kiro reviewer away from.

---

## 10. Verification

Each negative assertion is paired with a positive control — §3's own history is why.

**Containment**
- [ ] A reviewer launch sets a fresh, empty, `0700` `KIRO_HOME` under a daemon-owned root
- [ ] With it, `kcap-flows` is absent from the reviewer's session; **control:** the same handshake
      with the real home shows it present
- [ ] The §5 assertion fails the round when a server outside the injected set initializes;
      **control:** an ordinary launch, whose injected set is exactly what appears, passes
- [ ] A launch that observes **no** `server_initialized` at all fails (silence is not a pass, §5)
- [ ] `server_init_failure` naming the result channel yields
      `kiro_reviewer_result_channel_unavailable`, not a round timeout
- [ ] The operator's global `~/.kiro/settings/mcp.json` is byte-identical after a round (§4.3)
- [ ] A worktree carrying `.kiro/settings/mcp.json` has it removed before launch (AI-1632 regression)

**Trust**
- [ ] `flow-result` is callable with **zero** `session/request_permission` frames;
      **control:** dropping the namespaced entry produces exactly one frame naming the tool
- [ ] An `fs_write` or `execute_bash` attempt raises a frame and `Fail` ends the round;
      **control:** the same request succeeds when that tool is trusted
- [ ] A trust-list entry Kiro warns about fails the launch (`kiro_reviewer_trust_list_rejected`)
      rather than silently trusting nothing (§3.2)

**Consent and availability**
- [ ] A daemon without the Kiro flag refuses with `kiro_unattended_reviewer_disabled` and does not
      advertise the vendor; asserted with the operator flag alone, so binary resolution cannot make
      the test pass for the wrong reason
- [ ] The gate runs before any connection source (a supplied source cannot bypass it)
- [ ] A host without `kiro-cli` does not advertise the reviewer

**Round behaviour**
- [ ] `start_review_flow(kind="spec-review", vendor="kiro")` completes findings→clean unattended
- [ ] Same for `code-review`
- [ ] **Zero** human-routed interactions across the round
- [ ] Reviewer session captured and reaped; no orphan
- [ ] A caller-supplied reviewer model is refused with a coded error, not ignored (§8)
- [ ] The applied model is reported on the launch attempt, including the `KCAP_KIRO_MODEL` case (§8)
- [ ] An unresolvable binary, and a peer that exits before `initialize`, both surface coded errors
      rather than hanging (§6)

**Home lifecycle**
- [ ] Created `0700` at creation time under a daemon-owned root (§7)
- [ ] A stale home from a *previous* daemon epoch is swept at startup; a *current*-epoch home
      belonging to a live peer is **not**
- [ ] The reviewer process is reaped before its home is deleted
- [ ] Deletion does not follow symlinks and refuses a path resolving outside the daemon root

---

## 11. Acceptance

The issue's original criterion named a `model="<priced-model>"` clause. Model override is out of
scope (§8), so the criterion is:

> `start_review_flow(kind="spec-review", vendor="kiro")` completes a real round unattended
> end-to-end, on a daemon whose operator has enabled the Kiro reviewer.

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
- Upstream trust-flag prompt leaks (#7398). Under `Fail` these surface as failed rounds — a bug
  report, not a silent auto-approval. If they prove frequent, the answer is an allowlist-aware
  interaction policy specified as its own foundation work, never `AutoApprove`.
