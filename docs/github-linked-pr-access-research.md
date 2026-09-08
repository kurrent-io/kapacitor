# Using linked GitHub identities for desktop PR reads

Research date: 2026-09-07. The user approved the linked-identity permission-check
approach; its current policy is in the [PR context design](superpowers/specs/2026-09-07-desktop-pr-context-design.md).
This note records the evidence and limitations, not implementation sign-off. No
production code or credentials were changed.

## What account linking supplies

The server can identify the caller's verified GitHub account, but the existing
linking flow does not supply a reusable per-user repository credential:

- The explicit proxy linking flow requests `read:user` and proves identity.
  Source: `kcap-server/src/Capacitor.AuthProxy/LinkGitHubHandler.cs`.
- The proxy exchanges the OAuth code and reads `/user`, then sends the tenant a
  signed `LinkCompletionPayload` containing purpose, numeric GitHub ID, login,
  avatar, tenant origin, nonce, and expiry. It contains no GitHub access or refresh
  token. The link branch does not use the native sign-in token cache.
  Source: `kcap-server/src/Capacitor.AuthProxy/CallbackHandler.cs`.
- The tenant validates that payload and calls `LinkAsync` with identity/profile
  fields. `GitHubLinkService.GetStateAsync` reads holder/link/merge state rather
  than token material.
  Sources: `kcap-server/src/Capacitor.Server/Auth/GitHubLink/GitHubLinkEndpoints.cs`
  and `GitHubLinkService.cs` in that directory.
- Automatic WorkOS linking can supply only the numeric GitHub ID: username/avatar
  can be absent. Username text is therefore not a reliable identity key.
  Source: `kcap-server/docs/AUTHENTICATION.md`, GitHub account linking section.

The link is useful as proof of **who**, not proof of access to **which repository**.

## Feasible option: identity plus a repository-permission check

Keep the existing server integration token for fetching data, but make a GitHub
permission decision for the linked user a prerequisite for serving it.

GitHub documents:

1. [`GET /user/{account_id}`](https://docs.github.com/en/rest/users/users#get-a-user-using-their-id)
   resolves a durable numeric account ID to its current public profile/login. The
   documentation explicitly distinguishes the durable ID from a changeable login.
   Enterprise Managed User visibility can cause a 404 for an unauthorized token.
2. [`GET /repos/{owner}/{repo}/collaborators/{username}/permission`](https://docs.github.com/en/rest/collaborators/collaborators#get-repository-permissions-for-a-user)
   returns the user's calculated base repository permission and role, considering
   repository, team, organization, and enterprise grants. Legacy permission values
   are `admin`, `write`, `read`, and `none`; maintain maps to write and triage to
   read. The response includes the target user, including numeric ID when present.
3. The permission endpoint supports GitHub App installation tokens, App user
   tokens, and fine-grained PATs, with **Metadata: read** for fine-grained tokens.
   Source: the endpoint's fine-grained token section at the same official URL.

Private-PR admission sequence:

- Authenticate the Capacitor caller and retain the existing Full-session,
  repository visibility, and linked-PR identity gates.
- Resolve the caller's current verified GitHub identity. Use the current holder
  state, not a stale login claim or a pending identity-merge marker. GitHub-native
  users already have an identity; WorkOS users use the existing linking flow.
- Resolve the current login from the numeric ID; do not infer it from the server's
  generic display-name/username field.
- Ask GitHub for that user's permission on the PR's base repository, using the
  existing integration credential. Require a matching returned user ID and an
  explicit read-capable permission. Unknown, absent, malformed, denied, or failed
  evidence does not authorize the read.
- Apply the authorization gate before any detail response, including cached
  content and continuation pages. Positive evidence may only be reused within a
  short, explicitly bounded validity window; never reuse stale permission on an
  upstream failure. Unlink/account changes invalidate evidence and client bodies.
- No link produces a **Link GitHub to read PR details** action using the existing
  browser flow. Linking by itself still does not grant repository access.

This avoids manual per-repository user allowlists and avoids adding per-user token
storage or broadening the existing identity-only OAuth flow for v1. The integration
still needs sufficient access to read the underlying PR/check/comment content.
An independent feature switch could disable interactive reads without disabling
tracker enrichment.

### Limits and deployment validation

- This is a server-enforced **repository-role check**, not a GitHub request made
  with the caller's token. Do not describe it as full per-user GitHub session/SSO/IP
  policy parity or native GitHub audit attribution to the user.
- The permission query and content read are not atomic. State a bounded evidence
  lifetime and revalidation behavior, including revocation during a long fetch.
- Public-repository access needs its own explicit rule: lack of collaborator status
  does not mean a public PR is unreadable. Verify repository visibility from GitHub
  before using publication as evidence; do not derive it from a session label.
- A renamed username must not transfer access to a different numeric account. A
  permission response with a missing or mismatched target user cannot authorize.
- The [deployment preflight](github-pr-context-preflight.md) verified numeric-ID
  lookup and the permission endpoint for the current operator on both approved
  repositories, using the existing fine-grained PAT inside the server container.
  No credential left that environment. That PAT run tested only the operator;
  later App controls below cover a non-admin reader and denied user. Private
  check-suite/status/commit endpoints returned scope-related
  403s. GitHub also explicitly lists Checks API access as a fine-grained PAT
  limitation, not a missing toggle; see the preflight report's primary-source
  links. The role gate works for the tested user, but full checks need a compatible
  credential path or an approved scope change, not Actions/Code quality permissions.
- The operator selected the existing `kurrent-capacitor` GitHub App. Protected
  metadata reads confirm the App and its `kurrent-io` installation now have all six
  required read-only grants. That does not upgrade the server's PAT. The App key
  and token exchange live in `Capacitor.AuthProxy/GitHubApiClient.cs`, with a
  50-minute authentication-use cache in `TenantMatching.cs`; the tracker provider
  only reads its configured token. There is no tenant-scoped PR-read/token-broker
  bridge to reuse as-is. WorkOS discovery deliberately does not use GitHub-auth
  tenant registration, so adding such a row is not a valid credential workaround.
  No installation token was issued or exported by that metadata-only probe.
- The operator then authorized one temporary, repo-restricted App token. It was
  issued and used inside the proxy, proved to have exactly the six read-only grants
  and two approved repositories, and revoked with HTTP 204 afterward. Numeric-ID
  lookup and permission checks returned 200/admin for the operator on both repos;
  complete private overview, commit metadata, suite-scoped checks and status reads
  succeeded. Populated discussion cursor samples succeeded, including a public
  nested thread. REST/GraphQL reported 12,500 limits and every sampled GraphQL query
  cost 1. No token or body was exported. That operator-only run supplied capability
  evidence, not non-admin or denied-user coverage.
- A further protected control used a token restricted to Metadata read and the
  private fixture. The designated account's username and numeric-ID lookups and
  permission response succeeded, with matching IDs, but GitHub reported `admin`
  rather than the operator-described team `write` grant. That does not isolate the
  team's grant or validate a non-admin control. No private content was fetched;
  the token was revoked with HTTP 204. Grant provenance was not independently audited.
- A subsequent operator-designated pair passed the private permission controls:
  the non-admin team-member fixture returned `write`, and the external fixture
  returned `none`. Username/numeric lookups and both permission responses matched
  the intended numeric IDs. A metadata-only, single-repository App token sufficed;
  no content was fetched and the token was revoked with HTTP 204. Team membership
  and grant provenance were operator-supplied, not independently audited; the
  positive role is Write, not least-privileged Read. These are GitHub API controls,
  not end-to-end Capacitor session/link admission tests.
- This introduces permission-query traffic; it belongs in the interactive request
  budget and must not starve background enrichment.

## Stronger alternative: delegated GitHub user tokens

GitHub App user access tokens enforce the intersection of the user's repository
access and the app's installed access/permissions. GitHub explicitly documents that
an app cannot access a repository on a user's behalf if the user cannot access it,
even when the app itself is installed there.

Source: [Authenticating with a GitHub App on behalf of a user](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user).

That is stronger than checking another user's role with an integration token, but
it is not available merely because an account is linked. It needs an explicit
authorization flow plus encrypted server-side per-user token storage, refresh,
revocation, unlink cleanup, and user-partitioned caches. Automatic WorkOS identity
linking alone cannot mint this delegation.

## Recommendation

The approved read-only v1 approach is linked GitHub identity plus a fail-closed
repository-role gate. The design specifies the public-repository case, proof
lifetime and cache/page admission. Server fetches remain fail-closed; the draft gives
already visible client content a bounded, labelled transient-outage display grace,
never a grant to fetch or reveal more content. Deployment validation is partial:
the App's required grants and operator read-capability probes now pass, including
private checks and populated pagination samples. The operator-designated non-admin
Write and denied-user controls now pass with matching identities. The
[credential addition](superpowers/specs/2026-09-07-github-app-pr-credentials-design.md)
consolidates the qualifications and proposes a tenant-scoped broker with managed
renewal. It awaits approval before implementation planning. The existing tracker
provider accepts an opaque configured token but does not obtain or renew App
tokens; it is not inherently PAT-only.
Use delegated user tokens if exact user-to-server authorization is required.
