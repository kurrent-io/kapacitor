# Kurrent Capacitor CLI

Records and visualizes [Claude Code](https://claude.ai/claude-code) sessions via hooks, backed by [KurrentDB](https://kurrent.io).

## Installation

```bash
npm install -g @kurrent/kapacitor
```

## Setup

```bash
kapacitor setup    # guided first-run configuration
kapacitor login    # authenticate with your Capacitor server
kapacitor status   # verify connection
```

## How it works

The CLI integrates with Claude Code via hooks. When you start a Claude Code session, the CLI forwards lifecycle events (session start/end, subagent start/stop, notifications) and transcript data to your Capacitor server for real-time visualization.

### Hook events

| Hook | Purpose |
|------|---------|
| `session-start` | Session created, begins recording |
| `session-end` | Session completed |
| `subagent-start` | Subagent spawned |
| `subagent-stop` | Subagent completed |
| `notification` | Claude Code notification |
| `stop` | Session interrupted |
| `pre-compact` | Before context compaction |
| `permission-request` | Permission prompt |

### Background watcher

On session start, the CLI spawns a background watcher process that polls the `.jsonl` transcript file and streams new lines to the server via a persistent SignalR connection.

### Agent daemon

`kapacitor agent start` runs a long-lived daemon that manages Claude CLI agents in isolated git worktrees. It connects to the Capacitor server via SignalR and receives launch/stop/input commands.

### PR review context

`kapacitor review <pr>` launches a Claude Code session with MCP tools that query implementation context from recorded sessions, grounding code review in actual development transcripts.

## Commands

```bash
kapacitor session-start          # Forward session-start hook
kapacitor session-end            # Forward session-end hook
kapacitor watch                  # Start transcript watcher
kapacitor agent start|stop|status # Manage agent daemon
kapacitor review <pr-url>        # PR review with session context
kapacitor mcp review             # MCP server for PR review tools
kapacitor login|logout|whoami    # Authentication
kapacitor setup                  # First-run configuration
kapacitor config show|set        # Configuration management
kapacitor update                 # Check for updates
kapacitor status                 # Health check
kapacitor errors                 # Review session errors
```

## Building from source

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release
```

The CLI is compiled with NativeAOT for fast startup and small binary size.

## License

[Kurrent License v1](LICENSE)
