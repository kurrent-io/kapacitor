# Kapacitor Plugin for Claude Code

This plugin integrates [Kurrent Capacitor](../README.md) with Claude Code by automatically registering lifecycle hooks, providing skills for session review, and exposing MCP tools for PR review context.

## What it does

**MCP Tools** — PR review context tools, available automatically when you're on a branch with an open PR. Claude can query implementation session transcripts to understand why code was changed:

| Tool | Description |
|------|-------------|
| `get_pr_summary` | Overview: sessions, files changed, test runs |
| `list_pr_files` | Files changed with session links and event counts |
| `get_file_context` | Why a specific file was changed, with transcript excerpts |
| `search_context` | Free-text search across session transcripts |
| `list_sessions` | Sessions that contributed to the PR |
| `get_transcript` | Full transcript of a specific session |

The MCP server auto-detects the current repo and PR from git. If you're not on a PR branch, the tools return a helpful message suggesting `kapacitor review <pr>`.

**Hooks** — Automatically captures session activity and forwards it to the Kurrent Capacitor server:

| Hook | Event |
|------|-------|
| `SessionStart` | Session begins |
| `SessionEnd` | Session ends |
| `SubagentStart` | Subagent spawned |
| `SubagentStop` | Subagent finished |
| `Notification` | Permission/idle prompts |
| `Stop` | Claude finishes a turn |

Each hook pipes its JSON payload through the `kapacitor` CLI, which enriches it with git/PR info and forwards it to the server. A background watcher process streams transcript lines in real time.

**Skills** — Slash commands for reviewing recorded sessions:

- `/kapacitor:session-recap` — Retrieve a structured summary of a session (user prompts, assistant responses, plans, file changes)
- `/kapacitor:session-errors` — Extract tool call errors from a session for post-session review and pattern detection
- `/kapacitor:validate-plan` — Verify that all planned items were completed
- `/kapacitor:session-disable` — Stop recording and delete all server data for the current session
- `/kapacitor:session-hide` — Hide the current session (owner-only visibility)

## Prerequisites

- The `kapacitor` CLI must be on your PATH (see [publishing instructions](../README.md#2-publish-the-cli-tool))
- The Kurrent Capacitor server must be running (default: `http://localhost:5108`)

## Installation

### Option A: Interactive plugin manager (recommended)

From inside a Claude Code session:

```
/plugin marketplace add /path/to/kapacitor
/plugin install kapacitor@kurrent
```

Or run `/plugin` and browse the **Discover** tab to install interactively.

### Option B: Plugin directory flag (one-time)

```bash
claude --plugin-dir /path/to/kapacitor/plugin
```

### Option C: Settings file (persistent, no CLI needed)

Add to `.claude/settings.local.json` or `~/.claude/settings.json`:

```json
{
  "extraKnownMarketplaces": {
    "kurrent": {
      "source": {
        "source": "directory",
        "path": "/path/to/kapacitor"
      }
    }
  },
  "enabledPlugins": {
    "kapacitor@kurrent": true
  }
}
```

### Verify

Run `/hooks` in Claude Code to confirm the kapacitor hooks are registered.

## Configuration

Set `KAPACITOR_URL` to override the default server URL:

```bash
export KAPACITOR_URL=http://my-server:5108
```

## Plugin structure

```
plugin/
  .claude-plugin/
    plugin.json          — Plugin manifest (name, version, description)
  .mcp.json              — MCP server config (PR review context tools)
  hooks/
    hooks.json           — Hook definitions for all lifecycle events
  skills/
    session-recap/
      SKILL.md           — /kapacitor:session-recap skill
    session-errors/
      SKILL.md           — /kapacitor:session-errors skill
    validate-plan/
      SKILL.md           — /kapacitor:validate-plan skill
    session-disable/
      SKILL.md           — /kapacitor:session-disable skill
    session-hide/
      SKILL.md           — /kapacitor:session-hide skill
```
