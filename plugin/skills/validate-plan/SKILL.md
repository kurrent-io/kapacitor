---
name: validate-plan
description: >-
  This skill should be used when the user asks to "validate plan",
  "verify plan", "check plan completion", "did I finish everything",
  "is the plan done", "what's left to do", "validate my work",
  or wants to verify that all planned items were completed.
---

# Validate Plan

Verify that all items in the current session's plan have been completed. Plans come from either a continuation (`SessionStarted.planContent`) or an in-session `ExitPlanMode` write to `~/.claude/plans/`.

## Usage

Run `kapacitor validate-plan` via the Bash tool with the **current session ID**:

```bash
kapacitor validate-plan <sessionId>
```

The current session ID is available as your session identifier — use it directly.

## What It Returns

The command outputs three sections:

- **`## Plan`** — the full plan text
- **`## Work Done`** — list of files created (`Write`) and modified (`Edit`) during the session
- **`## Instructions`** — asks you to compare the plan against the work done

## What To Do With The Output

1. Read the plan carefully and identify each distinct planned item
2. Compare each item against the files listed under "Work Done"
3. If all items are complete, confirm to the user that the plan is fully implemented
4. If there are gaps, list the missing items and complete them now

## When No Plan Is Found

If the output says "No plan found for this session", inform the user that no plan was detected for this session. A plan is only present when:
- The session continued from a previous session that had a plan (`planContent`)
- The session used `ExitPlanMode` to create a plan file (written to `~/.claude/plans/`)

## Environment

The `KAPACITOR_URL` environment variable overrides the default server URL (`http://localhost:5108`).
