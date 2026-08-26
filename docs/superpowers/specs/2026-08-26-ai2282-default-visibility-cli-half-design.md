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

A dropped value degrades to null, and what null then means depends on whether the step settled — see
below. Either way the degradation cannot widen anything: on a settled step the profile is carried
unchanged, and on an unsettled one the user is asked.

## Null is not a value, and the two nulls are not the same

The wire field is null both when the step is unanswered and when the user answered it and set nothing.
An earlier draft of this design said both fall through to the prompt. **That is wrong, and it was the
one defect an external review found here**: the prompt cannot leave the profile alone, because its
cursor starts on `org_public`, so a single Return on a re-run silently widens an existing `private` —
on a screen the user had already answered. The lane's own contract says a null answer leaves the
profile alone; falling through to the prompt violates it.

So the two nulls are separated by whether the **step settled**, which `FirstRunAgentsAnswer` already
encodes by existing at all:

| Agents step | `default_visibility` | Step 3 |
| --- | --- | --- |
| settled | a stop | applies it |
| settled | null | re-writes what the profile already holds, and says so |
| never settled | — | prompts, exactly as before |

Re-writing the profile's own value rather than skipping the write keeps `defaultVisibility` a single
non-nullable string for everything downstream — the profile write, the `saved` context, step 6's
import stamp — instead of threading a nullable through paths that have no meaning for one.

The rule is `SetupCommand.DecideVisibility`, extracted because `HandleSetupAsync`'s interactive
branches have no test coverage at all: every `HandleAsync_*` test drives `--no-prompt`, which never
reaches the browser leg. A rule a reviewer just found a defect in should not be the part that is
untestable.

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

The boundary's own: every stop the wire can name is carried, null degrades to null, an unknown stop and
an empty string both degrade to null, and declining every harness still carries the answer. Two
mutations confirm they bind — ignoring the validation, and never reading the field — and both had to be
reshaped to compile, because dropping the reference outright trips IDE0005/IDE0051 as errors. Analyzer
protection is worth noting but is not test coverage.

`DecideVisibility`'s three arms are pinned separately, with three more mutations: an answered-but-unset
screen falling through to the prompt, an unsettled screen treated as answered, and the kept branch
inventing a fallback of its own instead of carrying the profile's value.

The field is also round-tripped through the source-generated JSON context against WireMock, present and
absent. Nothing else covered that: every other test builds `FirstRunFlowResponse` directly, so a naming
or AOT-binding slip would have left the profile untouched for ever with the whole suite green.
