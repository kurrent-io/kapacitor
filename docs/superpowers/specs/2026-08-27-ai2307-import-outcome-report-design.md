# AI-2307 — Report the import outcome to the first-run flow

## Problem

The flow's `POST /api/first-run/flows/{id}/import-outcome` route takes `{imported, skipped, failed,
reason, decided_at}`, folds into `FirstRunFlowState.ImportOutcome`, and **had no caller**. So the Done
screen's `N imported · N skipped · N failed` caption was unreachable, and — because the outcome is also
the signal that the run *finished* — the screen could not tell a working import from a stalled one.

Note the route ships with AI-2038, which is **still open** as kcap-server#1671. The ticket described it
as shipped; it is not. This half is written against the contract on that branch, and the CLI degrades on
a 404 the way it does for every other route a newer server introduced.

## Where the counts come from

`ImportCommand.FinalCounts` already exposes exactly the route's three, and they are not re-derived here:
`Imported` folds loaded and resumed (a resume is an import that finished, not a third thing), `Skipped`
folds already-loaded, too-short and excluded, and `Failed` folds probe errors and upload errors.

**Two things the run has that the three counts did not.**

*A session held back by the visibility preflight.* AI-2222 made `--private` fail closed: a session whose
privatisation write is lost is dropped from the upload rather than uploaded shared. Those sessions land
in none of `FinalCounts`' three — not loaded, not skipped, not errored — so a report built from it alone
silently understates the total. They fold into `failed`, which matches that field's own definition:
should have landed and did not, and re-running retries exactly these.

*A pass that produced no accounting.* `--private` is per invocation, so a decision spanning both levels
runs two imports and the report has to sum them. A pass that threw, or returned without reaching its Done
grid, leaves its sessions unaccounted — and **three counts cannot express "some unknown number failed"**.
So the lane returns null for that run and nothing is reported at all: sending the surviving pass's figures
would state a clean import over a run that lost one. The screen's existing "cannot tell" is honest about
that; a wrong number is not. This is the same argument the ticket makes for retrying a lost status before
counting it.

## Null is not (0,0,0)

The distinction is load-bearing in two places, and in both the wrong choice reads as success:

| Situation | Reported |
| --- | --- |
| ran, moved nothing, nothing to move | `(0,0,0)`, no reason |
| ran, lost a pass | nothing |
| repositories chosen, no vendor readable | `(0,0,0)` + `no_readable_agents` |
| decision this build cannot map | `(0,0,0)` + `decision_unreadable` |

Three zeroes are also what a clean run over an already-loaded history looks like, which is the whole
reason the reason token exists. The server enforces the other half: it rejects a token on an outcome that
moved something, so a refusal is three zeroes or it is nothing.

**An empty answer has two causes and only one is a decline.** `Choices` is also empty when every
repository in the decision named a level this build cannot map, and reporting *that* as a clean zero tells
the screen "you chose not to import" about a user who chose otherwise. `FirstRunImportAnswer.IsDecline`
already draws the line (`Choices == 0 && Unreadable == 0`) and is what the branch tests, so the
all-unreadable case reports `decision_unreadable` alongside the other two unreadable shapes.

The partial case — some levels readable, some not — runs and reports its counts, with the dropped
repositories not represented. That is a limitation rather than a choice: the wire has one reason field and
the server rejects a reason on counts that moved something, so there is nowhere to say "and two more were
dropped". The terminal summary does say it.

**A deliberate empty exit reports its zero.** `HandleImport` returns 0 without reaching `onFinished` on
three "found nothing" paths — no sessions in scope, no transcript files, no agents installed. Read
through the null contract above, a clean run over an out-of-scope selection therefore looked like a lost
pass and reported nothing at all, which loses the finished signal this change exists to send. Those exits
now report a measured zero, which is the rule the discovery report in the same file already follows:
zero is an answer, and silence is indistinguishable from having died.

## The spin this fixes

`FirstRunFlowOutcomes.Import` returns null when the decision names a window or titles value this build
cannot map. `ActOnImportDecisionAsync` returned early **without stamping the cursor**, so it re-evaluated
that same unreadable decision on every poll tick for the life of the flow, reporting nothing.

Polling again cannot make a newer server's vocabulary readable, so this is reported rather than retried:
the cursor moves and `decision_unreadable` goes to the screen. The server had defined the token and
nothing could produce it.

Scope note: this was found while adding the report, not filed separately, because the fix is one stamp
inside the branch the report is being added to. Leaving a known infinite loop in place to keep a diff
tidy is not a trade worth making.

## Correlation is structural, not asserted

`decided_at` identifies the answer that ran, and the server records nothing for a superseded one. It
cannot be wrong inside the lane: `Import(view)` builds `answer.DecidedAt` *from* `view.ImportDecidedAt`,
so the two are one value. A mutation swapping them is an equivalent mutant and no test can separate them
— worth stating rather than covering.

Where it *can* go wrong is the retry. The report is held in `ImportLaneState.Outcome` across ticks, so a
decision may change while one is still owed. `DeliverOutcomeAsync` takes no view and therefore cannot
re-stamp, which is the guarantee; the test pins the observable half — a held report keeps its own stamp
while a later decision reports its own.

The flush matters for the same reason. The poll returns on a finished flow, so the tick that reports is
the last one that exists, and an outcome refused once would otherwise never be sent.

**The retry is deliberately uncredited against the poll budget.** The loop extends its own 30-minute
backstop by time spent on work — a disk scan, an upload — on the reasoning that those are the flow
progressing rather than a terminal nobody is sitting at. The owed-outcome retry is neither: it runs on
every tick for as long as the report is refused, so crediting it back would let a server that never
accepts the report stretch the backstop from thirty minutes into hours. Only the run itself is credited.
A first draft credited every exit, which is exactly that bug.

## Retrying a status, opt-in

`SendWithRetryAsync` retried transport faults with backoff to a budget but **returned** any completed
response, so the caller read `.StatusCode` and counted the session failed. One 503 during a deploy cost a
session with no second attempt, while an unreachable server got thirty seconds of trying. That asymmetry
is what made `failed` too weak to show — a number mixing one unlucky response with a persistently stuck
server is not actionable either way.

**Opt-in per call site rather than a change to the shared helper.** Every hook, watch, daemon and MCP path
goes through it, and their budgets are shaped around a single attempt; the hook path already keeps
`PostOnceAsync` for exactly that reason. Turning it on for all of them at once would be a timing change
across the CLI in service of one screen's caption. It is set on the three transcript uploads and the
visibility PUT — the calls whose loss is counted and shown to someone.

Retryable is 408, 429 and 5xx: a timeout, a rate limit, and anything the server calls its own fault. A
4xx is a refusal the server meant, and re-sending a body it will refuse identically just spends the
budget.

Two details that are not incidental:

- **An exhausted budget returns the status, never a throw.** The call sites catch `HttpRequestException`
  only, so throwing would turn a 503 into an unhandled crash mid-import — and it would report a transport
  failure about a server that answered every time. The last refusal is held for that, handed over by
  nulling the field so the `finally` disposes only what nobody received, and dropped otherwise so it
  cannot leak a connection. **A refusal in hand outranks a late transport fault** on the same reasoning:
  the server did answer, and a connection reset on the final attempt must not erase what it said.
- **`Retry-After` wins when longer than the backoff**, in either header form, and is still capped by the
  remaining budget so a server asking for an hour cannot stretch an import by an hour.

## Tests

The lane's summing and folding are pinned directly: totals across both passes, a resume counting as
imported, a probe error as failed rather than skipped, a held-back visibility write as failed, and both
null paths (a throw, and a pass with no grid) reporting nothing — plus a clean run *not* collapsing into
null, since that is the case the narrow null exists to stay out of.

The loop's four report shapes each have a test, and the spin has one that would have failed before this
change: three identical unreadable polls produce exactly one report and no import.

The wire round-trip goes through the source-generated context against WireMock, asserting the JSON names
and that a reason token crosses verbatim. Nothing else covered it — every other test in that file builds
the request object directly, so a naming or AOT-binding slip would have left the screen waiting for ever
with the suite green.

Sixteen mutations, fifteen killed and one shown to be equivalent (the correlation swap above). The retry
predicate is pinned from both sides: retrying a 4xx kills six tests, not retrying a 429 kills two, and
defaulting the flag to on kills the opt-in test.

Four of those pin defects external review found here, and each has a test that fails without its fix:
treating every empty answer as a decline, the nothing-in-scope exit reporting nothing, crediting the
stalled report back to the deadline, and a late transport fault outranking the refusal in hand. Two of
them initially survived mutation against branches the tests did not reach — the deadline one exits through
the cursor-match arm rather than the not-settled one, and the transport one needs the fault to land
*after* the budget to reach the unguarded catch. Both were mutation-aim errors rather than coverage gaps,
and both die once aimed correctly.
