# GitHub App installation token lifecycle for a shared broker

Research date: 2026-09-07. Scope: GitHub.com primary documentation and GitHub's
public OpenAPI description. No credentials or live installations were inspected.
Broker recommendations below are design inferences, not GitHub guarantees.

## Issuance, expiry, and scope

The app signs an `RS256` JWT with its private key. GitHub recommends the client ID
as `iss`, an `iat` 60 seconds behind the current time, and an `exp` no more than
10 minutes ahead. The JWT authenticates app-level operations; its expiry is
separate from the installation token's expiry. Source:
[Generating a JWT](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app).

`POST /app/installations/{installation_id}/access_tokens`, authenticated with the
app JWT, creates an installation token. Tokens expire after one hour. An expired
token returns 401; renewal means creating another token through the same endpoint,
not using an OAuth refresh token. Source:
[Create an installation access token](https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app).

The issuance request can restrict `repository_ids` or `repositories` to at most
500 repositories and supply an explicit `permissions` map. Restrictions cannot
exceed the installation's repository access or the app's granted permissions.
Omitting repository restrictions grants access to every repository available to
the installation; omitting permissions grants the app's granted permissions.
The response includes `expires_at`, permissions, and repository information when
applicable. GitHub is rolling out longer installation tokens; a fixed 40-character
assumption is invalid. Source:
[Generating installation tokens](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).

**Broker inference:** keep the private key and app JWT at the proxy. Issue only
explicit repository IDs and a broker-owned permission profile. Reject an empty
repository selection before contacting GitHub. Treat tokens as opaque bearer
secrets and return their actual expiry. Cache and renew by tenant binding,
installation, repository set, permission profile, and binding generation; a
single installation-only cache could cross tenant or purpose boundaries. Renew
before expiry with a small clock margin and coalesce concurrent requests. GitHub
does not document a caller-selected shorter expiry in the issuance contract.

## Installation selection and authority

GitHub warns that an `installation_id` in a setup URL is spoofable. It directs
apps to obtain a user access token and check that the installation is associated
with that user. A valid OAuth state binds the browser transaction but is not, by
itself, proof that the submitted installation belongs to the user. Source:
[About the setup URL](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/about-the-setup-url).

`GET /user/installations` lists installations accessible to the authenticated
GitHub App user token through read, write, or admin access. It is not an
installation-owner list. `GET /user/installations/{installation_id}/repositories`
lists repositories that user can access within the installation and includes the
user's repository permissions. Both lists are paginated. These endpoints support
GitHub App user access tokens; an identity-only OAuth credential is not an
interchangeable credential type. Source:
[Installation and repository discovery](https://docs.github.com/en/rest/apps/installations#list-app-installations-accessible-to-the-user-access-token).

A user access token is restricted to the intersection of the app's and user's
permissions and resources. Installation tokens instead act independently of the
installing user; an organization installation can continue working after that
person leaves. Sources:
[User token access](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app),
[GitHub App independence](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/differences-between-github-apps-and-oauth-apps).

**Broker inference:** distinguish three decisions: the caller administers this
Capacitor tenant; the GitHub installation belongs to the registered App and is
accessible to the verified GitHub identity; the identity may delegate the chosen
repositories to that tenant. A positive installation-list result establishes only
the second decision. Bind numeric account, installation, and repository IDs, plus
the explicit repository grant, in proxy-owned state. Do not accept a tenant's
asserted installation ID as the authorization record.

If product policy requires organization-owner approval, GitHub provides membership
state and role through `GET /user/memberships/orgs/{org}`. This endpoint supports
App user tokens with organization `Members: read`; a pending membership is not
active membership. GitHub's membership documentation defines `admin` as the
organization-owner role. Sources:
[Authenticated-user organization membership](https://docs.github.com/en/rest/orgs/members#get-an-organization-membership-for-the-authenticated-user),
[Organization membership roles](https://docs.github.com/en/rest/orgs/members#set-organization-membership-for-a-user).
Requiring that permission and owner role, or permitting repository administrators
to delegate individual repositories, is a product policy choice that must be
decided explicitly. Neither GitHub discovery nor a Capacitor tenant-admin role
automatically establishes the other authority.

## Revocation and changing grants

| Change | GitHub behavior | Broker consequence (inference) |
| --- | --- | --- |
| Individual token revocation | `DELETE /installation/token` invalidates the token used to authenticate that request. It needs an installation token, not the app JWT. [Revoke a token](https://docs.github.com/en/rest/apps/installations#revoke-an-installation-access-token) | Keep enough protected state to revoke each outstanding tenant lease on disconnect. Revoking one lease must not break another tenant's shared token. |
| App uninstalled | Associated installation tokens are deactivated. [Credential revocation reference](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/github-credential-types) | Disable the binding and discard all its cached tokens. |
| Installation suspended | API and webhook access to that account is blocked. [Suspend an installation](https://docs.github.com/en/rest/apps/apps#suspend-an-app-installation) | Deny issuance until current GitHub state confirms the suspension was removed. |
| Repository removed | Existing installation tokens lose access to the removed resource. [App/OAuth comparison](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/differences-between-github-apps-and-oauth-apps) | Reconcile repository grants and invalidate affected leases. |
| App permissions reduced | Removed permissions take effect immediately. [Modify App permissions](https://docs.github.com/en/apps/maintaining-github-apps/modifying-a-github-app-registration#changing-the-permissions-of-a-github-app) | Recheck the required permission profile; do not interpret a cached issuance response as current authority. |
| App permissions increased | Each installation must approve additional repository/organization permissions; the existing grant remains until acceptance. [Approve permissions](https://docs.github.com/en/apps/using-github-apps/approving-updated-permissions-for-a-github-app) | Expose missing-permission state and mint again after approval; do not assume registration changes updated every installation. |

If a tenant binding is removed only within Capacitor, GitHub has not itself revoked
the bearer credential. **Inference:** stopping future issuance and deleting local
cache entries cannot recall a token already delivered to the tenant. Successful
per-token revocation, upstream installation changes, or expiry ends its GitHub
access. A shorter broker lease only controls cooperating callers; it does not
shorten GitHub's token lifetime.

## Lifecycle notifications and reconciliation

GitHub's public OpenAPI schemas define these actions:

- `installation`: `created`, `deleted`, `new_permissions_accepted`, `suspend`,
  `unsuspend`.
- `installation_repositories`: `added`, `removed`.
- `installation_target`: `renamed`.

Source: [GitHub's OpenAPI description](https://github.com/github/rest-api-description/blob/main/descriptions/api.github.com/api.github.com.json),
the `webhook-installation-*` schemas. GitHub Apps receive installation and
installation-repository events by default. `repositories_removed` can be empty
when repository selection changes from `all` to `selected`; applying only the
listed removals is therefore incomplete. User authorization revocation is a
separate `github_app_authorization` event and does not uninstall the app. Source:
[Webhook events and payloads](https://docs.github.com/en/webhooks/webhook-events-and-payloads#installation).

GitHub does not automatically redeliver failed deliveries. Source:
[Handling failed deliveries](https://docs.github.com/en/webhooks/using-webhooks/handling-failed-webhook-deliveries).
**Broker inference:** authenticate webhook bodies, deduplicate delivery IDs,
invalidate affected leases, and reconcile current installation/repository state.
Webhooks accelerate reconciliation; issuance and periodic reconciliation must
also cover missing deliveries. Unknown authorization state must not extend an
existing positive grant. A rename should update display labels, not transfer a
binding to another numeric account.

## Limits to carry into the implementation design

- GitHub supplies no tenant concept. Delegation approval, binding exclusivity or
  sharing, tenant authentication, and disconnect semantics belong to the broker.
- Installation access does not implement per-user access checks for desktop PR
  reads. Preserve the separate linked-identity repository gate described in
  [linked GitHub access research](github-linked-pr-access-research.md).
- The cited docs do not give an end-to-end propagation SLA for permission changes,
  guarantee webhook order, or promise atomic authorization and content reads.
  Define bounded cache lifetimes and fail-closed revalidation behavior explicitly.
- The lifecycle schema has an acceptance notification for increased permissions;
  it does not justify assuming a corresponding permission-reduction notification.
- GitHub.com findings do not establish support across GitHub Enterprise Server
  versions. No live token issuance, approval, revocation, or cross-tenant isolation
  test was performed in this research.
