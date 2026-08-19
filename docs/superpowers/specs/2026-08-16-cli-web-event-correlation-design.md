# Correlating CLI, server and web PostHog events

## Problem

One human, four strangers.

Web, CLI, kcap-web's Worker and kcap-server all publish into the **same** PostHog project
(`phc_DeHB…`, EU, via `phog.kurrent.io` — `kcap-web/src/config.ts:6`,
`Capacitor.Cli.Core/Telemetry/CliTelemetry.cs:13`, `kcap-server` `PostHogOptions.cs:20`). Nothing is
siloed by project. What is siloed is **person identity**:

| Producer | `distinct_id` | Set where |
|---|---|---|
| Browser | posthog-js anon id, then `identify(email)` on the org form | host-scoped cookie; `OrgForm.astro:126` |
| CLI | random GUID in `telemetry-device.json` | `CliTelemetry.cs:80` |
| kcap-web Worker | **the tenant slug** | `kcap-web/src/server/signup/provision.ts:134` |
| kcap-server | `HMAC-SHA256(PostHog:IdSalt, userId)` | `PosthogUserIdentity.cs:13` |

So "Reddit ad → site visit → copied install command → `kcap setup` → workspace live → 50 recorded
sessions" is recorded as four unrelated people. The question the 2026-08-08 telemetry spec set up but
could not answer — *which acquisition channel produces activated workspaces* — remains unanswerable.
The arm a person saw is the single fact nobody can attach to a CLI signup today.

> **Corrected 2026-08-20.** An earlier draft of this section claimed the marketing A/B experiment's
> *primary* metric was `create_workspace_requested`, which the redesign arm could not fire. That was
> read from the branch, not from the experiment. PostHog experiment 90854 — created 2026-08-17, a day
> after this spec — deliberately makes the primary metric an upstream funnel step (`$pageview` on
> `/signup` / `/get-started`) precisely because `create_workspace_requested` runs at ~0.17% and is
> hopelessly underpowered; it is a **secondary** metric there, explicitly labelled directional only.
> Its owner had already solved that problem. This work still makes CLI signups arm-attributable, which
> is worth having — but it is not rescuing a broken primary metric, and this spec should not claim it is.

There is a second, subtler split: `www.kurrent.io` and `capacitor.kurrent.io` mint **separate** web
persons. posthog-js is initialised with no `cookie_domain` and no `cross_subdomain_cookie`
(`kcap-web/src/components/PostHog.astro:106-121`), so cookies are host-scoped. Campaign traffic lands
on capacitor; organic and direct land on www. The same human browsing both is already two people
before the CLI is involved.

This spec closes all of it with one opaque key.

## Core idea: bridges, not storage

Every producer already stamps its own `distinct_id` on everything it sends. So we do **not** need to
propagate a correlation key through every event, and we do not need to persist it anywhere. We only
need to publish, once per setup run, a small set of **bridge facts** — each pairing the key with one
identity. Everything else follows transitively at query time.

```
                        ┌─────────────────────────────────────────┐
                        │              join_id                    │
                        │   32 hex chars, one per auth run,       │
                        │   opaque, grants nothing                │
                        └────┬───┬────────┬─────────┬─────────────┘
                             │   │        │         │
   bridge 1 ─────────────────┘   │        │         └────── bridge 4
   cli_auth_return               │        │                 cli_setup_completed
   distinct_id = capacitor web id│        │                 distinct_id = HMAC(userId)
                                 │        │
   bridge 2 ─────────────────────┘        └────── bridge 3
   cli_auth_return                        signup_reserved
   distinct_id = www web id               distinct_id = tenant slug

   bridge 0 (free): every cli_* event already carries distinct_id = device id,
                    and now also carries join_id.
```

Five bridges. `cli_setup_completed` carrying both the server's HMAC id and the `join_id` is what
reaches the server's events with **no `users` column and no `ProjectionGroup` version bump** (which
would force a full rebuild of the users read model from `$all` on deploy).

### Query contract — exactly what is and is not joinable

"Transitively joinable" is a precise, limited claim, and stating it loosely would produce confident
wrong activation numbers. The join is **query-time only**: no later event ever carries `join_id`
itself. A consumer resolves an identity first, then selects that identity's events.

Supported:

```
join_id  ──▶ server distinct_id  (the one on the cli_setup_completed bearing this join_id)
         ──▶ that distinct_id's OTHER server events, restricted to timestamp >= that setup event
```

Not joinable, and each must be excluded from any activation metric built on this:

| Gap | Why |
|---|---|
| Every user who completed `kcap setup` **before this ships** | `RecordCliSetup` is once-only (`UserCommandService.DecideCliSetup`: `if (state.CliSetupAt is not null) yield break;`). Their setup event already fired without a key and **will never fire again**. The entire pre-rollout user base is permanently unbridged server-side. |
| A returning user's second `setup` (new machine, re-run) | Same guard. |
| Server events **before** the setup event for that identity | No ordering guarantee that they belong to this acquisition. |
| Workspace activity by **other members** of the org | Their events carry their own HMAC id. Org-level activation is attributable to a workspace, never to the acquisition of the person who created it. |
| Any server event where `PostHog:IdSalt` was rotated in between | Rotation re-pseudonymises every user; the bridge points at a dead id. |

**Acceptance criterion.** The metric this feature exists to serve is defined as: *first-touch web
person → CLI setup run → workspace reserved*, i.e. bridges 0–3. Bridge 4 extends it to post-setup
server activity **for newly-acquired users only**. Anyone writing a query that treats bridge 4 as
covering the whole user base is using it wrong, and the spec's own verification query (below) must
therefore be scoped to a `join_id` minted after rollout.

If whole-population server-side joining is ever required, that is the `users.join_id` column and the
`ProjectionGroup.Users` bump this design deliberately declined — a separate, larger decision with a
read-model rebuild attached, not a quiet extension of this one.

## How the browser gets the key

WorkOS AuthKit runs entirely between the browser and `api.workos.com`; the browser never touches a
kurrent.io host during CLI sign-in. The only moment we can reach it is the loopback closing page —
today a dead end served from the user's own machine
(`Capacitor.Cli.Core/Auth/LoopbackBrowser.cs:69-82`).

```
  http://127.0.0.1:PORT/callback?code=…
     │  serve TODAY'S success page unchanged, then location.replace() to hop 1
     ▼
  https://capacitor.kurrent.io/api/cli/return?j=<join_id>&p=<port>
     │  CONSENT GATE: kcap_consent cookie == accepted?
     │    no  → read nothing, capture nothing, pass nothing on
     │    yes → read ph_<token>_posthog ($device_id) + kcap_ab (arm),
     │          capture cli_auth_return                       ← bridge 1
     ▼  302  (either way — the redirect is how they get back to their terminal)
  https://www.kurrent.io/api/cli/return?j=<join_id>&p=<port>&v=<arm>&w1=<capacitor $device_id>
     │  same gate, same handler, hop="www"                    ← bridge 2
     ▼  302
  http://127.0.0.1:PORT/joined?j=<join_id>&v=<arm>&w1=…&w2=…   (w* = $device_id, never distinct_id)
        CLI validates, merges into its telemetry shared properties,
        serves the same success page. End of chain.
```

### Why each part is the way it is

**A JS `location.replace()`, not a 302 from `/callback`.** The person sees "Authentication
successful!" *first*, then gets navigated. If any hop is slow or broken they have already read the
success message, and a no-JS browser simply stays on it — graceful degradation to today's behaviour.
A 302 would replace a page that cannot fail with one that can, immediately after sign-in, where a
browser error reads as "signup failed". Still a top-level GET navigation, so `SameSite=Lax` cookies
are sent (an `<img>` beacon or `fetch` would **not** send them — this is exactly why a navigation is
required).

#### What the redirect actually proves — and what it does not

**The closing page is NOT evidence that authentication succeeded.** This is the sharpest thing to get
right in the whole design, because the obvious reading is wrong and would silently inflate every
funnel built on it.

`LoopbackBrowser` decides success from one test — `!query.Contains("error=")` — then writes the page
and returns the raw query (`LoopbackBrowser.cs:54-58`). Everything that actually establishes identity
happens **after** that:

- the CSRF **state check** is at `OAuthLoginFlow.cs:263`, seventeen lines after `InvokeAsync` returns
  at `:246` — so a *state mismatch, i.e. a possible CSRF attempt*, still renders the success page;
- the authorization-code → token exchange happens later still (OidcClient for WorkOS, the
  server-side `codeExchangeUrl` POST for GitHub) and can fail on its own;
- `resp.IsError` is re-checked at `:257` against the parsed response, not the substring test the page
  used.

So the redirect fires — and `cli_auth_return` is captured — for runs that go on to fail auth entirely.

Two consequences, both normative:

1. **`cli_auth_return` means exactly "a loopback callback arrived carrying no `error=` parameter."**
   It does not mean signed in, and must never be labelled or documented as a sign-in event. Any
   funnel numerator that wants "signed in" must pair the `join_id` with the CLI's own
   `cli_setup_signin_completed` (which fires at `WorkOSDiscovery.cs:78`, *after* the token is in hand)
   or with `signup_reserved`. The Query contract's exclusion discipline applies here too.
2. **The "nothing is on the critical path" argument survives, but its justification changes.** The
   hops are safe because the token exchange neither waits for them nor is affected by them — not
   because the credential is already in hand. It may not be.

Making `LoopbackBrowser` wait for real auth success before redirecting was considered and rejected:
it would put a network hop inside the `IBrowser` contract, invert the dependency between the browser
shim and the token exchange, and delay the closing page behind an exchange that can take seconds.
Reporting an honest, weaker event is better than a strong claim we cannot substantiate at that point.

**Under `/api/` on the capacitor hop — load-bearing.** `isApiPath` is step 0 of the A/B branch's
redirect ladder (`kcap-web/src/server/api-paths.ts`), ahead of every other rule, so an `/api/*` path
is never assigned an arm and never has `kcap_ab` set. Our visitor **observes** that experiment
without **joining** its sample. On `main` today the same prefix is already excluded from the old-host
301 (`redirects.ts:183`), so the route works before and after his merge — no ordering dependency on
that branch. The request still *carries* an existing `kcap_ab` cookie, because reading is not
assignment.

**Both hosts.** Per the host split above, a single hop is blind to whichever host the person's
marketing touch used. Two hops cost ~200ms nobody notices. For a person who used both, this is also
the first time we ever link their two web identities.

**`p` is a port integer, never a URL.** The Worker builds `http://127.0.0.1:{p}/joined` itself, with
`p` validated as an integer in 1024–65535. Accepting a return URL would be an open redirect on a
production host. The hop-2 host is a compile-time constant.

**The final page is local (question 4, option 2).** Nothing changes visually. The payoff is the
return trip: the CLI learns the arm and the web ids and stamps them onto **its own** events, so
"CLI signups broken down by A/B arm" becomes a point-and-click breakdown rather than a HogQL join.

## The join key

32 lowercase hex characters (`Guid.NewGuid().ToString("N")` — the same shape and minting discipline
as the existing `MachineId`/`TelemetryDeviceId`). Opaque, single-use, one per auth run.

**It grants nothing.** It is not a credential, not a session, not a capability. It is never accepted
as authentication by anything.

**Key hygiene.** "Grants nothing" is the reason a leak is not a breach, but it is not a licence to
spray the key around — it is the bridge token, and an access log that contains it is a copy of the
join. So, actively:

- `referrer-policy: no-referrer` and `cache-control: no-store` on **every response in the chain — the
  two Worker 302s AND the two CLI-served local pages** (`/callback` and `/joined`). The local pages are
  easy to overlook and are not optional: `/callback`'s HTML embeds the first-hop URL (key included) and
  is the referrer for the first cross-origin navigation, and `/joined`'s URL carries the key and the
  web device ids. `LoopbackBrowser.WriteClosingPageAsync` currently sets only `ContentType` and
  `ContentLength64` (`LoopbackBrowser.cs:69-80`), so both headers are new code there.

  **On `/callback` this is a security control, not hygiene.** That URL's query contains the OAuth
  `code=` and `state=`. Modern browsers default to `strict-origin-when-cross-origin`, which would send
  only `http://127.0.0.1:PORT` — but a browser or embedded webview configured with
  `no-referrer-when-downgrade` or `unsafe-url` would put the **authorization code itself** into
  `Referer` on the hop to `capacitor.kurrent.io`, i.e. into our own Worker access logs. `no-referrer`
  removes that possibility outright, and it costs one header on a page we already generate.
- The loopback `/joined` response strips the query from browser history — the page it serves carries
  `history.replaceState({}, "", "/joined")`, so the recorded entry holds no key and no web ids.
- The key is never written to disk by the CLI (see `Accept` below), and never logged — **including
  under `KCAP_TELEMETRY_DEBUG=1`, which requires a code change to honour.** `CliTelemetry.Capture`
  merges the shared bag and then prints the whole properties object
  (`CliTelemetry.cs:122-127`), so a debug run would otherwise emit the bridge key to stderr on every
  `cli_*` event. The debug renderer must replace `join_id`'s value with a fixed placeholder — print
  `"join_id":"[set]"`, never the value. A placeholder rather than omission because the useful debug
  signal is *whether the property is attached*, which is exactly what the placeholder shows; the value
  itself is only ever needed in PostHog, where it legitimately lives.

**Residual, accepted:** the two hop URLs contain the key in Cloudflare Worker access logs, and the
capacitor web id appears in www's logs. First-party, short-retention, and harmless given the key
grants nothing — but stated rather than glossed.

**It is minted only when telemetry is enabled.** `KCAP_TELEMETRY=0`, `DO_NOT_TRACK`, persisted
opt-out, or the desktop app's `KCAP_APP_SPAWN_NO_TELEMETRY` marker all mean: no key, no redirect, no
`joinId` on any request, and the closing page behaves exactly as it does today. Opt-out is enforced
at the mint, so every downstream surface is off by construction rather than by four separate checks.

## Changes by repo

### kcap-cli

New `src/Capacitor.Cli.Core/Telemetry/SetupJoin.cs`:

| Member | Responsibility |
|---|---|
| `Mint()` | Returns the key and registers `join_id` as a telemetry shared property. No-op returning null when telemetry is disabled. |
| `Current` | The key for this run, or null. |
| `FirstHopUrl(int port)` | `{ProvisioningEndpoint.Url}/api/cli/return?j=…&p=…`, or null when there is no key. Reuses the existing endpoint constant so `KCAP_SIGNUP_URL` retargets it for local testing. |
| `Accept(string query)` | Validates and merges the returned context. Single-use. |

`Accept` is the trust boundary. The `/joined` query arrives over plain HTTP on loopback and is
attacker-influenceable by **any** local process, browser tab, or web page that can cause a GET to the
ephemeral port. Arrival at `/joined` is therefore **not** evidence that the web hops happened; only a
key match is. Normative requirements:

- The key is minted **before** the closing page is rendered, so a match proves the value round-tripped
  through something that was given it by this process.
- The key lives **in memory only, for this auth attempt**. It is never written to disk, never added to
  a config file, never logged, and never reused across runs — so nothing on the machine can replay it.
- A value is admitted only if `j` equals this run's key (constant-time compare) and the join has not
  already been consumed. Then, **field by field, independently**:
  - `v` is **optional**. Present → must be `legacy` or `redesign`, else that field alone is dropped.
    Absent → `site_variant` is simply omitted. This is the common case, not an edge case: `kcap_ab`
    only exists for visitors the experiment actually assigned, so **today nobody has one at all**. A
    rule that required `v` would reject every valid web id along with it and the feature would ship
    dead. One malformed field never discards the others.
  - each web id must match `^[A-Za-z0-9_-]{1,64}$` — see below, this is a privacy guarantee, not a
    formatting convenience.
- **Exactly one** `Accept` can ever succeed. A second call — matching key or not — is a no-op and can
  never overwrite already-merged join state.
- A mismatch is dropped silently: nothing is merged, nothing is persisted, nothing is sent to PostHog,
  and no error surfaces to the user. A mismatched value must not appear anywhere, including debug
  output, since it is attacker-chosen.

On success it merges `site_variant`, `web_device_id_capacitor`, `web_device_id_www` into the
telemetry shared bag.

#### The value returned to the CLI is `$device_id`, never `distinct_id`

Once a visitor submits the org form, posthog-js runs `identify(email)` — so from that moment the
web person's **`distinct_id` IS an email address**. Returning that to the CLI would put a real email
into CLI telemetry, flatly violating the never-collect list and the README promise, and it would make
the privacy section's "a random analytics id, not personal data by itself" false.

So the Worker returns posthog-js's **anonymous `$device_id`** (stable, random, survives `identify`),
never `distinct_id`, and the CLI's properties are named `web_device_id_*` to make that impossible to
misread. The `^[A-Za-z0-9_-]{1,64}$` regex is the enforcement: a UUID passes, an email cannot
(`@` and `.` are outside the class). It is a **privacy guarantee, deliberately fail-closed** — if a
future change ever routed a `distinct_id` here, `Accept` drops it silently rather than importing an
identifier we promised not to collect.

If no `$device_id` is available, the Worker sends no web id back. The bridge is unaffected: the
`cli_auth_return` event still carries the web person server-side, where PostHog already holds that
identity — the constraint is specifically on what crosses back into *CLI* telemetry.

The pre-implementation spike must therefore check the cookie in **both** states — anonymous, and
after `identify(email)` — and confirm `$device_id` is present and distinct from `distinct_id` in each.

`CliTelemetry` gains one public member, `AddSharedProperty(string, JsonNode?)`, swallowing like
everything else in that class.

`LoopbackBrowser` (`Auth/LoopbackBrowser.cs`) takes an optional `ILoopbackJoin` collaborator
(`string? FirstHopUrl(int port)` / `void Accept(string query)`) so it stays dumb and testable:

- `WriteClosingPageAsync` appends `<script>location.replace("…")</script>` when a first-hop URL
  exists **and** the callback carried no `error=` parameter (the same weak test the page itself uses —
  see "What the redirect actually proves"; it is not an auth-success signal). The error page never
  redirects, and no drain starts on that path.
- `WriteClosingPageAsync` also sets `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on
  **both** the `/callback` and `/joined` responses — see Key hygiene; on `/callback` this protects the
  OAuth `code=` in that URL, not just the join key.
- The `/joined` page carries `history.replaceState({}, "", "/joined")` so no history entry retains the
  key or the web device ids.
- `InvokeAsync` returns as it does today — auth is never blocked or delayed by any of this.
- With no `ILoopbackJoin`, behaviour is byte-identical to today, `using var` included.

#### Listener ownership contract

Today the listener is disposed for free by `using var` (`LoopbackBrowser.cs:21`) with explicit
`Stop()` on both exits (`:40` timeout, `:56` success). Removing `using var` to let the listener
outlive `InvokeAsync` removes that safety net on every path, so ownership must become explicit:

1. **Exactly one owner stops and disposes the listener, exactly once.** An `int` ownership flag
   transitioned with `Interlocked.Exchange` decides it; both the drain and `Dispose` attempt the
   transition and only the winner tears down. Idempotent `Dispose`.
2. **`InvokeAsync` keeps a `try`/`finally` that disposes the listener on every path that does NOT
   hand off ownership.** Handoff happens at exactly one point: after a *successful* callback whose
   closing page carried a redirect. Every other exit — timeout, cancellation, auth-error callback, an
   injected `_openBrowser` that throws, a non-callback 404 response that fails to write, a closing-page
   write that throws — retains ownership and disposes, matching today's behaviour.
3. **A pending `GetContextAsync` must always be observed**, preserving the existing discipline at
   `:41-42` (a faulted accept task with no continuation is an unobserved exception). The drain follows
   the same pattern for its own accept.
4. **Both deadlines break the pending accept.** The 15s drain cap and the ≤3s `Dispose` wait are
   enforced by cancelling the accept (via `Stop()` on the winning owner), not by abandoning a task that
   keeps the listener alive.
5. **`/joined` arriving after teardown** is a connection failure in the browser, never an exception in
   the CLI. `/joined` arriving *before* `InvokeAsync` returns is impossible in practice but must be
   safe: the drain is armed as part of the handoff, before the closing-page response completes.
6. Requests to the drain that are not `/joined` get a 404 and are ignored, as `/callback`'s loop does
   today for favicon requests.

`Dispose` is called by the two callers at the end of their flow. For `kcap setup` the drain finished
minutes earlier (the user is at an interactive prompt), so the wait is instant. For the short-lived
`kcap login` the bounded ≤3s tail is what keeps its browser tab from landing on a dead port.

#### Where the key is minted — command entry, not the auth lane

**`Mint()` is called once at the top of the `setup` and `login` handlers, before any funnel event
fires and before any lane is chosen.** Not in `WorkOSDiscovery` or the browser lanes. Two reasons,
both load-bearing:

- `SetupCommand.HandleAsync` fires `SetupFunnel.Started` at `SetupCommand.cs:62`, long before
  discovery is entered (`:119`). Minting inside the auth lane would leave `cli_setup_started` — the
  funnel's entry event — without the key, breaking "every `cli_*` event for the run carries `join_id`".
- The device-code, headless and no-browser lanes never construct a `LoopbackBrowser` at all. Minting
  there would produce no key, contradicting the coverage claim that bridges 0, 3 and 4 survive
  without a loopback. The key is a **per-run correlation id, not a browser artifact**; only the
  *redirect* is browser-specific.

Deliberately NOT minted in `CliTelemetry.Initialize` for every command: `join_id` means "one
interactive auth run", and stamping it on `kcap recap` or `kcap import` would redefine it as a
process id and put it on events that have nothing to correlate.

#### Call sites and disposal ownership

`FirstHopUrl`/`Accept` reach `LoopbackBrowser` via the optional collaborator at its three
construction sites (`OAuthLoginFlow.cs:224`, `OAuthLoginFlow.cs:584`, `WorkOSDiscovery.cs:37`).
Disposal ownership must be stated at the boundary, because two of those sites are shaped so that a
naive implementation either leaks the drain or disposes an object it does not own:

- **A locally-constructed `LoopbackBrowser` is owned by whoever constructed it.** Today two sites do
  `new LoopbackBrowser()` inline as a sub-expression; each must be hoisted into a local and disposed
  in a `finally` around the auth flow. Leaving them inline is the leak.
- **An externally supplied `IBrowser` stays caller-owned and must never be disposed by the callee.**
  `RunGitHubBrowserFlowAsync(..., IBrowser? browser = null)` (`OAuthLoginFlow.cs:222-224`) takes a
  test seam and only falls back to `browser ??= new LoopbackBrowser()`. So the *same local* is
  sometimes owned and sometimes borrowed: dispose only the instance this method created. Disposing an
  injected browser would tear down a test double — or, worse, a future caller's shared instance.
- `IBrowser` does not extend `IDisposable`, so the drain handle must be reachable without downcasting
  every `IBrowser`. Keep the concrete type in the local that owns it.

Tests: a locally-created browser is disposed on every exit path; an injected `IBrowser` is never
disposed.

Two request bodies gain the key. **Both wire names are `joinId`, camelCase, on the wire; the PostHog
property is `join_id`, snake_case. That asymmetry is deliberate** (it matches each side's existing
convention) and is the single most likely thing to be implemented wrong:

- `ProvisionRequest` (`Auth/ProvisioningModels.cs:10`) — **needs an explicit
  `[JsonPropertyName("joinId")]`**. `CapacitorJsonContext` is globally SnakeCaseLower, so a bare
  property would serialise as `join_id` and kcap-web would silently never see it. A body-assertion
  test pins the literal wire name.
- The `cli-setup` ping (`Commands/SetupCommand.cs:965`, body literal at `:1006`) — currently a
  hand-built JSON string (`{"cliVersion":…}`), deliberately not routed through the source-generated
  context. Keep it a literal and add `"joinId"` to it: introducing a typed DTO here would inherit the
  global SnakeCaseLower policy and serialise `cli_version`, silently breaking an endpoint that works
  today. A body-assertion test pins both field names.

**Mixed-version matrix.** All four combinations must be non-breaking, and none may fail loudly:

| | Old server | New server |
|---|---|---|
| **Old CLI** | today's behaviour | `joinId` absent → property omitted, no bridge |
| **New CLI** | extra JSON member ignored (System.Text.Json default for kcap-server; kcap-web reads named fields only) → no bridge, no error | full bridge |

The only ordering constraint that actually bites is the redirect target, not these payloads — see
Rollout.

Telemetry never blocks on the returned context — no event waits for it. (The ≤3s dispose above is a
wait on the *listener*, so the browser's last hop finds a live port; it is not a wait on data.) The
key itself rides every event from mint time, so the bridges work regardless; only the convenience
properties depend on the return trip, and they land long before `cli_setup_workspace_*` and
`cli_setup_succeeded` fire.

### kcap-web

New route `src/pages/api/cli/return.ts` — an 8-line Astro shim over
`src/server/cli/return.ts`, matching the convention every other API route uses (all logic in
`src/server/`, because `src/worker.ts` cannot be imported by the test pool).

The handler, in this order:
1. Rejects `j` unless `^[0-9a-f]{32}$`, and `p` unless an integer in 1024–65535. Invalid → 400, no
   event, no redirect.
2. **Consent gate.** Reads `kcap_consent`. Anything other than `accepted` → skip straight to the
   redirect at step 5: no further cookie is read, no event is captured, nothing is passed on.
   Deliberately NOT inferred from the analytics cookie's presence — that cookie survives withdrawal
   (measured; see "The consent gate").
3. Only now reads `ph_<token>_posthog` for `$device_id` (URL-encoded JSON; parse failure treated as
   absent, never an error) and `kcap_ab` (plain cookie, `ab.ts:COOKIE_RE`) for the arm. As belt-and-
   braces, treat a `$user_state` of anything other than `anonymous` as a reason to double-check that
   `$device_id`, not `distinct_id`, is what was read.
4. `fireAndForget(ctx, capture({event: "cli_auth_return", distinct_id: webDistinctId,
   properties: {join_id, hop, site_variant}}))` via the existing `src/server/posthog.ts`.
   Fire-and-forget so the redirect is not delayed. No `??` fallback for `distinct_id`: the gate
   guarantees an identity exists by the time this line runs, and a synthetic fallback would
   manufacture a person for someone who declined — the opposite of the gate's purpose.
   The event's own `distinct_id` is the web person's real `distinct_id`, which is correct here and is
   what makes the bridge resolvable; only the value passed onward in `w1`/`w2` is restricted to
   `$device_id`.
5. 302 to the next hop — **unconditionally, gate open or closed** — with
   `x-robots-tag: noindex, nofollow` (set explicitly: `decorate` skips `/api/*`, so this path does not
   inherit the host's noindex), `cache-control: no-store`, and **`referrer-policy: no-referrer`**.

The www hop additionally re-validates the `v` and `w1` values it received from the capacitor hop
against the same vocabulary and regex before reflecting them into the loopback redirect. It is the
same handler with a different `hop` value; hop-1 output is untrusted input to hop 2, because anyone
can navigate to the www URL directly with values of their choosing.

#### The consent gate — decided; it is step 2 of the handler above

**No web-side bridge is created for a visitor who did not accept cookies.** Unless `kcap_consent`
says `accepted`, the handler reads no further cookie, captures nothing, and passes nothing onward.

This is a deliberate reversal of an earlier draft, which fired `cli_auth_return` unconditionally so
that the consent gap could be *measured*. That draft would have created an analytics record about
someone from a terminal-stored value after they had taken the only action available to refuse — the
one thing on this design's risk list with a plausible complaint attached. Measurability is not worth
it; the gap is still sized from the CLI side (absence of any `cli_auth_return` for a `join_id`).

**How the gate is evaluated — an explicit consent cookie, NOT cookie-presence inference.**

An earlier draft inferred consent from the presence of the analytics cookie, reasoning that posthog-js
never loads without consent so the cookie cannot exist without it. **That inference was measured and is
false in the withdrawal direction.** Verified in a real browser on `www.kurrent.io`, 2026-08-18:

| Step | `capacitor:cookie-consent` | `ph_<token>_posthog` cookie | posthog-js loaded |
|---|---|---|---|
| before consent | *(absent)* | **absent** | no |
| after Accept | `accepted` | **present** — `$device_id`, `distinct_id`, `$sesid`, `$initial_person_info`, `$user_state` | yes |
| after Withdraw consent | `declined` | **STILL PRESENT** | no |
| fresh page load after withdrawal | `declined` | **STILL PRESENT**, `$device_id` intact | no |

Withdrawal calls what appears to be posthog-js's reset — `distinct_id` is **rotated to a new value**
while `$device_id` is preserved — and then **rewrites the cookie rather than deleting it**. So a
withdrawn visitor presents a fully-formed, valid-looking analytics cookie indefinitely, and a
presence-based gate would open for exactly the person who refused. That is the precise harm this gate
exists to prevent, so the inference is rejected.

**Therefore kcap-web must write an explicit consent record that the Worker can read**, and the gate
reads only that:

- a first-party cookie (e.g. `kcap_consent=accepted`) written by the banner on Accept and **deleted on
  Withdraw, in the same handler that sets the flag to `declined`**;
- **not** `HttpOnly` — the banner is JavaScript and must write and delete it; it holds no secret;
- **`SameSite=Lax`**, because the hop is a cross-site *top-level GET navigation* from `127.0.0.1`, and
  Lax cookies are sent on exactly that. The existing `kcap_ab` cookie already meets this, so there is
  precedent; getting it wrong closes the gate for everyone;
- `Path=/; Secure`, host-scoped per origin — matching consent already being per-origin, so a visitor
  can legitimately be consented on one host and not the other;
- **backfilled on page load**: if the stored flag says `accepted` and the cookie is missing, write it.
  Without this every already-consented visitor is gated closed until they touch the banner again, which
  would look like the feature simply not working.

A cookie whose only content is a consent decision is the textbook case of one that does not itself
require consent, so there is no circularity.

*Two alternatives were eliminated by the same measurement.* Gating on posthog-js's own opt-out flag is
impossible: `__ph_opt_in_out_<token>` lives in **localStorage, not a cookie** (confirmed), so a Worker
cannot see it. And reading `capacitor:cookie-consent` directly is impossible for the same reason — it is
localStorage too.

**Gate the capture, never the redirect.** The 302 must happen either way — it is how the person gets
back to their terminal, and it is functional, not analytics. Suppressing the navigation for a refuser
would strand them on a dead page. Only the reads and the `capture()` are conditional.

**Scope: bridges 1 and 2 only.** The other three — `join_id` on the CLI's own events, on the
provisioning request, and on the cli-setup ping — are first-party service data governed by kcap's own
opt-out (`KCAP_TELEMETRY`, `DO_NOT_TRACK`, persisted config), not by browser cookie consent, and are
unaffected. Stated explicitly because an implementer would otherwise reasonably wonder whether the
gate applies to all five.

Tests: `kcap_consent=accepted` → analytics cookie and arm read, event captured, values passed on;
`kcap_consent` absent or any other value → **no cookie read, no event captured, nothing passed on, and
the 302 still issued**; and a regression test that an analytics cookie WITHOUT `kcap_consent` does not
open the gate — that is the withdrawal case, and it is the one that was measured broken.

`src/server/signup/provision.ts` accepts an optional `joinId` on the request body, validates it with
the same regex, and adds `join_id` to `signup_reserved`, `signup_blocked` and `signup_flagged`
(`:134`, `:79`, `:124`). Absent or malformed → the property is simply omitted; old CLIs are
unaffected.

### kcap-server

Four lines and a test, following the `cli_version` precedent exactly:

1. `CliSetupPayload(string? CliVersion)` → `(string? CliVersion, string? JoinId)`
   (`Auth/WelcomeEndpoints.cs:38`).
2. `RecordCliSetup(string UserId, string? CliVersion)` → `+ string? JoinId`
   (`Auth/UserCommands.cs:9`).
3. `UserCliSetupCompletedEvent` gains an optional `JoinId` (`Capacitor.Server.Core/Events/UserEvents.cs:74`).
4. `PosthogEventMapper.CliSetup` gains `if (e.JoinId is { } j) props["join_id"] = j;` — mirroring the
   `cli_version` line above it.

Do **not** name it `source` or `org`: those are stamped last in `PostHogAnalytics.cs:16-24` and
cannot be overridden by a caller property.

**Accepted limitation — and it is bigger than it first looks.**
`UserCommandService.DecideCliSetup` is guarded once-only
(`if (state.CliSetupAt is not null) yield break;`). So bridge 4 is available **only for users whose
very first `kcap setup` happens after this ships.** Every existing user is permanently unbridged on
the server side, and so is any returning user's second machine. This is acceptable because bridge 4
serves post-signup activation for *newly acquired* users, which is exactly the population the
acquisition question is about — but it is a hard boundary on the metric, not a rounding error, and the
Query contract above states it as an exclusion that every consumer must apply. Relaxing the guard was
rejected: `cli_setup_completed` currently means "this user set up the CLI for the first time", and
making it fire repeatedly would silently redefine an event that existing insights already depend on.

`DaemonConnect` (`Agents/DaemonCommands.cs:105`) is an all-optional-tail record that fires on every
reconnect and would be the natural carrier if per-machine or whole-population joins are ever wanted;
explicitly out of scope.

## Event and property catalog

| Where | Event | New |
|---|---|---|
| CLI | every `cli_*` event for the run | `join_id`; plus `site_variant`, `web_device_id_capacitor`, `web_device_id_www` once the return trip lands |
| kcap-web Worker | **`cli_auth_return`** (new) | `join_id`, `hop` (`capacitor`\|`www`), `site_variant` (omitted when no arm cookie). **Means "a loopback callback arrived with no `error=`" — NOT "signed in"**; see "What the redirect actually proves". Deliberately carries NO `has_web_identity`: the consent gate means the event only exists when an identity was found, so such a property would be constant-true — the structurally-constant-measure trap the 2026-08-08 spec documents. |
| kcap-web Worker | `signup_reserved`, `signup_blocked`, `signup_flagged` | `join_id` |
| kcap-server | `cli_setup_completed` | `join_id` |

`cli_auth_return` is emitted by the **Worker**, not the CLI, so it must be added to the known-foreign
list in the CLI's name-collision regression test
(`test/Capacitor.Cli.Tests.Unit/Telemetry/SetupFunnelTests.cs:110`), which exists precisely to stop
two producers sharing an event name.

## Privacy

This is the part that needs an explicit decision rather than an implementation.

**What changes.** `web_device_id_*` on CLI events is a new identifier crossing into CLI telemetry. It
is posthog-js's anonymous `$device_id` — random, and never the `distinct_id`, which becomes an email
address once the org form calls `identify(email)`. So no email or name ever enters CLI telemetry, and
the `Accept` regex enforces that fail-closed rather than by convention.

That said, **the linkage is still real and must not be undersold**: `$device_id` resolves to the same
PostHog person as that email, so anyone querying the project can walk CLI device → web person →
email address. The bridge creates that path whether or not we ever merge persons; what the
`$device_id` rule buys is that the email is never *stored in* CLI events, not that the join is
unresolvable.

`README.md:1842` currently states positively that the only identifiers are the random device id plus
(SaaS only) the workspace slug. **That sentence becomes false and must be rewritten in the same PR**,
along with the CLI paragraph in kcap-web's privacy policy and kcap-server's README claim that its
`distinct_id` is "not reversible" (it stays non-reversible in itself, but becomes joinable to a less
pseudonymous identity space).

The first-run notice's "anonymous usage data" wording needs review by whoever owns the privacy policy.
It is defensible — nothing here collects a name, email or path, and the whole mechanism is off for
anyone who opted out — but it is a change in kind, not degree, and should be signed off rather than
assumed.

**Not merging persons, for now.** Per decision, the shipped code creates no PostHog person merge.
Merges are irreversible and would permanently staple an anonymous device to an email-identified
person inside PostHog's own data model. Once real coverage numbers exist, the merge can be performed
retroactively by a one-off script over events carrying both ids — which is strictly better than a
live flag in shipping code: it is a deliberate, auditable, scopeable decision (e.g. only devices that
reached `cli_setup_succeeded`) rather than a switch someone flips by accident. *This is a small
deviation from "build it behind a flag" and should be confirmed.*

**Consent — gated; see "The consent gate".** posthog-js is not even loaded until cookie consent is
accepted (`PostHog.astro:20,146`), and consent is per-origin. A visitor who declined, or who never
visited that host, has no analytics cookie — and therefore gets **no web-side bridge at all**: nothing
read, nothing captured, no `site_variant`. Their refusal takes effect.

**Two things this design must not be allowed to claim.**

*"No stored mapping" is not anonymity.* The test is linkability, not storage. Bridges are query-time
only and nothing is persisted, which is genuine minimisation — but a device id resolvable to an
identified person is personal data regardless of where the resolution happens. Anyone citing the
absence of a mapping table as an anonymity argument has misread this section.

*Returning `$device_id` rather than `distinct_id` is not anonymisation either.* It keeps an email out
of CLI event payloads, which is real and worth having. The device id still resolves to the same person
inside PostHog.

**Consequences to write down rather than discover.** Once one bridge exists the device id is linkable
to an identified person, so **CLI telemetry already collected under the "anonymous" wording becomes
attributable retroactively** — the dataset's character changes, not merely new events. Three
follow-ons are therefore not optional: the disclosure surfaces below; a **retention decision** for CLI
telemetry, which does not exist today and matters far more now; and the accepted fact that rights
requests get harder precisely *because* there is no mapping table to work from. The deferred
person-merge is irreversible in PostHog, which is a rights problem as much as an analytics one — a
second, independent reason it stays deferred.

**Three disclosure surfaces change in the same PRs, and none is checked by code:** the first-run
stderr notice (`CliTelemetry.cs:208-212`), `README.md:1840`, and `README.md:1842`. Stop calling the
data *anonymous*; say **pseudonymous**, and state plainly that CLI usage can be associated with a
workspace and with the person who created it. Note the notice fires once per machine and records that
it has, so anyone who saw the old wording will never be shown the new one.

## Failure handling

Nothing here is on the critical path: the token exchange neither waits for the hops nor is affected
by them. Note this is *not* because auth has already succeeded when the page renders — it may still
fail afterwards (see "What the redirect actually proves").

- Any hop unreachable → the person has already seen "Authentication successful!" and stays on a
  browser error at worst. No functional impact.
- CLI process gone before `/joined` → browser error on a localhost URL. Bounded by the ≤3s dispose
  wait on the short-lived `login` path; `setup` is interactive and long-lived.
- No JS → no redirect, no bridge, today's page. Device flow and headless → no loopback at all;
  bridges 0, 3 and 4 still work.
- CLI telemetry code must never throw — an escaping exception aborts a NativeAOT process with
  SIGABRT (`Program.cs:113`). Every new path is wrapped, including the background drain.

### Expected coverage loss, and how it gets sized

Every gap below costs a *web* bridge (1 and 2) only; bridges 0, 3 and 4 are unaffected. None of these
losses can be quantified in advance, and this spec deliberately does not guess at them — instead the
`hop` property on `cli_auth_return`, and the event's presence or absence per `join_id`, exist so each is countable from
day one, and the first analysis task after rollout is to publish the actual denominator.

| Population | Web bridge | Sizing instrument |
|---|---|---|
| Never accepted cookie consent on that host | Lost **by design** — the consent gate declines to record anything | absence of any `cli_auth_return` for that `join_id` |
| **Browsed on a phone, installed on a laptop** | Lost — the bridge links *this machine's* browser to *this machine's* terminal, and nothing else | no instrument; structurally invisible |
| Visited only the *other* host | Lost for that host, found on the other | `hop` breakdown |
| Visited neither host (heard about kcap elsewhere) | Lost — genuinely nothing to join | no `cli_auth_return` on either hop |
| Device flow / headless / SSH / no browser | Lost — no loopback, no navigation | CLI `is_headless` + absence of any `cli_auth_return` for that `join_id` |
| JS disabled | Lost | absence of `cli_auth_return` |
| Closed the tab before the redirect ran | Lost | absence of `cli_auth_return` |
| Pre-upgrade binary | Lost until they update | no `join_id` on `cli_*` events at all |

Two gaps are non-random and must be reported alongside any channel number, not as footnotes.

**Consent.** The gate means a refuser produces no `cli_auth_return` at all, so this gap is sized by
*absence* — count `join_id`s present on `cli_*` events but on no `cli_auth_return`. That bucket mixes
refusers with no-JS, closed-tab and never-visited cases, so it bounds the loss rather than attributing
it. It selects against privacy-conscious users by construction.

**Cross-device — the big one.** The bridge links *one machine's browser* to *that same machine's
terminal*. Someone who sees an ad on their phone and runs `kcap setup` on their laptop stays two
unrelated people, and no instrument here can even detect that it happened. Given the campaign that
motivated this work ran overwhelmingly to mobile visitors while setup requires a desktop terminal,
**the majority of the interesting acquisition paths remain unattributed.** This design closes the
browser↔terminal gap on a single machine. It does not close the phone→laptop gap, and nothing here
should be read as doing so.

## Verification

**The cookie spike has been run — 2026-08-18, real browser, `www.kurrent.io`.** Recorded here because
it was a blocking prerequisite and its outcome shaped the design.

Cookie name `ph_<projectApiKey>_posthog`, value URL-encoded JSON, ~448 bytes. Contents after consent:

```
$device_id            01a0140d-…   ← UUID, anonymous, survives withdrawal
distinct_id           01a0140d-…   ← equal to $device_id while anonymous
$sesid                [3-element array]
$initial_person_info  { r: "$direct", u: "https://www.kurrent.io/" }
$user_state           "anonymous"
```

**`$device_id` is present and server-readable, so the design ships as specified** — the browser half is
viable and the fallback (a consent-gated HTML hop reading localStorage) is not needed.

Two incidental findings, both usable:

- **`$user_state`** flips to `identified` after `identify()`, giving the Worker a direct way to know
  whether `distinct_id` is an email *without inspecting its shape*. Worth asserting alongside the
  `$device_id` rule as belt-and-braces.
- **`$initial_person_info`** carries the first-touch referrer and landing URL, server-readable. That is
  the marketing attribution, available without any PostHog join. Tempting, and **partly off-limits**:
  `u` is a full URL, and URLs are on the CLI's never-collect list, so the raw value must not cross into
  CLI telemetry. Extracting only `utm_source`/`utm_campaign` values would be permissible and would let
  CLI events carry campaign directly — a genuine simplification, but a scope increase, so it is noted
  here and deliberately not adopted in this spec.

**Not tested, deliberately:** the post-`identify(email)` state. Calling `identify` against production
would fire a real `$identify` and **merge** the anonymous person into that email — the same irreversible
merge this design defers. `$device_id`'s persistence across withdrawal (observed) plus `$user_state`
make that state adequately determined without performing the merge to check.

Then:

- **kcap-web** (vitest + `@cloudflare/vitest-pool-workers`, pattern-match `test/redirects.test.ts`
  and `test/posthog.test.ts`): valid/invalid `j`; `p` out of range and non-numeric; no cookies; both
  cookies present; **all three** response headers on both hops — `x-robots-tag: noindex, nofollow`,
  `cache-control: no-store` and `referrer-policy: no-referrer`; and — importantly — an assertion in
  `test/api-paths.test.ts` that `isApiPath("/api/cli/return")` is true, so no future change can let
  this path enter the experiment. Note CI never runs `npm test`; the local run is the only gate.
- **kcap-cli** (TUnit). `SetupJoin`: mints only when telemetry is enabled; **mints before
  `cli_setup_started` fires, and mints on the device-code/headless lanes too** (regression tests for
  the two placement bugs the mint section exists to prevent); the key is never written to any file
  under `KCAP_CONFIG_DIR`; `Accept` rejects a mismatched key, a second call (including a second
  *matching* call), an out-of-vocabulary `v`, an over-long web id, and a missing `j`; a rejected value
  never reaches the shared property bag nor debug output.

  Per-field independence, since these are the rules that decide whether the feature works at all:

  | Input | Expect |
  |---|---|
  | **missing `v`, valid web ids** | web ids merged, `site_variant` absent — the common case today, since nobody has a `kcap_ab` cookie yet |
  | invalid `v`, valid web ids | web ids merged, `site_variant` absent — one bad field never discards the others |
  | valid `v`, one malformed web id | `site_variant` + the good id merged, bad id dropped |
  | **an email-shaped web id** | rejected by the regex; nothing resembling an email ever reaches the property bag (the privacy guarantee, asserted directly) |

  `LoopbackBrowser` — the listener-ownership contract needs a test per numbered clause above, because
  every one of these paths currently gets disposal for free and would leak silently:

  | Case | Asserts |
  |---|---|
  | success + redirect + `/joined` arrives | context merged, listener disposed once |
  | success + redirect + `/joined` never arrives | disposed at the drain cap, no hang |
  | success + redirect + `Dispose` called early | disposed at the ≤3s wait, pending accept cancelled |
  | **auth-error callback** | no redirect emitted, no drain armed, disposed inline |
  | timeout with a pending `GetContextAsync` | disposed, accept task's exception observed |
  | injected `_openBrowser` throws | disposed, exception propagates as today |
  | closing-page write throws | disposed, ownership not handed off |
  | non-`/callback` request during the drain | 404, drain still waiting |
  | no `ILoopbackJoin` | response bytes and disposal identical to today |
  | `Dispose` twice | second call is a no-op |
  | locally-created browser, every exit path | disposed by its creator |
  | **injected `IBrowser`** | never disposed by the callee |
  | `/callback` response headers | `Referrer-Policy: no-referrer` + `Cache-Control: no-store` present |
  | `/joined` response headers | same two headers present |
  | debug output with a key attached | prints `"join_id":"[set]"`, and the key's actual value appears nowhere on stderr |

  Existing loopback tests bind real listeners on OS-assigned ports and are `[NotInParallel]` for the
  alloc→bind race documented at `LoopbackBrowserTests.cs:6-9` — every new one must be too.

- **Wire names**: body-assertion tests (WireMock, per the repo's existing HTTP-mocking convention) that
  `POST /api/signup/provision` and the `cli-setup` ping each carry literal `joinId`, and that the
  cli-setup ping still carries literal `cliVersion`.
- **kcap-server**: one `PosthogEventMapperTests` case for `join_id` (pure, no host).
- **AOT**: `dotnet publish -c Release` and grep for `IL[23][01][0-9]{2}` — `dotnet build` does not
  surface them. Build the properties bag with `new JsonObject(...)`, never a collection expression.
- **End to end**: `/api/*` is hard-403'd on preview deployments, so this cannot be smoke-tested on a
  preview URL. Local miniflare with `KCAP_SIGNUP_URL` pointed at it, then one real run against
  production after merge with `KCAP_TELEMETRY_DEBUG=1`. What debug is there to show: that `join_id` is
  **attached** (as the `[set]` placeholder), and the actual values of `site_variant` and
  `web_device_id_*`, which are not secret. It must NOT print the key itself — if it does, the
  redaction above is missing and that is a defect, not a convenience.
- **The join actually joins**: after the production run, one HogQL query returning a single row that
  contains the web pageview, the CLI funnel events, `signup_reserved` and `cli_setup_completed` for
  one `join_id` — minted **after** rollout, by a user who had never run `kcap setup` before, since the
  once-only guard makes any other account a false negative (see Query contract). Until that query
  returns a row, this feature is not done. Commit the query itself alongside the spec; it is the
  executable form of the Query contract, and the place the excluded populations get encoded as filters.

## Rollout

**Ship order is a hard constraint: kcap-web first, kcap-cli last.** The CLI redirects to
`/api/cli/return`; if that route does not exist yet the person lands on a 404 immediately after signing
in. kcap-server can land any time (unknown JSON members are ignored by default, so an early CLI is
harmless).

**The kcap-web work now has two parts, and the consent cookie is the one to sequence first**: the
banner must write and delete `kcap_consent` (with the page-load backfill) before the route can gate on
it. A route that ships ahead of the cookie would find no consent record for anyone and would gate
closed for everyone — inert rather than wrong, but it would read as the feature being broken.

**Nothing collects until the kcap-cli release.** The route is only reachable from a CLI redirect, and
`signup_reserved` only carries the key when a CLI sends one. That is what lets the disclosure sign-off
(`kcap-web#168`) run concurrently with the first two merges instead of blocking them.

**Coverage ramps with upgrades.** Every currently-installed binary has the dead-end page compiled in.
Arm-attributed CLI signups only appear as people update, so for the first weeks most CLI signups will
carry no bridge — in **both** arms, not one. (An earlier draft said the redesign arm's signups were
exclusively CLI and therefore wholly invisible; that was wrong. Experiment 90854 serves
`/signup/new` and `/signup/provisioning` from the current build to both arms, so both can sign up on
the web.) Anyone breaking a result down by `site_variant` should know the denominator is
upgrade-limited at first.

Docs, per the standing same-PR rule: kcap-cli `README.md` Telemetry section (new properties, the
post-auth navigation, that opt-out suppresses all of it); kcap-web privacy policy; kcap-server README
analytics section.

Specs ride implementation PRs in these repos — this document ships with the kcap-cli PR and is
referenced from the other two. Per the repo rule, each PR references both its GitHub issue (with a
closing keyword) and the Linear issue Linear auto-imported from it.

## Out of scope

- PostHog person merge (`$create_alias`) — retroactive script, decided later.
- `cross_subdomain_cookie` / `.kurrent.io` cookie domain. It would fix the www↔capacitor person split
  at the root and make one hop sufficient, and it is worth doing — but it re-issues every existing
  visitor's id, inflating new-person counts for weeks. Doing that during a live campaign and a live
  A/B experiment would corrupt the measurements this work exists to enable. Separate piece of work,
  after both finish.
- Per-machine (rather than per-user) joins via `DaemonConnect.JoinId`.
- Closing the copy→run hop (a per-visitor token in the install snippet) — rejected in the 2026-08-08
  spec and still rejected.

## Decisions taken

1. **`site_variant` for a visitor who declined cookies — suppressed.** Settled by the consent gate:
   no read, no event, nothing passed on. The earlier "fire it anyway so the gap is measurable" draft
   is rejected.
2. **The first-run notice's "anonymous usage data" wording does not survive.** All three disclosure
   surfaces change to *pseudonymous* in the same PRs, and state that CLI usage can be associated with
   a workspace and its creator.
3. **The person-merge stays out of shipping code** and becomes a retroactive, scopeable script decided
   later — now for two reasons, analytics reversibility *and* the rights problem an irreversible merge
   creates.
4. **Telling the marketing experiment's owner is no longer a merge blocker** (revised 2026-08-20). The
   original reason was false — see the correction in Motivation: experiment 90854's primary metric is an
   upstream funnel step, not `create_workspace_requested`, and its own description already documents
   that the signup form is identical across arms and out of test. Nor does this work change the
   experiment's sample: excluding `/api/*` from arm assignment matches its server-side, cookie-based
   design rather than altering it. The experiment has also never launched — `draft`, `start_date: null`,
   feature flag `active: false` as of 2026-08-20 — so there is no live measurement to perturb.
   <br>Two things remain worth one message, neither blocking: the `page_not_found` guardrail metric
   could be polluted by CLI users if the CLI ever ships before the kcap-web route is deployed (which is
   why that deploy order is a release gate), and the experiment sitting in `draft` while its serving
   code is live means nothing is being computed — useful to its owner regardless of this work.

## Tracking

| Item | Where |
|---|---|
| This feature, scoped per repo with ship order | `kurrent-io/kcap-cli#574` |
| Disclosure wording, retention, sign-off — **gates the kcap-cli release** | `kurrent-io/kcap-web#168` |
| Withdraw consent does not clear the PostHog cookie (pre-existing bug found by this spec's spike) | `kurrent-io/kcap-web#167` |

`#167` is a dependency of the consent gate in spirit but not in code: the gate is designed not to care
whether that bug is fixed, because it reads `kcap_consent` rather than inferring from the analytics
cookie. Fixing `#167` is worth doing on its own merits.

## Still open

1. **Retention for CLI telemetry.** None is defined today. It mattered less when the data was
   described as anonymous; it is now a required decision, not a nicety. Tracked in `kcap-web#168`.
2. ~~Whether the consent-cookie-presence proxy is sound.~~ **Answered 2026-08-18: it is not.** The
   analytics cookie survives withdrawal, so the gate now requires an explicit `kcap_consent` cookie
   written and deleted by the banner. Folded into the design above; no longer open.
3. **Sign-off on the disclosure wording** from whoever owns the privacy policy — tracked in
   `kcap-web#168`. **Correction to an earlier draft of this spec:** the gate is the **kcap-cli
   release**, not the kcap-web merge. The new route does nothing until a CLI redirects to it, and
   `signup_reserved` only gains the key when a CLI sends one, so kcap-web and kcap-server can land
   while sign-off is in flight.
