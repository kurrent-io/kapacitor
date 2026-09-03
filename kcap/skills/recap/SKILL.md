---
name: recap
description: >-
  This skill should be used to read, search, or recall past Kurrent Capacitor
  sessions. Triggers include: "read a previous session", "get session history",
  "recap session", "what happened in session X", "load context from a previous
  session", "continue from session", "what did we do last time", "catch me up
  on session X", "summarize session", "show me what happened", "what have we
  been working on", "recently we implemented", "what was done in this repo",
  "recent changes", "recent sessions", or providing a session ID to review.
  Also covers search/recall asks: "find the session where we…", "which session
  discussed X", "did we ever debug Y", "look up the session about Z", "recall
  the session that…", "search past sessions for…". Provides instructions for
  retrieving and searching session history via the kcap CLI and the
  kcap-sessions MCP tools.
---

> **For agents:** When the `kcap-sessions` MCP server is available, prefer its tools (`search_sessions`, `list_repo_sessions`, `get_session_summary`, `list_turns`, `get_turn`, `get_session_transcript`) for retrieving past sessions. This CLI-wrapped skill remains a fallback for shell use and when MCP isn't installed.

# Session Recap

Retrieve session history recorded by Kurrent Capacitor. Supports single-session recap, continuation chains, and **repository-wide session summaries** for understanding recent work across multiple sessions.

## Usage

**IMPORTANT:** Always use the `kcap recap` CLI command. Do NOT call the HTTP API directly via `curl`, `WebFetch`, or `HttpClient` — the CLI handles formatting, error handling, and server URL resolution.

```bash
# Current session summary + per-turn outline (default)
kcap recap

# Full transcript (all prompts, responses, file changes)
kcap recap --full

# Full continuation chain (all linked sessions, oldest first)
kcap recap --chain

# Both: full transcript across all chained sessions
kcap recap --chain --full

# Recent session summaries for the current repository
kcap recap --repo

# Compact per-turn metadata index (no prose)
kcap recap --per-turn

# One turn's full transcript, drilling down from the outline
kcap recap --get-turn <N>

# Explicit session ID (overrides env var)
kcap recap <sessionId>
kcap recap --full <sessionId>
kcap recap --chain <sessionId>
kcap recap --get-turn <N> <sessionId>
```

`kcap recap` resolves the current session id from the environment when the host agent CLI exposes one. If no session id is available, pass it explicitly: `kcap recap <sessionId>`.

## Repository Recap (`--repo`)

Returns AI-generated summaries from the most recent ended sessions in the current git repository. Each entry includes the session title, date, summary, and a command to get the full transcript.

**Use this when:**
- The user says "what have we been working on recently?"
- The user references prior work ("recently we implemented X")
- You need context about recent changes in this repo
- Starting a new session and want to understand recent activity

**Progressive disclosure:** Start with `--repo` for the overview. If a specific session's summary is relevant, drill into it with `kcap recap --full <sessionId>` to get the complete transcript. This avoids loading full transcripts for all sessions into context.

## Default Output

`kcap recap` (no flags) prints, in order:

1. **`## Plan`** — the plan captured for the session, if any.
2. **`## Summary`** — an AI-generated narrative covering:
   - **Context** — why the work was done
   - **Key decisions** — trade-offs and design choices that matter for future work
   - **Unfinished/Risks** — anything deferred or left incomplete
3. **`## Turns`** — an outline with one line per turn:
   - If the turn has a prose summary, that summary (1-3 sentences).
   - Otherwise, a truncated user-prompt excerpt plus tool/file metadata (tool names, file count).
4. A closing pointer: `→ kcap recap --get-turn <N> [sessionId]` for one turn's full detail.

A session that has turns but no summary yet (generation hasn't run, or an ended session has no `whats_done`/`plan` entry) still shows the `## Turns` outline — only the plan/summary blocks are skipped. If there's neither a summary nor any turns (e.g. an active session with no recorded turns), recap prints a hint to use `--full` instead.

With `--chain`, this same summary + outline is rendered per session under a `# Session <id>` header, oldest first.

## Turn-by-turn drill-down

Once you've read the `## Turns` outline, fetch one turn's complete transcript (user prompt, tool calls + results, assistant text):

```bash
kcap recap --get-turn <N> [sessionId]
```

For a plain metadata index instead of the outline (turn #, prompt excerpt, tool names, file count, token count, time range — no prose), use `kcap recap --per-turn [sessionId]`.

**MCP agents:** call `list_turns` to get a session's full turn map (`turn_index`, `prose`, `user_prompt`, `tools`, `files`, token counts), then `get_turn(session_id, turn_index)` for one turn's full transcript. Use `get_session_summary` for the whole-session narrative instead of drilling into individual turns.

## Full Output (`--full`)

The complete transcript with these section types:

- **`## User Prompt`** — what the user asked
- **`## Assistant`** — Agent text responses
- **`## Plan`** — plans that were created
- **`## Write <path>`** — files that were created (with syntax-highlighted content)
- **`## Edit <path>`** — files that were edited (with diff content)

When using `--chain`, sessions are separated by `# Session <id>` headers, and agent activity appears under `### Agent (<type>)` sub-headers.

## When to Use Each Flag

- **`--repo`** (`kcap recap --repo`) — recent session summaries across the repo (start here for "what did we do recently?")
- **No flags** (`kcap recap`) — summary + per-turn outline for the current session
- **`--per-turn`** (`kcap recap --per-turn`) — compact metadata index per turn (prompt excerpt, tools, files, tokens, time), no prose
- **`--get-turn <N>`** (`kcap recap --get-turn <N>`) — one turn's full transcript, drilling down from the outline or the per-turn index
- **`--full`** (`kcap recap --full`) — whole transcript: exact prompts, responses, and file contents for a specific session
- **`--chain`** (`kcap recap --chain`) — understanding the full history of a task that spanned multiple sessions
- **`--chain --full`** — complete transcript across all continuations

## Environment

The `KCAP_URL` environment variable overrides the default server URL (`http://localhost:5108`).

## Tips

- **For "what have we been working on?"** — use `--repo` first, then drill into specific sessions.
- Start with the default summary + turn outline. Drill into a specific turn with `--get-turn <N>` before reaching for `--full`.
- When continuing work from a previous session, use `--chain` to get summaries across continuations.
- Summarize key decisions and changes for the user rather than echoing the full recap output verbatim.
- The `kcap` CLI must be available on PATH (typically installed at `~/.local/bin/kcap`).

## Error Handling

- If the session is not found, the command prints "Session not found" and exits with code 1.
- If not in a git repository (for `--repo`), the command prints an error and exits with code 1.
- If the Kurrent Capacitor server is unreachable, the command prints an HTTP error. Ensure the server is running.
