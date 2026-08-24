<!--
Body target: 1,200 characters, bullets included. Go past it only when the excess is
Verification evidence or a table — never more prose. A table only where it replaces
prose: values in cells, not sentences. Exactly the three headings below — no others,
no renames. Delete every section you cannot fill concretely, all three for a trivial
change. The diff holds the detail; this box holds what a reviewer cannot get from it.

Fill in the reference line above the first heading — it is what links the PR to both
trackers, and it is not part of the character target. Left with its placeholders,
it links nothing.

Never:
- spec/plan coordinates ("§4", "Task 12", "Phase 2")
- review rounds, findings, reviewer or bot names
- "as built" records, or what you tried and abandoned
- change narration ("previously", "no longer")
- inventories the diff already carries (file lists, per-site or per-code counts)
- assertions without evidence ("all tests pass")

Agents: when the template is not rendered for you (`gh pr create --body`), reproduce
these headings yourself and keep to this comment's rules.
-->

Closes #<issue> — AI-<id>

<!-- The line above is the reference line: a closing keyword for the GitHub issue, and the
Linear id so Linear links the PR back to the imported issue. Replace both placeholders. Drop
either half only when it does not exist, and say which. -->

## What & why

<!-- Two to four sentences: the change and why it is needed. When the issue states only
the problem, add the shape of the approach — a few sentences, not a walkthrough. -->

## Where to look

<!-- One or two lines: what most deserves attention, or an easy-to-miss consequence.
Delete when the diff speaks for itself. -->

## Verification

<!-- Evidence, not assertion: a command and its result, a reproduced failure, a number.
Delete if you have nothing concrete. -->
