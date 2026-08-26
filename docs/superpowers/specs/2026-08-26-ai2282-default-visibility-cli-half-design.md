# AI-2282 — Apply the Agents step's default-visibility answer

## Problem

The Agents step asks who may read the sessions this machine records from now on, records the answer on
`FirstRunAgentsDecidedEvent`, and serves it on the poll as `default_visibility`. **No CLI read it.** The
field was absent from `Capacitor.Cli.Core/FirstRun`'s wire models, so it was dropped at
deserialisation, and `SetupCommand`'s step 3 then prompted unconditionally and wrote its own answer to
the profile.

That made it the one place in the flow that asked a question and discarded the answer. The Agents
decision itself is applied; this rides the same event and was not.

AI-2215's spec named the CLI half as AI-2216, and AI-2216 merged without it — a gap between two
tickets rather than a decision.

## Where it lives

**On `FirstRunAgentsAnswer`, not beside it.** It rides the same decision and the same timestamp, so a
separate reader would need the same gate (the Agents step settled), the same null handling, and could
disagree with the choices about which decision it came from. One answer, two fields.

**Validated, never forwarded.** The value lands in profile config and is stamped on every session this
machine records afterwards. A stop a newer server invented would therefore be written to a file this
build owns and read back later by a server that may no longer mean the same thing by it — which is the
same argument the lane already makes for vendor keys and enum members, applied to the one field that
persists locally. `AppConfig.ValidVisibilities` is the closed set; AI-2215's own spec argued against a
parallel enum, and this is why it was right: the CLI already has the set, and a second spelling of it
would be a mapping table with no meaning.

A dropped value degrades to null, which leaves the profile exactly as it was — the same outcome as
never having asked, and the only degradation that cannot make a session more visible than the user's
existing configuration already allows.

## Null is not a value

Null covers two situations the CLI must not tell apart: the step is unanswered, and the user declined
everything. Neither asks for a default, so both fall through to the prompt.

**`IsDecline` says nothing about it.** Declining every harness and still choosing who may read future
sessions is a coherent answer — the screen asks two questions — so the two are read separately.

## The precedence question that turned out not to exist

The ticket expected a rule for `--default-visibility` against the browser's answer, on the precedent
that `--skip-<agent>` still wins over the Agents decision. There is no case: that flag is read **only**
under `--no-prompt`, and the browser leg is skipped under `--no-prompt` entirely (it waits on a human).
So the two can never both be present, and inventing a precedence rule would have been dead code with a
test that could only assert its own scaffolding.

Interactively the flag is ignored today, before and after this change. Making it live is a separate
behaviour change and not this ticket's.

## Copy

Step 3 reports rather than prompts, as steps 4 and 6 already do when the browser answered them. The
stop labels moved into `SetupCommand.VisibilityLabel` so the prompt's converter and the report share
one list — two lists that have to correspond are one list, and a stop described differently in two
places is how a user learns not to trust either.

## Tests

The boundary's own: every stop the wire can name is carried, null leaves the profile alone, an unknown
stop and an empty string both degrade to null, and declining every harness still carries the answer.
Two mutations confirm they bind — ignoring the validation, and never reading the field — and both had
to be reshaped to compile, because dropping the reference outright trips IDE0005/IDE0051 as errors.
Analyzer protection is worth noting but is not test coverage.
