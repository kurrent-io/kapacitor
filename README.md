# Kurrent Capacitor

Full observability for your Claude Code sessions. Record every session, visualize agent activity in real time, and review code changes grounded in the actual development transcripts.

Capacitor captures the complete picture — session lifecycle, transcript data, subagent trees, tool usage, and token consumption — then surfaces it through a real-time dashboard and PR review tools that give you context no diff can provide.

## Getting started

You need the server URL from your admin (e.g. `https://capacitor.example.com`).

### 1. Install the CLI

```bash
npm install -g @kurrent/kapacitor
```

npm automatically selects the right native binary for your platform:

| Platform | Architecture |
|----------|-------------|
| macOS | ARM64 (Apple Silicon) |
| Linux | x64, ARM64 |
| Linux (Alpine/musl) | x64, ARM64 |
| Windows | x64 |

The CLI is compiled with NativeAOT — fast startup, no runtime dependency.

### 2. Run setup

```bash
kapacitor setup
```

The setup wizard walks you through everything:

1. **Server URL** — enter the URL your admin provided
2. **Login** — authenticates via GitHub Device Flow (if the server requires auth)
3. **Default visibility** — choose how your sessions are visible to others (all private, org repos public, or all public)
4. **Claude Code hooks** — choose where to install hooks (user-wide, project-only, or full plugin)
5. **Agent daemon** — configure the daemon name for remote agent execution

That's it. Verify with `kapacitor whoami` and `kapacitor status`.

For non-interactive environments:

```bash
kapacitor setup --server-url https://capacitor.example.com --default-visibility org_public --no-prompt
```

### 3. Import existing sessions (optional)

```bash
kapacitor history    # discovers and uploads local Claude Code transcripts
```

This backfills your past sessions from `~/.claude/projects/` so they appear in the dashboard. It's idempotent — safe to run multiple times.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code session is captured automatically via hooks:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage

## CLI commands

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kapacitor recap <sessionId>              # summary (default)
kapacitor recap --full <sessionId>       # full transcript
kapacitor recap --chain <sessionId>      # summaries across continuation chain
kapacitor recap --chain --full <sessionId>  # full transcript across chain
```

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kapacitor plugin, the session ID is set automatically.

If the kapacitor plugin is installed, you can also use the `/kapacitor:session-recap` skill inside Claude Code, or just ask:

```
Recap session c4de7fbe-cff5-4e2c-bf80-9858d02f58be and propose what should be done next.
```

### Plan validation

Verify that all items in a session's plan were completed.

```bash
kapacitor validate-plan <sessionId>
```

With the plugin installed, use the `/kapacitor:validate-plan` skill or ask naturally:

```
Did I finish everything in the plan? Check what's left to do.
```

### Error extraction

Scan a recorded session for tool call errors — failed bash commands, file read/write errors, agent failures, etc.

```bash
kapacitor errors <sessionId>              # single session
kapacitor errors <meta-session-slug>      # meta session
kapacitor errors --chain <sessionId>      # full continuation chain
```

Useful for post-session review: identify recurring mistakes, discover patterns to avoid, and update project instructions accordingly.

### PR review with full context

```bash
kapacitor review <pr-url>
```

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

### Loading historical sessions

Backfill older sessions from local transcript files:

```bash
kapacitor history                                  # all sessions
kapacitor history --cwd /path/to/project           # from a specific directory
kapacitor history --session abc123                  # single session
```

This discovers Claude Code transcript files at `~/.claude/projects/`, checks each against the server, and loads any that are missing or incomplete. The command is idempotent and resumable.

### Agent daemon

The agent daemon connects to the Capacitor server and runs Claude Code agents in isolated git worktrees, controlled from the dashboard.

```bash
kapacitor agent start              # start in foreground
kapacitor agent start -d           # start in background (daemonize)
kapacitor agent status             # check if daemon is running
kapacitor agent stop               # stop the background daemon
```

### Configuration

```bash
kapacitor config show    # show current configuration
kapacitor config set <key> <value>
```

**Default session visibility** controls how your sessions appear to other users. Set during `kapacitor setup` or change at any time:

```bash
kapacitor config set default_visibility private      # only you can see your sessions
kapacitor config set default_visibility org_public   # org repos visible, others private (default)
kapacitor config set default_visibility public       # all sessions visible to everyone
```

**Repository exclusions** prevent specific repos from sending any data to the server — hooks are silently skipped, no session is recorded:

```bash
kapacitor config set excluded_repos "myorg/secret-project,personal/diary"
```

### Other commands

```bash
kapacitor status         # server health check
kapacitor update         # check for CLI updates
kapacitor logout         # delete stored tokens
```

## License

[Kurrent License v1](LICENSE)
