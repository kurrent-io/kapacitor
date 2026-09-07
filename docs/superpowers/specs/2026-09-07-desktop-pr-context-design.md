# Desktop pull-request context

## Agreed outcome

Find the PR, understand its state, and read feedback without first visiting the
Capacitor web UI. GitHub remains the destination for actions and full diff browsing.

The user selected **C: sidebar summary plus a wide reader**, read-only v1, and
server-supplied GitHub data. The user also approved using the caller's **linked
GitHub identity plus a GitHub repository-permission check**, rather than granting
all Capacitor members the integration credential's repository access.

Keep the existing 400px work-context sidebar. Add a compact PR card and a **Pull
request** tab beside **Chat** and **Terminal**. Clicking Checks, Reviews, or
Conversation in the card selects the PR and opens that reader section.

No posting, replying, resolving, requesting/submitting reviews, rerunning checks,
changing draft state, closing, or merging. No local `gh` prerequisite, new desktop
credential store, or new per-user GitHub OAuth grant. The written design and its
independent review must be approved before implementation planning.

## Experience

### Sidebar and PR selection

Place the card after the work-item card, before the linked issue. Preserve the
existing work item, contributors, and session facts.

The card shows repository, number, title, source/target branches, lifecycle state,
checks summary, review decision, section navigation, and last successful fetch.
Lifecycle is Draft, Open, Closed, Merged, or Unknown. Merged takes precedence over
closed; draft is an open PR with its draft flag set. Unknown values never imply
success or closure. Zero checks says **No checks reported**, not Passed. Skipped,
neutral, cancelled, and unknown results retain their actual labels.

Clicking the title opens the reader. Provide copy-number, copy-URL, copied feedback,
and an explicit Open on GitHub action. The card does not claim merge readiness:
check success and approvals do not prove satisfaction of branch protection.

For multiple PRs, show one card plus a repository/number/title selector. Identity is
provider + host + base repository + number, never head repository or number alone.
Collapse duplicate links for that identity. Fork PRs retain their base-repo identity.

The server orders the list by canonical lower-case owner, lower-case repository,
then positive numeric PR number ascending. A client initially selects a unique
match on the session summary's primary repository and its exact head branch. The
primary repository is its unique `is_primary` repository, else its legacy
`repo_owner`/`repo_name` pair; ambiguous/missing primary data means no preferred
match. No match or multiple matches uses the first ordered link. Keep an explicit
user selection across polls, even if another PR matches better later.

Successful empty data from the new list route alone means **No pull request linked**.
Unknown session identity means **Waiting for the session to register…**. Failures,
legacy empty data, and permission denial never mean no PR. If the selected PR is
unlinked, clear its detail state and show unavailable without silently selecting
another PR. Unsupported hosts/providers retain existing safe external links.

### Reader

Use the center column, not an overlay, new window, embedded browser, or another
sidebar. Repeat identity/state and the external action in the header. Include an
expandable PR description and the provenance line **Via the workspace GitHub
integration · access checked for @login**.

| Section | Content |
| --- | --- |
| Checks | Latest head commit's check runs and legacy status contexts, with actual outcome, provider/app, timing when supplied, and an external details link. |
| Reviews | GitHub's PR-wide review decision, current reviewer/request state including team requests, published review bodies, and inline threads. Outdated and resolved are separate states. |
| Conversation | Top-level PR comments, author, created time, edited indication, Markdown body, and comment link. Automated authors are labelled and optionally collapsed, not removed from totals. |

Thread expansion shows the provider's file path, current/original line information,
diff hunk, published comments/replies and timestamps. Do not reconstruct historical
code from the local checkout. File-level comments and outdated comments without a
current line are valid. Superseded published review bodies remain readable.

Each collection has Load more. Later-page failure keeps already loaded rows while
access remains valid, exposes Retry, and does not mark traversal complete. Surface
snapshot time and incomplete-coverage labels. A resolved-thread filter is
server-side and is part of snapshot/cursor identity, not a filter that hides an
entire loaded page without explanation.

No full-file diff browser, check-log viewer, PR search, attachment downloads, or
manual link editor in v1.

### Navigation and native view state

Detecting a PR never changes the active tab. A card-row action explicitly selects
the Pull request tab/section and moves keyboard focus to its heading.

`WorkspaceViewModel` owns one PR view model. It passes that same instance to the
reader and to a child `PullRequests` property on `WorkContextViewModel`; the latter
only hosts it and neither fetches nor disposes it. Workspace teardown is its one
owner. Work-context failures cannot blank PR state; PR failures cannot degrade work
item state.

Keep the existing Chat and Terminal hosts realized while changing visibility and
hit testing. Do not replace them through a tab-content factory. This preserves
chat draft/scroll, terminal buffer/attach, and agent lifetime. The PR host is created
once on first explicit opening and remains realized until workspace teardown.
Selection/expansion/scroll metadata lives in workspace state, independently of
bounded body caches.

For a workspace without PTY support, retain its initial no-terminal explanatory
view and offer a Pull request tab when an admitted link exists. Discovery does not
auto-select it. Clicking the card selects it. Chat/Terminal buttons remain absent;
keyboard cycling includes only enabled tabs. With PR as the only available tab,
cycling stays on it. No-terminal/attach/end banners belong only to the non-PR
content host and never overlay the reader. Ended sessions continue normal PR
polling while visible; existing access resolution, not ended status, admits them.

Expose selected-state tab semantics, labelled copy/refresh/external actions, text
alongside status colors, visible focus, and a polite live region for asynchronous
section completion/failure. Long paths and bodies must wrap or scroll without
changing the sidebar width.

## Data ownership and compatibility

The current `SessionPullRequestDto` is identity/link metadata only. The existing
`/api/review` routes are coding-session/transcript context, not GitHub reviews.
`WorkContextReader` also gates its summary through work-item outcomes. New PR
reads must not inherit that work-item coupling or subscription gate.

Add `pull_request_reads_version: 1` to every provider's existing `/auth/config`
response. It describes protocol availability only, not token configuration, user
link state, or repository access. This route is already anonymous; the new field
contains no user/tenant-private data. Add the nullable field to
`AuthDiscoveryResponse`; absence on a successfully parsed response means an older
server. Version 1 is advertised even when interactive reads are disabled.

Discover once per profile activation; cache for five minutes. Recheck on manual
refresh, reconnect, and profile change. Until a supported version is observed, do
not call the new routes. An unknown higher version is unsupported until explicitly
supported by the client. Malformed/non-JSON capability responses are a discovery
failure, not proof of an old server; retry discovery with 30s, 60s, then five-minute
backoff. Manual refresh is coalesced and cannot flood discovery.

Source precedence is explicit:

1. With supported capability, the new link-list route is authoritative. Do not
   union its results with legacy links, resurrect missing entries, or override its
   ordering/titles from legacy data. A 404 clears protected PR state, not capability.
2. Without capability, retain only legacy links from a successful, independently
   admitted session-summary read. Read that summary independently of work-item
   assignments for this fallback. An empty legacy result says details unavailable,
   not No PR linked. There is no live-details polling.
3. A malformed response on an advertised new route is an unavailable protocol
   result, never route-unsupported or an authoritative empty list. Back off as
   above; do not downgrade authorization by switching to legacy detail data.

Capability discovery is shared by the app's profile-scoped source; old desktop
clients ignore the additive field. There is no new daemon IPC or SignalR contract.

## Linked-user authorization

### Policy and configuration

`PullRequestReads:Enabled` defaults to true. Effective detail availability still
requires a configured integration, a verified positive GitHub user ID, Capacitor
admission, and GitHub access evidence. Setting Enabled false disables interactive
PR reads without removing the token or disabling enrichment/reviewer polling. It
invalidates detail caches and access leases. Document the switch in server settings;
a new settings UI is not required for v1.

There is no admin-only role floor and no new repository allowlist. Any human caller
who passes **both** Capacitor admission and the following GitHub gate can read.
Service principals and synthetic/no-auth identities without a real GitHub identity
cannot receive detail data. AuthStatus.NoAuthRequired still makes the HTTP client
usable; it does not fabricate a GitHub identity.

Existing linking is identity-only: the proxy requests `read:user`, sends signed
identity fields, and the tenant records the association, not reusable OAuth tokens.
Use `IGitHubLinkService.GetStateAsync` for the canonical caller: require `Linked`
and a positive `GitHubId`, not the pending `LinkedGitHubId` claim marker, a cached
login string, or a generic display name. This includes GitHub-native human accounts;
do not resurrect a missing holder association by parsing an old `github:` user ID.
Unlinked WorkOS
users see **Link GitHub to read PR details**, opening the configured tenant's
existing `/auth/github-link/start` browser flow; a sign-in to the website may be
needed if the browser lacks its cookie. Recheck association on return/manual refresh.
Do not transfer desktop tokens in a browser URL or mint new linking credentials.

### Gate, on every detail read

1. Resolve the authenticated caller and existing Full session access; also apply
   `IsRepoVisibleAsync` to the requested base repository. This existing repository
   gate is session-derived and is **not** evidence of GitHub access.
2. Resolve the admitted link afresh from this session's data. Validate a positive
   Int32 PR number and the repository hash; zero/multiple canonical matches fail
   closed. Hidden sessions may contribute no rows, titles, counts or ordering.
3. Check Enabled, integration generation, and the caller's current GitHub identity.
4. Resolve the current GitHub login with `GET /user/{account_id}`. Verify returned
   numeric ID. Cache this non-authorizing identity lookup for up to one hour;
   missing or mismatched identities cannot authorize. An ID/login mismatch on the
   permission call discards this lookup and allows one fresh lookup on next retry.
5. Fetch canonical base-repository metadata using the integration token. Require a
   matching repository identity and explicit visibility. Only `visibility=public`
   supplies public-read evidence; `private`, `internal`, missing, or unknown values
   do not. The caller still needs a linked identity for this v1 reader.
6. For non-public repos, call
   `GET /repos/{owner}/{repo}/collaborators/{login}/permission`. Require a matching
   returned user ID and explicit `permission` of `read`, `write`, or `admin`.
   GitHub maps triage/maintain onto those base roles. No role-name substring checks.
   Missing/null user, unknown permission, denial, malformed data, or failed probe
   cannot authorize. Public repos do not require collaborator membership.
7. Admit before accessing a cached body. Re-read current Capacitor admission and
   holder/configuration state before delivery; renew expired external evidence
   before releasing a long-running result. Always construct provider URLs from the
   freshly admitted link. Neither
   a cursor nor a provider node ID supplies repository/host authority.

Permission/public-visibility evidence has a maximum 30-second positive lifetime.
Renew any proof with ten seconds or less remaining instead of returning another
near-expiry lease; concurrent renewals coalesce. It is bound to integration generation, canonical repository ID, and numeric user
ID (public visibility itself may be shared, but the caller's identity gate is not).
No stale positive evidence is usable. A failed fresh permission check clears the
proof; never fall back to a previous successful proof after that failure. Negative
results can be cached for at most 30 seconds. Every page and cached read still
rechecks current Capacitor access and current identity/configuration state.

Changing the configured token value or Enabled increments an in-process integration
generation, invalidating proofs, payload caches, snapshots, and cursors. A process
restart invalidates opaque handles. Generation is not a logged token hash. A
GitHub-side permission change without an options change takes effect when observed,
with positive evidence bounded to 30 seconds; final-response revalidation bounds
long reads too. This is not instantaneous revocation.

The result contains a remaining access lifetime. The desktop masks protected bodies
when this lifetime expires and obtains new evidence before revealing them again.
It also masks bodies immediately when a workspace becomes hidden and before
revealing retained data on return. A local profile/sign-out change clears all PR
state. When permission/configuration failure or a session/list 404 is observed,
clear **all** loaded bodies/cursors in the affected PR/session, not just the polled
section. Retain only separately admitted link metadata, if still admitted.

This checks repository roles/publication, not all of GitHub's per-user SSO, session,
IP, or conditional-access policies. Requests are made as the integration, not with
a delegated user token. The user accepted this read-only v1 trade-off. Exact
user-to-server authorization would need a separate grant and encrypted per-user
token lifecycle; that is outside this feature.

### Audit and provider safety

Record structured server access events for detail decisions: caller ID, session ID,
repository hash/PR number, section, UTC timestamp, admitted/denied/unavailable result,
and cache-hit flag. Use existing protected operational logging/retention, not a new
public analytics view, event-store stream, or audit UI. Do not log bodies, tokens,
raw provider errors, cursor contents, or full query strings. This is an operational
access trail, not a compliance-grade immutable audit product.

Live details are github.com-only. Resolve fixed provider origins and refuse
off-origin redirects. Provider identities and thread membership must match the
admitted PR, even for globally addressable node IDs. Outbound PR/comment/thread
links must be HTTPS github.com links for that PR. Check details URLs may legitimately
belong to external CI providers: accept absolute HTTPS without userinfo, show the
destination host, and open only on an explicit user action. Never fetch those URLs.
They are not authority for an API request. Do not silently restrict CI to github.com.

Use raw Markdown `body`, never provider `body_html`. Render through native text
modules, not an HTML browser. Unsupported constructs render as inert text; external
images/attachments are not fetched. Only explicit absolute HTTP/HTTPS body links
pass through LinkPolicy. Relative links, mentions and issue-reference shorthand
remain text in v1; no repository-relative URL inference. Disallowed schemes are inert.

## Server module and request budget

Add a dedicated pull-request read module, separate from enrichment. Reads do not
append work-item events or change correlation/lifecycle state. Reuse only the
credential provider and common request/header helpers, not the enrichment HTTP
client registration: it currently has a ten-second timeout. The integration must
support the permission endpoint plus repository metadata, PRs/reviews, issue
comments, checks, and commit-status reads. Insufficient provider permissions are a
supported unavailable result, never a reason to discover another credential. The
daemon and desktop never receive the integration token.

Register `GitHubPullRequestReads` with fixed GitHub origins, a 15-second upstream
headers-and-body deadline, explicit cancellation, no automatic retries and no
off-origin redirect following. Bound a complete admitted HTTP operation, including
queue wait and capture, to 30 seconds. Desktop request deadline is 35 seconds.
Deadline expiry is typed unavailable, never empty or complete. Limit decoded
upstream bodies to 16 MiB while streaming, before JSON parsing, and client-side
server response reads to 4 MiB. Neither side may buffer an unbounded response merely
to discover that its normalized form exceeds the budget.

The lightweight overview uses one bounded GraphQL request for identity/lifecycle,
head SHA, description, `reviewDecision`, the first 50 current-review/request rows,
and the first 50 status-rollup contexts plus totals/page information. Request the
actual GraphQL rate-limit cost/remaining/reset data. Aggregates from incomplete
connections are lower bounds or unknown, never exact. The overview must not fetch
thread/comment bodies or traverse those connections to render the card.

A cold private-repo overview therefore costs at most three REST gate calls (user
lookup, repo metadata, permission) and one GraphQL overview call. A repeated
30-second poll normally reuses user lookup, with at most two REST gate calls plus
one GraphQL call; coalesce repo metadata and overview fetches across viewers, and
permission checks only for the same numeric user/repository/generation. The link
list and capability reads never contact GitHub.

Add a credential-scoped rate-limit observer used by the interactive client and the
existing GitHub background HTTP clients. This observer consumes response headers
and GraphQL cost information; background scheduling semantics stay unchanged.
Interactive reads yield when REST or GraphQL remaining quota is below 20% of its
reported limit, during any primary/secondary cooldown, or while the corresponding
background provider gate is cooling down. No interactive request bypasses this rule
for an access probe, cache revalidation or manual refresh.

Limit interactive upstream concurrency to two and starts to 30 requests/minute per
credential. Charge GraphQL by actual reported points, separately from REST calls.
An unknown primary budget permits one serialized observation request; missing
budget headers retain conservative local pacing rather than pretending quota is
unlimited. A cooldown without a supplied reset/retry time defaults to 60 seconds.
Return `retry_at`/`poll_after_seconds`; server backoff always wins over ordinary
polling and early access-lease renewal. With no backoff, `poll_after_seconds` is zero.
Expired access proof remains masked while yielding. The feature may become
unavailable rather than starving background work.

## Stable pagination and bounded storage

### Collection snapshots

A native GitHub cursor is not assumed to provide a transactional snapshot. For
client pagination, the server freezes a bounded **metadata manifest** for each PR
collection before returning its first page. It holds stable provider IDs, sort keys,
publication/resolution flags and body-fetch references, not nested reply bodies.
Each thread's comments get their own manifest on expansion.

Capture traverses native GitHub connections into server-owned storage; client
cursors never encode live provider offsets. GitHub's schema does not offer a
created-at keyset/order parameter on every collection, so do not invent that API.
Capture spans an interval, not an atomic historical snapshot. After the manifest
is frozen, newly created items wait for explicit refresh.

Record provider connection counts/boundaries before and after capture, and compare
unique enumerated IDs with the unfiltered provider count before applying publication
or resolved filters. Changed boundaries/counts, missing nodes, or repeated/non-advancing
provider cursors cannot produce complete coverage. Retry enumeration at most once
within the same deadline, merging by stable ID; otherwise return a limited manifest
with a changed-during-capture reason and an explicit Refresh action. An API without
sufficient enumeration/completeness evidence also yields limited coverage. Complete
means a verified traversal over that capture interval, not transaction isolation or
a historical point-in-time view. In-place body/state edits retain that caveat.

Paging the frozen manifest cannot skip/reorder captured IDs when GitHub grows.
Tests cover growth during capture as a stable retry or visibly limited result, and
no missed captured rows when growth happens between client pages. Deleted items
may be tombstones only with positive deletion evidence. Null/missing nodes or 404
alone are ambiguous and follow the authorization-class failure policy.

Once frozen, a manifest cannot reorder as the user pages. The server sorts the
whole captured sequence before cutting pages; the client appends without re-sorting:

- Conversation and published reviews: `created_at` descending, provider ID as a
  stable tiebreak. Editing changes `updated_at`, never the order.
- Thread comments: `created_at` ascending, then provider ID.
- Threads: unresolved before resolved, then root-comment `created_at` descending,
  then thread ID. The default is unresolved-only; `resolved=all` starts a separate
  manifest. Root-comment metadata is allowed during capture, not its body/replies.
- Reviewers: actor type, canonical login/team slug, then stable actor ID.
- Checks: failed, pending/running, unknown, successful, neutral/skipped; then app ID,
  name and stable run/context ID. Preserve cancelled/timed-out/action-required
  labels in the failed/non-success group instead of calling them passed.

Check manifests are tied to a head SHA. Use the provider's latest check-run filter;
retain suite/app/name identity and never collapse distinct suites solely because
names match. Within the same suite/app/name, retain the most recent attempt by
`started_at`/`created_at` and ID tiebreak. Legacy statuses use latest `created_at`, ID
per context. If the provider cannot establish the latest set, mark coverage limited
and do not publish an exact success aggregate. A fixture must cover two workflows
with the same job name, as well as reruns; name-only deduplication is forbidden.

Manifest capture is bounded to 5,000 metadata entries, 4 MiB, and the operation
deadline. If the entry/size ceiling is reached, a limited manifest can still be
read, with `coverage=limited`, lower-bound counts, and **More on GitHub** after the
captured rows. Do not call it the whole collection. Timeout/provider failure returns
unavailable rather than an ostensibly complete manifest; rate advice is preserved. These limits deliberately bound exceptional PRs;
ordinary large bodies alone do not make an entire collection unreadable.

### Pages and cursors

Pages have a maximum of 50 items and 4 MiB of normalized JSON. Hydrate bodies only
for that page's IDs. Return fewer items when needed to fit, and point the cursor to
the next unreturned manifest entry. Never consume/drop an item just because it did
not fit. For a single oversized body, include an explicit truncated preview,
`body_truncated=true`, and item URL; bound the preview to 256 KiB. Preserve identity
and truncation metadata. An unavailable whole section is the last resort, not the
normal handling of an oversized body.

Use 256-bit random **server-side opaque cursor handles**, not client-authored JSON.
A handle record binds caller, tenant, session, PR identity, section/filter, thread
where applicable, integration generation, snapshot ID, next position, and expiry.
Expire after five minutes; the snapshot lives no longer than its handles. Bind to
the numeric GitHub identity used for access too. A cursor supplies position only:
re-resolve the link and thread membership to build each upstream request. Unknown
or foreign handles never cause a GitHub call. A well-formed handle missing from the
bounded store returns generic `cursor_unavailable`; a known foreign handle returns
400. Retained records can identify expiry/eviction/generation changes precisely;
do not keep unbounded tombstones just to distinguish all forgotten handles.

`has_more=true` iff `next_cursor` is non-null/non-empty. `has_more=false` only means
end of the captured manifest; `coverage` separately says whether it covers the
whole provider collection. The client rejects inconsistent combinations and never
runs a continuation loop automatically. Zero-length pages with a continuation
are a protocol failure. Stable IDs prevent duplicate appends on retried pages.

Server bodies/manifests/handles share a 64 MiB payload budget and 256 payload-entry
cap; evict least recently used entries, with ten-minute idle expiry and the shorter
five-minute manifest/handle lifetime. Coalesced data is keyed by integration
generation, canonical PR, section/filter, snapshot/page, and check SHA. Mutable overview/body payloads have a 30-second freshness window;
use conditional requests where supported. Manual refresh can reuse a still-fresh
payload and must retain its actual timestamp. Frozen manifest metadata remains its
own snapshot until explicit refresh/expiry; fresh body hydration does not reorder it.
Authorization is never cached inside shared body entries. Evicted handles/snapshots
require restart.

Desktop retains data for only the current PR, at most eight pages per section and
32 MiB total per workspace. Evict least-recently-viewed non-visible pages first;
show a Reload earlier control rather than claiming evicted rows are still loaded.
A single manifest page can always be re-requested through its saved page handle
while valid. First pages carry a page handle too. Expired handles require refresh.
On PR switch, cancel old fetches and drop bodies/cursors; preserve only selection,
expanded-ID and scroll metadata in a 20-PR LRU. Restore scroll/expansion after the
corresponding data is reloaded, not by retaining unbounded content.

## Wire contract

Paths are additive and session-scoped:

```text
GET /api/sessions/{sessionId}/pull-requests
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/checks
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/reviewers
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/reviews
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/threads
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/threads/{threadId}/comments
GET /api/sessions/{sessionId}/pull-requests/{repoHash}/{number}/conversation
```

The list does no GitHub fan-out: it returns Capacitor-admitted session link metadata,
not hydrated GitHub bodies. Its `data` is `{ "items": [...] }`, with each row's
identity fields plus nullable `url`, `title`, and `head_ref`. Its successful response
can be empty. All detail routes
apply the additional GitHub gate. Resolve `repoHash` against this session's admitted
canonical links, using the existing hash derivation; do not treat the hash itself as
authorization. No match, ambiguous identity, unknown repository, or unreadable
Capacitor repository returns 404, even if the integration token can read it.

Only identity/container fields necessary to address/interpret the result are
required. Optional content fields tolerate absence/null. Unknown JSON members are
ignored. Wire discriminators are strings parsed through explicit known-value maps;
unknown values become Unknown, never exceptions or implicit success. A missing or
unknown result status cannot expose its data. No `UnmappedMemberHandling.Disallow`.

### Common shapes

Field names below are normative; JSON fixtures exercise exact source-generated
serialization/deserialization in both repositories.

```json
{
  "status": "ready",
  "subject": {"provider":"github","host":"github.com","repo_hash":"0123456789abcdef","owner":"example","repo_name":"repo","number":42},
  "data": {},
  "fetched_at": "2026-09-07T10:00:00Z",
  "reason": null,
  "retry_at": null,
  "poll_after_seconds": 0,
  "access_valid_for_seconds": 20
}
```

`status`: ready, stale, unavailable. `data` is nullable and typed per route.
`subject` is absent on the link-list envelope; its rows carry the same identity.
`fetched_at` is the successful upstream fetch/validation time of that data, not
cache insertion or response time; a 304 revalidation advances it. Section/snapshot
times remain separate. All absolute times are RFC 3339 UTC strings ending in Z;
`retry_at` is absolute UTC. Durations are nonnegative integers in seconds.

`access_valid_for_seconds` is remaining positive proof lifetime at server delivery,
zero on unavailable or failed authorization. The client measures expiry from its
request's monotonic **start**, not receipt, so network time cannot extend the lease.
It caps the duration at 30 seconds and masks on expiry. The link list does not grant
a detail access lease. Each body response needs valid current authorization too.

Reasons: disabled, not_configured, github_not_linked, github_access_denied,
github_access_unverifiable, provider_unauthorized, provider_forbidden,
provider_not_found, rate_limited, budget_exhausted, timeout, provider_unavailable,
protocol_error, capture_unstable, payload_unavailable. Unknown reasons use a generic
unavailable label. Only transient content failures/rate limits with a still-valid
access proof may carry stale data. Unknown authorization status never may.

Count: `{ "value": 2, "kind": "exact" }`; kinds exact, lower_bound, unknown.
Unknown requires null value. Unknown kind is interpreted as unknown. Negative
values are malformed. Never show a lower bound as an exact sidebar count.

Section availability: `{ "status":"ready", "reason":null, "fetched_at":"…" }`.
Overview includes identity/title/URL, lifecycle, is_draft, head_ref/base_ref/head_sha,
raw Markdown description, PR updated_at, GitHub review_decision, and:

- `checks`: availability, provider rollup, counts by actual normalized outcome,
  and `head_sha`. Exact aggregate only when complete latest-set evidence exists.
- `reviews`: availability, published/approved/changes-requested/outstanding-user/
  outstanding-team count objects. Bounded GraphQL connections can supply lower
  bounds; review_decision remains authoritative independently.
- `conversation`: availability and comment count if available without body reads.

Absent optional fields remain unknown. Failed section availability is not a zero
count. An unchanged `updated_at` is not proof every body/check is unchanged; freshness
is explicit. The reader's update hint is advisory, not a consistency guarantee.

Page `data`:

```json
{
  "snapshot_id": "opaque",
  "snapshot_started_at": "2026-09-07T10:00:00Z",
  "snapshot_completed_at": "2026-09-07T10:00:02Z",
  "coverage": "complete",
  "head_sha": null,
  "total": {"value":2,"kind":"exact"},
  "items": [{"id":"IC_example","url":"https://github.com/example/repo/pull/42#issuecomment-1","created_at":"2026-09-07T09:00:00Z","updated_at":"2026-09-07T09:00:00Z","author":null,"body":"Example comment","body_truncated":false}],
  "page_cursor": "opaque-current-page-handle",
  "has_more": true,
  "next_cursor": "opaque-next-page-handle"
}
```

Coverage is complete or limited; unknown coverage is limited. A limited page also
carries `coverage_reason`: entry_limit, size_limit, changed_during_capture, or
provider_limit; unknown reasons retain the limited label. Each collection item carries stable provider ID, its validated
external URL if available, and the section's typed content. Bodies additionally
carry `body_truncated`. User/team actors have stable ID, kind, nullable login/name,
and no automatically downloaded image. Pending review state `PENDING` is dropped
server-side unconditionally; review comments/threads must also exclude unpublished
review content, rather than assuming the integration cannot see its owner's drafts.

### HTTP outcomes and reset semantics

401 is Capacitor authentication failure only. Missing/hidden/below-Full/unlinked
Capacitor subjects are indistinguishable 404s. GitHub denials are typed unavailable
results for an already admitted Capacitor link, not a Capacitor 401/404. Malformed
inputs or known-foreign/malformed cursor handles return 400 without upstream work.

409 uses `{ "error":"restart_required", "reason":"…" }`, where reason is
head_changed, cursor_expired, cursor_unavailable, snapshot_evicted,
cursor_version_changed, integration_changed, or identity_changed. The current subject must pass admission
before returning a detailed restart reason. Otherwise return the ordinary denied
outcome, not a hint about another user's cursor.

- Head changed: discard checks and their counts/cursors immediately; these cannot
  remain displayed as the current head's checks. Reload on explicit refresh.
- Integration/identity changed: clear all protected bodies and proofs; reauthorize.
- Cursor expired/unavailable/version changed/snapshot evicted: disable Load more, retain visible
  rows only while their access lease is valid, label them as an old snapshot, and
  offer Refresh. Do not silently replace pages under the user.
- Manual Refresh explicitly resets the selected collection to page one and scrolls
  it to the top. Retain expansion IDs only for rows that reappear. This is an
  intentional exception to ordinary background-refresh scroll preservation.

## Desktop lifecycle and freshness

Core owns NativeAOT-safe wire records and a small read client in `PullRequests`.
The app owns the shared workspace state/source. No server assembly references.
Extract a shared internal authenticated-client lease helper from the established
source only as needed; preserve its authentication retry and retired-client lease
discipline. Borrowing/building is serialized, not HTTP requests. A slow
comment read cannot serialize work-context reads behind itself.

Limit desktop PR HTTP concurrency to four per profile, with one request per
workspace/section and overview priority. Collapse refresh clicks to one pending
follow-up. PR/session changes cancel old operations, not just suppress their
results. Bind result application to profile, session, PR, section and monotonically
increasing selection generation, including A→B→A. UI updates use the UI scheduler.
Disposal cancels, awaits, then releases resources; workspace teardown owns this once.

Polling eligibility means selected workspace in a visible, non-minimized,
foreground app window. Poll the link list and selected PR overview on a 30-second
target schedule, subject to server backoff. While protected content is displayed,
renew via the overview five seconds before the conservative access deadline, with
at least 15 seconds between attempts; reuse fresh content but renew expiring access
proofs. Early renewal normally avoids a blank flash at each lease boundary, not a
guarantee during slow/failed requests. It cannot bypass server pacing/cooldowns.
Ended sessions follow the same rule. Stop polling and mask bodies when ineligible;
on return, revalidate before showing retained bodies, even if a previous timer had
not expired. Load other sections on demand.

Background overview refresh never replaces reader pages. It may show an update
hint. Sections show their own times; manual refresh restarts them. Valid new access
evidence can authorize redisplaying an old body snapshot, but the snapshot stays
labelled with its old fetch time. A fresh overview cannot label those bodies fresh.

| Outcome | UI and retention |
| --- | --- |
| First read pending | Section loading, unknown counts. |
| New authoritative link list empty | No PR linked. |
| Successful empty captured collection | No comments/reviews/checks, only if coverage is complete. |
| Missing GitHub link | Link GitHub action; no bodies. |
| Disabled/missing integration | Known PR link and explanatory note; no bodies. |
| Denied/unverifiable user permission | Clear bodies/proofs, link retained only if Capacitor-admitted. |
| Provider 401, non-rate 403, or 404 | Authorization-class: clear fetched bodies, never stale; do not change link or lifecycle. |
| Content transport/5xx/timeout | Retain stale bodies only until an existing access lease expires. |
| Rate/budget limit | Respect retry advice; expired evidence is masked, not extended. |
| Oversized item | Explicit truncated preview and its external link; paging continues. |
| Limited manifest | Label loaded subset/lower bound; More on GitHub, never all-loaded. |
| Caller sign-out/session 404 | Clear affected protected state immediately. |
| Older server | Legacy admitted links only; no unsupported-route polling. |

## Verification and delivery

Server tests exercise admission and normalized responses with stubbed GitHub data:
Full/hidden/team gates, forged session/PR/thread/cursor identity, missing and changed
GitHub links, numeric-ID mismatch/login rename, public vs internal/private repos,
team/base-role read and denied/unverifiable permission, cache-hit authorization,
proof expiry during fetch, token/flag rotation, GitHub-side revocation, and denial
on 404. Verify that cached bodies cannot bypass the user's GitHub permission gate.

Pagination tests must cover inserts during capture (stable retry or explicit limited
coverage) and between pages (no missed manifest rows), deletions/tombstones,
snapshot-stable ordering, resolved filter,
body-budget short pages without lost items, one oversized body, repeated cursors,
limited manifests, eviction and every 409 reason. Include pending/dismissed reviews,
file-level/outdated threads, deleted actors, cross-repo equal PR numbers, same-name
workflow jobs and reruns. Test rate-limit yielding and coalescing without changing
background-job scheduling or emitting enrichment events.

Core/app tests pin tolerant JSON parsing, unknown status/enum handling, exact
count/page invariants, source precedence, unsupported discovery/backoff, provider
failure versus Capacitor sign-out, memory eviction, monotonic access expiry,
visibility return masking, and profile/selection cancellation. Avalonia tests assert
realized Chat/Terminal preservation, non-PTY and ended layouts, card-row focus,
selected tab accessibility, live announcements, readable long content, and failures
in one sidebar module not contaminating the other.

Create one canonical v1 JSON fixture bundle plus SHA-256 manifest. The server's
contract tests serialize representative public response records and compare with
that bundle; the CLI's tests deserialize the identical bundle with generated
metadata and verify semantics. Both repositories pin the same bundle digest in
versioned contract metadata. A server field rename fails its serialization test
against the checked-in canonical fixture; tests must not regenerate golden files.
No new cross-repo CI credentials are assumed. The implementation driver compares
the bundle digests at the paired candidate commits before merge and before release;
a contract revision needs both pins updated together. This catches accidental field
drift in local CI and coordinated revision mismatches in paired integration.

The implementation driver owns a read-only smoke check against the paired server
candidate before requesting implementation sign-off and again before desktop
release: public and authorized private PRs, team-only reader, denied user, existing
linked/unlinked accounts, and a long paged discussion. Use designated test accounts;
do not retrieve/log the production integration token or mutate PRs. Until executed,
report this as unverified, not covered by stub tests. Verify the configured token
can call the permission endpoint and exercise the chosen GitHub connection adapters,
including completeness evidence; do not claim a transactional/keyset guarantee the
provider cannot supply.

Rebuild every changed project without warnings, run affected suites plus the desktop
suite at normal parallelism, and Release-publish the CLI for IL3050/IL2026 checks.
No code changed during design; the pre-change desktop baseline passed 1,442 tests.

Two work parts: server read/access module and desktop card/reader. Agree fixtures
first; desktop can develop against them, but live integration/release is blocked
on the additive server routes and capability field. Deploy server first.

This spec lives on `feat/desktop-pr-sidebar` in `.worktrees/desktop-pr-sidebar` and
rides the first desktop implementation PR. No spec-only PR. Record the approved
cross-repo design in a Linear document linked to both implementation issues; the
server part references that record, without opening a server spec-only PR. Create
CLI issues in GitHub (Linear auto-import), server issues in Linear. CLI PR bodies
reference both IDs; server PRs follow that repository's Linear reference convention.
No external issue identifier is invented here.

Archive the throwaway three-layout HTML on a non-production branch at sign-off and
link it from the design record; never merge its switcher into the app. Local source:
`.superpowers/brainstorm/94464-1788768185/content/pr-sidebar-layouts.html`.

Capacitor parent `26c1058d5f49597faad21b663ab786a1`; server part
`b5202e2821b259e9bd7bdd178b388b1e`; desktop part
`a7d3de097c3454c186fc449bd4659f9d`. The declared server→desktop dependency gates live
integration/release, not parallel fixture-based development.

## Primary-source references

- [Linked-account feasibility research](../../github-linked-pr-access-research.md):
  actual server linking flow and the distinction between identity and delegation.
- [Get a user using their ID](https://docs.github.com/en/rest/users/users#get-a-user-using-their-id).
- [Get repository permissions for a user](https://docs.github.com/en/rest/collaborators/collaborators#get-repository-permissions-for-a-user):
  effective base roles across direct/team/org grants; Metadata read for supported
  fine-grained token types.
- [List check runs for a Git reference](https://docs.github.com/en/rest/checks/runs#list-check-runs-for-a-git-reference):
  latest filter and the provider's 1,000-suite limit; do not claim complete coverage
  if a provider ceiling was reached.
- [GitHub's published GraphQL schema](https://docs.github.com/public/fpt/schema.docs.graphql):
  PR/review/thread/comment connections expose native cursors; review-thread/review
  connections lack a created-at order parameter, and IssueCommentOrderField exposes
  UPDATED_AT only. Frozen client paging must not depend on an invented keyset API.
- [GitHub App user-to-server authorization](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user):
  the stronger, separately scoped alternative, not implemented by identity linking.
