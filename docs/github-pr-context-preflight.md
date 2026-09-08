# Desktop PR context: deployment preflight

## Result

**The requested live credential/API controls pass for the approved App fixtures.**

Allowed non-admin and denied-user permission checks now both pass with matching
numeric IDs, alongside the successful private-read capability probe. Team grant
provenance is operator-attested; the allowed fixture has Write, not least-privileged
Read. This is not a deployed tenant integration or end-to-end session-admission test.
The [App credential proposal](superpowers/specs/2026-09-07-github-app-pr-credentials-design.md)
consolidates this evidence and defines the acquisition/renewal path. The user
accepted it and the stated evidence qualifications, waived independent review,
and authorized implementation.

## Initial PAT probe

The approved current deployment was probed on 2026-09-07, running Capacitor 0.11.39,
using the approved `kurrent-io/kcap-cli` public and `kurrent-io/kcap-server` private
repository fixtures. The integration is a **fine-grained PAT** from encrypted server
settings (`Integrations:GitHub:Token`). Only the operator's current linked identity
was tested; no other test identities were selected implicitly.

| Check | Observed result |
| --- | --- |
| Current verified GitHub holder → numeric-ID lookup | HTTP 200; returned ID matched. |
| Public/private repository identity and visibility | HTTP 200; both matched the fixtures. |
| Repository-permission query for current linked user | HTTP 200 on both; matching user ID and `admin` permission. |
| Public PR overview query | HTTP 200, no GraphQL errors; reported cost 1. |
| Private PR overview query | HTTP 200 with partial data and one FORBIDDEN error at `repository.pullRequest.commits.nodes`. Not a successful complete overview. |
| Bounded PR discussion queries | HTTP 200, no GraphQL errors for both fixtures; reported cost 1. This does not establish long-thread pagination or nonempty coverage for every collection. |
| Private check-suite endpoint | HTTP 403; `X-Accepted-GitHub-Permissions` required `checks=read`. No Retry-After; primary budget remained available. |
| Private legacy commit-status endpoint | HTTP 403; required `statuses=read`, also without rate-limit evidence. |
| Private commit metadata HEAD | HTTP 403; required `contents=read`. No commit body was requested. |
| Private commits-only GraphQL control | FORBIDDEN at the same commits connection without any check/status fields. |
| Primary limits | REST and GraphQL reported 5,000; remaining budgets stayed above the design's 20% yield threshold. |

The linked-user permission-check approach is supported by this credential for the
tested administrator. It is **not yet verified** for a team-only reader or denied
user. The initially tested token was incompatible with the complete proposed
private-PR reader: Checks and Commit statuses reads were unavailable, and its
GraphQL commit connection was forbidden independently of those fields. Contents read is required
by the commit-metadata endpoint and is the permission to verify for that connection.
A cost of 1 for a partially denied query does not certify the final fully authorized
query's cost; remeasure it after the permission correction.

## GitHub App permission verification

The operator selected the existing GitHub App and updated both its requested
permissions and the installation grant. A protected probe at 13:22 UTC on
2026-09-07 confirmed the deployed auth proxy's configured App is
`kurrent-capacitor`, installed on `kurrent-io`, not suspended, with repository
selection `all`. Both App metadata and installation metadata report **read** for
Metadata, Contents, Pull requests, Issues, Checks and Commit statuses.

This was two successful GETs (`/app`, `/orgs/kurrent-io/installation`) from inside
`kcap-system/auth-proxy`, using its existing App signing key for authentication.
No key or JWT was returned to the agent or saved in artifacts; the JWT was used
only to authenticate to GitHub. No installation token was created. Temporary
binaries were removed. The proxy image was
`kurrentplatform/kcap-auth-proxy:0.0.0-main.20260903.7fbf43a6`; tenant server remained
0.11.39. Neither metadata response supplied primary-rate-limit headers, so these
reads do not establish the installation token's REST/GraphQL budgets.

**App metadata alone is not a complete preflight.** The token probe below verifies
actual reads for the operator. Installation-wide `all` selection is not permission
to widen the diagnostic's targets; the issued token was restricted to the two
approved repositories and six read-only permissions.

Source inspection shows `GitHubApiClient` already signs App JWTs and exchanges them
for installation tokens. `TenantMatching` caches authentication-use tokens for
50 minutes and obtains a new token on a cache miss. This is not a tenant PR-read
integration: `GitHubCredentialProvider` still reads `Integrations:GitHub:Token`
(an opaque configured token, not a PAT-only API) without acquisition or renewal,
and the auth proxy has no tenant-scoped PR-read/token-broker route. App permission
changes do not convert or replace the server's PAT. No server setting was switched.
A WorkOS tenant must not be registered as a GitHub-auth tenant merely to obtain App
access; those registrations affect authentication and tenant discovery.

## Protected App-token read probe

The operator explicitly authorized temporary token issuance and revocation. At
14:09 UTC on 2026-09-07, the same protected auth-proxy environment created **one**
installation token, with exactly the six read-only permissions and only `kcap-cli`
and `kcap-server`. Its returned permissions and `/installation/repositories` were
validated before any PR read. Reported expiry was 59 minutes. Before issuance, a
separate guarded read-only tenant transaction refreshed the operator's current
`users.github_id`; it read no tracker credential or Data Protection key.

| Check | App-token result |
| --- | --- |
| Holder ID lookup and repository-role check | HTTP 200; matching numeric IDs and `admin` on both repositories. |
| Public overview, `kcap-cli#803` | Complete query succeeded without GraphQL errors. |
| Private overview, `kcap-server#1834` | Complete query succeeded without GraphQL errors, including the commit/check connection denied to the PAT. |
| Commit metadata and commits-only query | HTTP 200 and no GraphQL errors on both repositories. |
| Suite-scoped latest runs | All enumerated: 5 runs across 5 public suites; 21 runs across 6 private suites. Run head/suite identities matched; head rechecks were unchanged. |
| Legacy statuses | HTTP 200 on both: one public status, zero private statuses on the selected heads. Private permission is verified, not a populated private-status case. |
| Public conversation/reviews/threads | Populated cursor reads succeeded; public reviews and threads remained partial after the three-page sampling cap. |
| Private discussion, `kcap-server#1831` | Four conversation comments across two pages, five submitted reviews across three pages, three threads across two pages; all reached their connection ends with no duplicate sampled IDs. |
| Nested thread-comment cursor | Public fixture: three published comments across two pages, parent PR/repository identity matched. No qualifying long private thread was encountered in the bounded sample. |
| GraphQL cost | 22 successful queries, each cost 1; no GraphQL errors. |
| Quota snapshots | REST and GraphQL reported 12,500; all observed remaining values were at least 12,478, above 99.8%. These are snapshots, not a sustained background-interference benchmark. |
| Token cleanup | `DELETE /installation/token` returned HTTP 204; revocation confirmed. Temporary pod files removed. |

The run made **53 GitHub requests**: 51 reads, one token exchange and one revocation.
No repository data, membership, permissions, deployment or server credential setting
was changed. Tokens, App keys, PR bodies and diffs were not returned to the agent or
written to artifacts. The token was used only inside the proxy for GitHub requests.
Neither a production PR endpoint nor an automatic tenant credential/renewal bridge
was implemented by this diagnostic.

These results resolve the App credential's tested read-capability blocker. That
operator-only run did not exercise the further permission controls recorded below,
complete large manifests, requested-team visibility, or a sustained four-reader workload. The small paging
samples demonstrate live cursor behavior, not provider snapshot isolation.

## Designated private writer control

At 14:48 UTC on 2026-09-07, the operator-designated team member was checked against
private `kcap-server`. The operator described a team Write grant. A new temporary
installation token was restricted to **Metadata: read** and that single repository;
both scope restrictions were verified. No PR or code-content request was permitted.

Username lookup, numeric-ID lookup, private repository metadata and the permission
endpoint all returned HTTP 200. The permission response matched the designated
numeric ID but reported **`admin`**, not the expected `write`. The existing Write
grant may still be present; this endpoint does not identify which grant supplies
the effective higher role. No team membership, organization ownership or other
access source was independently audited.

This confirms a metadata-only token can perform the permission lookup. It does
**not** validate the intended non-admin/team-only control. The diagnostic stopped
on its expected-role assertion, not a GitHub authentication or API failure. It also
was not a Capacitor link/session-admission test for this fixture account.

Nine requests were made: seven reads, one token exchange and one revocation.
Revocation returned HTTP 204; temporary pod files were removed. No content, credential
or participant identifier was included in the sanitized report. The fixed named
fixture is retained only in the ignored local probe source and working notes.

## Paired non-admin and denied-user controls

At 15:13 UTC on 2026-09-07, two newly operator-designated accounts were checked
against private `kcap-server`, using one temporary token restricted to **Metadata:
read** and that repository. The operator described the positive fixture as an
team member, with a team Write grant, and the negative fixture as external
with no repository access. Named access details remain in ignored local artifacts
and working notes, not this public report.

| Control | Observed result |
| --- | --- |
| Positive fixture | Username and numeric-ID lookups succeeded; permission endpoint returned HTTP 200, matching user ID, `permission=write`. The read-role predicate admits it without administrator privilege. |
| Denied fixture | Both identity lookups succeeded; permission endpoint returned HTTP 200, matching user ID, `permission=none`. The read-role predicate denies it. No missing-identity or 404 fallback was needed. |
| Scope and privacy | Private repository identity/visibility and exact single-repo, metadata-only token scope verified. Zero PR/code-content requests, including for the denied fixture. |
| Cleanup | Token revocation returned HTTP 204; temporary pod files removed. |

Both control assertions passed. This run made **13 requests**: eleven reads, one
token exchange and one revocation. It validates the expected positive/negative
GitHub permission API shapes, not the fixture accounts' Capacitor link/session
admission. Team provenance was supplied by the operator, not independently audited;
no exclusive-team or least-privileged Read-role claim is made.

## Operator action and remaining gate

**Checks is not a grantable fine-grained PAT permission.** The operator's token
editor has no such option, and GitHub explicitly lists calling the Checks API as a
[fine-grained PAT limitation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-tokens-limitations).
The response header `checks=read` identifies the endpoint's requirement; it does not
prove the current token type can be granted that permission. The instruction to add
Checks to this PAT was incorrect. Actions and Code quality are separate APIs, not
replacement permissions for arbitrary check runs.

Contents and Commit statuses **read-only** remain valid permissions to enable/approve
on the existing PAT and retest. They do not remove the Checks API limitation.

Full check-run support needs a supported credential path, preferably a GitHub App
installation with [Checks read](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-checks)
and managed token renewal. A classic PAT with `repo` scope is documented for private
check reads, but is materially broader and is not a silent substitute. Reusing an
existing App was selected by the operator; replacing server credentials or shipping
without check runs is not implied by that permission update. The App's read
capabilities and the paired private permission controls now pass their live probes.

The operator updated the App and installation grants; the agent made no permission,
credential-setting or deployment changes. Do not paste tokens or keys into an agent session.
Do not weaken the approved per-caller repository gate as a workaround.

The requested allowed/denied API controls are complete using the operator-provided
fixtures. A literal least-privileged Read role and exclusive team-grant provenance
were not demonstrated; retain that qualification in consolidated sign-off rather
than relabelling the observed Write role. Large manifests, churn/isolation behavior,
requested-team visibility and sustained multi-reader scheduling were not certified
by these bounded capability probes.

The [credential addition](superpowers/specs/2026-09-07-github-app-pr-credentials-design.md)
accepts these capability/role controls for implementation planning,
with the stated qualifications and final broker/end-to-end/load tests required
before rollout. The user approved this architecture and qualification. The deployed
tenant still reads its configured PAT; the auth proxy's existing authentication
cache is not a tenant PR-read bridge. A one-off installation token is not a production
configuration or renewal procedure. No extra membership or repository permission
changes are required merely to collect another fixture.

## Safety and reproducibility

The throwaway .NET probe ran inside the existing server container. It read only
one encrypted setting, the existing key ring and the operator's holder identity.
Every database read followed `SET TRANSACTION READ ONLY` and a successful
`SHOW transaction_read_only` guard. The transaction was rolled back. Key generation
was disabled and the in-memory key repository rejected writes. It did not start the
server application or invoke startup migrations.

Data reads used fixed `api.github.com` origins, redirects disabled, read-only
GET/HEAD and GraphQL queries, bounded response sizes/timeouts, pacing, and rate-limit
stop guards. Each App-token probe additionally allowed its one authorized token
exchange and revocation; no repository-write operation was allowed. Output was restricted to status codes, approved fixture
identity checks, permission categories, whitelisted error paths and rate counters.
No token, connection string, key material, PR body or diff was returned to the
agent or local artifacts; the token was used only for in-pod GitHub authentication. Temporary uploaded probe binaries were removed after every invocation.
No deployment, daemon, application source, server settings or GitHub repository data changed.

Two initial attempts stopped at the database read-only guard, before reading the
credential or making GitHub calls: the connection startup option alone resulted in
`transaction_read_only=off`. Explicit transaction mode fixed the probe without
removing that guard. Three subsequent invocations made 29 bounded GitHub requests
in total (11, 8, 10); no write API was called. The diagnostic compiled without
warnings and its local self-test passed, but it is not production implementation.

Sanitized runtime reports and the throwaway probe source are retained locally under
`.superpowers/preflight/2026-09-07/`, ignored by Git. The reports are first-party
observations from the deployment/GitHub APIs, not inferred from documentation.
The App-permissions probe source and sanitized report are under the `app-permissions/`
subdirectory, with a separate SHA-256 manifest. Its local signing/path/permission
projection self-test passed and it built without warnings. App-token data-probe
source and reports are in `app-data/`, including the identity-only probe source and
a separate hash manifest. Both helpers built without warnings and passed local
self-tests; the token probe's test includes revocation after a rejected grant.
The metadata-only role probe is archived in `app-role/`; it built warning-free and
passed local scope, identity and rejected-grant cleanup self-tests. Its live expected
Write-role assertion did not pass because GitHub returned Admin.
The successful paired controls are archived separately in `app-role-pair/`, retaining
the earlier Admin-mismatch probe and its original source unchanged. The paired helper
built without warnings and passed local identity, scope and revocation self-tests.
Across all probes, GitHub requests total **106**: 100 reads, three token exchanges and
three successful revocations. The initial recorded desktop test baseline is unchanged;
no new production app tests were run.

Related documents:

- [Approved design](superpowers/specs/2026-09-07-desktop-pr-context-design.md)
- [Linked-identity research](github-linked-pr-access-research.md)
