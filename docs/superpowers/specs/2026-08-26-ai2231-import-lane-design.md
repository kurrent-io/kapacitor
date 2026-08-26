# AI-2231 — Report import discovery, and act on the import decision

## Problem

The server lane and the Import screen shipped, and nothing on the CLI side fed or read them. Against
a real `kcap setup` the screen rendered its waiting state for ever — correctly, since nothing had
reported — and any answer given on it was recorded and then ignored.

## Report the discovery

### Counts are a cell, not a margin

`ImportDiscoverySummary.Build` bucketed per repository *or* per window, never both. Separate totals
cannot answer the only question the screen asks — "how many sessions will *this* selection import" —
because that answer is an intersection.

The summary now carries `SessionsByWindow` per repository and `UnmatchedByWindow` alongside, from the
same single pass, so a margin cannot disagree with its cells. `SessionCount` stays a total computed
independently of the window list, so it still means "every session for this repository" when a caller
asks for windows that do not span them all.

Windows became keyed. `Build` takes `ImportDiscoveryWindow(Key, Since)` rather than a bare
`DateOnly?`, and `DiscoveryWindows()` derives both halves from `FirstRunImportWindows` — the same
constant the report travels under. That makes `kcap import --discover`'s own windows and the screen's
picker one list rather than two that correspond by convention. `--discover --json` is unchanged: it
renders `since` from the same record.

### The filter is applied to the sources, not to the counts

The session set is scoped before the summary is built, by building only the kept vendors' import
sources. Filtering afterwards would mean walking directories for agents the user had just declined,
and — because the counts feed a screen that states what a selection will import — reporting figures
the selection does not match.

**Only an explicit refusal drops a vendor.** The server normalises a harness nothing was turned on
for out of the decision, so on the wire "refused" and "never offered" are identical. This machine
knows the difference, because it knows what it reported: `FirstRunMachineReport.Detected` is the set
the screen could offer, and a vendor in it that the answer does not record is refused. One with
history on disk and nothing installed now was never offered, so its absence says nothing and its
history still counts. An unanswered step scans everything, for the same reason.

### Where it runs

Inside the browser leg's poll loop, gated on the Agents step having settled — because its answer *is*
the filter. The scan runs once; the POST that delivers it is retried every tick until the server takes
it, since the screen is what waits on it. A scan that throws, or that produces nothing usable, reports
nothing: the screen keeps waiting, which is honest, where claiming an empty disk would be a failure
rendered as a result.

Bounds are the server's own, and the ticket's "32 KiB body" is not one of them —
`FirstRunFlowEndpoints.MaxImportBytes` is **96 KiB**, sized so that 200 maximum-length repositories
cannot be refused. So the CLI respects the repository cap and needs no byte cap of its own: 200
repositories, newest activity first (which is the order the summary already produces), with
`repo_total` counted before the cap so what it hid is disclosable. An over-long owner or name is
**dropped, not truncated**, because that pair is what resolves back to `--repo owner/name`.

## Act on the decision

### Two passes, because `--private` is per invocation

`OnlyMe` repositories import under `--private`, `Shared` ones without, each scoped to the chosen
window's `--since` and to the decision's vendor flags, with `--skip-title` unless titling is `Local`.
Narrowest first, so a run interrupted between the passes has uploaded the private history rather than
the shared.

The shared pass then needs an explicit per-session `visibility=org` write, which is what
`SetVisibilityForAll` is. Without it "shared" delivers owner-only on every provider but `GitHubApp`:
the profile default produces the `default:org` class, which `VisibilitySql` admits only where the
repository's owner matches the tenant's configured org — and that is empty unless `Auth:Provider` is
`GitHubApp`. `SetVisibilityNoneForAll` became the second caller of one generalised helper rather than
gaining a near-copy.

### Where it runs, and why polling stops

Also inside the leg, when the decision lands. The import writes its own progress, and two live Spectre
renderables cannot share a terminal — so the poll pauses for the duration rather than running
alongside. Nothing is lost by it: the only thing left to notice is the flow finishing, and the next
tick sees that. The browser is on its last screen while it runs, which is what the flow design asks
for.

**Both lanes add their elapsed time back to the poll budget.** That budget exists to catch a terminal
nobody is sitting at; a disk scan and an upload are work, and letting them spend it would abandon a
flow that is progressing.

**The decision's timestamp is a cursor, not a flag.** The server advances it only when the answer
*changes*, so going Back and widening the window runs the wider import — which has real work in it —
while re-confirming the same answer runs nothing. It is stamped before the run rather than after: a
throw must leave a failed upload to `kcap import`, not to a retry on the next tick.

Setup's step 6 then **reports rather than prompts**. It can only offer the current repository, and the
screen just chose several, so re-asking would offer to redo part of what already ran.

### The one silent failure worth naming

A decision names repositories and a vendor list. If this build knows none of the vendors named, the
mapping drops them all, the scan finds nothing, and the import would have reported success for history
that never moved. `FirstRunImportAnswer.NoReadableVendors` is that state — reachable only by this
build having dropped every vendor, since a machine that truly scanned none would have offered no
repositories to choose — and it skips the run and says why.

By contrast an unknown **window** or **titles** answer voids the whole decision: both name what to do
with everything selected, and there is no safe guess, since narrower silently skips history and wider
silently uploads more than was asked for. An unknown **level** costs one repository and is counted, so
the rest of the answer still applies.

## Tests

The loop's ordering and guards are driven over a fake lane and a `FakeTimeProvider`, so neither half
needs a disk or a socket. Eight mutations confirm they bind rather than describe: removing the Agents
gate, scanning a refused vendor, treating a never-offered vendor as refused, dropping the report
retry, collapsing the decision cursor to a flag, stamping it after the run, spending the backstop on
either lane, and reporting a missing window count as zero.

The wire is pinned in both directions against WireMock — every key the server reads by name, and a
decision parsed out of a canned response — because a rename on either side surfaces as a picker with
no figures rather than as a failure.

## Out of scope

- **The Agents step's `default_visibility`.** The poll has carried it since the visibility choice
  shipped, and no CLI reads it: setup's step 3 still prompts unconditionally, so the browser's answer
  is recorded and then overwritten. A separate defect, not this lane's.
- **Re-scanning.** The scan runs once per leg. A browser tab does not outlive the disk changing under
  it by enough to matter, and rescanning would cost minutes per tick.
