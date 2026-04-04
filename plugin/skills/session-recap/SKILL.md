---
name: session-recap
description: >-
  This skill should be used when the user asks to "read a previous session",
  "get session history", "recap session", "what happened in session X",
  "load context from a previous session", "continue from session",
  "what did we do last time", "catch me up on session X",
  "summarize session", "show me what happened",
  or provides a session ID they want to review.
  Provides instructions for retrieving session history via the kapacitor CLI.
---

# Session Recap

Retrieve the history of a Claude Code session recorded by Kurrent Capacitor. By default, shows a concise AI-generated summary (context, key decisions, unfinished work). Use `--full` for the complete transcript with all user prompts, assistant responses, plans, and file changes.

## Usage

**IMPORTANT:** Always use the `kapacitor recap` CLI command. Do NOT call the HTTP API directly via `curl`, `WebFetch`, or `HttpClient` — the CLI handles formatting, error handling, and server URL resolution.

```bash
# Session summary (default — concise AI-generated overview)
kapacitor recap

# Full transcript (all prompts, responses, file changes)
kapacitor recap --full

# Full continuation chain (all linked sessions, oldest first)
kapacitor recap --chain

# Both: full transcript across all chained sessions
kapacitor recap --chain --full

# Explicit session ID (overrides env var)
kapacitor recap <sessionId>
kapacitor recap --full <sessionId>
kapacitor recap --chain <sessionId>
```

The session ID is automatically set by the `KAPACITOR_SESSION_ID` environment variable (persisted at session start). You can pass an explicit ID to review a different session.

## Default Output (Summary)

Shows the plan (if any) and an AI-generated summary with:
- **Context** — why the work was done
- **Key decisions** — trade-offs and design choices that matter for future work
- **Unfinished/Risks** — anything deferred or left incomplete

If no summary is available (e.g., active session), a hint is shown to use `--full`.

## Full Output (`--full`)

The complete transcript with these section types:

- **`## User Prompt`** — what the user asked
- **`## Assistant`** — Claude's text responses
- **`## Plan`** — plans that were created
- **`## Write <path>`** — files that were created (with syntax-highlighted content)
- **`## Edit <path>`** — files that were edited (with diff content)

When using `--chain`, sessions are separated by `# Session <id>` headers, and agent activity appears under `### Agent (<type>)` sub-headers.

## When to Use Each Flag

- **No flags** (`kapacitor recap`) — quick context on what happened and why
- **`--full`** (`kapacitor recap --full`) — when you need exact prompts, responses, or file contents
- **`--chain`** (`kapacitor recap --chain`) — understanding the full history of a task that spanned multiple sessions
- **`--chain --full`** — complete transcript across all continuations

## Environment

The `KAPACITOR_URL` environment variable overrides the default server URL (`http://localhost:5108`).

## Tips

- Start with the default summary. Only use `--full` when you need specific details.
- When continuing work from a previous session, use `--chain` to get summaries across continuations.
- Summarize key decisions and changes for the user rather than echoing the full recap output verbatim.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints "Session not found" and exits with code 1.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
