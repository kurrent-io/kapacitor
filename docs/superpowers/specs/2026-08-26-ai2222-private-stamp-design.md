# AI-2222 — `--private` stamps `private`, rather than saying nothing

## Problem

`kcap import --private` promises owner-only history — the confirmation prompt says
`visibility: private (--private)` out loud. It delivered that eventually, and only eventually: every
session it uploaded was **org-visible for the whole interval between upload and the privatising
pass**, and permanently for any session whose PUT failed.

The mechanism is that **an omitted `default_visibility` is not "no default"**. The server's generated
column reads

```sql
WHEN COALESCE(default_visibility, 'org_public') = 'org_public' THEN 'default:org'
```

so a session-start that says nothing lands as `default:org`, and `default:org` is not inert. Two
`VisibilitySql` arms admit it — the configured-org arm, and `ProjectDefaultArm`, which is
**provider-independent**, so the exposure was never GitHubApp-only. `SetVisibilityNoneForAll` pulled
each session back to `explicit:none` one PUT at a time at the very end of the run. For a large
import that window is minutes. For a session whose PUT failed it never closed: those failures go to
stderr and are swallowed by design, so nothing retried and nothing surfaced it.

Six of the nine sources omitted the field under `forcePrivate`: the chain path (Claude, Codex) via
`chainDefaultVisibility = forcePrivate ? null : defaultVisibility`, and Cursor, Copilot, Gemini and
Kiro via a `!ctx.ForcePrivate` guard on their own stamp. The other three — Pi, OpenCode,
Antigravity — already stamped the literal `"private"` in their payload builders, which is what made
the hole easy to miss: a reader checking one source found it correct.

## Why the split existed

Not an oversight. The 2026-07-20 unified-agent-install-and-import spec, which introduced the Step-3
default, deliberately left force-private alone:

> Uniformly folding `forcePrivate` into a shared rule … [would] add a Cursor payload mechanism — a
> standalone-`--private` semantic expansion we deliberately scope out.

That spec also already contains the argument this change generalises. Reasoning about the chain path
alone, it concluded that a session can succeed at session-start and then fail before
`importedSessionIds` is ever reached, so **relying on post-hoc privatisation is unsafe** — and
resolved the value up front for that one path. The same failure shape exists in every routed source;
only the chain path got the remedy.

So this is the expansion that spec postponed, filed as a bug because the postponement is a privacy
window rather than a missing feature.

## The change

**One rule, in one place.** `ImportContext.VisibilityStampFor(status)` is the only code that decides
what a session-start carries, and every source reads it:

```csharp
public string? VisibilityStampFor(ImportCommand.ClassificationStatus status) =>
    ForcePrivate                                        ? "private"
  : status is ImportCommand.ClassificationStatus.New    ? DefaultVisibility
  :                                                       null;
```

The chain path has no `ImportContext`, so `HandleImport` resolves the same rule into
`chainDefaultVisibility = forcePrivate ? "private" : defaultVisibility` and threads that through as
before.

**The two halves are deliberately asymmetric**, and the asymmetry is the whole reason a single
`string?` can express both. The Step-3 default is a *creation* default and says nothing about a
session that already exists, so it is stamped on `New` alone. `private` is sent on every status
because it costs nothing and `New` is the one status it can still reach — see below for why it is not
a floor.

That makes the change **a widening at every site and a narrowing at none**. Pi, OpenCode and
Antigravity stamped `private` on every status already; the other six now match them. Nothing that
previously carried a value stops carrying one.

Consequences worth naming:

- **The three builder-level stamps are gone**, along with the `bool forcePrivate` parameter on
  `BuildSessionStartPayload` in Pi, OpenCode and Antigravity. There is now exactly one stamp site
  per session-start payload, which is what stops the three answers drifting to four.
- **Antigravity's `AlreadyLoaded` repair branch** posts session-start from a second call site, so it
  holds the payload in a local and stamps it like the main path rather than passing a flag down.
- **`SetVisibilityNoneForAll` becomes belt-and-braces.** It is not removed: it still corrects
  sessions an older CLI created without a stamp, and it is the only thing that reaches
  `explicit:none` rather than `default:private`. What changes is that privacy no longer *depends* on
  it — except on the one path below.
### A stamp cannot narrow a session that already exists

The read model has two branches for a `SessionStarted`, and they differ on exactly this column. The
ordinary one writes

```sql
default_visibility = CASE
    WHEN sessions.default_visibility = 'prestart' THEN @default_visibility
    ELSE COALESCE(@default_visibility, sessions.default_visibility)
END
```

so a non-null stamp wins. The `isImportOverlap` branch — which a re-import of an already-closed
session takes — omits the column from its `SET` list entirely, and says so: *"every
terminal/lifecycle column (…, `default_visibility`) is simply omitted from the SET list, leaving it
exactly as stored."*

**So the stamp is a creation-time value only.** It closes the window for a session this run creates,
and does nothing for one it revisits. `private` is still sent on every status, because it costs
nothing and `New` is the status it can still reach — but it is not a floor, and nothing should be
written as though it were.

**For a revisited session the closing pass is the only mechanism**, which is why membership in it
cannot depend on an outcome. `importedSessionIds` gains a session only where this run did new work;
`privateScopeSessionIds` covers only routed sources that attach child content on replay, which
excludes Copilot, Kiro, Pi and OpenCode. So a routed `Partial` replay that failed, or a chain resume
whose session-end POST failed, was privatised by nothing at all.

`scopedSessionIds` closes that: every in-scope session the server already has, captured before the
import. The bound is classification status — the scope filter runs before classification and an
excluded source has its status flipped to `Excluded`, so `New | Partial | AlreadyLoaded` is exactly
the selected-and-present set, and a too-short session is not written to.

### The window, not just its eventual closure

A closing pass guarantees a revisited session ends up owner-only. It does not stop the content
uploaded into it being readable while the run proceeds — which is the window this ticket is named
after, so leaving it open would be fixing the symptom the title does not mention.

So an existing session is narrowed **before** anything is uploaded into it: one `visibility=none`
pass over the in-scope `Partial` and `AlreadyLoaded` sessions, ahead of both import phases. `New` is
deliberately absent — it does not exist yet, so there is nothing to narrow and a write would name a
session the server has never seen; its creation stamp is the mechanism that works there.

**And it is fail-closed per session.** The write is best-effort — it logs each failure and swallows it
— so awaiting the pass establishes nothing on its own. A session whose write was lost is dropped from
the run: from `chains` and from `routed`, with a line naming it, and counted into the run's
`VisibilityFailures`. Uploading into it would publish new content to exactly the audience the user
just excluded, which is worse than not importing it at all. Per session rather than aborting, so one
lost write does not cost the rest of the history.

The closing pass stays, and its role is now precise: recovery for a session created during the run.
A revisited session is therefore written twice, which is why the tests assert *which* sessions were
privatised rather than how many writes it took.

**And it leaves `--private` with nothing for the in-scope capture to do.** The shared stop's
`scopedSessionIds` (AI-2231) exists because sharing has no other mechanism: no pass ahead of it —
widening opens no window to close — and no usable stamp, since `org_public` as a default lands in
`default:org` rather than the class the predicate admits unconditionally. Under `--private` both of
those exist, so the capture would add writes and no protection. Mutation-checked in both directions:
disabling it for the shared stop fails, and disabling it for the private one changes nothing.

## Tests

`ImportVisibilityTests` is where the deferred behaviour was pinned, so the same file is where the
reversal is pinned. Nine tests asserted the field's absence under `forcePrivate` and now assert
`"private"`; the `RoutedSourceCase.OwnPrivateStamp` flag, which existed only to branch the matrix on
which of the two answers a source gave, is deleted along with the branch it fed. A matrix row is
added for **`forcePrivate` × `AlreadyLoaded`**, which nothing covered and which several sources —
Antigravity in particular — answer from a branch neither the `New` nor the `Partial` row reaches.

Four mutations confirm the tests bind the rule rather than describe it: reverting either half of
`VisibilityStampFor`, reverting the chain resolution, and removing Antigravity's repair-branch stamp
are each killed (20, 12, 1 and 1 failures respectively).

## Out of scope

- **Subagent / child session-start payloads.** Whether a nested child stream carries a visibility of
  its own, or inherits the parent's server-side, is a separate question from the one this ticket
  names.
- **A retroactive sweep.** Sessions an earlier `--private` run left at `default:org` stay there until
  something privatises them; per the README, re-running with `--private` is the supported route, and
  it now closes the window rather than racing it.
