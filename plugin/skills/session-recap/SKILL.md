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

Retrieve the history of a Claude Code session recorded by Kurrent Capacitor. The recap contains user prompts, assistant responses, plans, and file writes/edits — everything needed to understand what happened in a session or to continue where it left off.

## Usage

Run `kapacitor recap` via the Bash tool to fetch a session recap:

```bash
# Single session
kapacitor recap <sessionId>

# Full continuation chain (all linked sessions, oldest first)
kapacitor recap --chain <sessionId>
```

The session ID is a UUID like `7d309c69-0fcd-41ae-962d-1a23162ff088`. Find session IDs from:
- The Kurrent Capacitor web UI at http://localhost:5108
- The user providing one directly
- The current session ID (available in hook payloads)

## Output Format

The command outputs markdown with these section types:

- **`## User Prompt`** — what the user asked
- **`## Assistant`** — Claude's text responses
- **`## Plan`** — plans that were created
- **`## Write <path>`** — files that were created (with syntax-highlighted content)
- **`## Edit <path>`** — files that were edited (with diff content)

When using `--chain`, sessions are separated by `# Session <id>` headers, and agent activity appears under `### Agent (<type>)` sub-headers.

## When to Use Each Flag

- **No flag** (`kapacitor recap <id>`) — reviewing a single known session
- **`--chain`** (`kapacitor recap --chain <id>`) — understanding the full history of a task that spanned multiple sessions, or resuming work that was continued across sessions

## Environment

The `KAPACITOR_URL` environment variable overrides the default server URL (`http://localhost:5108`).

## Tips

- The output can be large for long sessions. Scan for `## User Prompt` headers to quickly locate specific interactions.
- When continuing work from a previous session, use `--chain` to get the full context across continuations.
- Summarize key decisions and changes for the user rather than echoing the full recap output verbatim.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints "Session not found" and exits with code 1.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
