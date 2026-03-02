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

Run `kapacitor validate-plan` via the Bash tool. The session ID is automatically set by the `KAPACITOR_SESSION_ID` environment variable (persisted at session start).

```bash
# Validate the current session's plan
kapacitor validate-plan

# Explicit session ID (overrides env var)
kapacitor validate-plan <sessionId>
```

## What It Returns

The command outputs three sections:

- **`## Plan`** — the full plan text
- **`## What's Done`** — two sub-sections:
  - **Summary** — AI-generated summary of what was accomplished (from `WhatsDoneGenerated` events, if available)
  - **Details** — list of files created (`Write`) and modified (`Edit`) during the session
- **`## Instructions`** — asks you to compare the plan against the summary and file list

## What To Do With The Output

1. Read the plan carefully and identify each distinct planned item
2. Compare each item against the summary and file list under "What's Done"
3. If all items are complete, confirm to the user that the plan is fully implemented
4. If there are gaps, list the missing items and complete them now

## When No Plan Is Found

If the output says "No plan found for this session", inform the user that no plan was detected for this session. A plan is only present when:
- The session continued from a previous session that had a plan (`planContent`)
- The session used `ExitPlanMode` to create a plan file (written to `~/.claude/plans/`)

## Environment

The `KAPACITOR_URL` environment variable overrides the default server URL (`http://localhost:5108`).
