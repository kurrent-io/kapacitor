# Kurrent Capacitor Plugin

This plugin integrates [Kurrent Capacitor](../README.md) with Claude Code and Codex CLI by automatically registering lifecycle hooks, providing skills for session review, and auto-installing MCP servers that expose past-session context to the agent.

## What it does

**MCP servers** — stdio servers auto-registered on plugin install (no manual `claude mcp add` or `~/.codex/config.toml` edit):

### `kcap-sessions`

Search and recall past Kurrent Capacitor sessions from inside the agent.

| Tool | Description |
|------|-------------|
| `search_sessions` | Free-text + author search over past sessions (and subagent transcripts), defaulted to the cwd's repo |
| `get_session_summary` | Concise `summary_text` + `plan` for a session |
| `get_session_transcript` | Speaker-tagged transcript window, with `around_event` drill-in for search hits |

Repo-aware: it resolves the cwd to a repo hash at startup, so `search_sessions` defaults to *this* repo.

### `kcap-review`

PR review context tools. Each PR-scoped tool accepts an optional `pr` argument (`"owner/repo#123"` or a GitHub PR URL), so you can review any PR from any branch — no need to check it out first. When `pr` is omitted, the server falls back to the PR passed at startup (set by `kcap review <pr>`) or to git auto-detection against the current branch.

| Tool | Description | `pr` arg |
|------|-------------|----------|
| `get_pr_summary` | Overview: sessions, files changed, test runs | optional |
| `list_pr_files` | Files changed with session links and event counts | optional |
| `get_file_context` | Why a specific file was changed, with transcript excerpts | optional |
| `search_context` | Free-text search across session transcripts | optional |
| `list_sessions` | Sessions that contributed to the PR | optional |
| `get_transcript` | Full transcript of a specific session | n/a (keys off `session_id`) |

### `kcap-memory`

Search, save, and update durable team memories — preferences, feedback, project facts, and references scoped to you, your team, or the org.

| Tool | Description |
|------|-------------|
| `search_memories` | Hybrid semantic + keyword search over memories visible to you |
| `get_memory` | Fetch a memory's full content by id or slug |
| `save_memory` | Save a new memory (`audience`, `slug`, `description`, `content`, `kind`); scoped to the cwd repo unless `global: true` |
| `update_memory` | Update an existing memory's description/content/kind |
| `rescope_memory` | Change a memory's audience (e.g. promote user → team/org) |
| `archive_memory` | Soft-delete a memory |

Repo- and machine-aware: it resolves the cwd to a repo hash and the local persisted machine id at startup to scope saves and bias search results.

### `kcap-analytics`

Governed read-only SQL over the org's curated coding-agent analytics views (sessions, tool/skill/token usage, cost, commits, PRs, evals). Auto-registered for every supported harness — it resolves its repo scope from the working directory, so it rides the same registration path as `kcap-sessions`.

| Tool | Description |
|------|-------------|
| `get_analytics_schema` | The governed schema document: queryable views/columns, glossary, SQL rules, worked examples. Called once before writing SQL |
| `query_analytics` | Run one governed Postgres SELECT (`sql`, optional `scope` `"repo"`/`"global"`, optional `max_rows`); a rejected query returns the validator's reason to fix and retry |

Repo-aware: defaults to the cwd's repo; pass `scope: "global"` for org-wide questions. Requires `kcap login` and a kcap-server new enough to expose the `/api/analytics` endpoints.

`kcap mcp judge` is intentionally not auto-registered. Add it with `claude mcp add kcap-judge -- kcap mcp judge` if you want it.

**Hooks** — Automatically captures session activity and forwards it to the Kurrent Capacitor server:

| Hook | Event |
|------|-------|
| `SessionStart` | Session begins |
| `SessionEnd` | Session ends |
| `SubagentStart` | Subagent spawned |
| `SubagentStop` | Subagent finished |
| `Notification` | Permission/idle prompts |
| `Stop` | Claude finishes a turn |

Each hook pipes its JSON payload through the `kcap` CLI, which enriches it with git/PR info and forwards it to the server. A background watcher process streams transcript lines in real time.

**Skills** — Slash commands for reviewing recorded sessions. Claude reads `skills/` (unprefixed folder names); Codex and Cursor read `~/.agents/skills/` (`kcap-*` prefixed folder names):

- `recap` / `kcap-recap` — Retrieve a structured summary of a session (user prompts, assistant responses, plans, file changes)
- `errors` / `kcap-errors` — Extract tool call errors from a session for post-session review and pattern detection
- `validate-plan` / `kcap-validate-plan` — Verify that all planned items were completed
- `disable` / `kcap-disable` — Stop recording and delete all server data for the current session
- `hide` / `kcap-hide` — Hide the current session (owner-only visibility)

In Claude they're invoked as `/kcap:recap`, `/kcap:errors`, etc.

## Prerequisites

- The `kcap` CLI must be on your PATH (`npm install -g @kurrent/kcap`)
- The Kurrent Capacitor server must be running (default: `http://localhost:5108`)

## Installation

### Option A: CLI command (recommended)

```bash
kcap plugin install            # Claude Code, user-wide
kcap plugin install --codex    # Codex CLI, user-wide (hooks + agent skills)
kcap plugin install --skills   # Agent skills only (~/.agents/skills/), no Codex hooks
kcap plugin install --project  # current project only (hooks scope; skills always user-wide)
```

### Option B: Interactive plugin manager

- Claude Code: run `/plugin` inside a session and browse the **Installed** tab.
- Codex CLI: `codex plugin marketplace add kurrent-io/kcap-cli` then enable from the marketplace.

> **Note:** Codex's native plugin loader installs the MCP servers only. To get hooks and agent skills, additionally run `kcap plugin install --codex`.

### Option C: Settings file (manual)

Add to `.claude/settings.local.json` or `~/.claude/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "kcap": {
      "source": {
        "source": "directory",
        "path": "/path/to/kcap/kcap"
      }
    }
  },
  "enabledPlugins": {
    "kcap@kcap": true
  }
}
```

### Verify

- Claude Code: `/hooks` (hooks) and `claude mcp list` (MCP servers).
- Codex CLI: `/hooks` (then trust each kcap entry) and `codex mcp list`.

## Configuration

Set `KCAP_URL` to override the default server URL:

```bash
export KCAP_URL=http://my-server:5108
```

## Plugin structure

```
kcap/
  .claude-plugin/
    plugin.json          — Claude manifest (name, version, description)
    marketplace.json     — Marketplace manifest for plugin discovery
  .codex-plugin/
    plugin.json          — Codex manifest (refs ./.codex-mcp.json)
  .mcp.json              — Claude MCP servers (camelCase mcpServers shape)
  .codex-mcp.json        — Codex plugin MCP servers (also camelCase mcpServers shape)
  hooks/
    hooks.json           — Hook definitions for all Claude lifecycle events
  skills/
    recap/
      SKILL.md           — /kcap:recap skill (Claude)
    errors/
      SKILL.md
    validate-plan/
      SKILL.md
    disable/
      SKILL.md
    hide/
      SKILL.md
```

The two MCP files exist because Claude requires top-level `mcpServers` (camelCase) while Codex accepts only `mcp_servers` (snake_case) or a bare server map — the schemas don't overlap. Keep them in sync when adding or removing servers.
