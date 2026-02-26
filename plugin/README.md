# Kapacitor Plugin for Claude Code

This plugin integrates [Kurrent Capacitor](../README.md) with Claude Code by automatically registering lifecycle hooks and providing skills for session review.

## What it does

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

**Skills** — Two slash commands for reviewing recorded sessions:

- `/kapacitor:session-recap` — Retrieve a structured summary of a session (user prompts, assistant responses, plans, file changes)
- `/kapacitor:session-errors` — Extract tool call errors from a session for post-session review and pattern detection

## Prerequisites

- The `kapacitor` CLI must be on your PATH (see [publishing instructions](../README.md#2-publish-the-cli-tool))
- The Kurrent Capacitor server must be running (default: `http://localhost:5108`)

## Installation

### Option A: Plugin directory flag

```bash
claude --plugin-dir /path/to/kapacitor/plugin
```

### Option B: Local marketplace (persistent)

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
  hooks/
    hooks.json           — Hook definitions for all 6 lifecycle events
  skills/
    session-recap/
      SKILL.md           — /kapacitor:session-recap skill
    session-errors/
      SKILL.md           — /kapacitor:session-errors skill
```
