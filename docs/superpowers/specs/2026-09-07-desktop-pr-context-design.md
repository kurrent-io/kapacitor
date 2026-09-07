# Desktop pull-request context

## Outcome and agreed scope

Find the current PR, understand its state, and read feedback without visiting the
Capacitor web UI first. GitHub remains the destination for actions and full diff
browsing.

The selected layout is **C: sidebar summary plus a wide reader**. Keep the existing
400px work-context sidebar. Add a compact PR card there and a **Pull request** tab
beside **Chat** and **Terminal**. Clicking a section in the card selects that PR and
opens the corresponding reader section directly.

The user approved:

- Layout C rather than an expanding sidebar card or dedicated sidebar tab.
- Read-only v1: no posting, replying, resolving, requesting/submitting reviews,
  rerunning checks, changing draft state, closing, or merging.
- Server-supplied GitHub data using the configured integration, rather than a
  desktop dependency on `gh` or a second desktop sign-in.

This document specifies the remaining interaction and technical details for review.
Implementation starts only after the written design is approved.

## Experience

### Sidebar

Place the PR card after the work-item card and before the linked issue. Keep work
item, contributors, and session facts in their existing places.

The card shows:

- Repository, PR number, title, and source/target branches.
- Explicit **Draft**, **Open**, **Closed**, **Merged**, or **Unknown** lifecycle state.
  Draft is an open PR with its draft flag set; merged takes precedence over closed.
- Copy-number and copy-URL actions with visible copied feedback; an explicit
  **Open on GitHub** action. Clicking the title opens the in-app reader.
- Checks summary: failed, running/pending, passed, and skipped/neutral as distinct
  categories. Zero checks says **No checks reported**, not **Passed**.
- GitHub's review decision when available; otherwise an honest review summary,
  without claiming that the PR meets branch protection or is ready to merge.
- Navigation rows for **Checks**, **Reviews**, and **Conversation**. Counts appear
  only with their completeness known. An unresolved-thread count is optional, not
  a reason to download every comment before showing the card.
- A last-successful-fetch time and refresh action. Refresh failure marks retained
  data as stale; it does not change the PR to closed, empty, or passing.

When a session has multiple PRs, show one expanded summary and a selector labelled
with repository, number, and title. Keep the user's selection for that workspace.
For an initial selection, prefer a unique match on the session's primary repository
and branch. Otherwise use the server's deterministic repository/number ordering;
never silently guess that the largest PR number is the relevant PR. Subsequent
polls must not switch away from a PR the user is reading.

PRs are identified by their **base repository and number**, not just the number or
head branch. A fork's head repository must not become the PR's identity. A
cross-repository session can expose distinct PRs with equal numbers.

A successful, authoritative empty link list shows **No pull request linked**. An
unregistered session shows **Waiting for the session to register…**. A failed link
read cannot manufacture the empty state. Unsupported providers keep their existing
external links but do not offer GitHub-specific detail controls.

### Reader

The reader uses the center column, not a new window, overlay, embedded browser, or
second nested sidebar. Its header repeats PR identity/state and the GitHub action.
The header includes a collapsed, expandable PR description.

Three sections provide the following:

| Section | Content and order |
| --- | --- |
| Checks | Latest head commit's check runs and commit-status contexts. Failures first, then pending/running, then successful and neutral/skipped results. Show name, provider/app, actual outcome, timing when available, and the check's GitHub/details link. |
| Reviews | GitHub review decision, current reviewer/request status, submitted review bodies, and inline discussion threads. Group loaded threads unresolved-first; offer Show resolved, and label outdated separately from resolved. Keep the pagination indicator visible so the loaded subset cannot look like the whole discussion. |
| Conversation | Top-level PR comments, newest first, with author, timestamp, edited indication when supplied, and Markdown body. Identify automated authors and allow their comments to be collapsed; never hide their existence from counts. |

Review threads show the file path, available current/original line information,
review-provided diff hunk, comment bodies, replies, author/time, and a direct link
to the thread. Do not reconstruct historical context from the local checkout.
File-level comments and outdated comments without a current line are valid.
Published review submissions remain available even when superseded by another
verdict. Do not expose unpublished pending reviews visible only to the integration
credential's owner.

Checks, reviewer summaries, review submissions, threads, thread replies, and
conversation comments are paged. **Load more** extends the relevant collection. A failed later page retains
the loaded portion with a retry action; it never labels the collection complete.
No full-file diff browser, check-log viewer, PR search, or manual link editor is
included in v1.

### Navigation and preservation

- Opening a PR is always an explicit action; detecting a newly linked PR does not
  steal focus from Chat or Terminal.
- Returning to Chat preserves its draft and scroll position. Switching tabs must
  not detach, restart, or dispose the terminal/agent.
- Returning to the reader restores its selected PR, section, thread expansion, and
  scroll state for the workspace's lifetime. No disk persistence is needed.
- PR availability is independent of PTY support. A workspace with no Chat/Terminal
  surface can still display the PR reader. Existing terminal absence banners must
  not overlay it.
- Ended sessions can still read their linked PRs while their workspace is open.
- If a selected PR is unlinked or access is revoked, clear its detailed content and
  explain that it is unavailable. Do not silently replace it with another PR.
- Keyboard focus, accessible tab selection, text labels alongside status colors,
  and readable long paths/bodies are part of the design.

## Architecture

### Existing code and the missing data

`WorkContextView.axaml` currently renders PRs as number/title links.
`SessionPullRequestDto` in Core's `WorkItems/WorkContextDtos.cs` carries identity,
URL, title, and head ref, but no checks or discussions. `WorkContextReader` also
couples its summary result to work-item route outcomes; the new PR read must not
inherit that coupling.

The server's `/api/review` surface supplies recorded coding-session context, files,
and transcript excerpts. It is not a GitHub discussion/checks interface. Existing
`GitHubEnrichmentSource` reads lifecycle and review aggregates for tracker
processing; `GitHubReviewRequestClient` handles requested-reviewer observations.
Neither inspected surface provides the complete reader.

### Server module

Add a dedicated pull-request read module. It owns GitHub reads, normalization,
pagination, conditional requests, caching, and typed failures. Keep interactive
reads out of the background work-item enrichment pipeline: opening a panel must
not append enrichment events or trigger work-item lifecycle transitions.

Reuse the configured GitHub tracker credential provider and HTTP setup. The
configured credential needs read access to PRs, issue comments, checks, and commit
statuses for the relevant repositories. Missing configuration or insufficient
scope is a supported UI outcome, not a request to discover another credential.
The daemon and desktop never receive this token.

GitHub REST and GraphQL are implementation details of the server module. Use the
provider's review decision and resolution/outdated flags rather than inferring
these from text or comment ordering. Include both check runs and legacy commit
statuses. A repeated run must not count an obsolete attempt as a current failure.
Preserve unknown provider statuses as unknown rather than treating them as success.

### HTTP interface

Use additive, session-scoped routes; do not change the meaning of `/api/review` or
require a new daemon protocol. The route family is:

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

The list route returns admitted, linked PR identities without a GitHub fan-out.
It is independent of work-item assignments and work-item subscription gates. The
un-suffixed PR route returns the compact overview and description. Collection
routes accept an opaque cursor and return items plus `next_cursor` and
`has_more`; the server fixes the page size at 50. The normalized response budget is
4 MiB; a response exceeding it returns an explicit unavailable outcome and GitHub
link rather than silently truncating comment bodies.

Use 401 only for Capacitor authentication failure, 404 for unadmitted subjects,
400 for invalid cursors/inputs, and 409 for a page sequence that must restart.
Admitted reads use a typed result with `status` (`ready`, `stale`, `unavailable`),
nullable `data`, `fetched_at`, `reason`, and optional `retry_at`. Provider credential
failure is a reason inside that result, never a Capacitor 401. Only transient or
rate-limit failures may return stale data. Overview sections have their own
availability fields; partially available overview data does not manufacture zeros.

All responses identify their PR and carry fetch metadata. Check responses also
identify the head commit they describe. The overview can report partial section
availability without making unavailable counts zero. A count is exact, a labelled
lower bound, or unknown; those shapes must be distinct on the wire.

Reviewer summaries, published review submissions, and threads are separate paged
collections. Reviewer summaries distinguish a person's latest published verdict
from an outstanding review request, including team requests. The authoritative
PR-wide review decision is not recomputed from whichever reviewers fit on a page.
Thread list rows carry file/line/outdated/resolved metadata and a comment count;
opening a thread loads its comment page. No nested comment fan-out is needed to
list threads. Replies retain their own continuation cursor.

Cursors are versioned, bounded, and validated against the requested PR, section,
and thread where relevant. They cannot contain arbitrary upstream URLs. A cursor
for checks is also tied to the head SHA; a changed head returns a typed restart
outcome rather than appending another commit's checks. Other collections are
live provider reads, not an atomic historical snapshot: stable provider IDs allow
deduplication, and explicit refresh restarts pagination.

### Admission and privacy

Authorization precedes every response, including cache hits and every continuation
page. Require the existing session access resolver's **Full** level and the existing
tenant/team repository visibility gate. Resolve owner/name/provider from the
server's admitted linked-PR data; the route's repository and number must match it.
Do not accept a caller-supplied GitHub URL, host, or unrelated PR identity. A thread
identifier must belong to the admitted PR, even when GitHub can address it globally.
Missing, hidden, below-Full, and unlinked subjects return indistinguishable 404s.

The configured integration is tenant-shared tracker access, **not a mirror of each
caller's personal GitHub permissions**. This interface uses Capacitor's repository
and session access model, as existing tracker reads do. That policy is part of the
spec approval; the implementation must not claim GitHub-user ACL parity. The
credential should be scoped to the repositories the tenant intends to expose.

Use the existing identity/visibility helpers rather than a new approximation based
on an owner string or session presence alone. Do not let another hidden session
contribute an additional linked PR, title, count, or ordering to this session's list.
Revalidate access before delivering an upstream result after a long fetch; cache
entries never cache a caller's authorization decision.

Only `github.com` receives live detail support in v1. Other hosts/providers retain
safe external links. Build provider request URLs from validated identities and
fixed provider origins; refuse off-origin redirects. This is not a generic proxy.

Do not log credential material, comment bodies, description text, or raw provider
error payloads. Upstream failures are normalized before returning to the client.

### Desktop module

Core gains a `PullRequests` namespace for wire records, source-generated JSON
metadata, and the read client. It remains NativeAOT-safe and has no Avalonia or
GitHub-token dependency. Checked-in literal response fixtures pin the interface
between the two repositories; the desktop does not reference server assemblies.

The app gains a workspace-owned pull-request view model plus an authenticated
server source. The sidebar card and center reader project the same state, so they
cannot independently select different PRs or display divergent fetch results.
`WorkspaceViewModel` owns tab navigation and passes session/workspace visibility to
this module. `WorkContextViewModel` retains responsibility for work-item content.
Replace its plain PR link presentation without doing unrelated sidebar refactoring.

The server source uses the existing profile-aware authentication setup and respects
both `Ok` and `NoAuthRequired`. Authentication retry/client retirement and teardown
must use the established lease discipline; share that implementation at an internal
seam if needed rather than making a second subtly different credential lifecycle.
A profile change retires the source and its entire PR cache.

Every asynchronous result is associated with session, PR, section, and a selection
generation. Old results cannot update a new selection, including an A→B→A switch.
Dispose cancels owned requests, awaits them, and prevents late bound-state writes.
All observable collection/property updates occur on the UI scheduler.

Render Markdown and code through the app's existing native text modules. Raw HTML
is not executed and remote images are not automatically fetched. All explicit
external-link actions go through `LinkPolicy`; disallowed schemes remain inert.
Do not fetch attachments or evaluate suggested code.

## Freshness and failure behavior

- While the workspace is visible, refresh the linked-PR list and selected PR
  overview every 30 seconds, as well as on first activation and manual refresh.
  Hidden/minimized workspaces stop polling and refresh on return if stale.
- Discovering multiple links does not poll all of them. Fetch full sections only
  when the user opens them; switching to an unfetched section shows its own loader.
- An open reader retains its content and reading position. Overview refreshes can
  show **PR updated — refresh details**; they must not replace pages under the user.
  Opening a section after returning to the reader refreshes it if older than 30
  seconds. Manual refresh restarts the selected collection and preserves stable
  expanded thread identities where they still exist.
- Every section shows its own last successful fetch time. A freshly read overview
  does not imply that previously loaded comments are also fresh.
- Server overview/page caches have a 30-second freshness window, coalesce identical
  requests, and use conditional requests where GitHub supports them. Manual refresh
  may reuse data still within that window, and the returned timestamp says so.
- Respect primary and secondary rate limits and `Retry-After`. Do not retry a
  failed provider call immediately; recovery uses the next eligible poll or manual
  refresh under the provider cooldown. Use the existing response time budget and
  cancellation behavior. Repeated clicks cannot bypass cooldowns.
- Bound the server cache to 256 entries and 64 MiB of normalized payload, evicting
  least-recently-used entries and entries idle for ten minutes. These are operational
  defaults, not a limit on how many pages the user can request. Cache identity
  includes the tenant/integration generation, PR identity, section and cursor, and
  check head SHA. Removing/rotating the credential invalidates cached detail data;
  authorization/configuration failures never serve stale bodies.

| Outcome | Presentation |
| --- | --- |
| First read pending | Section-specific skeleton/loading text; unknown counts. |
| Confirmed no linked PR | No pull request linked. |
| Confirmed empty collection | No checks/reviews/comments, specific to that successful read. |
| Temporary provider/network failure after success | Retain that section with stale time and Retry. |
| Temporary failure before success | Couldn't load this section; Retry and GitHub link if already known. |
| Integration not configured | GitHub details aren't configured on this server; retain the known PR link. |
| Provider credential/permission failure | Details unavailable; clear fetched bodies, retain only independently admitted link metadata. |
| Response exceeds the size budget | This section is too large to display; provide its GitHub link without calling it empty or complete. |
| Rate limited | Last successful data where safe, explicit rate-limit notice, retry time when supplied. |
| Caller signed out | Clear protected data and offer the app's existing sign-in action. |
| Access removed or PR unlinked | Clear protected detail data; unavailable state, not a cached preview. |
| Older server without the routes | Retain existing admitted PR links, show details unavailable, leave work context/chat working. |

A provider-side 404 does not prove that a PR was deleted: private resources can
return 404 for insufficient permissions. It never clears the server's session link
or changes lifecycle state. A missing route, inaccessible subject, and failed
provider fetch must not be collapsed into an authoritative empty PR list.

## Verification contract

Server tests exercise the public read interface with provider responses stubbed:

- Access floor, team/repository visibility, hidden-session provenance, unlinked PRs,
  forged cursors, foreign thread IDs, and unauthorized cache reads.
- Configuration removal, token rotation, 401/403/404/429, both kinds of rate limit,
  partial provider responses, repeated cursors, and cancellation.
- PR state normalization, fork/base-repository identity, equal PR numbers across
  repositories, rerun checks, legacy statuses, and unknown outcomes.
- Dismissed/superseded/published reviews, pending review exclusion, resolved versus
  outdated threads, file-level comments, deleted authors, pagination and replies.
- Cache coalescing/expiry, complete versus incomplete counts, head changes while
  paging, and no enrichment/event-store mutations from reads.

Core/app tests pin literal wire fixtures and source-generated metadata, profile
isolation, rapid session/PR/tab changes, stale success versus access revocation,
partial page retries, no duplicate rows, and lifecycle teardown.

Avalonia tests pin direct navigation from each card row, non-PTY PR availability,
ended-session reading, retained Chat/Terminal state, accessibility, long Markdown
and file paths, multiple PRs, and older-server fallback. Exercise all lifecycle and
failure presentations, not only the successful example shown in the prototype.

Rebuild all changed projects without warnings. Run affected suites and the desktop
suite at normal parallelism. Publish the CLI in Release to verify Core changes
introduce no IL3050/IL2026 warnings; a normal build is not evidence of AOT safety.
Run a live read-only smoke check against a public and an authorized private test PR,
including a long paged thread. No test may post or mutate a real PR.

## Delivery and record

The work has two parts:

1. **Server PR read interface and provider module** — authorization, normalized
   overview/pages, cache/failure semantics, tests, and integration documentation.
2. **Desktop PR card and reader** — Core client, shared workspace state, views,
   compatibility handling, and tests.

Agree the wire fixtures first. Desktop development can use them in parallel, but
live desktop integration and release depend on the server part being available.
Deploy the additive server routes before advertising full desktop PR details.
Existing desktop builds ignore the new routes; new desktop builds degrade safely
against older servers.

No production code has been changed for this design. The worktree is
`.worktrees/desktop-pr-sidebar` on `feat/desktop-pr-sidebar`. The pre-change desktop
baseline passed all 1,442 tests.

The three-option HTML is a throwaway visual reference, not production source. Its
local artifact is
`.superpowers/brainstorm/94464-1788768185/content/pr-sidebar-layouts.html`.
Archive it on a throwaway branch when design sign-off is complete, and retain a
reference with the implementation tracking. Do not merge the prototype or its
switcher into production.

Keep this spec on the feature branch so it rides the first implementation PR;
do not open a spec-only PR. On sign-off, copy the approved design into the linked
Linear issue/document per the team's recording convention. No external issue
number is assigned by this document.

Capacitor work-item correlation:

- Parent: `26c1058d5f49597faad21b663ab786a1`.
- Server part: `b5202e2821b259e9bd7bdd178b388b1e`.
- Desktop part: `a7d3de097c3454c186fc449bd4659f9d`.
- Server part blocks desktop live integration/release.
