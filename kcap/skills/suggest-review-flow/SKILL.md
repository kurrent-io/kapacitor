---
name: suggest-review-flow
description: >-
  Use this skill when implementation is complete — a feature or bugfix is functionally
  finished, tests pass, or you are about to commit / open a PR / merge — or when a spec is
  finalized. At that moment, proactively OFFER an independent second-harness review even if
  the user did not ask, because a fresh, separate reviewer catches what the author's own model
  misses. It calls list_reviewer_vendors to recommend a reviewer that will actually run for
  this repo, offers a spec-review or code-review flow, and hands off to the review-flows skill
  only after the user accepts — it never starts a flow itself. Do NOT use it when the user
  asks you to review something yourself ("review this", "code review this", "look over my
  spec"), or mid-task before the work is finished.
---

# Suggest a review flow

You have reached a milestone where an **independent** review adds real value: a
separate coding-agent session, with none of this session's context, reviews the
work — and a different model catches what the author's own model glossed over.
Most people never think to do this. Your job here is to **offer** it at the
right moment, recommend a reviewer that will actually run, and — only if the
user says yes — hand off to the `review-flows` skill. **This skill never starts
a review flow itself.**

## When to offer (and when not to)

Offer at exactly two milestones:

- **Spec complete** — you just produced AND presented a finalized spec/design
  this session → offer a `spec-review` flow.
- **Implementation complete** — the change is functionally finished (you say so,
  or you are about to commit / open a PR / merge) → offer a `code-review` flow.

Do NOT offer (stay silent) when:

- The user asked you to **review something yourself** ("review this", "code
  review this", "look over the spec"). Just perform that review locally — no
  offer, no flow.
- You are only part-way through: rough mid-stream brainstorming, a partial plan,
  a routine edit, or work whose known checks are still failing.
- A review flow for this artifact is already engaged, active, or just completed.
- Another skill (brainstorming, writing-plans, finishing-a-development-branch,
  requesting-code-review) is mid-flow — let it finish; offer at the seam.

**Offer at most once per artifact.** Key the offer on (milestone type + the
target's identity + its revision): the same spec or the same change never gets
offered twice, but a materially revised artifact may be offered again, and a
second, different artifact of the same kind may be offered on its own. If the
user declines or moves on, stay silent for that artifact. A failed availability
lookup counts as the one prompt for that artifact — do not re-prompt.

## How to recommend a reviewer

1. Call **`list_reviewer_vendors`** (read-only; safe to call before offering).
   It returns `reviewers[]` (vendors that can actually run for this repo now),
   `driver_vendor` (the harness you are running in, or absent if unknown), and
   `diagnostics`.
2. **Prefer a reviewer different from the driver.** A different model is the
   highest-value review. If one non-driver reviewer is available, recommend it;
   if several, offer a short choice — never assert one vendor is "better".
3. **If the only available reviewer is your own vendor, still offer it** —
   framed as an independent, context-free reviewer session. The value is the
   clean second look, not necessarily a different model.
4. **If `driver_vendor` is absent (unknown)**, do NOT claim "a different model";
   frame the offer as "a separate, independent reviewer session" and offer a
   choice from `reviewers[]`.
5. **If `reviewers[]` is empty**, read `diagnostics.reason` and give one line of
   guidance instead of a dead end:
   - `no_daemons_connected` → start a daemon (`kcap daemon start -d`).
   - `no_repo_hosting_daemon` → run a daemon on a machine that has this repo.
   - `no_unattended_reviewer` → install/certify a second harness as a reviewer.
   - `repo_unresolved` → the current directory isn't a recognized repo.
   - `lookup_failed` → couldn't reach the server; suggest checking `kcap status`.
   - `schema_skew` → the kcap client/server is out of date; suggest updating.

## Handing off (only after the user accepts)

- You MAY run the read-only `list_reviewer_vendors` lookup automatically, but you
  **MUST NOT start a review flow until the user affirmatively accepts THIS
  offer.**
- On acceptance, invoke the **`review-flows`** skill to run it, preserving the
  chosen `kind` (spec-review / code-review), the target, and the chosen reviewer
  vendor.
- If the reviewer vendor turns out to be unavailable at start (a snapshot race),
  `review-flows` surfaces the reason — do not silently substitute another vendor;
  re-recommend and get fresh consent.

## If `list_reviewer_vendors` is not available

Your harness caches the kcap MCP tool list when it connects. If kcap was
upgraded mid-session, `list_reviewer_vendors` may be missing from this session.
Do NOT guess availability and do NOT shell out as a workaround — tell the user
to restart the harness (or reconnect the `kcap-flows` MCP server) so the tool
appears, then offer again.
