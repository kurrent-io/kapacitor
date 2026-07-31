# AI-899 — Gemini CLI as an ACP hosted agent

**Status:** specced 2026-07-31 against `gemini 0.53.0` and `origin/main` (`e2b2821`).
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
folding needs no plain-text workaround here; `authMethods` is non-empty (Kiro `[]`), which is what gives
AI-1413 a credential story at all.

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
    Argv:                ["--experimental-acp", "--skip-trust"],
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
   Gemini settings, MCP definitions, and any hook/extension mechanism.
2. **Then either clamp them, or state the accepted risk explicitly in the README**, with the reasoning
   visible to an operator deciding whether to enable hosted Gemini on untrusted branches.

This is a gating item, not a follow-up: the flag is required for the feature to work at all, so the
question cannot be deferred past the thing that needs it. It is also a question every ACP vendor with a
trust model will face, so the answer is worth writing down once.

### 3.2 `SupportsUnattended: false` — hosting only, for now

Unattended review is AI-1413. Withholding it here follows the Kiro precedent, and for the same structural
reason: the containment story has to be settled on its own issue rather than inherited by default.
`UnattendedTrustArgv` is therefore empty; when AI-1413 lands it will need `--approval-mode yolo` (or
`plan`, see §3.5) plus whatever MCP clamp the review flow requires.

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

§7.5a therefore **pins the actual outcome with a test** rather than asserting the mechanism: given
Gemini's measured `session/new` response, assert the reported model id and assert the resulting state is
an explicit *cost unavailable*. And it asserts the surface does **not** render that as `0` or "free" —
a silent zero is worse than a blank, because it reads as a measurement.

This is accepted rather than fixed here, and the acceptance is real: with §3.3 withholding model
selection, there is no path to a priced hosted Gemini session inside this issue. It interacts with
AI-1612 (reported-vs-running model). What this spec owes is that the behaviour is known, tested, and
visibly absent rather than discovered later as a cost-report gap.

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

### 4.2 Serialisation safety — validate the value, do not trust its name

An earlier draft argued these eight are safe on Windows because *of what the values are*: project ids and
regions are `[a-z0-9-]`, the flags are booleans, the base URLs are URLs, a Windows path cannot contain a
quote. **That argument is withdrawn.** `Build` copies whatever string the environment holds; nothing
validates that a variable named `GOOGLE_CLOUD_PROJECT` contains a project id. A semantic label is not
validation, and the one serialiser that cannot take arbitrary input is the one that matters:

`WindowsTaskUnit` emits a `.cmd` wrapper of `set "K=V"` escaped by `ServiceText.CmdValue`, which escapes
**only** `%`. An embedded `"` terminates the quoted assignment, after which `&`, `|`, `<`, `>` and `^`
are live batch metacharacters in a file the service executes — i.e. **arbitrary command execution in the
daemon's own startup wrapper**. That is why `KCAP_COPILOT_TOKEN_CMD` is Windows-excluded, and the reason
generalises rather than being specific to that variable.

**Decision: enforce it in code, at capture.** `ServiceEnvironment.Build` gains a per-platform
representability check and **drops** (never silently mangles) a value the target serialiser cannot carry,
recording the drop so `kcap status` can surface it. Preferring a drop to a throw keeps a hostile or
merely odd value from bricking `service install` outright, and preferring either to a raw write keeps a
malformed value from becoming an execution vector.

The three targets, stated as what they can carry rather than as "safe":

| Writer | Escaping | Can carry |
|---|---|---|
| launchd (`LaunchdUnit` → `ServiceText.Xml`) | XML entity escaping | any value XML 1.0 permits — **not** raw control characters |
| systemd (`ServiceText.SystemdValue`) | CR/LF → space | anything else; note this **rewrites** rather than rejects |
| Windows (`WindowsTaskUnit` → `ServiceText.CmdValue`) | `%` → `%%` only | values containing no `"` and no CR/LF |

**This is a pre-existing hole that these eight variables widen, not one they create** — `PATH` and
`KCAP_URL` go through the same writers today. Fixing `CmdValue` properly is the better long-term answer
and is out of scope here; the check keeps this issue from making the exposure worse, and §7 requires
adversarial tests against the **actual writers**, not only against `Build`.

**Secondly, one of these is not merely a config string.** `GOOGLE_GEMINI_BASE_URL` and
`GOOGLE_VERTEX_BASE_URL` are URLs, and a URL can carry userinfo or a query-string token. "It is a URL"
therefore does not make it non-secret. See §4.2a.

### 4.2a Disclosure: what the unit file actually exposes

The earlier draft called the unit "world-readable-ish" and left the threat model unstated. **Measured
instead:** `ServiceFiles` writes units with `UnixFileMode.UserRead | UnixFileMode.UserWrite` — **0600,
owner-only** — re-applying the mode explicitly because umask does not apply to a chmod, and it *refuses*
to write into a directory that is group- or other-writable.

That materially bounds the question. The reader of the unit is the same local user who owns the
credential file it points at, so:

* **`GOOGLE_APPLICATION_CREDENTIALS`** — persisting the path discloses the credential's location to a
  principal that can already read the credential itself. Accepted, and now on the stated grounds of
  measured permissions rather than an analogy to `PATH`.
* **The two base URLs** — accepted with the same reasoning, but flagged in the README: if a deployment
  puts a token in the URL, that token lands in a 0600 file on disk. An operator who considers that
  unacceptable should unset the variable before `service install`.
* **`GOOGLE_API_KEY` / `GOOGLE_CREDENTIALS`** remain excluded regardless. The 0600 mode is a bound on the
  blast radius, not a licence to persist secrets that have a non-persistent alternative — the same
  principle as `KCAP_COPILOT_TOKEN_CMD`, where a *command that prints* a token is persisted and the token
  is not.

### 4.3 Install-time capture is a footgun, and must be surfaced

The values are read from the installing shell, so an operator who exports `GOOGLE_CLOUD_PROJECT` *after*
`kcap daemon service install` gets a unit without it — which is exactly what happened. Capturing more
variables makes this worse, not better, because the failure stays silent until a launch fails with §1.1's
misattributed message.

`StatusCommand` already reports per-vendor hook state and therefore already knows whether Gemini is
present. It gains one warning line, on this condition only:

> Gemini is installed **and** the daemon service is installed **and** its unit carries no
> `GOOGLE_CLOUD_PROJECT`

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
  Changing it is then a deliberate edit to a pinned expectation, not an accident.
* The verbatim error is never interpolated into the hint sentence, so no amount of copy editing can make
  the hint appear to be *about* a cause it merely accompanies.

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
   * §4.2's representability check drops an unrepresentable value on the platform that cannot carry it,
     and keeps it on the platforms that can.

   Both directions matter. A test that only asserts the captures would stay green if someone widened the
   allowlist to `GOOGLE_*`, which is the one change that must never pass.
4a. **Serialiser adversarial tests — against the actual writers**, not only `Build`. `LaunchdUnit`,
   `SystemdUnit` and `WindowsTaskUnit` each fed values containing `"`, `%`, `&`, `|`, `<`, `>`, `^`,
   CR/LF, and a non-ASCII character; assert the emitted unit is well-formed and that no value can escape
   its assignment into an executable position. §4.2's whole argument is about these writers, and testing
   `Build` alone would leave it unexercised.
5. **Status warning** — the §4.3 condition is a pure predicate over (gemini installed, service installed,
   unit environment) and is tested as one, plus a boundary test that `StatusCommand` reads the **installed
   unit** rather than the current process environment (set the variable in the process and *not* in the
   unit: the warning must still appear). Negative cases: no service installed → no warning; variable
   present in the unit → no warning; present-but-empty and malformed → treated as absent, not as set.
5a. **Pricing** — given Gemini's measured `session/new` response, assert the reported model id and that
   the resulting cost state is an explicit *unavailable*, and that no surface renders it as `0`/free
   (§3.6). This pins the accepted behaviour rather than the mechanism.
6. **Diagnostic** — the structured hint (§5.0) carries the `possible_auth_or_project_configuration` kind;
   the rendered copy matches a **golden string**; the vendor's error survives **verbatim** — including
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
