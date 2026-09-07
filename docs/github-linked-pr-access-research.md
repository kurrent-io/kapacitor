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

Proposed private-PR admission sequence:

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

### Limits and checks before adopting it

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
- The deployed integration token's ability to call the permission endpoint has not
  been tested here. No server token was retrieved. Before implementation sign-off,
  verify it with a public repository and an authorized private test repository,
  including team/base-role access and a denied user. Use only read operations.
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
lifetime and cache/page admission. Deployed-token compatibility remains unverified.
Use delegated user tokens if exact user-to-server authorization is required.
