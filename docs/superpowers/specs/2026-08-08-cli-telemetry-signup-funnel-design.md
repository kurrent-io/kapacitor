# CLI telemetry and the signup-funnel measurement gap

## Problem

Two PostHog vantage points exist today and neither sees the middle of the signup funnel.

**Web** (`kcap-web/src/pages/signup.astro`) fires `cli_snippet_copied` and
`signup_path_selected {path: "cli"}` when a visitor copies the install snippet. The snippet is the
static string `npm install -g @kurrent/kcap && kcap setup` — it carries no per-visitor token, so the
web person's trail ends at the clipboard.

**Server** (`kcap-server/src/Capacitor.Server/Posthog/`) emits `cli_setup_completed`,
`user_registered`, `session_ingest_started` and friends, keyed on
`HMAC-SHA256(Posthog:IdSalt, canonicalUserId)`. A tenant must already exist for any of it to fire.

Between "copied the command" and "a tenant exists" there is nothing. Someone who runs `kcap setup`,
signs in, discovers they have no workspace, is offered one, and quits — the single most important
population for the signup funnel — is invisible from both ends. The CLI has no telemetry at all.

This spec adds CLI-side telemetry whose primary job is to make that middle segment measurable, and
whose secondary job is to report which kcap commands people actually use.

## Decisions

### 1. The CLI ships events directly to `phog.kurrent.io`

The public write-only project token is embedded in the binary, targeting the same EU project the web
and server already use. Routing through the user's own Capacitor server was rejected: during the
segment this feature exists to measure there **is** no server — no `server_url`, no tenant, no token.
A design that depends on the server can't observe the pre-server funnel, which is the whole feature.

Self-hosted installs report too, subject to the opt-out in Decision 5. Restricting reporting to
SaaS installs was considered and rejected: it would blind us to command usage from exactly the
population whose workflows we understand least.

### 2. The CLI person stays anonymous, and joins the org as a group

`distinct_id` is an anonymous device id, generated once and persisted. The CLI cannot compute the
server's pseudonymous id (`Posthog:IdSalt` is server-side and must stay there), so CLI events
necessarily form a third person alongside the web person and the server person.

Where a workspace is known, the CLI attaches the `organization` group — the same group key the server
uses (`PosthogGroupIdentityService.cs:13`, `PostHogCaptureSink.cs:14`) — plus a matching `org`
property. CLI usage then rolls up per workspace and sits alongside server events in group-level
analysis, without ever linking a device to a named human.

Merging the anonymous device into the identified user via `$identify` was considered. It would need a
new server endpoint handing the client its own pseudonymous hash, and it buys person-level joins that
the funnel question does not require. Not in scope; the group is the cheap 80%.

The group's value on the server is `Tenant:Name` (`PosthogServiceCollectionExtensions.cs:71`), which
the Helm chart sets from the tenant slug — `Tenant__Name` is documented as "tenant slug; chart sets
to `{{ .Values.slug }}`" (kcap-server `docs/superpowers/specs/2026-06-09-server-diagnostics-design.md:111`).
Since a SaaS tenant is served at `{slug}.kcap.ai`, the CLI derives the identical value from the
server URL's host label and the two producers land in the same group. Slugs are canonicalised
lowercase (`SlugValidator.Canonicalize`) and host labels are lowercase, so no case reconciliation is
needed.

**This holds for SaaS only.** On a self-hosted deployment `Tenant:Name` defaults to `"local"` and is
otherwise whatever the operator configured; it has no defined relationship to the server's hostname.
Deriving a group from the host label there would produce a group that *looks* joined to the server's
but isn't — a silently wrong dashboard, which is worse than no dashboard.

So the `organization` group and its accompanying `org` property are both attached only when the
server URL is a `*.kcap.ai` host, where the chart guarantees the correspondence, and both are omitted
otherwise. Shipping the property unconditionally was considered and rejected: off SaaS its only
possible value is a fragment of an internal hostname, which buys no analysis (it can't be joined to
anything) and puts company-identifying data in the payload, against the never-collect list below.

### 3. Human-invoked commands and MCP tool calls; hooks excluded entirely

`kcap hook --claude` runs on every tool use of every recorded session — thousands per user per day,
inline in the agent's critical path. It emits nothing. Neither do the other machine-driven surfaces:
`watch`, `mcp` (as a *process*), `permission-request`, `generate-whats-done`, `set-title`,
`copilot-finalize`, `cursor-verify-appendonly`.

Sampling was considered as a way to keep hooks partially visible and dropped: with hooks excluded the
volume argument disappears, and unsampled events are exact, cheaper to reason about, and carry no
scale-up factor to misremember at query time. Everything reported is reported 1:1.

Recap and memory are used mostly *through MCP* rather than as terminal verbs, so MCP is instrumented
per **tool call**, not per process start. That is where the signal is: which tools agents actually
reach for.

### 4. Delivery is hybrid — eager for the funnel, at-exit for everything else

kcap commands are short-lived, so an event must escape before the process does.

- **In-memory queue** by default.
- **Setup funnel steps flush eagerly, mid-command.** Setup is already network-bound and human-paced,
  so the added round trip is invisible — and it means a run abandoned at any step has already
  reported every step it reached. This is load-bearing: the cohort being measured is precisely the
  one that never runs `kcap` again, so anything deferred to a later invocation is lost forever.
- **Everything else flushes once** from a `ProcessExit` handler under a ~1.5s budget. Verified by
  spike: `ProcessExit` observes `Environment.ExitCode` set from a top-level `Main`'s `return`, so
  per-command exit codes are recordable with a three-line change and no restructuring of the
  ~630-line dispatch switch in `Program.cs`.
- **A failed flush spills to a bounded disk spool** (`telemetry-spool.jsonl`, ~256KB, drop-oldest)
  that the next successful flush from any process replays. Offline runs survive without putting disk
  I/O on the normal path.

### 5. On by default, with a first-run notice

Precedence, highest first:

1. `KCAP_TELEMETRY` set explicitly — `0`/`off`/`false`/`no` disables; `1`/`on`/`true`/`yes` enables
2. `DO_NOT_TRACK` non-empty and not `0` — disables
3. Persisted `kcap config set telemetry off`
4. Default: enabled

`KCAP_TELEMETRY` outranks `DO_NOT_TRACK` in both directions. It is the kcap-specific, deliberate
statement, and it is the only way someone carrying a blanket `DO_NOT_TRACK` in their shell profile
can opt back in. This is the common convention among CLIs but it is a judgment call, so it is
documented explicitly rather than left to be discovered.

Opt-in-by-default was rejected on measurement grounds: people who abandon setup are the least likely
to have opted in, so an opt-in funnel is biased hardest exactly where it needs to be accurate.
Silent opt-out was rejected on disclosure grounds for an EU-hosted product.

CI is not disabled, only tagged `is_ci`. Ephemeral CI machines mint a fresh device id per run and
would otherwise inflate unique-device counts, so funnel insights filter `is_ci = false`.

## Architecture

New namespace `Capacitor.Cli.Core/Telemetry/`:

| Component | Responsibility |
|---|---|
| `TelemetrySettings` | Resolves enabled/disabled once, as a pure function over an injected env dictionary plus persisted config. No environment access at the seam, so the precedence table is directly testable. |
| `TelemetryDeviceId` | Anonymous device id in `~/.config/kcap/telemetry.json`, using the same `FileMode.CreateNew` race discipline as `MachineId` (exclusive create; loser adopts the winner's id; persistent corruption heals by overwrite). Also holds the `notice_shown` marker and the persisted enable flag. No id is generated while telemetry is disabled — a user who opts out before first run never gets one written. |
| `TelemetryClient` | Queue, batch POST to `/batch/`, budgeted flush, spill-on-failure, spool replay. The only component that knows PostHog's wire format. |
| `CliTelemetry` | Static facade the call sites use: `Command`, `Funnel`, `McpTool`, `Flush`. Every method swallows. |

`telemetry.json` is deliberately **not** `machine.json`. The latter is an auth-relevant identifier
sent to the Capacitor server to prove daemon/machine identity; conflating an analytics id with an
authentication id mixes purposes that should be separable, and a separate file means opt-out can
delete the analytics id outright without touching authentication.

### Call sites

- `Program.cs` — three lines: start timestamp, denylist check, `ProcessExit` flush.
- `SetupCommand` / `WorkOSDiscovery` / `SpectreTenantProvisioner` — the funnel steps.
- The eight `DispatchToolCallAsync` sites in `Commands/Mcp*Server.cs` — one line each.

## Event catalog

Every CLI event carries `source: "cli"` (mirroring the server's `source: "server"`), `cli_version`,
`os`, `arch`, `is_ci`, `is_headless`, and `$ip: null`.

**No CLI event may reuse a server event name.** The server already emits `cli_setup_completed`
(`PosthogEventMapper.cs:39`); a second producer of that name would double-count across two different
persons and corrupt every existing insight built on it. Hence `cli_setup_succeeded` below.

### `cli_command`

One per human-invoked verb, flushed at exit. Properties: `command`, `subcommand`, `flags[]`,
`exit_code`, `duration_ms`, `has_server`, `logged_in`.

`setup` is not special-cased here: it emits `cli_command` at exit like every other verb, *in addition
to* its funnel events. The funnel measures where a run died; `cli_command` measures that it ran and
how it ended. A run that dies mid-setup produces funnel events and no `cli_command`, which is itself
the signal.

Positional arguments are the sharp edge: `kcap recap <sessionId>`, `kcap ignore <path>`, and
`kcap remap <path>` all carry identifying data. `subcommand` is therefore drawn from a **per-verb
allowlist of known literals** (`daemon start`, `plugin install`, `config set`, `curate apply`, …) and
omitted when the token doesn't match — never raw argv.

`flags[]` carries flag *names* only, sorted, values never sent. Names are admitted by **shape**
rather than by a name allowlist: a token qualifies only if it matches `^--[a-z][a-z0-9-]{0,28}$`
after any `=value` suffix is stripped. A global allowlist across ~40 commands would rot silently as
flags are added, and shape is what actually makes a flag name safe.

The 30-character bound is load-bearing, not incidental. An earlier draft allowed 40, which admitted
`--`-prefixed GUIDs: a UUID's alphabet is lowercase hex plus hyphen, exactly the character class
here, so any GUID beginning with a hex letter — ~37% of UUIDv4s — satisfied the pattern. At 30 a
GUID token (`--` plus 36 characters) cannot fit, while every real kcap flag clears it with room to
spare; the longest is `--skip-antigravity-hooks` at 24. With that bound the pattern genuinely cannot
express a path, URL, GUID, or email address. A future flag name longer than 30 characters is dropped
rather than reported, which is the allow-by-exception default behaving correctly.

Non-matching tokens, and every non-`--` token, are dropped; the flag list is additionally capped at
12 entries.

### Setup funnel

Flushed eagerly, in order:

| Event | Fires when | Notable properties |
|---|---|---|
| `cli_setup_started` | entry | `has_existing_profile`, `server_url_provided`, `no_prompt` |
| `cli_setup_signin_opened` | browser opened / device code shown | `mode`, `provider` |
| `cli_setup_signin_completed` | auth returns successfully | `provider` |
| `cli_setup_signin_failed` | auth fails or times out | `reason` |
| `cli_setup_tenant_none` | **signed in, zero tenants — the "go sign up" fork** | `provider` |
| `cli_setup_workspace_offered` | provisioner prompt shown | |
| `cli_setup_workspace_declined` | declined or cancelled out | |
| `cli_setup_workspace_requested` | slug confirmed, provisioning POSTed | |
| `cli_setup_workspace_provisioned` | poll resolves live | |
| `cli_setup_workspace_failed` | poll fails | `reason`: `slug_taken`, `reserved`, `poll_timeout`, `unauthorized` |
| `cli_setup_succeeded` | setup finishes | `agents_configured` (count, not vendor names) |

The drop-off measure is `cli_setup_tenant_none` minus `cli_setup_workspace_provisioned`, split by the
last step reached. Combined with the web's `cli_snippet_copied`, the funnel reads copy → run → signin
→ signup → live, with only the copy→run hop uncorrelated per person.

Closing that last hop would require a per-visitor token in the copied snippet
(`kcap setup --ref=…`). Rejected: it makes the headline install instruction look like tracking, and
breaks the "copy this, paste anywhere" property. Aggregate day-over-day `cli_snippet_copied` versus
`cli_setup_started` sizes the leak adequately.

### `mcp_tool_called`

Properties: `server`, `tool`, `ok`, `duration_ms`. Tool arguments are never sent.

### `cli_first_run`

Once per device, emitted alongside the notice. Provides the installed-but-never-set-up denominator.

## Privacy

Never collected: argv values, file paths, repo names or URLs, session ids, prompt or transcript
content, environment variable values, usernames, email addresses. Enforced by construction — events
are assembled from allowlists, so omission is the default for anything not explicitly named.

`$ip: null` on every payload suppresses geo-IP resolution, matching the IP-discard posture the
privacy policy already states for web. PostHog's current handling of this property should be
confirmed during implementation rather than assumed.

First-run notice, stderr only (never stdout, so scripted output stays clean), once per device:

```
kcap collects anonymous usage data — command names only, never arguments, file paths, or
transcript content. Opt out: kcap config set telemetry off (or DO_NOT_TRACK=1).
https://capacitor.kurrent.io/privacy
```

`kcap uninstall` already removes the config directory, which takes `telemetry.json` with it.

## Failure handling

Telemetry never changes a command's exit code, stdout, or stderr. Every entry point is wrapped and
swallows; the flush is budgeted; the spool is bounded.

This is stricter than ordinary defensiveness because `Program.cs:113` documents that an exception
escaping to the NativeAOT runtime aborts the process with SIGABRT and a macOS crash report. A
throwing telemetry path would turn a reporting feature into a crash-on-every-command regression.

`KCAP_TELEMETRY_DEBUG=1` prints what would be sent to stderr, for our own diagnosis.

## AOT

Payloads serialize through source-generated `CapacitorJsonContext`. The properties bag is built with
`new JsonObject(...)` rather than a collection expression, which compiles to `Add<T>()` and requires
dynamic code. Verified with `dotnet publish -c Release` — `dotnet build` does not surface IL3050 or
IL2026.

## Testing

- `TelemetrySettings` precedence rendered as a truth-table test over injected env dictionaries
- Redaction: a GUID, an absolute path, and a repo URL passed as positionals never reach a property
- Name-collision test asserting CLI event names don't intersect the server's set, with the server
  list hardcoded and commented to stay in sync — makes the `cli_setup_completed` trap permanent
- Denylist test: `hook`, `watch`, and `mcp` verbs emit nothing
- Funnel sequence tests against a fake sink: happy path; `tenant_none` → declined; provisioning
  failure
- Group derivation: `https://acme.kcap.ai` yields group `acme`; a self-hosted URL yields no group at
  all, only the `org` property
- Spool: bounded, drop-oldest, replayed by the next successful flush
- Offline: a failed flush leaves exit code and stderr untouched
- Path assertions use `Path.Combine`; this repo has a Windows CI leg that catches separator literals

## Documentation

Same PR, per the repo's standing rule:

- `README.md` — a Telemetry section covering what is collected and every opt-out, plus the new
  `kcap config set telemetry` surface in the per-command section
- `src/Capacitor.Cli.Core/Resources/help-config.txt`

Companion PR in kcap-web: a CLI paragraph in the privacy policy, which currently describes web and
server collection only.

## Out of scope

- Merging the anonymous device into the identified user (`$identify`) — Decision 2
- Per-visitor correlation of the copied snippet to the CLI run — Event catalog
- Any telemetry from `kcap hook` — Decision 3
- Server-side or web-side event changes, beyond the privacy-policy paragraph
