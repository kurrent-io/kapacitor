# Desktop PR context: deployment preflight

## Result

**Partial verification; implementation planning remains blocked.**

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
user. The current token is incompatible with the complete proposed private-PR
reader: Checks and Commit statuses reads are unavailable, and its GraphQL commit
connection is forbidden independently of those fields. Contents read is required
by the commit-metadata endpoint and is the permission to verify for that connection.
A cost of 1 for a partially denied query does not certify the final fully authorized
query's cost; remeasure it after the permission correction.

## Operator action and remaining gate

The token owner must enable/approve these **read-only repository permissions** for
the intended repositories, or provide a compatible scoped integration credential:

- Checks
- Commit statuses
- Contents, for the specified commit/GraphQL overview path

No scope or credential change was made by this session. Any required organization
approval belongs to the operator. Do not paste the token into an agent session.
Do not weaken the approved per-caller repository gate as a workaround.

After correction, repeat the protected probe and finish the remaining controls:

- Designated team-only reader and designated denied user, including numeric-ID
  matching and expected permission results.
- Full private overview with no GraphQL errors, suite-scoped latest check runs and
  legacy statuses, and actual query cost/headroom.
- Suitable existing populated/paged discussion fixtures to validate connection
  behavior; a one-row query on the latest PR is not a long-pagination test.

The approved design's preflight gate is **not passed** until these are resolved.

## Safety and reproducibility

The throwaway .NET probe ran inside the existing server container. It read only
one encrypted setting, the existing key ring and the operator's holder identity.
Every database read followed `SET TRANSACTION READ ONLY` and a successful
`SHOW transaction_read_only` guard. The transaction was rolled back. Key generation
was disabled and the in-memory key repository rejected writes. It did not start the
server application or invoke startup migrations.

GitHub calls used fixed `api.github.com` origins, redirects disabled, read-only
GET/HEAD and GraphQL query operations, bounded response sizes/timeouts, pacing,
and rate-limit stop guards. Output was restricted to status codes, approved fixture
identity checks, permission categories, whitelisted error paths and rate counters.
No token, connection string, key material, PR body or diff was returned to the
agent or local artifacts; the token was used only for in-pod GitHub authentication. Temporary uploaded probe binaries were removed after every invocation.
No deployment, daemon, application source, server settings or GitHub data changed.

Two initial attempts stopped at the database read-only guard, before reading the
credential or making GitHub calls: the connection startup option alone resulted in
`transaction_read_only=off`. Explicit transaction mode fixed the probe without
removing that guard. Three subsequent invocations made 29 bounded GitHub requests
in total (11, 8, 10); no write API was called. The diagnostic compiled without
warnings and its local self-test passed, but it is not production implementation.

Sanitized runtime reports and the throwaway probe source are retained locally under
`.superpowers/preflight/2026-09-07/`, ignored by Git. The reports are first-party
observations from the deployment/GitHub APIs, not inferred from documentation.
The initial recorded desktop test baseline is unchanged; no new app tests were run.

Related documents:

- [Approved design](superpowers/specs/2026-09-07-desktop-pr-context-design.md)
- [Linked-identity research](github-linked-pr-access-research.md)
