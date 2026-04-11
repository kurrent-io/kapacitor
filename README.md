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

The setup wizard walks you through:

1. **Server URL** — enter the URL your admin provided
2. **Login** — authenticates via GitHub Device Flow (if the server requires auth)
3. **Default visibility** — choose how your sessions are visible to others
4. **Claude Code plugin** — installs hooks, skills, and collaborative memory (user-wide or project-only)
5. **Agent daemon** — configure the daemon name for remote agent execution

Verify with `kapacitor whoami` and `kapacitor status`.

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

### Profiles

Profiles let you work with multiple Capacitor servers — for example, a company server for work repos and a separate one for open-source projects. Each profile stores its own server URL, visibility settings, and daemon configuration.

```bash
kapacitor profile add work --server-url https://cap.example.com
kapacitor profile add oss --server-url https://cap.oss.dev --remote "github.com/myorg/*"
kapacitor profile list
kapacitor profile show work
kapacitor profile remove work
```

The `--remote` flag associates a profile with git remote patterns. When you open a repo whose remote matches a pattern, that profile activates automatically.

#### Switching profiles

```bash
kapacitor use work                  # bind 'work' profile to current repo/directory
kapacitor use work --global         # set 'work' as the global default
kapacitor use oss --save            # bind and write .kapacitor.json for team sharing
```

Without `--global`, `use` binds the profile to the current git repo root (or the current directory if not in a repo). With `--save`, it writes a `.kapacitor.json` file that can be committed so the whole team uses the same profile.

#### Profile resolution order

The CLI resolves which profile to use in this order:

1. `--server-url` CLI flag
2. `KAPACITOR_URL` environment variable
3. `KAPACITOR_PROFILE` environment variable
4. `.kapacitor.json` in the repo root (or current directory if not in a repo)
5. Git remote pattern matching from `--remote` flags
6. Directory binding from `kapacitor use`
7. Global active profile (or `default`)

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
kapacitor whoami         # show current authenticated user
kapacitor login          # authenticate via OAuth
kapacitor update         # check for CLI updates
kapacitor logout         # delete stored tokens
```

## License

[Kurrent License v1](LICENSE)
