# Local pull-request context

## Decision

The desktop reads a linked pull request through the user's own GitHub CLI when one
is installed and signed in, and falls back to the server-supplied route only when
it is not. The reader, its card, and its view model keep their shape: reading moves
behind a registry of **reader providers**, each declaring which hosts it serves.
The registry implements the existing `IPullRequestSource`, routes every read to
the first ready provider for that PR's host, and ships with two providers, the
GitHub CLI and the server. A GitLab CLI provider, or any other, is one new type
and one registration line.

The [server route](2026-09-07-desktop-pr-context-design.md) and its
[GitHub App credential broker](2026-09-07-github-app-pr-credentials-design.md)
stay merged and deployable. Their per-tenant activation becomes fallback work
for users without `gh`, not a condition of the desktop release.

## Why the shape changed

Native reading through the server needs, per tenant, an App installation bound
to one deployment, a sealed service credential, a hashed proxy grant, trusted
ingress CIDRs, a compatible tenant image, and a paired smoke test. None of that
is self-service, so until an operator does it for a tenant the merged reader
shows external links only.

The stated non-goal of "no local `gh` prerequisite" bought less than it seemed.
The CLI already links a GitHub session to its pull request by running
`gh pr view` during repository detection, so a machine without a signed-in `gh`
mostly has no linked PR to read in the first place.

Reading with the user's own identity is also the stronger authorization: the
user sees exactly what GitHub shows them, there is no linked-user gate to get
wrong, no short access lease to renew, and GitHub Enterprise hosts work wherever
`gh` is signed in. A PR can be found the moment it exists on GitHub rather than
when the server links it.

## Experience

The card and reader behave exactly as the server design describes when `gh` is
present and signed in to the PR's host: description, checks, requested reviewers,
published reviews, inline threads, and conversation, with the same lifecycle
labels, copy actions, section navigation and last-fetch times.

### Sidebar note

The card is already shown whenever the feature is wired, linked PR or not. Its
existing notice slot carries a note about the local prerequisite:

| State | Note | Actions |
| --- | --- | --- |
| `gh` not found | Install GitHub CLI to read pull requests here. | **Install GitHub CLI** opens `https://cli.github.com` through `LinkPolicy`; **Recheck** re-probes. |
| `gh` found, no signed-in host | GitHub CLI is not signed in. Run `gh auth login` to read pull requests here. | **Recheck** |
| `gh` signed in, but not to the selected PR's host | GitHub CLI is not signed in for `<host>`. Run `gh auth login --hostname <host>` to read it here. | **Recheck** |
| gh too old for auth status --json | Update GitHub CLI to read pull requests here. | **Install GitHub CLI** (opens the install URL); **Recheck** |

The note shows regardless of whether a PR is linked, so the prerequisite is
learned before the first PR arrives. It is suppressed when another ready provider
serves the same host, which today means the server route is `Supported` there. It
never replaces a linked PR's title, selector, or Open on GitHub action, and the
Capacitor **Sign in** and **Link GitHub** affordances are never shown for a local
reader state.

The note is provider-generic. A CLI provider reports its tool name, install URL
and sign-in command with its status, and the card renders the note for the
providers whose hosts match the session's repositories, primary first. A GitHub
session never sees a GitLab note, and a session with no repository shows none.

**Recheck** is the existing manual refresh. Foreground return and profile change
also re-probe, as they already re-run discovery.

### Live discovery

The PR selector lists the union of the server's links for the session and the
open or recently closed PRs whose head branch is the session's current branch on
its primary repository, found by each ready provider that offers live discovery,
`gh pr list` for GitHub. Identity is host, owner, repository and number;
duplicates collapse; ordering is the canonical lower-case owner, lower-case
repository, ascending number. The default selection rule is
unchanged: a unique match on the primary repository and exact head branch, else
the first ordered link, with an explicit user selection preserved across polls.

The primary-repository hint the view model receives grows from a repository hash
to provider kind, host, owner, name and hash. The work-context session summary
already carries owner and name for its `is_primary` repository but no host, so
a session repository is assumed to be on `github.com`; a later change may infer
the host from a linked PR sharing the repository hash.

## Provider registry and routing

Two concerns are kept apart. **Session links**, which PRs a recorded session is
linked to, are a Capacitor concept and always come from the server: the v1
link-list route when supported, the independently admitted session summary
otherwise. **Reading** a PR is provider-specific and goes through the registry.

Providers are registered in precedence order, local CLI providers before the
server, in one place in the desktop app. Discovery runs once per profile
activation, probes every provider concurrently, is cached for five minutes, and
re-runs on manual refresh, foreground return, reconnect and profile change, as the
server design already requires.

Routing is per PR, not per app mode. A read for a subject goes to the first
provider, in registration order, that is ready and declares it serves that
subject's provider kind and host:

- the GitHub CLI provider serves `github` subjects on every host that
  `gh auth status --json hosts` reports in state `success`;
- the server provider serves `github` subjects on `github.com` when its
  capability is `Supported`;
- a future GitLab CLI provider would serve `gitlab` subjects on the hosts `glab`
  is signed in to.

A subject no ready provider serves reads as `Unavailable` with reason
`no_reader`; the card's note says what to install or sign in to for that host.

The registry's capability is `Supported` when any provider is ready. Otherwise it
is the session-link source's own capability, so an older server with no local
tool still yields `Legacy` and safe external links exactly as today. Each
provider's status is exposed beside the capability for the sidebar note.

Legacy links arrive from the server with provider `unknown` and a sanitized URL.
The registry offers each link's URL to every provider's link parser; a provider
that recognizes it yields a real subject, which is then read natively. The
server-side rule that legacy links are never native-read protects the server's
admission; a CLI provider enforces its tool's own.

A probe that completes with a determined answer, tool present or absent, signed in
or not, is cached. A probe that fails to start or times out is a discovery
failure with the same 30-second, 60-second, then five-minute backoff as server
discovery, and is never cached as absence.

When rediscovery changes which provider serves the selected PR, the registry
reports a capability change and the view model clears protected state through
its existing path before the next read.

## Components

All new code is NativeAOT-safe. The provider contract, the registry and the
GitHub CLI provider live in `Capacitor.Cli.Core` beside the HTTP client, each
provider in its own directory named after it. The server provider adapter, the
registration list and the card wiring live in the desktop app, because the
server source depends on the app's authenticated client lease.

### Reader provider contract

`IPullRequestReaderProvider` is what a provider implements:

- **Name**, a stable identifier such as `github-cli` or `server`.
- **ProbeAsync** returns a `PullRequestReaderStatus`: `Ready`, `ToolMissing`,
  `SignedOut`, `HostSignedOut` naming the host, or `Failed`; plus the tool name,
  install URL and sign-in command a CLI provider wants the note to show. The
  server provider reports `Ready` when `Supported`, `Failed` otherwise, with no
  tool hints.
- **Serves(kind, host)** decides from provider kind and host, never from
  network. A subject is served by its own kind and host; the card asks the same
  question for a session repository to decide whose note to show.
- **ParseLink(url)** turns a URL the provider recognizes into a subject, or null.
- **DiscoverAsync(repository, branch)** returns live links for a branch, or an
  empty list when the provider offers no discovery. The server provider offers
  none; session links are its separate, fixed role.
- **OverviewAsync** and **PageAsync** are the detail reads, with the same
  signatures and read kinds the view model already consumes.
- **PrLink(url, subject)** validates an outbound PR link for a subject it serves.
  The server provider keeps the `github.com` pin; the GitHub CLI provider
  validates the link against the subject's own host, whether or not that host is
  signed in, because the registry picks the validator by provider kind rather
  than readiness. The view model's own `github.com` gate and its direct use of
  the static link validator move behind the registry, which is what lets a
  provider with a different URL shape join later.

### Registry

`PullRequestReaderRegistry` implements `IPullRequestSource` and is what the
desktop wires into every workspace instead of the server source alone. It takes
the session-link source and the ordered provider list.

- **Discover** probes all providers, returns `Supported` when any is ready, else
  the session-link source's capability, and publishes the per-provider statuses.
- **List** reads the session links, parses legacy links through the providers,
  adds each ready provider's live discovery when the branch and
  primary-repository identity are known, and returns the merged canonical list.
- **Overview** and **Page** route per subject as described above.
- **ResetSession** forwards to the session-link source and every provider.
- A `ReaderStatuses` observable carries the per-provider statuses to the card.

Adding a provider is one new type under its own directory and one line in the
registration list. Nothing in the registry, the view model or the card names a
specific provider.

### GitHub CLI runner

One type owns locating and invoking `gh`.

Location on macOS and Linux resolves through the login shell's `PATH` from
`ILoginShellProbe.TerminalPathAsync`, then the process `PATH`, using
`BinaryProbe.Searching(...).Resolve("gh")`. A GUI app inherits launchd's `PATH`,
which omits Homebrew and user-local prefixes. Windows uses the process `PATH`
only; `BinaryProbe` already appends launchable extensions. The resolved path is
cached with discovery.

Invocation goes through `IProcessRunner` with an argument array, never a shell.
The environment overlay sets `GH_PROMPT_DISABLED=1`, `GH_NO_UPDATE_NOTIFIER=1`,
`NO_COLOR=1`, `GH_PAGER=cat` and `CLICOLOR=0`. The app never sets `GH_TOKEN`,
`GH_HOST` or `GH_CONFIG_DIR`, never reads `gh`'s keyring or configuration files,
and never stores a GitHub credential. Each call has a 20-second deadline.
`gh pr view` runs with a 16 MiB stdout cap because it bundles up to 100 reviews
and 100 comments in one payload; every other call keeps the 4 MiB cap that
matches the server client's response cap. Exceeding either is a transport
failure, not data.

Every identifier is validated before anything spawns, and a failure never spawns:

- host: present in the signed-in host set;
- owner: ASCII letters, digits and hyphens, not starting with a hyphen, at most 39 characters;
- repository: ASCII letters, digits, `.`, `_` and `-`, not `.` or `..`, at most 100 characters;
- number: a positive integer below 2^31;
- branch: a valid git reference name with no leading `-`, whitespace or control characters.

Every call requests JSON output:

| Purpose | Command |
| --- | --- |
| Signed-in hosts | `gh auth status --json hosts` |
| Overview and whole sections | `gh pr view <number> --repo <host>/<owner>/<repo> --json <fields>` |
| Live discovery | `gh pr list --repo <host>/<owner>/<repo> --head <branch> --state all --limit 20 --json number,title,url,headRefName,state,isDraft` |
| Inline threads and replies | `gh api graphql --hostname <host> -f query=<fixed query> -f owner=… -f repo=… -F number=… -f after=…` |

GraphQL variables are always passed as separate arguments, strings with `-f` and
the PR number with `-F`, never interpolated into the query text.

At most two `gh` processes run at once per app. Identical in-flight reads for one
subject, section and cursor are coalesced, and a completed read is reused for
ten seconds so the card and the reader asking for the same overview spawn once.

### GitHub CLI provider

`GitHubCliReaderProvider` implements the provider contract over the runner and
maps `gh` output onto the existing wire records, so the view model, the row
projections and the retention budget stay as they are.

- **Probe** reports `Ready` when the runner finds `gh` and at least one signed-in
  host, `ToolMissing` or `SignedOut` otherwise, with GitHub CLI as the tool name,
  `https://cli.github.com` as the install URL and `gh auth login` as the sign-in
  command. Present and absent are determined outcomes and carry no retry time. A
  `gh` whose `auth status` does not support `--json` probes as `Failed` with
  reason `unsupported_version`.
- **Serves** is provider `github` and a signed-in host. **ParseLink** accepts
  `https://<host>/<owner>/<repo>/pull/<number>` where the host is `github.com`
  or a signed-in host.
- **Discover** runs `gh pr list` for the branch on the repository.
- **Overview** is one `gh pr view` requesting title, url, state, isDraft,
  headRefName, baseRefName, headRefOid, body, updatedAt, reviewDecision, author,
  statusCheckRollup, reviewRequests, latestReviews, reviews and comments. It
  fills the overview and its nested checks, reviews and conversation summaries.
  Lifecycle mapping follows the server design: merged before closed, draft is an
  open PR with the flag set, anything else is Unknown.
- **Sections.** Checks come whole from `statusCheckRollup`, with both check runs
  and commit statuses mapped, each carrying its name, URL and outcome; the
  head-sha precedence rule over the rollup is unchanged. Reviewers are the union
  of `reviewRequests` and `latestReviews`. Reviews and conversation come from
  `reviews` and `comments`. `gh pr view` returns at most 100 of each; a list of
  exactly 100 — checks included — reports `coverage` limited and a lower-bound
  total, so the reader shows More on GitHub rather than claiming completeness.
  Threads come from the GraphQL `reviewThreads` connection in pages of 50, with
  each thread's root comment, path, side, lines, resolution, outdated flag and
  diff hunk; thread replies come from the thread's `comments` connection.
- **Snapshots and cursors.** Every section fetch mints a 32-byte random hex
  snapshot id. A paged section keeps its snapshot id and head sha for the whole
  cursor chain; a `next_cursor` is a minted opaque handle mapped in memory to the
  GraphQL end cursor. The map is bounded to 256 entries, least recently used
  first; a read on an evicted handle, or a head sha that changed since the chain
  started, returns a `Restart` read with reason `head_changed` or
  `snapshot_expired`, which the view model already handles.
- **Bodies.** Description, review, comment and thread bodies and diff hunks over
  256 KiB are cut to that bound with the truncated flag set, the server's own
  preview limit. Every item is `available`; the local path never redacts.
- **Access.** Every successful read reports `access_valid_for_seconds` of 30, the
  contract maximum, and `poll_after_seconds` of 30. Access is the user's own
  signed-in identity, so the lease is constant and the view model's existing
  renewal, masking and grace machinery keeps working without change.
- **Failures** map to the existing read kinds. A missing PR or repository is
  `Unavailable` with reason `not_found`; an authentication error is
  `Unavailable` with reason `tool_signed_out`; a rate-limit message is
  `Unavailable` with reason `rate_limited` and a `retry_at` sixty seconds ahead,
  which the view model treats as grace; another HTTP 403 is `Unavailable` with
  reason `tool_denied`; any other failed exit is `Unavailable` with reason
  `tool_failed`. A spawn that could not start is also `Unavailable` with reason
  `tool_failed`. A timeout is `TransportFailure`. Capped or malformed output is
  `InvalidProtocol`.

### Server provider

`ServerReaderProvider` in the desktop app adapts the existing
`ServerPullRequestSource` to the contract without changing it: probe maps the
server capability to a status, `Serves` is provider `github` on `github.com`
while `Supported`, the detail reads and `PrLink` delegate, link parsing and live
discovery return nothing. The same server source is handed to the registry as
the session-link source.

## Lifecycle, freshness and budget

Polling eligibility and schedule do not change: the link list and the selected
overview refresh on a 30-second target while the workspace is selected in a
visible, foreground window, with at least 15 seconds between renewals, and stop
when ineligible. When the GitHub CLI provider serves the selected PR, each
overview poll is one `gh` spawn and each list poll at most one. The worst case is about four spawns a minute per workspace, a
few hundred GitHub requests an hour against the user's 5,000-request hourly budget.

Reader pages load on demand only. Retention stays at eight pages per section and
32 MiB per workspace.

## Security

The local path adds a child process and removes a network hop. The child runs
without a shell, with validated identifiers, a fixed environment overlay, a
deadline and an output cap. Its output is parsed with `JsonDocument` at depth 64
and the size cap, then copied into the wire records; nothing from `gh` is
deserialized directly into a typed record. Every URL still passes `PrLink`,
`CheckLink`, `SafeLink` or `BodyLink` and opens through `LinkPolicy`; bodies still
render through `MarkdownView` with no remote images.

The app never holds a GitHub token. `gh` reads its own keyring; the app sees only
its JSON output. Uninstalling or signing out of `gh` is discovered at the next
probe and the card returns to the note.

## Compatibility

The server route, its wire contract, its pinned fixture and its tests are
untouched. An older server still yields legacy links, which the GitHub CLI
provider now reads natively when they parse as GitHub PRs. A newer server with the App broker
active serves users without `gh` exactly as designed.

There is no feature flag and no configured `gh` path; a machine whose `gh` is not
on the login shell's `PATH` sees the note. A configurable path is a follow-up if
it turns out to be needed.

A GitLab CLI provider is out of scope here but the seams it needs are in place:
the registry routes by provider kind and host, link parsing and validation are
per provider, and the wire records carry the subject's provider. What it would
still bring is its own runner, its `glab` field mapping, and reader labels that
say merge request where the subject is a GitLab one.

## Verification and delivery

- `GitHubCliTests` in the Core unit suite: location through a fake login-shell
  `PATH` and process `PATH` including Windows `.exe`, argument construction for
  each call, every invalid identifier refused before spawn, the environment
  overlay, the deadline and the output cap.
- `GitHubCliReaderProviderTests` driven by fixtures under `test/fixtures/gh/`
  captured from a real `gh` 2.93 run: an overview with both check-run and
  commit-status entries, a review list at the 100-entry cap, two thread pages
  with a head change between them, and each failure message.
- `PullRequestReaderRegistryTests` in the Core unit suite, driven by stub
  providers: registration-order precedence per subject, a subject no provider
  serves, capability derivation, legacy-link parsing, list merge and ordering,
  and a provider change on rediscovery reporting a capability change. One stub
  declares a `gitlab` kind with a merge-request URL shape, proving a provider
  with different links joins without touching the registry.
- View-model tests for the two moved gates: reading a subject the registry
  serves on a non-`github.com` host, and the main action opening only the
  selected PR through the registry's link validation.
- Card tests: the note for each local state, its host matching against the
  session's repositories, its suppression when another ready provider serves the
  host, and that the Capacitor sign-in affordances stay hidden.
- One integration test that runs the real `gh` against a public PR when it is
  installed and signed in, and is skipped with a reason otherwise.
- The existing server, client and view tests pass unchanged; existing view-model
  tests change only where the two gates moved behind the registry.

One PR in this repository carries the code, this spec, a `CHANGES.md` entry, and
the README's desktop prerequisites naming `gh` as optional for native PR reading.
The App activation checklist stays open as fallback work and no longer blocks
desktop release sign-off.
