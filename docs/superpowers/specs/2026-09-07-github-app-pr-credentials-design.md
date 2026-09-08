# GitHub App credentials for the PR reader

## Proposed decision

Add a tenant-scoped installation-token broker to the shared auth proxy. The proxy
keeps the App signing key and issues a repository-restricted read token to an
authenticated tenant server. The server holds the token in memory and performs
the PR queries and linked-user admission checks already specified in the
[desktop PR design](2026-09-07-desktop-pr-context-design.md).

The user authorized proceeding with this addition and explicitly waived its
independent review when the flow integration was unavailable. The earlier review
covers the reader and its access policy; no independent review of this credential
boundary has run. Deployment and secret provisioning remain separate from preparing
and testing the implementation.

The [protected preflight](../../github-pr-context-preflight.md) established that the
selected App can support the reader. Accept the observed non-admin Write role as
evidence of a readable private repository, alongside the explicit None denial.
Team provenance remains operator-attested; least-privileged Read, requested-team
visibility, sustained load, and end-to-end Capacitor admission remain verification
items. No further named-user probes are needed to make the architecture decision.

## Options and trade-offs

| Approach | Consequence |
| --- | --- |
| **Tenant-scoped token broker — recommended** | Keeps the App key in the proxy and PR query/caching logic in the tenant. Introduces a tenant service credential and sends short-lived GitHub tokens over the authenticated server-to-proxy connection. |
| Typed PR read gateway in the proxy | Keeps installation tokens in the proxy too, but moves GitHub query execution and shared traffic scheduling into that service. It adds a second PR read contract and carries private response bodies through the shared proxy. |
| Separate App and signing key per tenant | Avoids a broker and isolates each App's authority. Requires another App installation and protected signing-key lifecycle for every tenant. Copying the shared App key into tenants would instead expose its authority across installations. |

The broker is the smallest addition that preserves the chosen server module and
the existing protected App key. The tenant server becomes a holder of an
hour-lived GitHub bearer credential. The desktop and daemon receive neither that
token nor the broker credential.

## Existing boundaries

Source inspected at `kcap-server` revision
`5fe40a74ce22a0e7753dfba45047a939aeba79b4`:

- `src/Capacitor.AuthProxy/GitHubApiClient.cs` signs App JWTs and exchanges them for
  installation tokens. Its current exchange returns only a token string, with no
  explicit repository/permission narrowing or expiry contract.
- `src/Capacitor.AuthProxy/TenantMatching.cs` caches authentication-use installation
  tokens for 50 minutes. That cache is separate from the proposed reader leases.
- `src/Capacitor.AuthProxy/AdminEndpoints.cs` authenticates its admin group with one
  configured `X-Admin-Key`. A caller supplies the organization and installation in
  the admin request. This establishes no individual tenant identity.
- `src/Capacitor.AuthProxy/WorkOS/M2mApplicationsEndpoint.cs` authenticates a human
  administrator's WorkOS token to provision a machine application. It is not an
  existing unattended tenant-server authentication mechanism for this broker.
- `src/Capacitor.AuthProxy/WorkOS/WorkOSTenantResolver.cs` excludes WorkOS discovery
  results whose hostname appears in the GitHub-auth `TenantStore`. Broker grants
  must therefore live outside that store.
- `src/Capacitor.AuthProxy/WebhookHandler.cs` handles selected installation events.
  It logs permission acceptance and ignores `installation_repositories`; it does
  not yet invalidate PR credential leases.
- `src/Capacitor.Server/WorkItems/Enrichment/TrackerCredentialProviders.cs` reads an
  opaque configured token synchronously. Its shared `ITrackerCredentialProvider`
  contract has no repository argument, acquisition, expiry, or renewal operation.

Deployment sources also keep GitHub-auth registration separate from WorkOS:
`kcap-deployments/charts/kcap-tenant/templates/postsync-register.yaml` runs only for
GitHubApp authentication. The tenant and auth-proxy deployment templates wire
their existing shared secrets. They do not provision the proposed broker identity.

## Tenant identity and installation binding

Provision a random 256-bit broker secret unique to a tenant deployment. The tenant
holds it in its protected deployment secret; the proxy holds a SHA-256 verifier
and a public key identifier. Compare verifiers in constant time. Authenticate
only over HTTPS with normal certificate validation, and refuse redirects. Neither
the shared admin key nor a user session token is accepted by broker endpoints.

The proxy's operator-managed grant contains:

- Public broker key ID, verifier, and immutable tenant deployment ID.
- Enabled state and a monotonic policy revision.
- Explicit GitHub App ID, installation ID, account type, and numeric account ID.
- The fixed `pull-request-read-v1` permission profile.

The binding is an operator-established association between a deployment and an
installation. A hostname, linked GitHub user, WorkOS organization name, setup URL,
or caller-supplied installation ID cannot establish it. GitHub's installation
metadata verifies the configured App/account/installation association; it does
not establish which Capacitor deployment should receive access.

Store grants in a protected proxy configuration file mounted separately from
login configuration. Deployment automation supplies it and the tenant's secret.
No endpoint under the shared-key admin group may create, edit, or reveal broker
grants. Tenant secrets must be delivered only to their own namespace, without a
wildcard reflection to other tenants. A malformed replacement configuration fails
closed for broker reads and emits a sanitized diagnostic.

Support multiple explicit installation bindings for one deployment when needed
for cross-owner sessions. Each request must resolve to exactly one binding. In v1,
an installation can be bound to only one deployment; reject overlapping grants
to keep tenant-local scheduling meaningful. Multiple keys for the same deployment
are allowed during rotation. Multiple independently scheduled server replicas
sharing a binding require coordinated admission before being supported.

No new per-user or per-PR allowlist is introduced. The installation's selected
repository set is the integration's ceiling. A repository outside the configured
installations retains its admitted external link and has unavailable live details.
The linked-user repository-role check remains mandatory inside that ceiling.

## Broker contract and issuance

Use a versioned service-only route family outside `/admin`:

```text
POST /integrations/github/v1/token
POST /integrations/github/v1/validate
```

The bearer credential identifies the deployment. Token requests supply only the
validated GitHub base-repository owner/name and the fixed profile name. They carry
no tenant selector, arbitrary installation ID, upstream URL, permission set, or
GitHub query. Bodies are bounded to 8 KiB. Validation accepts a bounded set of
opaque lease IDs previously issued to that deployment; IDs confer no authority
without authentication and current grant checks.

For issuance, the proxy:

1. Authenticates the deployment and reads its current enabled grant.
2. Resolves the repository's installation using App-authenticated GitHub metadata.
   Verifies the exact configured App, account ID/type, installation ID and absence
   of suspension. It never searches other installations for a working credential.
3. Requests a token for exactly that repository and the explicit read-only profile:
   Metadata, Contents, Pull requests, Issues, Checks and Commit statuses. Even if
   the App later gains write permissions, issued reader tokens remain read-only.
4. Verifies the returned permission set and expiry, then uses
   `/installation/repositories` to verify that the token exposes exactly the
   requested repository, including its numeric repository and owner IDs. Refuses
   extra repositories, stronger grants, missing required grants, or identity drift.
5. Publishes a lease only after validation succeeds. Rejected tokens are revoked
   with an independently bounded cleanup attempt, including on caller cancellation.

If GitHub accepts an exchange but its response is lost, the proxy may not know the
token to revoke. Record an unknown issuance outcome, apply acquisition backoff,
and rely on GitHub expiry for that inaccessible token. Do not claim guaranteed
cleanup or immediately repeat an exchange after an ambiguous transport failure.

The response includes the token, provider expiry, opaque lease ID, policy revision,
canonical repository ID/name, and an opaque stable rate-budget identity for the
installation. Set `Cache-Control: no-store` on all broker responses. Suppress
request/response body logging, bearer headers, and token-bearing exception data
throughout this route and its tenant client.
Validation responses contain evidence and policy/budget metadata, never tokens.

Coalesce token acquisition for the same deployment, binding revision, repository
and permission profile. Use a separate bounded in-memory cache, capped at 256 token
leases across the proxy. At capacity, return an explicit retryable unavailable
result; do not mint a token that cannot be tracked. Reap retired/expired leases.
The existing authentication token cache is never lent to a reader.

Use 401 for invalid broker authentication, indistinguishable 404s for an unknown
or ungranted repository/lease, 400 for invalid input/version, and typed 429/503
results for cooldown or temporary unavailability. The tenant normalizes these as
integration failures; they cannot become a Capacitor sign-in 401 or a user's
GitHub repository denial.

## Renewal and revocation

GitHub installation tokens expire after one hour. Renewal is a fresh App-JWT
exchange, not a refresh-token flow. Use GitHub's returned `expires_at`; never
assume that receipt starts a fresh hour. Begin demand-driven replacement five
minutes before expiry, coalescing callers. An inactive reader causes no periodic
token creation. The proxy's cache uses the same renewal window, so acquisition
cannot repeatedly return the token the tenant is trying to replace. A token with
less than the full 30-second operation budget plus
30 seconds of clock allowance remaining cannot start a request. These are local
scheduling margins, not a change to GitHub's token lifetime.

On transient replacement failure, an existing unexpired token remains usable only
while the broker grant and the independent caller/repository proofs are live.
Once any expires, return unavailable. Respect cooldown; no immediate renewal loop
and no fallback to the configured PAT. A token rejected by GitHub is retired and
cannot be reused while waiting for the next eligible acquisition.

The tenant validates the broker grant before serving any detail, including cache
hits. Positive broker evidence lasts at most 30 seconds and coalesces per binding;
it never outlives the shortest supporting installation/token evidence. The proxy
rechecks installation metadata at that bound under demand and reports its actual
evidence expiry. Validation failure cannot extend evidence. Revalidate before
delivering a long-running response, alongside the existing caller/link/role checks.
Broker unavailability therefore blocks new data after the lease expires, even if
the GitHub token itself still works. Only the main design's already-visible,
bounded transient display grace remains available.

Keep two identities separate:

- **Policy revision** changes on grant, installation, repository-selection,
  permission-profile, Enabled, or broker-key revocation changes. It invalidates
  affected access evidence, bodies, manifests and handles.
- **Token instance** changes on normal renewal with the same verified scope.
  Replacement does not reset rate limits or invalidate the reader's pages. Fresh
  broker and caller proofs remain necessary before delivering cached content.

Signed installation suspension/deletion, accepted-permission changes, and
repository-selection events invalidate affected broker leases. Reconcile live
installation/repository metadata as well; webhooks are an early signal, not the
only revocation mechanism. A replacement installation ID requires a new explicit
binding. An event cannot silently associate another installation with a tenant.
An all-to-selected change may carry an empty removal list; reconcile the effective
scope rather than treating that list as complete. Account renames preserve numeric
identity. Authenticate and deduplicate webhook deliveries, and do not let delayed
events override newer live denial evidence.

Retire replaced tokens after bounded in-flight use; track retained instances until
revoked or expired. Disabling a grant or revoking a broker key stops issuance and
validation immediately at the proxy and attempts revocation of its outstanding
reader tokens. Cleanup failures are recorded without credential material. A proxy
restart loses in-memory leases, so tenants discard unknown leases and reacquire;
untracked old tokens expire at GitHub. This design does not claim immediate
revocation of a token stolen from a compromised tenant: expiry is the backstop if
revocation cannot complete. Shared App-key revocation remains an operator incident
action with effects beyond this reader.

Rotate a tenant broker key by installing a second verifier for the same deployment,
updating that tenant's protected secret, then removing the old verifier. The old
key cannot mutate its binding or provision its successor. Removing it invalidates
its leases. No secret values belong in the spec, issue, logs, or agent transcript.

## Tenant integration and request budget

Introduce an asynchronous, repository-aware PR credential source returning a
totalized lease result: availability, policy revision, budget identity, token
expiry and internal token access. It owns acquisition, validation, renewal and
retirement. PR read code does not implement token lifecycle itself.

Configure `PullRequestReads:CredentialSource` explicitly as `ConfiguredToken`
(compatibility default) or `AppBroker`. AppBroker also requires its HTTPS base URL
and protected service credential. Missing/invalid configuration is unavailable;
there is no automatic discovery or source fallback. The configured-token adapter
reads the existing provider and retains its capability validation; a fine-grained
PAT is still insufficient for full check support.

For v1, the broker supplies the interactive PR reader. Existing enrichment and
reviewer polling retain their configured credential and synchronous interface.
Switching those consumers to App tokens is separate work because it requires
repository-aware asynchronous acquisition across their call sites. This narrows
the credential change without making a second copy of the existing token store.

Use the main design's concurrency, request pacing, priority and 20% quota reserve.
Account App-backed activity by stable installation identity across repositories
and renewals, not by token string or token hash. A configured-token credential has
its own budget identity. Different PAT/App credentials need not share a quota;
actual provider headers remain authoritative for each.

Broker validation includes the proxy's observed installation cooldown/budget state,
including its existing GitHub authentication activity. The tenant reports observed
cooldown and budget exhaustion through the validation lane; reports may tighten a
cooldown, never raise the remaining quota or shorten an active cooldown. Proxy
credential and metadata calls participate in its provider pacing; App-JWT calls
use a separate resource identity when GitHub accounts them separately. A
scope-narrowed token provides no independent installation quota. Keep the four-reader target subject to a
measured whole-installation budget, including broker probes and authentication.
GitHub documents the installation-level primary quota in its
[REST rate-limit reference](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api#primary-rate-limit-for-github-app-installations).

## Delivery and acceptance

This adds auth-proxy and deployment work to the existing server/desktop feature.
Once approved, sequence the implementation around:

1. Broker authentication, protected grant provisioning, scoped token lifecycle,
   webhook invalidation, and tenant-isolation tests.
2. Tenant PR credential source and the admitted server read module, with checked-in
   literal wire fixtures for the desktop contract.
3. Sidebar summary and reader sharing workspace state; preserve Chat/Terminal
   lifetimes and all failure/retention semantics in the approved design.
4. Protected deployment validation through the finished broker, then the real
   tenant endpoints and desktop. Keep provider/feature configuration changes
   separate from preparing and testing the implementation.

Required credential tests cover wrong-tenant key/lease/repository, shared-admin-key
rejection, WorkOS discovery preservation, changed installation/account, an App
with extra write permissions, overbroad or incomplete returned scope, cancellation
after mint, expiry/clock margins, concurrent renewal, cache capacity, broker outage,
revocation during a read, missing webhooks, key rotation and restart recovery.
Verify that token replacement preserves policy-bound pages and installation
cooldowns, while policy changes invalidate them. Tests must also prove that
neither logs nor desktop contracts expose token material.

The live rollout check repeats capability and access validation using the final
broker path inside the protected runtime. Verify renewal without manual token
copying, denied-user/session handling, expiry and failure behavior, requested-team
data, and four active readers alongside background activity. The earlier probes
prove API feasibility and do not substitute for these integration checks.

GitHub facts and lifecycle sources are collected in
[the token research note](../../github-app-token-lifecycle-research.md).
