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

## Finding the current session ID

Derive the session ID from the transcript file on disk:

1. Take the current working directory (e.g. `/Users/alexey/dev/eventstore/kapacitor`)
2. Replace `/` with `-` to get the project directory name (e.g. `-Users-alexey-dev-eventstore-kapacitor`)
3. Find the most recently modified `.jsonl` file in `~/.claude/projects/<dirname>/`
4. The filename (without `.jsonl`) is the session ID

One-liner to get it:

```bash
ls -t ~/.claude/projects/$(pwd | tr '/' '-')/*.jsonl 2>/dev/null | head -1 | xargs -I{} basename {} .jsonl
```

The `tr` converts `/Users/foo/bar` → `-Users-foo-bar` (the leading `/` becomes `-`).

## Usage

Run `kapacitor validate-plan` via the Bash tool with the discovered session ID:

```bash
SESSION_ID=$(ls -t ~/.claude/projects/$(pwd | tr '/' '-')/*.jsonl 2>/dev/null | head -1 | xargs -I{} basename {} .jsonl)
kapacitor validate-plan "$SESSION_ID"
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
