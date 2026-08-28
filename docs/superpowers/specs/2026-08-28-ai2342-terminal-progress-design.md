# AI-2342 — Mirror the browser's progress in the terminal

## Problem

The browser leg is the longest stretch of `kcap setup` and the terminal said `.` for all of it. The
poll loop held a full `FirstRunFlowResponse` every two seconds and threw all of it away for rendering:
`IFirstRunFlowProgress.PollTick()` took no arguments. At the moment the user clicked Continue on the
Agents screen, the CLI had their vendor list in hand and printed a dot.

`Waiting…` covered four different waits — sign-in, the agents choice, the import choice and the final
Done click — and `view.Step` already distinguished them. That was the largest legibility win available
and the cheapest part of the change.

## The seam

`PollTick()` is replaced by two calls carrying the closed sets `FirstRunFlowOutcomes` already defines,
rather than handing the renderer a wire type it could read anything out of:

- `Waiting(FirstRunFlowStep? flowStep, bool healthy)`
- `Settled(FirstRunFlowStep flowStep, FirstRunStepOutcome outcome, string? detail)`

The loop maps wire to vocabulary, which is where that boundary already lives, so **no new wire data is
needed for any of this**. `detail` has exactly one source — `FirstRunFlowOutcomes.Agents(view)` →
`FirstRunAgentsAnswer.Labels` — and is null for every other step: the labels are already mapped through
`HarnessCatalog`, so naming them back is not forwarding a wire string.

`flowStep` is nullable because a step a newer server invents reaches the CLI as one this build cannot
name, and the wait still has to read as a wait. `flowStep` rather than `step` only because CA1716 rejects
the latter on an interface member.

`detail` cannot express everything the Agents step can be, and that is where the first draft was wrong.
`Labels` is empty for a real decline **and** for an answer naming only vendors this build cannot map, so
rendering null as "chose not to set any agents up" claimed a decline the user never made — the rule
AI-2307's spec states as "an empty answer has two causes and only one is a decline". The line is worded to
be true of both instead of widening the seam: step 4's `Unrecognised` warning is where the second cause's
reason and remedy already live, and a sentinel string would be the trap this repo names elsewhere.

### Why the unhealthy poll needs its own flag

The `SlowDown` and default arms both called `PollTick()`, so a 503ing server and a user reading a screen
rendered identically. A spinner saying `Choose your agents in the browser` while the server has been
silent for four minutes states a fact the CLI does not have, so `healthy: false` replaces the step's
wording outright rather than decorating it.

### Edge-triggered off the loop, not the renderer

`Announce` walks `KnownSteps` against a dictionary the loop owns. A poll blip that returns the same state
would otherwise tick a step twice, and a renderer comparing against what it last printed would be guessing
at state the loop already holds. A resumed link that comes back with three steps already settled on its
first poll prints all three ticks, which is the honest history and is what tells the user it is the same
link.

It runs before the import and machine-action lanes, so "chose your agents" precedes the scan it starts —
and the wait itself is set before *that*, not at the foot of the arm. Either lane can run for minutes, so
a wait still describing the previous poll spends all of it naming the wrong screen, or repeating an
unreachable-server warning the same poll has just disproved. `ImportEnded` reasserts whatever state is
held, which is what made the stale value survive the import too.

**Keyed on what the terminal was told, not on which steps it has heard of.** A `HashSet<FirstRunFlowStep>`
was the first shape and it is wrong: going Back in the browser, changing the harnesses and re-confirming
leaves the Agents step settled throughout, so presence alone suppresses the second tick — and with the
step 4 restatement gone (below), the terminal's only statement about what gets installed names the answer
that was abandoned. A regression created by the two halves together; neither was wrong alone.

The trigger is the step's **decision stamp**, `AgentsDecidedAt` / `ImportDecidedAt`, which is exactly what
it is for: the server advances it when the answer changes and not when it is merely re-confirmed. Comparing
the *content* instead looks equivalent and is not — the stamp is absent exactly when the decision is, so a
later view that carries neither must not be read as a change. Content comparison reads that absence as "the
answer became empty" and prints a decline over a choice the user made. So: announce when the step is new,
when its outcome changed, or when a stamp is present and differs; never on a stamp's absence, and the last
known stamp is kept when a view omits it.

## `t`, not any key

`IKeyWatcher.ReadKey()` already existed, so this is: read one key, compare case-insensitively, discard
anything else and keep waiting. Not Enter or Space, which get pressed by accident. Not `q` or `x` either:
both read as "quit setup", which is the one wrong message — the key hands the flow back to the terminal
and setup carries on.

**Every other key is consumed rather than left buffered.** A byte examined and left in place would be
re-examined on every 200ms slice for the rest of a thirty-minute wait. The pre-wait drain stays: a
leftover Return still deserves discarding even where it can no longer dismiss anything on its own.

## Withdrawing the offer once they have picked up

Handover is one-directional. Press `t` and the terminal spends every step that had settled, but nothing
ever reads the flow again, so later browser clicks are unseen. So the offer stands only while nothing past
the gate has settled: once any screen past it has an outcome the browser is where the answers are coming
from, and more are still to come. **Not sign-in:** the CLI already held a token before this leg ran, so that
step settling is not the user investing anything in a browser.

Stated that way rather than as "once a decision has been invested", which the code does not enforce and
could not: `IsSettled` is the permissive test, so `Skipped` and `Failed` withdraw the offer too, and
`Skipped` conflates a user declining with a step that never applied to this machine.

**The mechanism stays live throughout.** It is also the only way out of a thirty-minute wait, and a closed
tab still needs it. Only the advertisement is withdrawn.

This only works because the offer lives in a redrawn region. In the dot rendering it was scrolled history
and could not be taken back, which is why the two halves of this change need each other.

## The copy that was landing twice

`SetupCommand.BrowserAgentsSummary` rendered `Chosen in the browser: Claude Code, Pi` at step 4, after the
leg had ended. The live tick now says the same thing as the step settles, so the summary keeps only the
`Unrecognised` warning — which is not a restatement, and whose remedy is a command to run there.

`BrowserImportSummary` is deliberately left whole. Step 6 has nothing else to say, and what it reports is
the run's *outcome* — including the partial-import warning, which has no live equivalent — where the live
line is the choice being acted on. For the same reason the Import tick is neutral (`Chose what to import`):
a decline settles that step exactly as a selection does, and nothing in the loop can tell them apart.

The periodic URL reprint is dropped. It existed because dots scrolled the one line a machine with no
browser of its own needs to read; with four ticks and a pinned region there is nothing to scroll it away.

## Not `AnsiConsole.Status()`

A Spectre live region owns the console for the whole of one delegate, and this wait has to hand the console
over mid-flight: `ActOnImportDecisionAsync` runs the import inline and it renders its own bars. Two live
renderables cannot share a console, and nesting them throws. Driving a `Status` region from a background
task while the poll thread writes through the same console is the same problem with a race added.

`TerminalWaitLine` is therefore hand-rolled: one or two rows redrawn in place under a single lock, with a
frame timer at 100ms — the poll interval is two seconds, so a spinner advancing per tick would read as
frozen. It can be stopped and restarted as often as the import takes, which is what makes the existing
`Importing()` / `ImportEnded()` pair enough and leaves the flow's own `finally { WaitEnded(); }` as the
teardown. **Nothing had to wrap `RunAsync`.**

Lines are **truncated rather than wrapped**: a wrapped line costs a row the cursor arithmetic does not
know about, and the block would then erase the wrong rows. `Drawn` is exposed for the same reason — an
erase that gets the count wrong takes a line the caller already committed to the scrollback with it.

Four ways that arithmetic can be lost, all fixed rather than accepted, and the width took three passes to
get right — worth recording, because each pass fixed half of it.

The width is read live, not from `AnsiConsole.Profile.Width`, which Spectre fixes when the console is
created: a terminal narrowed mid-wait would be clipped against the old width and wrap. It is then used as
measured, with no floor — raising a narrow terminal to a comfortable minimum renders it as though it had
columns it does not have, which is the same wrap by another route. And **clipping the text was never
enough**: four cells of prefix sit before a character of it, so below the prefix's own width the prefix
wraps however hard the text is clipped, and an unreadable width has no safe number to stand in for it
(wider wraps, narrower is the same lie). So the block is drawn only when the width is known and at least
`MinWidth`; otherwise nothing is drawn and the permanent lines stand on their own, which is the
redirected-output behaviour reached by a second route. A later widening resumes.

The width is injected for the same reason the control writer is: the wrap happens inside Spectre's writer,
so the row count is its only observable consequence and a test host's real console is not the subject. The width is read from
`Console.WindowWidth` on every draw, not from `AnsiConsole.Profile.Width` which Spectre fixes when the
console is created — a terminal narrowed mid-wait would otherwise be clipped against the old width and
wrap. And the move between the two rows writes CR as well as LF: a lone LF does not return to column 0 on
a console with newline auto-return disabled, and the offer row would start mid-line and wrap. A resize can
still reflow rows underneath the block, which is beyond a hand-rolled one, but nothing here is the cause.

## Ctrl+C restores the terminal

`InteractiveLifetime` already had `setup` in its allow-list and exits 130 deterministically, so naming it
in the copy documents what happens. But `Environment.Exit` from a signal handler skips a live region's
teardown and **left the cursor hidden** — a region hides it for the duration of a render and restores it on
dispose, and there was no `CursorVisible` or show-cursor sequence anywhere in `src/cli`.

The four exit paths there go through `Environment.Exit`, which runs `ProcessExit`, so one registration
covers Ctrl+C, both signals and the parent watchdog, as does a normal return. Not *every* way a process can
die: a SIGKILL, a `FailFast` or a closed Windows console window delivers nothing to run it on, and in those
cases the terminal is going with it. Written raw rather than through Spectre: this runs on the way out of a
signal handler, where taking a renderer's locks is the last thing worth doing.

This already bit during the import's own `Progress` regions, so it is a pre-existing bug. This change puts
a region across the entire wait, which is where people are most likely to give up and interrupt, so it
turns a narrow bug into the common case and therefore owns fixing it.

## Non-TTY

`SpectreFirstRunFlowProgress` gated on nothing. Dots were safe when piped, but a region would print nothing
at all for the whole wait, which is worse. The gate is `!Console.IsOutputRedirected`, the same one
`ImportCommand` uses: transitions print as plain lines, the spinner and the offer line are not drawn, and
the unreachable notice is said once per episode rather than once per tick.

The offer is printed plainly there instead, once, when there is a keyboard at all. **It cannot be withdrawn
on that path** — there is no pinned line to take it back from — so the withdrawal above is a terminal-only
property and the README says so rather than claiming it generally. Printing it anyway is the better trade:
the alternative leaves a redirected run whose tab has closed with nothing but Ctrl+C, which kills setup
outright instead of handing the leg back.

`CanWatch` (`!Console.IsInputRedirected`) is what gates both the offer and the key, never the output gate,
so no unpressable key is ever advertised and no pressable one hidden — all four input/output combinations
agree.

## Not in scope

The import's slot rows still name sessions by raw id. Naming them by anything human is a separate change
that also affects plain `kcap import`. **Session titles do not exist at import time** — they are generated
afterwards by a server-side call, and `SessionMetadata` has no title field — so any such change would have
to use the repository or the first prompt, not a title.

## Tests

The loop's half is pinned over the fake channel: one announcement per step however many polls repeat it, a
resumed link announcing all four, the outcome crossing as the server reported it (Import is `Skipped` on the
Done fixture, and a skip is not a tick), the Agents detail carrying labels while no other step does, and both
unhealthy shapes — a blip after a good poll, and a first poll with no step to name at all.

`FakeKeys` gained a key and now consumes it on read, which is what the real watcher does; a fake that left
the press buffered would spin the loop that reads past keys it does not act on. A non-handover key is
asserted not to dismiss.

A changed answer is announced again, a re-confirmed one is not, and a view carrying no answer does not
un-say the one it had — the last of those pins the trap the content-keyed draft would have walked into,
rather than the bug the presence-keyed one had.

The renderer's copy is asserted from both sides of the de-duplication: the step 4 summary must not name the
harnesses, and the live line must.

`TerminalWaitLine`'s row bookkeeping and cursor discipline are the part nothing else can observe, so the
control writer is injected and read back: shrinking the block erases the rows it *had*, the cursor is hidden
once and given back once, a second `Stop()` does not give it back twice, a restart after the import hides it
again, and a redirected stream gets no escape sequences at all.

The interleaving of markup and control codes was checked by reading the emitted stream once, by hand, rather
than asserted — the markup half goes through Spectre's console and the control half through the injected
writer, so a test can hold one or the other but not the order between them.
