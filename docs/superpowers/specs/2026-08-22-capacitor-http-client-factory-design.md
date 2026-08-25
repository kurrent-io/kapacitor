# HTTP as three layers: named clients, per-platform clients, caller budgets

No ticket yet — open one before branching, and use its id for the branch and commit prefix.

Status: designed, not started. Lands after AI-2147's Part 1 (see *Order against AI-2147*). Every
count and `file:line` here was verified against the tree at `5d81d284`; re-check before relying on
one, since several drifted between drafts.

## Problem

Every request this CLI sends to its own server is built by one static method pair —
`HttpClientExtensions.CreateAuthenticatedClientAsync` / `CreateClientWithAuthStatusAsync`, **58 call
sites** across 37 files (count excludes `HttpClientExtensions.cs` itself and doc-comment mentions;
the number has drifted in earlier drafts, so the rule matters). Four problems, each measured.

**The abstraction already exists as untyped delegates, threaded by hand.**

| shape | where | occurrences |
|---|---|---|
| `Func<string?, CancellationToken, Task<HttpClient>>` (`memoryClientFactory`) | 9 hook commands, Claude included | 68 |
| `Func<Task<(HttpClient, AuthStatus)>>` and its `CancellationToken` variant | `ClaudeHookCommand`, `CursorHookCommand`, `AgentHookPoster`, `SpoolDrainLoop` | 9 |
| `Func<HttpClient>` (`httpFactory`) | `OnboardingFacade` | 6 |

`GeminiHookCommand` threads `memoryClientFactory` through four signatures to reach one construction
site. Its companion `disposeClients` flag has **opposite polarity in two vendors** —
`ClaudeHookCommand.cs:966` and `CursorHookCommand.cs:589` pass `memoryClientFactory is not null`,
the other seven pass `is null` — and **both are correct**: the seven default to a factory that mints
a client, Claude and Cursor default to handing back the hook's own caller-owned client, as
`CursorHookCommand.cs:584-586` states outright. The flag is not a bug; it is ownership bookkeeping
that only exists because the client arrives as a delegate. Injecting the client instead removes the
question, which is the point.

**The 401 recovery is copy-pasted six times.** `SendWithRefreshRetryAsync` in `McpMemoryServer:216`,
`McpSessionsServer:275`, `McpAnalyticsServer:274`, `McpWorkItemsServer:217`, `McpFlowsServer:438`,
`McpFlowResultServer:374` — each with its own `new TokenStore(PathHelpers.Config)`. Four are
byte-identical (`McpMemory`, `McpSessions`, `McpAnalytics`, `McpWorkItems`); `McpFlowsServer` differs
only by threading an optional `CancellationToken`; only `McpFlowResultServer`'s `allowRefresh` carries
meaning (see *Sites that do not simply convert*). Their ~31 *usages* all need unwrapping, not just
the six declarations.

> **Correction to an earlier draft.** That draft claimed these copies "replay a stream-backed body
> because they lack `CanResend`". They do not. Every `send` lambda builds its content *inside* the
> closure (`McpMemoryServer.cs:178-183`), so a retry **rebuilds** the request. Caller-side retry is
> strictly *more* capable than `CanResend`, not less. The duplication is real; that defect was not.

**No connection pooling anywhere.** Zero `SocketsHttpHandler` / `PooledConnectionLifetime` in
`src/`; 26 `new HttpClient(` sites; three carry a `// ReSharper disable once ShortLivedHttpClient`
apology (`ServerUrlNormalizer.cs:109`, `StatusCommand.cs:32`, `CodexHookCommand.cs:7`). The cost is
concentrated, not diffuse: 14 sites build a fresh `HttpClientHandler` — and therefore a fresh
connection pool — per request *inside processes that live for hours* (cluster C2 below).

**Two process-global caches with test seams:** `HttpClientExtensions.cachedProvider` (+
`ResetProviderCacheForTesting`) and `MachineTokenProvider`'s `cachedToken`/`cachedKey`/`cachedExpiry`
behind a `SemaphoreSlim` (+ `ResetForTesting`).

**And the "one choke point" promise is already broken five times.** `CreateClientCoreAsync:57-62`
claims to be "the ONE choke point … the one place that can promise every request the server sees is
tagged". Requests reach our server untagged from `WhoamiCommand.cs:88` (deliberate, re-attaches by
hand), `WatchCommand.cs:498` and `ServerConnection.cs:192` (SignalR, no tags at all), and
`AgentOrchestrator.cs:3505` (the `"Attachments"` named client, hand-attached bearer, no tags).

## What varies, and when

This is the analytical core: everything else follows from it.

| dimension | today | truth | evidence |
|---|---|---|---|
| server URL | per-call parameter; ~20 sites omit it | **per process** | no site targets a second server; `ReportVersionCommand.cs:23-27` re-derives the identical fallback chain by hand to avoid "authenticating against one host and probing another" |
| config root | ambient static | **per process** | AI-2147 |
| machine vs token store | branch after I/O (`:115`) | **per process** | `MachineAuth.cs:95-97` — two `GetEnvironmentVariable` reads |
| auth provider is `None` | branch after I/O (`:97`) | **per process, but only once discovery SUCCEEDS** | `HttpClientExtensions.cs:296-298`: the unreachable-server `catch` deliberately does not memoize — "don't cache — allow re-discovery next time". Freezing it would pin a daemon that started while the server was down to a `None` verdict for its whole life, sending every request bare |
| the bearer | read once, pinned to `DefaultRequestHeaders` and the handler's `_current` | **per request, off a memo** | both handlers already re-read on 401 (`UnauthorizedRetryHandler.cs:29`, `MachineUnauthorizedRetryHandler.cs:31`) |
| 401 recovery installed | per-call `bool autoRetryUnauthorized` | **per client identity** — a client with a credential can recover, one without cannot | `McpFlowResultServer.cs:125-126` and `:241` are literally the same bit expressed twice |
| rejected-token re-call | per-call `string?`, 3 sites | **dissolves** — it is the recovery step, not a construction knob | `SessionStartContextFetch.cs:32-42` builds a second client purely to hand the refused bearer back |
| redirect policy | per-call `bool`, 3 sites | **derived, not chosen: off wherever a bearer is attached** — see *Redirects* | measured: `Authorization` is stripped on every auto-redirect |
| stderr messaging | 2 verbs | **3 values**: interactive / background-silent / hook-silent | 23 / 14 / 13, see clusters |
| auth outcome returned | per verb | per call | 13 sites destructure `(client, status)` |
| timeout | per call, mutated after creation | **per call, free** — `HttpClient.Timeout`, never a handler property | 2s, 5s, 10h, `InfiniteTimeSpan`, default |
| transport retry budget | per request | **per request** | `SendWithRetryAsync(send, totalTimeout, perAttemptTimeout, ct)` |

**Smallest honest per-call set: messaging style, want-status, timeout, anonymous, redirect lane.**
The first four are encoded in *which verb you call*. The fifth is one verb. **No boolean parameter
survives.**

## The 58 call sites cluster into six

Exact partition. Counts sum to 58.

**C1 — interactive one-shot (23).** `using var`, stderr hint wanted, no budget.
`Program.cs:496,539,757` · `RecapCommand:23,82,338,409` · `ProjectsCommand:15,73` ·
`MachineCommand:216,385,445` · `EvalCommand:22,57` · `ErrorsCommand:8` · `ValidatePlanCommand:11` ·
`WhatsDoneCommand:34` · `CurateCommand:28` · `FeedbackCommand:78` · `ReviewCommand:23` ·
`ImportCommand:674` · `PermissionRequestCommand:136,174`

**C2 — background one-shot on the *interactive* verb (14). A live defect.** These run in the watcher
and the daemon, yet call the verb that writes `Run 'kcap login'` to a stderr nobody reads — once per
flush, per subagent link, per eval tick. They are also every socket-churn site.
`WatchCommand:987,1054,1143,1187,1707,2943` · `WatcherManager:604` · `CodexSubagentTeardown:57` ·
`GeminiSubagentTeardown:55` · `OpenCodeSubagentTeardown:80` · `AgentOrchestrator:348` ·
`EvalRunner:50,99,144`

**C3 — silent, status-inspecting, budget-bounded (10).**
`CodexHookCommand:620` · `CopilotHookCommand:444` · `GeminiHookCommand:461` · `ClaudeHookCommand:40` ·
`CursorHookCommand:79` · `AgentHookPoster:79,144,249` · `SpoolDrainLoop:52` · `ReportVersionCommand:30`

**C4 — the SessionStart context lane (3).** Exactly the `allowAutoRedirect: false` set *and* exactly
the `rejectedAccessToken` set — the same three sites.
`SessionStartMemoryHookSupport:41` · `ClaudeHookCommand:41` · `CodexHookCommand:144`

**C5 — long-lived stdio server, caller-side 401 (6).** `McpMemoryServer:53` · `McpSessionsServer:52` ·
`McpAnalyticsServer:58` · `McpWorkItemsServer:56` · `McpFlowsServer:68` · `McpFlowResultServer:126`

**C6 — long-lived stdio server, handler 401 (2).** `McpReviewServer:69` · `McpJudgeServer:83`

Cross-cutting: **anonymous-allowed = 3** (each a ternary against `new HttpClient()`), **post-creation
`Timeout` mutation = 3**, **long-lived holder = 8** (C5+C6), **needs status = 13** (C3+C4).

## Architecture

Three layers, split by *level*, not by resource.

```
L3  caller budgets          PostWithRetryAsync / GetOnceAsync / SendWithRetryAsync   (unchanged)
L2  per-platform clients    ICapacitorHttpClient   +   typed clients per foreign host
L1  raw pooled clients      IHttpClientFactory     +   3 named clients for our server
```

`IHttpClientFactory`, named clients and typed clients are the foundation, not an implementation
detail we might swap. L2 is a *consumer* of L1, never a replacement for it.

### Why one authenticated chain, not five

`CreateClientCoreImplAsync` branches at eight `NewClient(...)` sites, and an earlier draft concluded
that a registered — therefore fixed-per-name — handler chain could not express it. That was wrong.
The five branches are **token-plumbing outcomes, not pipelines**. They look like pipeline variation
only because the handler holds the token today. Once the handler asks an `ICredentialSource`:

| today's branch | what actually varies | chain |
|---|---|---|
| `provider == "None"` (`:98`) | source yields no bearer | **same** |
| `MachineAuth.Intended` (`:128`) | source *is* `MachineCredentials`, chosen once | **same** |
| `rejectedAccessToken` recovery (`:144`) | that is `RotateAsync(refused)` | **same** |
| normal (`:153`) | — | **same** |
| `Expired`/`NotAuthenticated`/`WrongServer` (`:159`) | source yields no bearer plus a status | **same** |

So named clients vary only on genuine **handler-level configuration**, of which there are two:
`AllowAutoRedirect` (a primary-handler property), and whether the credential handler exists at all.

We also do not need `IHttpMessageHandlerFactory`. The docs prescribe that escape hatch for
*scope-related* auth logic — "DO NOT cache any scope-related information … inside `HttpMessageHandler`
instances". Our credential source is a process **singleton**, not scoped, so the hazard it works
around does not apply.

### L1 — four named clients

```csharp
public static class CapacitorClients {
    public const string Default  = "capacitor";       // authenticated; redirects NOT followed
    public const string Anon      = "capacitor-anon";  // no credential handler; redirects followed
    public const string Loopback  = "capacitor-local"; // borrowed reviewer: 127.0.0.1, no proxy
    // Pre-resolution probing (`kcap setup`, ServerUrlNormalizer): the host is user-typed and may
    // never be adopted, so it gets neither our version tags nor a ServerVersionStore write.
    public const string AnonProbe = "capacitor-probe";
}

public static IServiceCollection AddCapacitorHttp(this IServiceCollection services) {
    // One object carrying (ServerUrl, ConfigRoot, resolved profile). Without it the handlers below
    // are unresolvable: ServerVersionCaptureHandler takes a bare string, and AddHttpMessageHandler<T>
    // resolves via GetRequiredService — which throws on the FIRST REQUEST, not at container build.
    services.AddSingleton<CapacitorServer>();
    services.AddSingleton<ICredentialSource, ResolvingCredentialSource>();
    services.AddTransient<ServerVersionCaptureHandler>();
    services.AddTransient<ObservationHeaderHandler>();
    services.AddTransient<UnauthorizedRecoveryHandler>();

    // follow ⇔ !authenticated. See *Redirects*: a bearer cannot survive a redirect, so following
    // one on an authenticated client can only produce a misleading 401.
    foreach (var (name, authenticated) in
             new[] { (CapacitorClients.Default, true), (CapacitorClients.Anon, false) }) {
        var b = services.AddHttpClient(name)
            // Capture FIRST so it stays outermost and observes the final response after a recovery
            // resend. Swapping these two lines silently caps the version at the pre-retry response.
            .AddHttpMessageHandler<ServerVersionCaptureHandler>()
            .AddHttpMessageHandler<ObservationHeaderHandler>();

        if (authenticated) b.AddHttpMessageHandler<UnauthorizedRecoveryHandler>();

        b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AllowAutoRedirect        = !authenticated,
            })
         // PooledConnectionLifetime owns DNS freshness, so handler rotation must be DISABLED or the
         // two expire together and every pool is cold. This pairing is the documented one; omitting
         // the second line is the pathological case.
         .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
         // Nothing is redacted by default: ShouldRedactHeaderValue ships as "redact nothing", so a
         // Trace-level provider would print the bearer verbatim. Belt as well as braces — the CLI
         // registers no provider either.
         .RedactLoggedHeaders(["Authorization", "Cookie", "Set-Cookie"]);
    }
    return services;
}
```

`Anon` carries **no base address**: `/auth/refresh` targets the server that *minted* the token, and
every existing call site already passes an absolute URL.

`PooledConnectionLifetime` alongside factory handler rotation is the documented combination; it means
a client held for hours by an MCP server still rotates connections even though its handler instance
does not.

### L2a — `ICapacitorHttpClient`, our own server

A process singleton that injects `IHttpClientFactory`. This is the shape the docs prescribe: "use the
*named client* approach … injecting `IHttpClientFactory` in the singleton service and recreating
`HttpClient` instances when necessary." A *typed* client would capture one `HttpClient` and freeze its
handler — the documented anti-pattern for singletons.

```csharp
public interface ICapacitorHttpClient {
    string ServerUrl { get; }

    /// C1 (23). An interactive command's client: a lapsed login prints the actionable re-auth hint.
    Task<HttpClient>  ForCommandAsync(CancellationToken ct = default);

    /// C2 (14). A background lane — watcher, daemon, subagent teardown. Same recovery, SILENT:
    /// nobody reads that stderr, and today it gets one "Run 'kcap login'" per flush.
    Task<HttpClient>  ForBackgroundAsync(CancellationToken ct = default);

    /// C3 + C4 (13). A hook's client plus the auth outcome, so a caller with no budget to retry can
    /// skip a POST rather than earn a 401. Writes nothing: Gemini reads hook stderr as the hook's
    /// result. C4 needed a second verb only for its redirect policy, which is now the default.
    Task<AuthAttempt> ForHookAsync(CancellationToken ct = default);

    /// C5+C6 (8). A long-lived stdio server's client, built on first tools/call and held.
    Task<HttpClient>  ForSessionAsync(CancellationToken ct = default);

    /// (3). No bearer, no token-store read, therefore no recovery: `import --discover`, a no-auth
    /// deployment. Resolves nothing, so sync.
    HttpClient        Anonymous();

    /// The borrowed reviewer's submit lane: 127.0.0.1 capability URL, no proxy, no credential, and
    /// no config-dir write. Its own name because that containment is a documented invariant.
    HttpClient        Loopback();
}

/// A client and why it is (or is not) carrying a bearer. Deconstructs, so all 13 existing
/// `var (client, status) = await …` sites compile unchanged.
public readonly record struct AuthAttempt(
        HttpClient Client, AuthStatus Status, string? Problem, string? IssuedServerUrl) {
    public bool Usable => Status is AuthStatus.Ok or AuthStatus.NoAuthRequired;
    public void Deconstruct(out HttpClient client, out AuthStatus status) { client = Client; status = Status; }
}
```

Each verb is one `(named client, messaging)` pair. No options bag, no booleans.

### L2b — the credential source

```csharp
public interface ICredentialSource {
    /// The bearer to send now, plus why there isn't one. Volatile read on the hot path; I/O only on
    /// a cold or expired memo.
    ValueTask<CredentialState> ResolveAsync(CancellationToken ct);

    /// The server refused `refused` (null = "we sent nothing, re-read the store"). Rotation is
    /// conditional on `refused` still being what is held, so a peer's fresh credential is adopted
    /// rather than rotated a second time.
    ValueTask<string?> RotateAsync(string? refused, CancellationToken ct);
}
```

Three implementations, one per branch that exists today: `TokenStoreCredentials`,
`MachineCredentials`, `NoCredentials`. `ResolvingCredentialSource` picks between them lazily and
memoizes **only a successful (or disk-cached) discovery**, per the table above.

This is the type that makes `autoRetryUnauthorized` disappear: the flag selected between two handler
*types*, and there is now one.

**`ResolveAsync` needs in-process single-flight — a correctness requirement, not a nicety.** On a stale
memo it reaches `GetValidTokensForServerAsync` → `RefreshWithCrossProcessLockAsync`
(`TokenStore.cs:400-405`), a profile-scoped **file lock**, now inside `SendAsync`. `McpFlowsServer`
holds concurrent long-polls with `Timeout = InfiniteTimeSpan` (`:74`); without single-flight every
in-flight send queues on that flock the moment the memo expires. `RotateAsync` has its `refused` guard
for exactly this reason and `ResolveAsync` has no equivalent. Alternative: keep proactive expiry
refresh out of the send path and let the 401 path handle it — state which, because they behave
differently under load.

**`MachineCredentials` keeps `MachineTokenProvider`'s `Gate`.** `GetTokenAsync` takes it *before* the
cache-hit check (`:90` before `:98-102`), so a naive "read the token at send time" serialises every
request in the process behind one semaphore. Only the four statics collapse — into one immutable
`(token, key, expiry)` snapshot swapped with `Volatile.Write` and read lock-free — with the gate
retained around the mint and the cache re-checked inside it. The gate is the single-flight the class
exists for (`:27-30`), and the ARM64 barrier reasoning at `:32-38` depends on it.

**The cycle, and how it breaks.** The source needs HTTP — `/auth/config`, `/auth/refresh`, the WorkOS
mint — so naively: source → `IHttpClientFactory` → `Default` → `UnauthorizedRecoveryHandler` →
source. It breaks because the source uses **`Anon`** (which has no credential handler) for our
server, and the typed `WorkOSClient` for the mint. That is the same structural rule as "refresh sits
below auth", enforced by which name it asks for rather than by a comment.

### L2c — foreign hosts are typed clients

One per platform, registered with `AddHttpClient<T>`, base address at registration, `HttpClient` by
constructor. None of them gets our credential or our observation headers — our version tags must
never travel to WorkOS or npm.

| platform | client | status |
|---|---|---|
| kcap auth proxy | `AuthProxyClient` | exists as `class AuthProxyClient(HttpClient http)` — register it |
| provisioning control plane | `TenantProvisioningClient` | same |
| GitHub OAuth / device flow | `GitHubOAuthClient` | new; absorbs `OAuthLoginFlow:98,273,324,395,470` |
| WorkOS | `WorkOSClient` | new; absorbs `TokenStore.RefreshWorkOSAsync:723` and `MachineTokenProvider.MintAsync` |
| npm registry | `NpmRegistryClient` | new; `UpdateCommand` |
| PostHog | `TelemetryClient` | exists but takes `HttpMessageHandler`; see the trap below |

**`SqliteNativeResolver` gets no typed client and stays as it is.** Its download runs inside a static
synchronous `Fetch` (`.GetAwaiter().GetResult()`), reached from a `NativeLibrary` DllImport resolver
where no container is in scope; its base may be a filesystem directory or a `file://` URL for
airgapped installs (`:143-156`), which a registration-time `BaseAddress` cannot express; and it sets a
per-download `UserAgent` and a 60s timeout. It is still the proof that anonymous lanes must follow
redirects (see *Redirects*) — `github.com/.../releases/download/…` always 302s to an object store.

`GitHubOAuthClient` absorbs **five** sites, not three: `OAuthLoginFlow:98,273,324,395,470`. Two more
foreign-host clients are uninventoried and need placing: `MachineCommand:98` (auth proxy) and
`SetupCommand:1051` (a raw client with a hand-set bearer against *our* server — so it is a
`ForCommandAsync` site, not a foreign one).

Typed clients register a named client under the hood (name = `typeof(TClient).Name`) and are
`Transient`. **Never register one a second time as a plain service** — that overwrites the link and
silently injects an unconfigured `HttpClient`.

`TokenStore.RefreshGitHubAsync:664` is the odd one: it posts to *our* server's `/auth/refresh`, but at
the *minting* server's URL, from below the auth layer. It takes `Anonymous()` with an absolute URL.

**Two loopback destinations get no client at all** — `CodexHookCommand:566` (daemon bridge) and
`McpReviewContextServer:42` (127.0.0.1 capability URL, `UseProxy = false`). They keep constructing
directly.

### L3 — caller budgets, unchanged

`PostWithRetryAsync`, `GetWithRetryAsync`, `PutWithRetryAsync`, `DeleteWithRetryAsync`,
`PostOnceAsync`, `GetOnceAsync`, `SendWithRetryAsync` stay verbatim. They are per-request budget
decisions — 30s default, 5s for refresh, 2s for a Codex hook — and a handler cannot see the caller's
budget. Two retry policies at two levels, each where its information lives.

## Redirects: never followed on an authenticated client

This was an open question and is now settled by three pieces of evidence. The rule it produces —
**follow ⇔ no bearer** — replaces a per-call boolean that had no recorded reason.

### 1. `Authorization` is stripped on every auto-redirect. Measured.

The API remarks say it, and the wording predates `SocketsHttpHandler`, so it was verified against
.NET 10 with two loopback `HttpListener`s and a control request that proves the harness observes the
header at all:

| case | `Authorization` at the target |
|---|---|
| control: direct request, no redirect | `Bearer TOKEN` |
| 307, same origin, absolute `Location` | **absent** |
| 307, same origin, relative `Location` | **absent** |
| 307, different port | **absent** |
| 302, same origin | **absent** |

Not origin-scoped — **blanket**, and it makes no difference whether the header was set on
`DefaultRequestHeaders` or per-request (which is what `UnauthorizedRetryHandler` does). Redirection
happens inside the primary handler, below every `DelegatingHandler`, so nothing of ours is re-invoked
to put the bearer back.

Consequence: following a redirect on an authenticated client cannot succeed. It can only convert
whatever caused the redirect into a **401**.

### 2. The server never redirects a CLI path anyway. By design.

Verified in `../kcap-server`. Both auth strategies intercept `OnRedirectToLogin` and answer with 401
JSON rather than a 302 for exactly our path space — `WorkOSAuthStrategy.cs:60-67` (`/api`, `/hooks`)
and `GitHubAppAuthStrategy.cs:85-95` with `ShouldReturn401:160-176` (`/api`, `/hooks`, `/_blazor`,
`/hubs`, and any non-navigation `Sec-Fetch-Mode`). The body is
`{"error":"unauthenticated","message":"Authentication required. Run 'kcap login' to authenticate."}`
— written for this client. Every CLI path outside those prefixes is `AllowAnonymous()`:
`/auth/config` (`AuthEndpoints.cs:21`), `/auth/refresh` (`:254`), `/auth/token` (`:252`).
`WhoamiCommand.ProbePath` is `/api/me/notification-prefs`, inside the 401 space.

Every other `Results.Redirect` in that repo is a browser flow — the AuthProxy, `GitHubLinkEndpoints`,
`/picker`, `/no-access`. None on a CLI path.

So `WhoamiCommand.cs:86-87`'s stated reason — "a login redirect would otherwise masquerade as some
other status" — describes something our server provably does not do. The precaution was right; the
threat model was not.

**The one real 3xx** is `app.UseHttpsRedirection()` (`Program.cs:3427`), unconditional outside
Development, unconfigured, so ASP.NET defaults apply: **307**, and a no-op when no HTTPS port is
known. An `http://` `server_url` reaching an https-capable deployment therefore 307s on every
request. Redirect handling is about *that*, not about auth.

### 3. The distinction was never justified

`allowAutoRedirect` entered in `99b820e0` (AI-1458, PR #350, "add shared SessionStart memory
foundation"), added to `CreateClientWithAuthStatusAsync` in the same commit as its first `false`
caller, alongside the `forceRefresh` parameter that later became `rejectedAccessToken`. No comment in
that commit or since explains the choice, at any of the three surviving sites
(`SessionStartMemoryHookSupport.cs:42`, `ClaudeHookCommand.cs:42`, `CodexHookCommand.cs:145`). The
only recorded rationale arrived separately in `8c498b49` (#387) for `WhoamiCommand`, and §2 retires
it. The memory lane was accidentally right.

### The rule

- **Authenticated lanes do not follow redirects.** A 3xx is surfaced to the caller as a 3xx, because
  it means a configuration fault — `server_url` is `http://` against an https deployment — and that
  is actionable where a laundered 401 is not.
- **Anonymous and foreign lanes do follow.** There is no bearer to lose, and one of them requires it:
  `SqliteNativeResolver` downloads from `github.com/.../releases/download/…`, which always 302s to an
  object store.

This is why `follow ⇔ !authenticated` is written as a derivation in `AddCapacitorHttp` rather than a
per-client constant: the two can never diverge without reintroducing the bug below.

### The pre-existing bug this exposes

Today's `Default` lane follows redirects **with** a bearer, so an `http://` `server_url` against an
https deployment produces, on every authenticated call: 307 → bearer stripped → **401** →
`UnauthorizedRetryHandler` rotates the token, **spending a single-use WorkOS refresh token** → still
401 → the user is told to run `kcap login`, which cannot help. A configuration fault is laundered
into a spurious auth failure that also destroys a credential.

Roughly 37 sites (C1 + C2 + C6) are exposed. Cheap to confirm: point the CLI at an https deployment
with `http://` in `server_url`. **This is a live defect independent of this design and should get its
own issue rather than waiting for it.**

Not to be confused with the reverse: on .NET 5+, `AllowAutoRedirect = true` does **not** enable
https→http redirection, so the insecure-downgrade direction cannot happen.

## The unusable server URL loses its process exit

`EnsureAbsolute` (`HttpClientExtensions.cs:341`) calls `Environment.Exit(2)` from inside a send path,
and `ProcessUrlPolicy` exists only to defuse that: agent-spawned commands set `Throw` at
`Program.cs:88` so an uncatchable exit cannot kill a hook before its stdout contract. Two mechanisms —
a process-wide mutable static and a validator that can end the process — answering a question that is
already settled before dispatch: is the URL this process resolved usable at all?

This layer removes the need for both, because it creates the one place that question belongs.
`CapacitorServer` is built from the resolution, once, at the composition root; validity is a property
of that object rather than a check re-run on every request.

```csharp
// Usable is IsPostable evaluated once, at construction. No send path re-validates, and nothing
// anywhere ends the process over a URL.
public sealed class CapacitorServer(ProfileContext profiles, ConfigRoot config) {
    public string    Url    => profiles.Resolution.ServerUrl ?? "";
    public bool      Usable { get; } = HookHttp.IsPostable(profiles.Resolution.ServerUrl);
    public UrlSource Source => profiles.Resolution.Source;   // remediation text follows the input
}
```

Each verb then answers in the shape its own callers already handle:

| verb | unusable URL |
|---|---|
| `ForHookAsync`, `ForBackgroundAsync` | an `AuthAttempt` carrying a new `AuthStatus.UnusableServerUrl`. No token-store read, no `/auth/config`, no socket. `Usable` is already false for it, so `AuthAttempt` needs no other change. |
| `ForSessionAsync` | the same — an MCP server reports a protocol error and stays up |
| `Anonymous()`, `Loopback()` | unreachable: neither resolves `server_url` |
| `ForCommandAsync` | throws `UnusableServerUrlException` |

**`ForCommandAsync` is the only thrower, and it is not in a send path.** `Program.cs`'s top-level
handler catches it, prints `SchemeMissingHint` and **returns** 2 — a return from `Main`, so the exit
code a user sees is unchanged while every `finally`, the telemetry `ProcessExit` flush and every
vendor's fail-open `catch` still run. That is all `UrlFailurePolicy.FailFast` ever bought, minus the
exit.

The fail-open population never reaches the throw, because it never calls `ForCommandAsync`: C2/C3/C4
callers destructure an `AuthAttempt` and already have a not-usable branch — spool, skip, or protocol
error. So `UrlFailurePolicy.Throw` has nothing left to defuse either, and `ProcessUrlPolicy`,
`UrlFailurePolicy` and `Program.cs:88` go with it. `UnusableServerUrlException` stays on a narrower
charter: one thrower, one catcher, which is what makes its "audit every `catch` that must re-throw
this" comment tractable.

**The scattered guards collapse into the status.** `AgentHookPoster:82,142,256`,
`WatcherManager:122,547,600`, `WatchCommand:224`, `ClaudeHookCommand:70,93` and `CursorHookCommand:97`
all ask `IsPostable` about a URL they are about to hand to a client that asks again; once the verb
answers, they read the status instead, and the two delegating predicates
(`SessionStartMemoryHookSupport.CanAttempt`, `CodexHookCommand.CanAttemptMemoryInjection`) delete
outright. The exception is a guard that exists to avoid *work* rather than a bad send:
`StartMemoryIndexTask` must short-circuit before it spawns a task and spends a lease, so it keeps a
pre-flight check — now `server.Usable`, with no client built to ask.

`HookHttp.IsPostable` and `IsAcceptableUrl` survive as the pure predicate and `SchemeMissingHint` as
the message. What dies is the *enforcement*: the exit, the policy static, and the re-validation on
every request.

### Why not refuse at dispatch

The obvious alternative — reject an unusable URL in `Program.cs` before dispatch — needs a list of
which commands need the server, and gets it wrong in the case that matters most: `kcap config set
server_url https://…` **is** the repair, so refusing it locks the user out of fixing their own config.
`kcap status` fails the other way: it should report the bad URL, not exit 2 before it can. Resolving a
client is a better signal than any such list, because a command that never asks for one never had the
problem. This is also why the refusal cannot live in `AddCapacitorHttp` or in `CapacitorServer`'s
constructor — container build must stay unconditional.

## The discovery cache, and what survives of `HttpClientExtensions`

**`AuthProviderCache` is the reason hooks are cheap and must not regress.** `ResolvingCredentialSource`
keeps its exact protocol: the usability gate first (`CapacitorServer.Usable`, where `EnsureAbsolute`
used to sit), then `TryGet` *before* any network probe, then `Set` **only** on a successful discovery
(`HttpClientExtensions.cs:265` → `:269` → `:286`). Two constraints:

- **The disk key is the raw `baseUrl` string** (`AuthProviderCache.cs:33,:65`) with no
  canonicalisation, while `AppConfig.NormalizeUrl` is `url.TrimEnd('/')` (`AppConfig.cs:223`). If
  `ICapacitorHttpClient.ServerUrl` normalises where today's threaded `baseUrl` did not, **every
  existing on-disk entry misses** — one extra `/auth/config` round trip per hook process after
  upgrade, and permanently if two paths pass different spellings. Folding
  `ReportVersionCommand:24-27`'s chain into `ServerUrl` (step 11) is exactly where this can happen.
- **The in-process memo is not the same cache.** `cachedProvider` moves onto the instance; the on-disk
  one stays process-independent, and it is the only thing that helps a hook, which is a fresh process
  every time.

**`DiscoverProviderAsync` has two callers outside the choke point** — `WhoamiCommand:38` and
`SetupCommand:818` — and it is `public`. `ICredentialSource` (`ResolveAsync`/`RotateAsync`) serves
neither. Keep a discovery verb on the credential source, or leave `DiscoverProviderAsync` public and
rooted; do not fold it away silently. These are also two more escapes from the "one choke point" that
the count of five does not include.

**What survives of `HttpClientExtensions`.** The class is not deleted. It keeps everything that is a
pure function of a URL or a string, none of which belongs to a client:

| member | fate |
|---|---|
| `AuthStatus` enum | stays (referenced by `AuthAttempt` and 9 destructuring sites) |
| the three header constants | stay |
| `IsAcceptableUrl`, `SchemeMissingHint` | stay — pure. `EnsureAbsolute` is deleted; see *The unusable server URL loses its process exit* |
| `RenderUnreachableError`, `WriteUnreachableError`, `UnreachableErrorText`, `StripControlCharacters` | stay — pure rendering, and `MachineTokenProvider` already borrows `StripControlCharacters` |
| the `extension(HttpClient)` retry members + `SendWithRetryAsync` + `PerAttemptTimeout` | stay (L3) |
| `HandleUnauthorizedAsync` | audit: check for remaining callers once the six MCP copies go |
| `CreateAuthenticatedClientAsync`, `CreateClientWithAuthStatusAsync`, `CreateClientCore*Async`, `AttachObservationHeadersAsync`, `cachedProvider`, `ResetProviderCacheForTesting` | deleted |

## Observation headers become structural

Today's guarantee is "we set them on every client we build, and hope nobody builds their own", and
five sites already break it. `ObservationHeaderHandler` sits second-outermost in a chain no caller can
reach, so the guarantee holds for `Anonymous()`, for the resend after a 401, and for anything added
later — and it cannot be cleared.

**Set-if-absent, never `Add`.** Today's code writes once to `DefaultRequestHeaders` (`:194`, `:200`).
A handler using `Headers.Add` would emit `X-Kcap-Cli-Version: v, v` if any future reordering put a
resending handler outside it — wrong on the wire rather than loud.

The async-ness disappears for free. `AttachObservationHeadersAsync` is `async` only because
`AppConfig.GetActiveProfileAsync` (`:401-407`) falls back to a disk read when the `ResolvedProfile`
static is unset. AI-2147 makes the resolved profile entry-point state, so both header values are
computed **once** at composition and the handler is a pure string write. That is what lets
`Anonymous()` be synchronous without dropping the tags.

## SignalR: out of the transport, in on the credential

SignalR owns a WebSocket and cannot ride our chain, but it must not keep its own auth:

```csharp
options.AccessTokenProvider = async () => (await credentials.ResolveAsync(default)).Bearer;
foreach (var (k, v) in observation.Headers) options.Headers[k] = v;   // API unverified
```

Two wins: the daemon stops opening `TokenStore` on every reconnect (`ServerConnection.cs:192`,
`WatchCommand.cs:498` both do `new TokenStore(PathHelpers.Config)` per call), and the hub starts
sending the version tags it has never sent. `ResolveAsync` must re-check expiry rather than returning
a naked memo — a reconnect storm after a long idle is exactly when a stale pin bites.

## Lifetime and disposal

**Callers dispose, and disposal is free.** Factory-created clients are safe to dispose; disposing one
does not dispose its handler. So all 35 `using var` sites stay as written, and the 8 held-field sites
keep holding — but see the trap: a client held for hours pins a handler past its lifetime, so the
long-lived holders rely on `PooledConnectionLifetime` for DNS freshness rather than on rotation.

`disposeClients` and `SessionStartContextFetch:51-55`'s reference-equality dance both go: hold-versus-
dispose stops being a correctness question.

## Testing

The seam is the interface, plus the documented named-client override.

- **Injecting a fake client:** take `ICapacitorHttpClient`. `Capacitor.Tests.Helpers` gains
  `FakeCapacitorHttpClient(HttpMessageHandler transport, AuthStatus status = AuthStatus.Ok)`. That one
  type replaces 68 `memoryClientFactory` occurrences, 12 `(HttpClient, AuthStatus)` factory
  parameters, 6 `httpFactory`, and `disposeClients`. Prior art to generalize rather than reinvent:
  `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorHarness.cs:60`'s
  `StubHttpClientFactory`.
- **Overriding a real registration:** re-registering a name appends configuration, so a test does
  `services.AddHttpClient(CapacitorClients.Default).ConfigurePrimaryHttpMessageHandler(() => fake)`.
  This is the documented mechanism, not a trick.
- `ResetProviderCacheForTesting` → **deleted**; the memo is a field on `ResolvingCredentialSource`.
  Its five callers construct a fresh instance and can drop the `[NotInParallel]` they carry for it.
- `MachineTokenProvider.ResetForTesting` → **deleted** with the four statics and `Gate`;
  `MachineAuthTests` constructs a `MachineCredentials`.
- `UnauthorizedRetryHandlerTests` → retargets to `UnauthorizedRecoveryHandler` over a fake
  `ICredentialSource`. Every assertion survives: attribution of `applied` versus a peer's rotation, no
  stale header on resend, first response disposed, exactly one extra attempt. The subset driving a real
  on-disk `TokenStore` moves to `TokenStoreCredentialsTests`, and the class stops needing
  `[NotInParallel(nameof(TokenStoreProfileTests))]` — a parallelism win.
- `ObservationHeaderTests` → asserts on the **request** via a recording inner handler instead of
  `client.DefaultRequestHeaders`. Strictly stronger: it can prove the tags on `Anonymous()`, on the
  recovery resend, and on the SignalR options, none of which it can assert today.
- `ServerVersionCaptureHandlerTests` → unchanged; only the handler's installation site moves.
- **New test the design owes:** that no logger provider is registered, so `LoggingHttpMessageHandler`
  cannot write to a stderr that vendors parse.

## Sites that do not simply convert

**`McpFlowResultServer`'s `allowRefresh` (`:369-376`)** is not a retry-policy axis. Its comment: "False
on the borrowed-reviewer capability path. That process has no token store — its HOME is a per-launch
state dir — so a 401 must be surfaced as-is rather than sent into TokenStore, which is the very read
this delivery path exists to avoid." `borrowed` ⟺ `submitUrlOverride is not null` ⟺
`allowRefresh == false` ⟺ *the client has no credential*. Give that path `Anonymous()` and there is
nothing to rotate — the invariant becomes structural instead of a flag that can be passed wrong. Note
`:131` also targets a different host (`capabilityBase`), so it is not merely an auth variant.

**`McpFlowsServer:438`** is composed inside `SendWithSettlementRetryAsync` (`:627-716`), which
inspects `IsSuccessStatusCode` and a 409 body and passes a per-attempt token. The settlement loop
stays outside, the handler goes inside the send — the same depth the copy sits at today (`:667`).

**`AppConfig.ResolvedUrlSource` is not the provider's business.** It is read at ~9 diagnostic sites
(`AgentHookPoster`, `ClaudeHookCommand:107`, the Cursor hook, …). `ICapacitorHttpClient` exposes
`ServerUrl` but not its provenance; those sites keep the value AI-2147 threads to them.

**`PermissionRequestCommand.PostAsync` (`:133-136`)** serves our server on one branch and the 127.0.0.1
daemon bridge on the other, selected by `bool authenticated`. Split it into two methods before either
converts.

**`ServerUrlNormalizer.HttpProbeAsync` and `StatusCommand:33`** probe `{candidate}/auth/config` before
a base address exists — `Anonymous()`, absolute URL.

**`WhoamiCommand:88`** is the one site the four names do not cover, and *Redirects* is why. It needs
no credential handler ("must not mutate auth state"), no redirect (it supplies the bearer itself, and
a followed redirect would strip it — the failure its own comment guards against, for the right reason
even if the stated threat was wrong), and it does want the observation tags it currently attaches by
hand. That is a fifth cell of the {credential handler} × {follow} grid:

```csharp
public const string Bearer = "capacitor-bearer";  // no credential handler, no follow, caller's bearer
```

Two ways out, and this is the design's **one remaining open decision**: register the fifth name, or
leave `WhoamiCommand` on its hand-rolled client. Registering it is ~4 lines and gains Whoami the
observation headers structurally; leaving it keeps the client set at four and one site outside the
layer. Recommendation: register it — a fifth `AddHttpClient` line is cheaper than the standing
exception, and *Observation headers become structural* is otherwise not true.

**`AgentOrchestrator`'s `"Attachments"` client (`DaemonRunner:409`, used at `:3505`)** talks to our
server with a hand-attached bearer and no tags. It becomes `ForBackgroundAsync()`, and that
registration is deleted — **but the call at `:3513` is `GetAsync($"/api/attachments/{id}")`, a relative
URI that works only because `DaemonRunner:409` sets `BaseAddress`.** No capacitor named client sets one
(all 58 sites already pass absolute URLs, and `Anon` must not have one because `/auth/refresh` targets
the minting server), so that URL must become absolute in the same commit or it throws
`InvalidOperationException`.

## Order against AI-2147

**AI-2147's Part 1 first, including step 9's deletion of `PathHelpers.ConfigDir`/`ConfigPath`/
`Config`.** The static HTTP layer does not block that: what blocks it is a *read* of the static, not a
static *method*, and a static utility taking a `ConfigRoot` parameter is exactly AI-2147's own rule.

The overlap is narrow. Of ~25 `PathHelpers` reads in the MCP servers only the 6 inside the duplicated
retry helper are absorbed here; the other ~19 (`CliTelemetry.Initialize`, `MachineId`,
`MachineIdProvider`, reviewer-vendor preference, the `loggedIn` probe) need a root regardless. What
this work absorbs is the 58 *arguments*, not the threading.

Two dependencies to respect:

1. **AI-2147 step 6 must land first.** It deletes `AppConfig.ResolvedServerUrl`, which *is* the ambient
   resolution at `HttpClientExtensions.cs:76` that ~20 sites rely on. Those sites get an explicit URL
   from AI-2147 and then lose it again here. That is the churn, and it is cheaper than the alternative:
   doing HTTP first and rebasing AI-2147 would collide in the ten hook commands and the `Program.cs`
   dispatch, converting them from static to instance twice.
2. **`ObservationHeaderHandler` being synchronous depends on step 6** giving the resolved profile to
   the entry point. If step 6 slipped, the composition root awaits `GetActiveProfileAsync` once and
   everything else holds.

AI-2147 step 5 is already converting command classes to instance classes with primary constructors, so
this work adds a second constructor parameter without touching bodies.

## Work order

1. Delete the dead `Microsoft.Extensions.Http` reference at `Capacitor.Cli.csproj:34`. **Do not touch
   `WatchCommand.cs:21`** — that `using Microsoft.Extensions.DependencyInjection` is load-bearing for
   `AddJsonProtocol` at `:506`, which is an extension method in that namespace. An earlier draft called
   it stray; it is not.
2. `Microsoft.Extensions.Http` moves to `Capacitor.Cli.Core`. Core sets `IsAotCompatible=true`, so the
   trim/AOT analyzers run at `dotnet build` here — cheaper than the doc implied, though still verify a
   Release publish is free of IL3050/IL2026 — the package carries `DynamicallyAccessedMembers` annotations and no
   `RequiresUnreferencedCode`/`RequiresDynamicCode`, and the daemon already publishes clean with
   `AddHttpClient`, but no `AddHttpClient<T>` exists in this repo yet, so typed clients are unproven
   under AOT until this step says otherwise.
3. `ICredentialSource` + the three implementations + `ResolvingCredentialSource`. Fold in
   `cachedProvider` and `MachineTokenProvider`'s cache; delete both `*ForTesting` seams and their 13
   test call sites in the same commit.
4. Make the handlers stateless: `UnauthorizedRecoveryHandler(ICredentialSource)` replaces both retry
   handlers; `ObservationHeaderHandler(version, updateCheckOff)` holds two precomputed strings;
   `ServerVersionCaptureHandler` is already stateless.
5. `AddCapacitorHttp` + the three named clients + redaction. Containers at CLI and app startup; the
   daemon calls the same extension.
6. `ICapacitorHttpClient` + implementation. No call sites yet.
7. Convert C1 (23) and C2 (14). C2 changes behaviour deliberately: the stderr spam stops.
8. Convert C3 + C4 (13) onto `ForHookAsync`. `SessionStartContextFetch` loses its two-client dance and
   its `disposeClients` flag; its other per-attempt semantics are **unaffected and must stay** —
   `HttpCompletionOption.ResponseHeadersRead` (`:60`), the 256 KiB bounded read (`:62-74`), and
   `Retry-After` parsing on 429 (`:76-84`). A resending handler sits below all three.
   **Behaviour change to state in the PR:** hook clients acquire 401 recovery they do not have today
   (`CreateClientWithAuthStatusAsync` hard-codes `autoRetryUnauthorized: false` at
   `HttpClientExtensions.cs:47-48`), so a rotation plus resend must fit inside `CodexHookCommand:540`'s
   2s deadline. Either bound recovery on the hook lane or accept it deliberately.
   **8b.** Delete `EnsureAbsolute`, `ProcessUrlPolicy`, `UrlFailurePolicy` and `Program.cs:88`; add
   `AuthStatus.UnusableServerUrl` and the `ForCommandAsync` throw + top-level catch; convert the guards
   listed in *The unusable server URL loses its process exit*. It lands here because it needs the hook
   verbs to exist (step 8) and nothing later depends on it. `ProcessUrlPolicyTests` and the
   `NativeTestHost`'s two `url-policy-*` modes (`Program.cs:75-93`) go in the same commit: those modes
   exist only because `EnsureAbsolute`'s two branches cannot both run in one process when one of them
   exits, and they drive `CreateClientWithAuthStatusAsync`, which this work deletes anyway. The host's
   other modes stay.
9. Convert C5 (6) and C6 (2); delete the six `SendWithRefreshRetryAsync` copies and `allowRefresh`.
10. Delete `memoryClientFactory` (68), the factory parameters (12), `httpFactory` (6), `disposeClients`
    (9 sites, **checking each vendor's polarity** — Claude and Cursor are inverted).
11. Split `PermissionRequestCommand.PostAsync`; convert `WhoamiCommand`, `ServerUrlNormalizer`,
    `StatusCommand`; fold `ReportVersionCommand:23-27`'s fallback chain into `ServerUrl` (absorbing its
    `AppConfig.NormalizeUrl` at `:35`).
12. `ServerVersionStore` and the rooted `Telemetry` trio — `CliTelemetry`, `TelemetryDeviceId`,
    `TelemetryState` (verified: the three types under `Core/Telemetry/` that take a `ConfigRoot`;
    `TelemetryClient` is separate and is the PostHog typed client above) — become registered
    singletons. **This is a second conversion:** AI-2147 step 5 makes them
    static-with-a-`ConfigRoot`-parameter and this step re-shapes them, which is the double conversion
    the ordering section otherwise avoids. Accept it (few call sites) or have AI-2147 skip them; decide
    before starting. `CliTelemetry` is the awkward one — a static class with 7 mutable statics, a public
    `Reset()`, a `TestSink` and a `ProcessExit` handler (`CliTelemetry.cs:18-39`) — and deserves its own
    commit rather than a bullet.
13. SignalR takes `ICredentialSource`; delete `AddHttpClient("Attachments")`.
14. The foreign typed clients, one commit per platform.
15. Publish; check IL3050/IL2026.

## Traps

- **Handler order is the version-cap contract.** `ServerVersionCaptureHandler` must be registered
  first so it stays outermost and sees the post-retry response. Nothing fails loudly if reordered.
- **Every handler in a registered chain must stay stateless.** The chain is cached and shared for its
  lifetime; a reintroduced mutable field is how one caller's token serves another's request. Token
  state lives on the credential source.
- **Nothing is redacted by default.** `ShouldRedactHeaderValue` ships as "redact nothing", so
  `RedactLoggedHeaders` is mandatory, and the CLI must register no logger provider — hook stderr is
  parsed by vendors as the hook's own result.
- **Never inject a typed client into a singleton.** It captures one `HttpClient` and freezes its
  handler. `TelemetryClient` is exactly this today and must take `IHttpClientFactory` instead.
- **Never register a typed client a second time as a plain service.** It overwrites the link and
  silently injects an unconfigured `HttpClient`.
- **`Anon` must have no credential handler and no base address.** Both are load-bearing: the
  borrowed-reviewer path must not read a token store, and `/auth/refresh` targets the minting server.
- **No platform may share another's named client.** Distinct names are what keeps our bearer and our
  version tags away from GitHub, WorkOS and npm.
- **A client held for hours outlives its handler's lifetime.** The 8 long-lived holders get DNS
  freshness from `PooledConnectionLifetime`, not from handler rotation. Consider
  `MaxConnectionsPerServer` on the primary: `PermissionRequestCommand.cs:142` holds a 10-hour
  long-poll and `McpFlowsServer.cs:74` an infinite one, so a cap would deadlock ordinary traffic
  behind them.
- **`follow` must stay derived from `authenticated`.** Hard-coding them separately is how the
  bearer-stripped-into-401 defect comes back.
- **The daemon *does* register a logger provider** (`DaemonRunner.cs:125`,
  `RollingFileLoggerProvider`), so `RedactLoggedHeaders` is its only protection and it needs
  `AddFilter("System.Net.Http.HttpClient", …)`. The "no provider registered" test is CLI-only.
- **The `Environment.Exit(2)` must die in the same work, not after it.** Leaving `EnsureAbsolute` in
  place while sends move under registered handlers is the worst of both: the exit is then reachable
  from inside a handler chain, where the caller's `catch` is even further away. *The unusable server
  URL loses its process exit* is a step in this work order, not a follow-on ticket.
- **`AuthStatus.UnusableServerUrl` must not be treated as a lapse.** `AgentHookPoster.IsAuthLapsed`
  answers "would this 401" and drives a spool-and-retry; an unusable URL will never become postable by
  retrying, and spooling for a URL that cannot be parsed grows a backlog nothing drains. Route it to
  the same disposition the `IsPostable` guards produce today — the `UnusableUrlDiagnostic` line, which
  is why `CapacitorServer` carries `Source`.

## What this deletes

68 `memoryClientFactory` occurrences · 12 client-factory parameters · 6 `httpFactory` · six ~28-line
retry copies (~170 lines) · `allowRefresh` · `disposeClients` and its inverted-polarity bug ·
`autoRetryUnauthorized` · the redirect boolean · `UnauthorizedRetryHandler`'s `_current` · `cachedProvider` +
`ResetProviderCacheForTesting` · `MachineTokenProvider`'s four statics + `ResetForTesting` (**the `Gate` stays** — it is the
single-flight the class exists for; the statics become one immutable snapshot swapped with
`Volatile.Write` and read lock-free, with the gate retained around the mint only) ·
the base-URL argument at all 58 sites, **and** the `ConfigRoot` argument AI-2147 step 5 adds at the
same sites · `ReportVersionCommand`'s hand-rolled fallback chain ·
`AddHttpClient("Attachments")` · 30 `new HttpClient` sites reduced to two loopback cases · one dead
package reference · `EnsureAbsolute` and its `Environment.Exit(2)` · `ProcessUrlPolicy` +
`UrlFailurePolicy` + the `Program.cs:88` assignment · ten hand-rolled `IsPostable` guards and the two
predicates that only delegate to it · the `NativeTestHost`'s two `url-policy-*` modes.

Added: `AddCapacitorHttp`, three handlers, `ICredentialSource` + three implementations,
`ICapacitorHttpClient` + implementation, six typed clients, one test double. ~40 production files and
~19 test files touched.

## Open questions

1. **Should the `http://`-misconfiguration defect be fixed first, separately?** See *Redirects*. It
   is live today, it burns a refresh token per call, and it is a few lines — a 3xx on an authenticated
   client should print "server_url is http:// but the server redirects to https://" and stop.
2. **`AgentOrchestrator`'s `"Attachments"` client** currently escapes both the choke point and the
   observation headers. Folding it in changes what the server observes from the daemon. In scope?
4. **`HttpConnectionOptions.Headers`** is unverified against the pinned SignalR version. Confirm before
   promising observation coverage on the hub.
5. **The container on the hook path.** A `BuildServiceProvider` per agent tool call is new cost against
   a 3.5s budget (`HookBudget.cs`, narrowed to 2s at `CodexHookCommand:540`). Registrations are lazy;
   if it shows, a hook resolves two services and can skip the container. Accepted, flagged.
6. **`Capacitor.App`** has one HTTP composition site (`WizardComposition:63`). Does it get a container,
   or just the two clients it needs?

## Settled — do not relitigate

Each of these cost a round of review or an experiment. A fresh reader looking at the same code will be
tempted to reopen them; the evidence is here so they don't have to.

**DI, `IHttpClientFactory` and typed clients are a project constraint, not a preference.** They are the
foundation everything else builds on. If something appears impossible on top of them, that is a
discussion, not grounds for replacing them with a bespoke abstraction. An earlier draft proposed a
hand-rolled factory and a private container; both were rejected.

**`IHttpMessageHandlerFactory` is not needed.** The docs prescribe that escape hatch for *scope-related*
handler state — "DO NOT cache any scope-related information … inside `HttpMessageHandler` instances".
Our credential source is a process **singleton**, so the hazard does not apply and the chain can be
registered declaratively.

**One authenticated chain, not five.** The five branches at `HttpClientExtensions.cs:98,118,122,128,142,144,153,159`
are token-plumbing outcomes, not pipelines. An earlier draft concluded a registered chain could not
express them; that was wrong. See *Why one authenticated chain, not five*.

**401 retry is no longer per-client, and that is not a reversal of the earlier decision** to keep it so
— it removes the state that had to be placed anywhere. `applied` becomes a local read at send time and
is still what is passed back as `refused`, so the "never blame a peer's freshly refreshed credential"
rule is intact.

**Two claims from earlier drafts were wrong and are retracted.** Both are easy to re-derive from the
same surface evidence, so they are recorded rather than deleted:

1. *"The six MCP retry copies replay a stream body because they lack `CanResend`."* They do not. Every
   `send` lambda builds its content **inside** the closure (`McpMemoryServer.cs:178-183`), so a retry
   rebuilds the request. Caller-side retry is strictly *more* capable than `CanResend`.
2. *"`disposeClients` has inverted polarity in Claude and Cursor, so they dispose injected clients and
   leak their own."* Both polarities are correct — the seven default to a factory that mints a client,
   Claude and Cursor default to handing back the hook's own caller-owned one, as
   `CursorHookCommand.cs:584-586` states outright.

**`WatchCommand.cs:21`'s `using Microsoft.Extensions.DependencyInjection` is load-bearing** for
`AddJsonProtocol` (`:505`), which is an extension method in that namespace. An earlier draft called it
stray and would have broken the build.

**`Capacitor.Cli.csproj:34`'s `Microsoft.Extensions.Http` reference genuinely is dead** — the only
`AddHttpClient` in `src/` is `DaemonRunner.cs:409`.

**The redirect experiment is reproducible, not stored.** Two loopback `HttpListener`s, a 307/302 from
one to a chosen `Location`, a client with `AllowAutoRedirect = true` and a bearer, plus a control
request with no redirect to prove the harness observes the header at all. Result table in *Redirects*.
Re-run it rather than trusting the table if the framework version moves.

## Related

- `docs/superpowers/specs/2026-08-21-ai2147-config-dir-explicit-context-design.md` — the config root
  this layer carries, and the precedent for turning a frozen static into injected context.
- `docs/superpowers/specs/2026-08-18-ai2009-daemon-paths-explicit-context-design.md` — the same move
  for `DaemonStore`.
