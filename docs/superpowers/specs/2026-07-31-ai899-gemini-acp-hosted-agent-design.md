# AI-899 — Gemini CLI as an ACP hosted agent

**Status:** rev 6, 2026-07-31, against `gemini 0.53.0` and `origin/main` (`e2b2821`).
**Implemented.** §3.1a's gating trust probe has been run, re-run on the correct code path, and its finding is
contained. Rev 6 records what eight rounds of code review changed after rev 5 shipped: §3.1d (a fixed
deny-all MCP name is attacker-matchable) and §4.2b (the serialisation section's scope was too small, in two
distinct ways). Both were real defects in rev 5's design, not just its prose.
**Repository:** kurrent-io/kcap-cli
**Parent:** AI-1399 (multi-vendor ACP hosted agents)
**Template:** the shipped Kiro child (AI-1404) and the Copilot child (AI-1403). Read those, not the
original 2026-06 sketch on the issue, which predates both.

Everything marked **measured** was observed directly against a live `gemini` process. The issue carries
four comments written while prepping this, two of which reached *wrong* conclusions and were retracted;
§1 exists so nobody re-derives them.

## 1. The retracted premises — read this before the issue's comment history

Two conclusions on the issue were wrong. They are corrected there, but the reasoning is worth carrying
because both are shapes that will recur.

### 1.1 "Gemini is tier-ineligible for headless use" — FALSE

Both `gemini --prompt` and the ACP `session/new` failed with:

> `IneligibleTierError: This client is no longer supported for Gemini Code Assist for individuals.`

The real cause was a missing **`GOOGLE_CLOUD_PROJECT`**. The error is raised by a function named
**`throwIneligibleOrProjectIdError`** — the same message for a missing project id as for a genuine tier
problem. The stack trace said "OR ProjectId" through every probe.

**The transferable rule: when a vendor error names a cause, read the function that threw it before
believing the attribution.** A confidently-wrong message is worse than a vague one, and this one cost
several probes and two retracted comments. It is also the direct motivation for §5.

### 1.2 "Hosting needs an API key" — FALSE, and over-engineered

Hosted agents run with a normal environment: the daemon spawns the vendor CLI as the daemon user with
`HOME` intact, so it uses whatever credential the operator already logged in with — exactly how Cursor,
Copilot, Claude and Codex already work. **"The operator logs their CLI in" is the correct and sufficient
assumption.**

A credential broker is only ever needed for a *sandboxed* reviewer, because `BorrowedReviewSandbox`
redirects `HOME` and deliberately grants nothing under it, so the vendor cannot see its own login. That
is a property of the sandbox, not of hosting, and it belongs to AI-1413.

## 2. Measured facts (`gemini 0.53.0`)

`initialize` response:

```json
{"protocolVersion":1,
 "agentInfo":{"name":"gemini-cli","version":"0.53.0"},
 "agentCapabilities":{
   "loadSession":true,
   "promptCapabilities":{"image":true,"audio":true,"embeddedContext":true},
   "mcpCapabilities":{"http":true,"sse":true}},
 "authMethods":[
   {"id":"oauth-personal","name":"Log in with Google"},
   {"id":"gemini-api-key","name":"Gemini API key"},
   {"id":"vertex-ai","name":"Vertex AI"},
   {"id":"gateway","name":"AI API Gateway"}]}
```

`session/new` returns `sessionId`, `modes`, and `models`:

| Fact | Value |
|---|---|
| `availableModels` | `auto`, `gemini-2.5-pro`, `gemini-2.5-flash`, `gemini-3.1-flash-lite` |
| `currentModelId` | `auto-gemini-2.5` |
| `availableModes` | `default`, `autoEdit`, `yolo`, `plan` (native read-only) |

Contrast with Kiro, where it matters: `embeddedContext` is **true** (Kiro `false`), so AI-1407's prompt
folding needs no plain-text workaround here; and `authMethods` is non-empty (Kiro `[]`), which is a
*starting point* for AI-1413's credential question rather than an answer to it — none of the four is shown
to work with a redirected `HOME`, to be suppliable non-interactively, or to fit the sandbox.

`DaemonConfig.GeminiPath = "gemini"` is **already correct** — the binary really is `gemini`. No repeat of
the Kiro `kiro`→`kiro-cli` trap, where a wrong default meant the vendor was never advertised on a correct
install. §7 still adds the zero-configuration availability test, because that is the test that would have
caught Kiro's.

## 3. The descriptor

```csharp
public static readonly AcpVendorDescriptor Gemini = new(
    Vendor:              "gemini",
    ResolveBinaryPath:   cfg => cfg.GeminiPath,
    ResolveDefaultModel: _ => null,
    Argv:                ["--experimental-acp", "--skip-trust",
                          // §3.1c/§3.1d — clamps repo-authored MCP servers. The PLACEHOLDER is
                          // substituted per launch with an unguessable name; a fixed one was
                          // measured attacker-matchable.
                          "--allowed-mcp-server-names", UnmatchableMcpNamePlaceholder],
    UnattendedTrustArgv: [],
    SupportsUnattended:  false,
    ModelSelector:       NoOpModelSelector.Instance,
    SupportsMcpServers:  false
);
```

Each non-obvious choice, with its reason:

### 3.1 `--skip-trust` is in `Argv`, and it has an unresolved question attached

**Measured:** 0.53.0 refuses a headless turn in an untrusted directory outright — `exit 55`, before any
model call, with *"Gemini CLI is not running in a trusted directory"*. A daemon-created worktree
**cannot be assumed** pre-trusted (whether trust is inherited from a parent path was not measured), so
without this a hosted launch fails.

`GEMINI_CLI_TRUST_WORKSPACE=true` is the documented alternative and is deliberately not used: an argument
is visible in the launch line and in `ps`, scoped to the one process, and cannot leak into anything else
the daemon spawns. An environment variable is none of those things.

**What the probe does NOT establish, and what this issue therefore owes.** The measurement shows only
that the turn is refused without the flag. It does not show *what the trust gate was gating*. A trust
prompt in a coding agent typically guards workspace-controlled configuration — repo-local settings, MCP
server definitions, hooks, extensions — i.e. **code execution controlled by whoever wrote the
repository**. A daemon-created worktree is not thereby benign: it is a checkout of a branch that may
carry contributor- or PR-authored content.

An earlier draft of this spec asserted "this is not a new risk surface" on the grounds that the daemon
created the worktree itself. **That was an inference, not a measurement, and it is withdrawn.** The
implementation must, before the descriptor ships:

1. **Probe which workspace-controlled facilities load under `--skip-trust`** — at minimum repo-local
   Gemini settings, MCP server definitions, and any hook or extension mechanism. Record what loaded.
2. **Then apply this rule**, so the disposition is decided here and not by whoever implements it:

   > **Any repository-controlled facility that can cause process execution, before an explicit
   > user-approved action, MUST be clamped.** A facility that only changes non-executable settings MAY be
   > documented instead.

   "Clamped" means suppressed at launch by argument or configuration — the same shape as Copilot's
   `--disable-builtin-mcps` and Cursor's `--approve-mcps` handling. If a facility can execute and cannot
   be clamped, hosted Gemini does not ship until it can: documentation is not containment, and a
   repo-authored MCP server or hook auto-starting under the daemon user is remote code execution with
   extra steps.
3. **The probe evidence and the chosen disposition are reviewed before the descriptor is enabled**, as
   part of this PR rather than a follow-up.

This is gating, not follow-up: the flag is required for the feature to work at all, so the question cannot
be deferred past the thing that needs it. It is also a question every ACP vendor with a trust model will
face, so the answer is worth writing down once.

### 3.1a THE PROBE HAS BEEN RUN — and it inverts the framing

**Method.** A throwaway git repo with a workspace `.gemini/settings.json` declaring (a) an MCP server and
(b) a `SessionStart` hook, each of whose command touches a distinctive marker file. A marker is therefore
proof of *repository-controlled process execution*. Run against `gemini 0.53.0` with the operator's real
credential, varying only trust and clamp flags. Every row is measured, not inferred:

| Trust state | `--skip-trust` | repo MCP server | repo hook |
|---|---|---|---|
| untrusted | yes | — | — |
| the worktree itself `TRUST_FOLDER` | no | **EXECUTED** | **EXECUTED** |
| a **parent** `TRUST_PARENT`, no entry for the worktree | **yes** | **EXECUTED** | **EXECUTED** |
| parent `TRUST_PARENT` + `--allowed-mcp-server-names <non-matching sentinel>` | yes | — | **EXECUTED** |

The second row is the positive control, and it is what makes the first row meaningful: without it, "no
markers" would equally well have meant the probe was mis-shaped.

**Three findings, and the third is the one that matters:**

1. **`--skip-trust` does not grant workspace-configuration trust.** It permits the turn; it does not
   activate repo-authored settings, hooks or MCP servers. So the flag is *not* the risk — the earlier
   draft's worry was aimed at the wrong thing.
2. **Trust INHERITS from a trusted parent, and `--skip-trust` cannot undo it.** With a parent marked
   `TRUST_PARENT` and no entry for the worktree at all, both facilities executed *while the flag was
   passed*. Passing `--skip-trust` is not a containment measure and must never be described as one.
3. **This is live for kcap, not theoretical.** `WorktreePathResolver` and `WorktreeManager` place daemon
   worktrees at `<repo>/.capacitor/worktrees/agent-…` — **inside the operator's repository**. An operator
   who has trusted their own repo (the normal thing to do when using Gemini in it; the machine this was
   measured on has a real `TRUST_PARENT` entry) will therefore have every hosted Gemini agent inherit that
   trust and auto-execute whatever `.gemini/settings.json` the checked-out branch carries — under the
   daemon user, with no prompt, before the model does anything. The branch in that worktree may be
   PR-authored.

**Against §3.1's rule, that is a MUST-clamp: a repository-controlled facility causing process execution
before any user-approved action.** So:

* **MCP servers are clampable.** `--allowed-mcp-server-names <sentinel>` suppressed the repo-authored
  server with trust inherited. It goes in `Argv`. Note `--allowed-mcp-server-names ""` is **not** usable —
  it crashes config load with *"mcpName is required if specified"*, before session start, which also makes
  it a trap for anyone verifying this: the run fails and reports no markers for the wrong reason.
* **Hooks are NOT clampable by any launch flag.** `gemini hooks` is a management subcommand, not a switch;
  there is no `--no-hooks`. The repo-authored hook executed under every configuration that had trust.

### 3.1b CORRECTION — §3.1a measured the wrong code path, and the finding is contained

Everything above was measured with `gemini --prompt` — the **print/CLI** path. **kcap hosts over ACP**
(`gemini --experimental-acp`). Re-measured there, with inherited trust:

| Path | repo-authored MCP | repo-authored hook |
|---|---|---|
| print/CLI (what §3.1a measured) | EXECUTED | **EXECUTED** |
| **ACP — what kcap uses** | **EXECUTED** | **blocked** |
| **ACP + `--allowed-mcp-server-names <injected names>`** | **blocked** | blocked |

So §3.1a was wrong in both directions. **Hooks do not execute on the ACP path**, so "hooks are unclampable,
therefore hosted Gemini cannot ship" is withdrawn. **Repo-authored MCP servers do execute on ACP under
inherited trust** — that half is real — **and the allowlist flag already in the descriptor contains it**,
verified: the injected server loads, the repo-authored one is blocked.

**The containment decision is therefore not needed.** No worktree relocation, no launch refusal, no
`.gemini/` stripping. The one correction the descriptor needed was the allowlist *contents*: a
non-matching sentinel blocks our own injected servers as well, which would have shipped hosted Gemini with
MCP silently broken.

**The lesson is the one §1.1 already teaches, and I re-learned it here:** the CLI and ACP paths have
materially different config-trust behaviour, in both vendors probed, and neither is predictable from the
other. §7 therefore requires the clamp test to run **through ACP**, not through the CLI.

Two probe-hygiene traps, recorded because each produced a confident wrong answer:

* `--allowed-mcp-server-names ""` crashes config load *before* session start, so a probe using it reports
  "nothing executed" for entirely the wrong reason.
* On Cursor, `--approve-mcps` **persists** an approval for the server name, so a reused probe identifier
  silently measures a stale approval on later runs. Use a virgin name per measurement.

The options below are retained only as the reasoning that the correction closed off — **none is needed**:

| Option | Effect | Cost |
|---|---|---|
| **(a) Refuse to launch when the worktree resolves as trusted** — read `~/.gemini/trustedFolders.json`, fail with a coded error | fail-closed, no upstream change, kcap-side only | hosted Gemini unavailable in exactly the repos an operator most likely trusts |
| **(b) Neutralise the worktree's `.gemini/` before launch** | removes the facility rather than the trust | mutates a checkout agents commit from; needs care with tracked files |
| **(c) Create daemon worktrees outside any trustable path** | removes inheritance at the root | `WorktreeManager` scans per-repo `.capacitor/worktrees/`; affects every vendor |
| **(d) Upstream request for a hook clamp** | correct long-term | not available now |

**Recommendation: (a) plus the MCP clamp for this issue, with (b) or (c) as the follow-up that restores
availability.** (a) is small, fail-closed, and honest about what it costs; shipping without it would mean
hosted Gemini silently executes branch-authored code on the machines of the operators most likely to use
it.

### 3.1c The allowlist contents depend on `SupportsMcpServers`, and rev 5 got this backwards

Rev 5 corrected the allowlist to "the servers kcap injects" — which contradicts §3.4, where
`SupportsMcpServers` is **false** and therefore kcap injects **nothing**. Both cannot be right, and the
implementation would have exposed it immediately.

Resolved:

* **While `SupportsMcpServers: false`** (now): there are no injected servers, so a single **non-matching**
  name is exactly right. It permits nothing, which blocks the repo-authored server and costs us nothing.
  Measured working — `--allowed-mcp-server-names __kcap_none__` clamped the repo server without crashing.
  **But the name must be unguessable, not merely unusual — see §3.1d.**
* **When §3.4's call-level probe flips the flag to true**: the allowlist must become the injected server
  names in the same change, or hosted Gemini ships with MCP silently broken. That coupling is the reason
  this subsection exists rather than a comment.
* **Never the empty string** — it crashes config load before session start (§3.1b).

The test in §7.7 asserts the coupling, not just the flag: allowlist contents and `SupportsMcpServers` must
agree, so flipping one without the other fails.

### 3.1d A FIXED deny-all name is attacker-matchable — the name must be generated per launch

Rev 5 shipped the literal `kcap-none` with a comment calling it "a name no MCP server will ever have."
Nothing enforced that, and the comment was the only thing standing between the clamp and a bypass.

**Measured:** a repository whose MCP configuration names its server `kcap-none` **executes**. The allowlist
is a name match, so an attacker who can read this source — it is a public repository — can satisfy it by
choosing the same name. A repo-authored server running under the daemon user is remote code execution, which
is exactly what §3.1's clamping rule exists to prevent.

The uncomfortable part: this collision class was predicted in writing on a *different* issue during this same
work, and then shipped here anyway. A guard whose safety rests on an adversary not choosing a particular
string is not a guard, and "no one would name it that" is the same shape of reasoning as §4.2's withdrawn
"the values are safe because of what they usually contain."

**Fix.** The descriptor carries a *placeholder*, and `AcpHostedAgentRuntimeFactory` substitutes an
unguessable name at each launch:

```csharp
internal const string UnmatchableMcpNamePlaceholder = "__kcap_unmatchable_mcp_name__";

static List<string> SubstituteUnmatchableNames(List<string> argv) {
    for (var i = 0; i < argv.Count; i++)
        if (argv[i] == UnmatchableMcpNamePlaceholder)
            argv[i] = $"kcap-deny-{Guid.NewGuid():N}";

    return argv;
}
```

Per launch, not per process: two agents launched from the same daemon get different names, so nothing
observable in one run helps the next.

**Verified against the worst case:** a repository naming its server *the placeholder itself* is blocked
(the placeholder never reaches the command line), and an ACP-injected server still loads — so the clamp is
proven in both directions rather than only the negative one.

### 3.2 `SupportsUnattended: false` — hosting only, for now

Unattended review is AI-1413. Withholding it here follows the Kiro precedent, and for the same structural
reason: the containment story has to be settled on its own issue rather than inherited by default.
`UnattendedTrustArgv` is therefore empty; AI-1413 will have to decide an approval posture and an MCP
clamp. This spec deliberately does not name the flags: ACP's measured `availableModes` says what the
*protocol* offers, not what the CLI's `--approval-mode` accepts or contains, and guessing would hand
AI-1413 a prescription dressed as a finding.

### 3.3 `NoOpModelSelector` — the write half is unverified and fails silently

`session/new` returns `models`, so `ConfigOptionModelSelector`'s *read* half fits. Its **write** half —
`session/set_config_option` actually taking effect — is unverified on Gemini and that selector fails
**silently**, producing a session that reports the requested model while running another.

`ResolveDefaultModel: null` alone would not be enough: `ResolveRequestedModel` prioritises a per-launch
`RuntimeStartContext.Model` and would reach a live selector anyway. The selector itself has to be the
no-op. This is the identical call the Kiro descriptor faced, resolved the same way.

Model override arrives with the follow-up that verifies the write half — which, note, interacts with the
`ModelSelectionLaunchPolicy` added for Kiro: with `CanSelectModel: false`, an explicitly requested
*reviewer* model is rejected rather than silently ignored, while an inherited one clears the reported
model. That is the correct behaviour here and needs no change.

### 3.4 `SupportsMcpServers: false` — pending a call-level probe

**What the flag actually gates, since the name does not say:** `AcpHostedAgentRuntimeFactory` reads
`descriptor.SupportsMcpServers ? ctx.McpServers : null` — a **blanket** switch on the `mcpServers` array
passed to `session/new`, not a per-transport one. Setting it false omits the field entirely.

That matters for the trade here. Gemini measurably advertises `{http: true, sse: true}`; what it does not
advertise is **stdio** — and stdio is what kcap injects (the review-flow servers are stdio processes;
Copilot's descriptor sets the same flag false for the same reason and preloads its servers through a
process argument instead). So `false` withholds the transport kcap would actually use, and the two
advertised remote transports are not something kcap has a caller for today.

**That advertisement is not a reliable discriminator either way**: AI-1404 established that Kiro honours
stdio servers passed in `session/new.mcpServers` despite advertising exactly the same `{http, sse}` shape.

So the flag starts `false`, and flipping it requires the evidence Kiro's flip required: a purpose-built
stdio server driven all the way to a real `tools/call`, with the tool's nonce reaching the model and the
turn ending `end_turn`. Explicitly **not** sufficient: a `server_initialized`-style notification, which
proves only that a server started — a tool can still be absent from `tools/list`, refused by policy,
mis-namespaced, or fail at invocation.

Starting `false` withholds an unverified stdio path rather than promising one that may not work. It is
not disabling a measured remote capability, because nothing in kcap currently injects one; if that
changes, the blanket flag is the wrong shape and should be split by transport rather than flipped.

### 3.5 `--approval-mode` is deliberately not passed for hosting

Interactive hosting should behave as the user's own session does. `plan` is a strong containment primitive
and belongs to AI-1413; pinning it here would silently make hosted Gemini read-only, which is not what a
hosted agent is for.

### 3.6 The default model is a pricing sentinel

`currentModelId` is measured as `auto-gemini-2.5`, and `availableModels` contains a literal `auto`. Per
`PricingSentinels`, placeholder ids like `auto`/`default` never price.

**What is not yet established is whether `auto-gemini-2.5` actually matches that sentinel rule.** The
presence of a separate literal `auto` in the list does not prove the reported id is normalised to it, and
`PricingSentinels`' matching (exact? prefix? after normalisation?) was not read. So the honest statement
is: the reported id is a placeholder-shaped id that **either** hits the sentinel and does not price,
**or** misses every pricing entry and does not price — and in both cases a hosted Gemini session shows no
cost, but by different mechanisms with different fixes.

**And "either way it does not price" is still a claim beyond the evidence** — the second draft's
disjunction was not exhaustive. If `auto-gemini-2.5` misses the sentinel it does not follow that it misses
every pricing entry; it could match one directly or after normalisation, in which case a hosted Gemini
session would price, possibly at the wrong model's rate. Three outcomes, not two.

**So the pricing result is UNKNOWN until measured, and §7.5a is a gating characterisation test rather than
a confirmation of a preferred answer.** It asserts the reported model id, then records what pricing
actually does with it. The acceptance criterion follows the observation:

| Observed | Then |
|---|---|
| explicit *cost unavailable* | accept; README says a hosted Gemini session shows no cost |
| a price at a **concrete** model's rate | **escalate** — reporting a cost for a model the session may not be running is worse than reporting none, and belongs to AI-1612 before this ships |
| rendered as `0` or "free" | **defect**, fix here — a silent zero reads as a measurement rather than an absence |

Whichever it is, the README states the measured behaviour. What this spec must not do is write down the
answer it expects and then test for it; that is how §1.1 happened.

The interaction with §3.3 is real either way: with model selection withheld, there is no path to a
deliberately-priced hosted Gemini session inside this issue.

## 4. The daemon environment — the operational requirement

**Whatever Gemini configuration the operator's shell carries, the DAEMON does not see it.** That part is
measured and unconditional: `~/.zshrc` is not sourced by a non-interactive shell, launchd inherits nothing
from an interactive one, and the running daemon's environment was verified to carry only `KCAP_PROFILE`
and `PATH`. Anything Gemini needs from the environment must be captured into the unit.

**What is NOT established: that `GOOGLE_CLOUD_PROJECT` is required on every auth path.** It was measured
as required on **one** configuration — the operator's `oauth-personal` / Gemini Code Assist login, where
its absence produced §1.1's misattributed tier error. `initialize` advertises four auth methods
(`oauth-personal`, `gemini-api-key`, `vertex-ai`, `gateway`), and nothing here shows the other three
behave the same way; an API-key configuration plausibly needs no project at all.

Saying "must be present, or hosted Gemini will fail" would be exactly the inference-from-one-auth-path
this spec spends §1.1 warning against, and it would contradict §5's own policy in the same document. So:

* **Supported and verified:** `oauth-personal` with a project. That is the configuration hosting is
  specced against, and the only one for which a requirement is claimed.
* **Not verified:** `gemini-api-key`, `vertex-ai`, `gateway`. Capturing their variables (§4.1) makes them
  *possible* without asserting they work; a positive acceptance case per configuration is follow-up, and
  the README says which one was verified rather than implying all four.
* Everywhere the absence of the project variable is surfaced (§4.3, §5), the wording is **conditional**.

### 4.1 Mechanism: extend `ServiceEnvironment.Keys`

`ServiceEnvironment` already exists for precisely this — an allowlist captured from the installing shell
and baked into the launchd/systemd unit, because *"supervised jobs don't inherit the interactive shell
PATH"*. `Build(profileName, source, isWindows)` is **pure**, which is what makes §7's tests possible
without touching a real service.

**Capture** — configuration and paths, safe in a file on disk (reference counts are occurrences in
Gemini's own bundle, i.e. evidence the CLI actually reads them):

| Variable | Refs | Why |
|---|---|---|
| `GOOGLE_CLOUD_PROJECT` | 108 | the one whose absence caused §1.1 |
| `GOOGLE_APPLICATION_CREDENTIALS` | 57 | ADC — a **path to** a credential, not the credential |
| `GOOGLE_CLOUD_LOCATION` | 39 | Vertex region |
| `GOOGLE_GENAI_USE_VERTEXAI` | 30 | selects the Vertex backend |
| `GOOGLE_GENAI_USE_GCA` | 24 | selects Gemini Code Assist |
| `GOOGLE_CLOUD_PROJECT_ID` | 18 | alternate spelling the CLI also honours |
| `GOOGLE_GEMINI_BASE_URL` | 30 | endpoint override (enterprise/proxy) |
| `GOOGLE_VERTEX_BASE_URL` | 21 | endpoint override (enterprise/proxy) |

**Never capture:** `GOOGLE_API_KEY` (36 refs) and `GOOGLE_CREDENTIALS` (12). These are secrets and the
unit is a file on disk. That split is the principle already encoded by `KCAP_COPILOT_TOKEN_CMD`: a
*command that prints* a token is safe to persist; the token is not. A deployment that genuinely needs
`GOOGLE_API_KEY` for an unattended reviewer belongs in AI-1413's broker discussion, not in the plist.

**The Vertex variables are captured even though the measured setup did not need them.** The operator's
explicit direction: assume the Vertex path is needed rather than force a second round-trip when it turns
out to be. They are inert when unset — `Build` only copies keys present in the source.

### 4.2 Serialisation safety — enforce at the sink, because the source is not a boundary

Two drafts got this wrong in two different ways, and reading the code made the answer smaller than either.

**Draft 1 argued the values are safe because of what they usually contain** — project ids and regions are
`[a-z0-9-]`, the flags are booleans, a Windows path cannot contain a quote. Withdrawn: `Build` copies
whatever string the environment holds, and nothing validates that a variable *named*
`GOOGLE_CLOUD_PROJECT` contains a project id. A semantic label is not validation.

**Draft 2 moved the check into `ServiceEnvironment.Build`** and had it drop unrepresentable values.
Withdrawn too, and this one is disproved rather than merely doubted: `DaemonCommands` builds the service
environment as `new Dictionary<string,string>(ServiceEnvironment.Capture(profileName)) { ["KCAP_DAEMON_SUPERVISED"] = id }`
— it **adds an entry after `Capture` returns**. So a value already reaches the writers today without
passing through `Build`. A check there is not a boundary; it is a courtesy at one of several doors.

**What the writers actually do** (read, not inferred — the earlier table was wrong about systemd):

| Writer | Handling | Consequence of a hostile value |
|---|---|---|
| launchd (`LaunchdUnit` → `ServiceText.Xml`) | XML entity escaping | contained; raw control characters are not XML 1.0-legal |
| systemd (`SystemdUnit.EnvAssignment`) | quotes the whole `KEY=VALUE` when needed, escapes `\` and `"`; `ServiceText.SystemdValue` rewrites CR/LF → space | contained, **but the CR/LF rewrite silently corrupts** a path or URL |
| Windows (`WindowsTaskUnit` → `ServiceText.CmdValue`) | `%` → `%%`, nothing else | **arbitrary command execution.** An embedded `"` closes the `set "K=V"` assignment, after which `&`, `|`, `<`, `>`, `^` are live batch metacharacters in a file the service runs |

**Decision: the Windows writer validates its own input and refuses to emit a value it cannot represent
— for every key, not just these eight.** `service install` then fails with the offending key named,
rather than writing a file that could execute something. Three reasons this is the right shape:

* It is a **boundary**: no caller can bypass it, including the `KCAP_DAEMON_SUPERVISED` path that already
  bypasses `Build`.
* It **closes the pre-existing hole** rather than merely not widening it. `PATH` and `KCAP_URL` go through
  the same writer today with the same exposure, so scoping the fix to Gemini's variables would leave a
  known execution vector live while congratulating itself.
* It makes the drop-recording contract unnecessary. `service install` is interactive: failing there with
  a named key is strictly better feedback than a silent drop plus a durable `(key, reason)` record that
  a later `kcap status` has to surface. Draft 2 needed that machinery; this does not.

Failing the install is acceptable because the trigger is a value that cannot be represented at all —
never a legitimate project id, region, boolean or path. And `CmdValue` gaining a correct implementation
later is a strict improvement that this check does not block.

**The CR/LF rewrite is named, not fixed here.** systemd silently turning a newline into a space corrupts
a value rather than executing anything, and `SystemdValue` is shared with `Description=`. So the
representability check rejects CR/LF in an *environment value* on every platform (no legitimate value
contains one), and the `Description` behaviour is left alone.

**Secondly, one of these is not merely a config string.** `GOOGLE_GEMINI_BASE_URL` and
`GOOGLE_VERTEX_BASE_URL` are URLs, and a URL can carry userinfo or a query-string token. "It is a URL"
therefore does not make it non-secret. See §4.2a.

### 4.2a Disclosure: what the unit file actually exposes

The first draft called the unit "world-readable-ish" and left the threat model unstated. The second
measured 0600 and generalised it to all platforms. **Both were wrong; here is what the code does.**

`ServiceFiles` writes with `UnixCreateMode = UserRead | UserWrite` — **0600** — then re-checks the mode on
the open handle (because `UnixCreateMode` is still filtered through the umask, so the request is not the
result), and refuses to write into a group- or other-writable directory.

**All of that is Unix-only.** Every permission path in `ServiceFiles` begins
`if (OperatingSystem.IsWindows()) return;`, with the comment *"ACL-governed, inherited from the user
profile"*. So on Windows the `.cmd` wrapper carries whatever the profile directory's inherited ACL grants
— typically the user, SYSTEM and Administrators, and **not** verified here. The 0600 measurement answers
nothing about Windows, and a spec that leans on it for all three platforms is asserting past its evidence
for the second time in this section.

**The principle, stated once so future additions do not get classified by variable name:** persist a value
into a service unit only when (a) there is no non-persistent alternative, **and** (b) the platform gives
an owner-only guarantee this code actually enforces.

Applying it:

| Value | Unix | Windows |
|---|---|---|
| `GOOGLE_CLOUD_PROJECT` / `_PROJECT_ID`, `GOOGLE_CLOUD_LOCATION`, `GOOGLE_GENAI_USE_VERTEXAI`, `GOOGLE_GENAI_USE_GCA` | capture — not secret-capable | capture |
| `GOOGLE_APPLICATION_CREDENTIALS` (a path), `GOOGLE_GEMINI_BASE_URL`, `GOOGLE_VERTEX_BASE_URL` (may carry userinfo or a query token) | capture — the reader of a 0600 unit is the user who owns the credential anyway | **exclude**, on (b) |
| `GOOGLE_API_KEY`, `GOOGLE_CREDENTIALS` | **exclude** | **exclude** |

The Windows exclusion follows the `KCAP_COPILOT_TOKEN_CMD` precedent exactly: a platform whose guarantee
is unverified does not get the secret-capable values. Hosted Gemini stays functional there for the common
case (a project-scoped login), and the README says which values Windows will not carry and why — so an
operator who needs Vertex-with-ADC on Windows knows to set them another way rather than wondering why the
unit is incomplete.

Establishing and enforcing a Windows ACL is a better answer and is deliberately not attempted here: it is
service-installer work touching all vendors, and guessing at it would be the third overreach in one
section.

### 4.2b What review found after this section shipped — the table above was still too small

§4.2's decision was right in shape and wrong in scope, and five review rounds took it apart. Recording the
outcome here because the *pattern* is the reusable part, not the individual escapes.

**The scope error.** §4.2 reasoned about environment *values*, one writer at a time. But each writer
interpolates several values into the same line or file, and in every one of them some were treated and their
neighbours were not:

| sink | guarded when §4.2 shipped | unguarded neighbour on the same line/file |
|---|---|---|
| Windows `.cmd` exec line | the binary path | the log path, every `ExtraArgs` token |
| Windows `set "K=V"` line | the value | the **key** (a quote in a key closes the assignment identically) |
| systemd `Environment=` | the value | the key (a key can inject an `ExecStartPre=` that runs on every restart) |
| systemd `ExecStart=` | nothing | the binary path, the log path, every argument |
| launchd plist | environment values | the label, `ProgramArguments`, `StandardOut/ErrorPath` |
| Task Scheduler XML | nothing | the wrapper path, the service id |

Every one of those was found by asking the same question the table above should have asked: *not* "is this
value safe?" but "does every value interpolated into this artifact pass the same guard?" Each sink now has
one guard-then-escape (or guard-then-quote) helper that all of its interpolations go through, so the
invariant holds for the next value added rather than for the ones that happened to be audited.

**The second error: escaping what a format expands, without checking what else it expands.** `CmdValue`
doubles `%` — correct, and it made me stop looking. Each format turned out to run *more* than one expansion
pass, and quoting alone was not a boundary in either:

* **cmd** — `& | < > ( ) ^` are metacharacters *outside* quotes, so quote-when-it-has-a-space left
  `8&calc.exe` bare in a file the OS executes at every logon. Now every exec-line value is quoted
  unconditionally. `!NAME!` delayed expansion works *inside* quotes and can be enabled machine-wide by
  registry, so the execution **mode** is now part of the artifact (`setlocal DisableDelayedExpansion` plus
  `/v:off` on the action) rather than an assumption about the host. And `cmd /c "<path>"` relied on cmd's
  conditional quote-stripping, which a path containing `&` defeats — now the nested-quote `/d /s /v:off /c
  ""<path>""` form.
* **systemd** — expands `%` specifiers *and* `$NAME`/`${NAME}` variables in `ExecStart=`, so a path under
  `/home/50%off` or one containing `${HOME}` was rewritten in the *executable* position. Both are doubled
  now and reversed by `BinaryFromUnit`. `$` is deliberately **not** doubled in `Environment=`, where systemd
  expands specifiers but not variables — same character, adjacent sinks, different correct treatment. The
  apostrophe is as structural to systemd's word lexer as the double quote and was missing from `NeedsQuote`.

**The same-character-two-sinks rule.** `%` is the clearest case: the wrapper *body* is a batch file, so
`%%` is correct there; the Task XML `Arguments` is a *command line*, where `%%` does not exist as an escape,
so a `%` in the wrapper path is **refused**. A per-character policy would have got one of the two wrong.

**Escape only where the escape is verifiable; otherwise refuse.** Nothing in CI or on the development
machines can exercise a real systemd or a real cmd parser. So where the documented escape is unambiguous
(`%%`, `$$`, `/s` nested quotes, `/v:off`) it is implemented and the tests assert the rendered text plus the
round-trip; where it is not (systemd's `\;` for a literal semicolon), the input is **refused** instead — a
refusal cannot be subtly wrong, and no legitimate daemon argument is a lone semicolon. The same reasoning
refuses all C0/DEL control characters at both systemd sinks rather than emitting C-style escapes.

**A silent rewrite is worse than a refusal.** §4.2 left `SystemdValue`'s CR/LF→space normalisation in place
as "corrupts rather than executes". That is now refused too: a service running with a value nobody chose is
harder to diagnose than an install that failed and named the variable.

**The XML predicate was not the XML predicate.** `char.IsControl` differs from XML 1.0 in both directions —
it accepts U+FFFE/U+FFFF and lone surrogates (which no encoder can represent) and rejects U+007F–U+009F
(which XML 1.0 permits). `XmlConvert.IsXmlChar` plus surrogate-pair awareness replaced it, so an emoji in a
path is accepted and a malformed surrogate is not.

Every guard above is mutation-tested: 27 mutants, each restored from git and verified by *content* rather
than by `git diff` — a diff can hide a bad restore through alignment, which happened once during this work.

### 4.3 Install-time capture is a footgun, and must be surfaced

The values are read from the installing shell, so an operator who exports `GOOGLE_CLOUD_PROJECT` *after*
`kcap daemon service install` gets a unit without it — which is exactly what happened. Capturing more
variables makes this worse, not better, because the failure stays silent until a launch fails with §1.1's
misattributed message.

`StatusCommand` already reports per-vendor hook state and therefore already knows whether Gemini is
present. It gains one warning line, on this condition only:

> Gemini is installed **and** the daemon service is installed **and** its unit carries **neither**
> `GOOGLE_CLOUD_PROJECT` **nor** `GOOGLE_CLOUD_PROJECT_ID`

**"Project configuration present" is the OR of those two, and that is the vendor's own definition**, not
an inference from reference counts. Gemini's error text reads: *"The GOOGLE_CLOUD_PROJECT (or
GOOGLE_CLOUD_PROJECT_ID) environment variable must be set…"*. §4.1 lists the alternate as honoured, so a
predicate keyed on the first alone would diagnose a correctly configured unit as broken — a false positive
the spec described and then walked into. §7.5 covers the alternate-only case explicitly.

> ⚠ Gemini detected, but the daemon environment has no `GOOGLE_CLOUD_PROJECT`. If your Gemini login
> needs a project (the Code Assist / `oauth-personal` path does), hosted Gemini agents will fail — and
> the error Gemini reports names the wrong cause. Set it where the daemon can see it, then re-run
> `kcap daemon service install`.

**Conditional, per §4**: the variable is required on the one auth path measured, and the warning says so
rather than asserting it universally. An API-key deployment that legitimately needs no project should not
be told its setup is broken.

Deliberately narrow in two more ways. It reads the **installed unit**, not the current process
environment — the current process is an interactive shell, which is the one place the variable being set
proves nothing. And it does not warn when no service is installed, because there is nothing to be wrong
yet. §7.5 tests both, because "reads the unit" is the whole point and is invisible from the output.

## 5. The launch-failure diagnostic — a possibility, never a verdict

When a hosted agent fails to launch and the failure could plausibly be auth or project configuration,
kcap must:

1. **Show the vendor's actual error verbatim** — never swallowed, never paraphrased.
2. **Suggest, not assert**, that it may be authentication not captured at setup time, or missing project
   configuration.
3. **Say concretely how to resolve it**: confirm the CLI is logged in; ensure `GOOGLE_CLOUD_PROJECT` (and
   the Vertex variables, if used) are set **where the daemon can see them** — the service unit, not
   `.zshrc` — then re-run `kcap daemon service install` and restart the daemon.
4. **Never claim certainty.** The failure may be entirely unrelated.

**Point 4 is the requirement, not politeness.** §1.1 is a live example of the alternative: a definitive
message naming the wrong cause sent this investigation down two wrong paths. A hint that says "this could
be X, here is how to check" is strictly better than a confident claim that is sometimes false.

### 5.0 Make it structured, or the requirement is untestable

"The text does not claim the cause" cannot be proven about arbitrary prose by grepping for forbidden
words — a reviewer was right to call that unfalsifiable, and a word-list would rot into a string-match
that passes while the copy drifts.

So the hedging is moved out of the prose and into the shape:

* The runtime attaches a **structured** hint — a `possible_auth_or_project_configuration` kind plus the
  vendor's verbatim error as a separate field — rather than a pre-composed sentence.
* The conditional rendering of that kind is **one fixed string**, owned in one place and **golden-tested**.
* The verbatim error is never interpolated into the hint sentence, so no amount of copy editing can make
  the hint appear to be *about* a cause it merely accompanies.

**The approved rendering is part of this design, not the implementer's choice.** A golden test freezes
whatever sentence gets written first; it does not make that sentence conditional. So the sentence is here,
and the golden test pins *this*:

> This **may** be an authentication or project-configuration problem, or it **may** be unrelated. If
> hosted Gemini has not worked on this machine before: check `gemini` is logged in, and that
> `GOOGLE_CLOUD_PROJECT` (or `GOOGLE_CLOUD_PROJECT_ID`) is set **where the daemon can see it** — the
> service unit, not your shell profile — then re-run `kcap daemon service install` and restart the daemon.

Every clause is deliberate. Two `may`s and an explicit "or it may be unrelated", because the failure often
is. "If hosted Gemini has not worked on this machine before" scopes the advice to the case where it
applies, instead of telling an operator whose setup was fine yesterday to go and reinstall a service.
And it never restates the vendor's error, which sits in its own field.

**Scope of the change, since this is bigger than the existing string seam.** `DiagnosticAuthHint` today
returns a `string` interpolated into an `InvalidOperationException` message that
`LaunchFailedAsync` forwards to the server. Introducing a structured kind therefore touches a wire shape
consumed by an older server, so the implementation keeps the existing message field verbatim-compatible
and carries the kind **additively** — an older consumer sees exactly today's text and ignores the new
field. If that turns out not to be achievable additively, the fallback is to keep the string seam and
golden-test the string alone; the honesty requirement is carried by the wording above either way, and it
is not worth a protocol break.

**Trigger policy, stated rather than implied.** §5 says "when the failure could plausibly be auth or
project configuration"; the seam in fact fires on **every** non-version handshake exception. Those are
different rules and the spec has to pick one. It picks the existing behaviour — **attach on every
handshake/`session/new` failure** — because the alternative requires pattern-matching vendor error text
to decide plausibility, and §1.1 is the case study in why that inference is unreliable. The honesty
requirement is then carried entirely by the wording ("if this is an auth/subscription issue…"), which is
exactly what makes a *possibility* the right register: it is attached to failures that are often
something else, and it must read that way.

### 5.1 The seam already exists and already covers the right failure

`AcpHostedAgentRuntime.DiagnosticAuthHint` is vendor-branched today (Cursor names `cursor-agent login`;
Copilot names `copilot login`; Kiro deliberately names no command, because none was ever observed). Gemini
gets an arm covering login **and** project/Vertex configuration.

**Checked, because the issue flagged it as an open question:** the hint fires from a `catch` whose `try`
spans **both** `initialize` and `session/new`. A missing project surfaces at `session/new` (`-32000`,
after a successful `initialize`), so it is already covered. No restructuring needed — but the test in §7
pins it, because "already covered" is exactly the kind of claim that quietly stops being true.

`DiagnosticBinary` needs no Gemini branch: the vendor key and the binary name are both `gemini`, so the
generic fallback is already correct. Cursor and Kiro need branches only because their keys differ from
their binaries.

## 6. Documentation

README's daemon-environment section already documents `KCAP_*_PATH`. It gains a Gemini entry covering:
the login assumption (§1.2); the project and Vertex variables and that they must reach the **daemon**
rather than an interactive shell (§4); the re-run-setup footgun (§4.3); and the fact that a hosted Gemini
session currently reports an unpriceable model (§3.6).

## 7. Test plan

Per the project's per-vendor convention, and mirroring what AI-1404 shipped.

1. **Descriptor** — `AcpVendorDescriptorTests`: vendor key, argv (including `--skip-trust`),
   `SupportsUnattended: false`, `SupportsMcpServers: false`, `ModelSelector` is the no-op,
   `CanSelectModel` is false.
2. **Orchestrator** — `AgentOrchestratorVendorTests`: gemini routes to the ACP runtime factory; an
   explicitly requested **reviewer** model is rejected rather than silently dropped; and — the case the
   first draft missed — an explicitly requested **hosted** model does not reach a live selector and be
   silently ignored. Those are different `ModelSelectionLaunchPolicy` dispositions and only one of them
   was covered.
3. **Availability** — a **hermetic** test: a stub PATH resolver, not the developer's machine. Assert that
   the shipped `DaemonConfig.GeminiPath` default is what gets probed, so a wrong default fails here. This
   is the test that would have caught the Kiro `kiro`→`kiro-cli` defect, where a wrong default meant the
   vendor was silently never advertised. Phrasing it as "resolves on a correct install" would make it
   environment-dependent and therefore worthless in CI.
4. **Environment capture** — `ServiceEnvironmentTests` against the pure `Build(profile, source,
   isWindows)`, asserting **both directions**:
   * every variable in §4.1's capture table is carried through when present in the source;
   * `GOOGLE_API_KEY` and `GOOGLE_CREDENTIALS` are **not**, on any platform;
   * absent variables produce no empty entries;
   * the §4.2a platform split holds: the secret-capable values (`GOOGLE_APPLICATION_CREDENTIALS` and
     the two base URLs) are captured off-Windows and **excluded on Windows**.

   Both directions matter. A test that only asserts the captures would stay green if someone widened the
   allowlist to `GOOGLE_*`, which is the one change that must never pass.
4a. **Writer validation — tested at the writer, directly.** `LaunchdUnit`, `SystemdUnit` and
   `WindowsTaskUnit` each fed values containing `"`, `%`, `&`, `|`, `<`, `>`, `^`, CR/LF and a non-ASCII
   character, **called directly rather than through `Build`** — routing through `Build` would make it an
   end-to-end pipeline test and would pass even if the writer were defenceless. Assert: launchd and systemd
   contain the value; the Windows writer **rejects** what it cannot represent, so `service install` fails
   with the key named; and no value reaches an executable position in the emitted `.cmd`. Include a value
   arriving by the `KCAP_DAEMON_SUPERVISED` route, which bypasses `Build` in production today — that is
   the case that proves the boundary is at the sink.
5. **Status warning** — the §4.3 condition is a pure predicate over (gemini installed, service installed,
   unit environment) and is tested as one, plus a boundary test that `StatusCommand` reads the **installed
   unit** rather than the current process environment (set the variable in the process and *not* in the
   unit: the warning must still appear). Negative cases: no service installed → no warning; either
   `GOOGLE_CLOUD_PROJECT` **or** `GOOGLE_CLOUD_PROJECT_ID` present → no warning (the alternate-only case
   is the false positive draft 2 would have shipped); present-but-empty and malformed → treated as absent,
   not as set.
5a. **Pricing — a characterisation test, gating.** Given Gemini's measured `session/new` response, assert
   the reported model id and **record** what pricing does with it, then apply §3.6's outcome table: an
   explicit *unavailable* is accepted, a price at a concrete model's rate escalates to AI-1612 before this
   ships, and a rendered `0`/"free" is a defect fixed here. The test must not be written to expect one of
   the three.
6. **Diagnostic** — the structured hint (§5.0) carries the `possible_auth_or_project_configuration` kind;
   the rendered copy matches the **golden string quoted in §5.0** (not whatever the implementer writes),
   including both `may`s and the "or it may be unrelated" clause; an older-consumer shape still receives
   today's message text verbatim; the vendor's error survives **verbatim** — including
   distinctive punctuation and newlines — and stays in its own field rather than being interpolated into
   the hint; and the hint is reached from a `session/new` failure, not only an `initialize` failure. Also
   drive an obviously *unrelated* `session/new` failure and assert the hint still reads as a possibility
   rather than a diagnosis. Golden-testing the copy is what makes §5's honesty requirement falsifiable
   instead of a word-list that rots.
7. **MCP boundary** — assert that with `SupportsMcpServers: false` the `session/new` payload **omits**
   `mcpServers` at the protocol boundary. A descriptor-field assertion does not prove the factory honours
   it (§3.4 quotes the one line that does).
8. **README (§6)** — no automated test; an explicit acceptance checklist on the PR, itemising each
   required point. The definition of done says "no requirement without a test", and documentation is the
   one place that cannot be honoured literally — so it is honoured visibly instead of quietly skipped.
9. **No live cert here.** Model receipt is AI-1463's cert; this issue's live evidence is the §2 probe,
   which is recorded rather than automated. A hosted-launch smoke test belongs with AI-1413, where a
   reviewer runs unattended end to end.
10. **The §3.1 trust probe is a gating deliverable, not a test.** Its output is either a clamp or a
   documented accepted risk; the PR cannot land with the question open.

**Every guard assertion must be mutation-proven.** The standard from AI-1404 and AI-1463: deleting the
guard must fail exactly the intended test, and nothing else. Both of those issues shipped, or nearly
shipped, a guard test that passed with the guard removed.

## 8. What this issue does NOT do

* **AI-1413 (Gemini unattended reviewer)** — containment, the sandbox credential question, `--approval-mode`,
  and the MCP clamp. Distinct issue, distinct epic.
* **AI-1612 (reported-vs-running model)** — §3.6 records the sentinel; fixing the reporting is that issue.
* **The MCP stdio flip** — §3.4 states the evidence required; the probe is follow-up work.
* **A credential broker** — §1.2. Hosting assumes the operator logged their CLI in.
