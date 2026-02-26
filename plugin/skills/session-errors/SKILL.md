---
name: session-errors
description: >-
  This skill should be used when the user asks to "show errors", "extract errors",
  "what went wrong", "find tool errors", "review errors from session",
  "check session errors", "list failures", "what failed in session X",
  "error report", "show mistakes", or wants to review tool call errors
  from a recorded session. Provides instructions for extracting tool errors
  via the kapacitor CLI.
---

# Session Errors

Extract tool call errors from a Claude Code session recorded by Kurrent Capacitor. The output lists each failed tool call — bash commands, file reads/writes, agent delegations, etc. — along with the error message and the tool that caused it.

## Usage

Run `kapacitor errors` via the Bash tool to fetch errors from a session:

```bash
# Errors from a single session
kapacitor errors <sessionId>

# Errors from the full continuation chain (all linked sessions)
kapacitor errors --chain <sessionId>
```

The session ID is a UUID like `7d309c69-0fcd-41ae-962d-1a23162ff088`. Find session IDs from:
- The Kurrent Capacitor web UI at http://localhost:5108
- The user providing one directly
- The current session ID (available in hook payloads)

## Output Format

Each error is printed as a block with:

- **Session ID** and optional **agent ID** (if the error occurred in a subagent)
- **Event number** and **timestamp**
- **Tool name** — the tool that failed (e.g., Bash, Read, Edit, Write, Grep, Glob, Task)
- **Error message** — the error output or failure reason

When using `--chain`, errors from all sessions in the continuation chain are included, ordered chronologically.

## When to Use Each Flag

- **No flag** (`kapacitor errors <id>`) — reviewing errors from a single session
- **`--chain`** (`kapacitor errors --chain <id>`) — reviewing errors across a full task that spanned multiple sessions

## Practical Applications

- **End-of-session review** — run after finishing a session to identify recurring mistakes and update CLAUDE.md with avoidance rules
- **Debugging** — quickly find what went wrong in a session without scrolling through the full timeline
- **Pattern detection** — use `--chain` across a multi-session task to spot repeated error patterns (e.g., wrong file paths, incorrect API usage)

## Environment

The `KAPACITOR_URL` environment variable overrides the default server URL (`http://localhost:5108`).

## Tips

- After extracting errors, look for patterns: the same tool failing repeatedly, or the same type of mistake across sessions.
- Propose concrete avoidance rules based on the errors found — these can be added to the project's CLAUDE.md.
- The `kapacitor` CLI must be available on PATH (typically installed at `~/.local/bin/kapacitor`).

## Error Handling

- If the session is not found, the command prints an error and exits with code 1.
- If no errors are found, the command prints "No errors found." — this is a good outcome.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
