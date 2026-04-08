# Kurrent Capacitor

Full observability for your Claude Code sessions. Record every session, visualize agent activity in real time, and review code changes grounded in the actual development transcripts.

Capacitor captures the complete picture — session lifecycle, transcript data, subagent trees, tool usage, and token consumption — then surfaces it through a real-time dashboard and PR review tools that give you context no diff can provide.

## Installation

Install the CLI globally via npm:

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

## Setup

Run the guided setup wizard:

```bash
kapacitor setup
```

This walks you through 5 steps:

1. **Server URL** — connects to your Capacitor server, validates reachability
2. **Authentication** — OAuth login (GitHub Device Flow or Auth0 PKCE, auto-discovered)
3. **Default visibility** — choose who sees your sessions (`private`, `org_public`, or `public`)
4. **Claude Code plugin** — installs the hooks that record your sessions
5. **Agent daemon** — optional background daemon for remote agent management

For non-interactive environments:

```bash
kapacitor setup --server-url https://capacitor.example.com --default-visibility org_public --no-prompt
```

Verify everything works:

```bash
kapacitor status   # shows server, auth, and daemon health
```

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code session is captured automatically via hooks:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage

## Key features

### Real-time dashboard

The Capacitor server provides a Blazor-based dashboard showing active and historical sessions, grouped by repository. Watch sessions unfold live or drill into completed ones — browse the reconstructed conversation, inspect the event timeline, explore the hierarchical call tree, or review stats and metadata.

### PR review with full context

```bash
kapacitor review <pr-url>
```

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

### Remote agent daemon

```bash
kapacitor agent start -d
```

Runs a background daemon that manages Claude CLI agents in isolated git worktrees. The server dispatches work, the daemon executes it — with full PTY hosting, permission handling, and live terminal output streaming.

### Session management

```bash
kapacitor errors              # review tool call errors from this session
kapacitor recap               # get a session summary
kapacitor hide                # make a session owner-only
kapacitor disable             # stop recording and delete session data
kapacitor history             # import local transcript history
```

## Building from source

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release
```

Requires .NET 10 SDK. On macOS, also requires Xcode command line tools (for the PTY shim compilation).

## License

[Kurrent License v1](LICENSE)
