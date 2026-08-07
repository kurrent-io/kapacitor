# Kurrent Capacitor

> Full observability for your AI coding-agent sessions — record every session, watch agent activity in real time, and review code changes grounded in the transcripts that produced them.

[![npm](https://img.shields.io/npm/v/@kurrent/kcap?color=cb3837&logo=npm&label=%40kurrent%2Fkcap)](https://www.npmjs.com/package/@kurrent/kcap)
[![license](https://img.shields.io/badge/license-Kurrent%20v1-blue)](LICENSE.md)
[![platforms](https://img.shields.io/badge/platform-macOS%20%7C%20Linux%20%7C%20Windows-lightgrey)](#1-install-the-cli)
[![built with](https://img.shields.io/badge/.NET%2010-NativeAOT-512bd4?logo=dotnet&logoColor=white)](#)

**Kurrent Capacitor** (`kcap`) records your coding-agent sessions and forwards them to a Capacitor server, where a real-time dashboard and PR-review tools surface the context no diff can give you: *why* code changed, what alternatives were weighed, and how it was actually built. It works across nine agents — Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, Pi, OpenCode, and Antigravity — capturing the full picture: session lifecycle, transcripts, subagent trees, tool calls, and token usage.

## Contents

- [Why Capacitor](#why-capacitor)
- [Requirements](#requirements)
- [Getting started](#getting-started) — [Install](#1-install-the-cli) · [Setup](#2-run-setup) · [Import](#3-import-existing-sessions-optional) · [Dashboard](#4-open-the-dashboard) · [MCP servers](#sessions-and-flows-mcp-servers-for-agents)
- [What it records](#what-it-records)
- [CLI commands](#cli-commands)
  - Sessions: [recap](#session-recap) · [validate-plan](#plan-validation) · [hide](#hide-session) · [disable](#disable-recording) · [errors](#error-extraction) · [eval](#session-evaluation-llm-as-judge)
  - Reviewing: [review](#pr-review-with-full-context) · [curate](#curate-guidelines)
  - MCP servers: [sessions](#sessions-mcp-server-for-agents) · [flows](#flows-mcp-server-for-agents) · [flow-result](#flow-result-mcp-server-hosted-reviewers) · [memory](#memory-mcp-server-for-agents)
  - Importing: [import](#loading-historical-sessions) · [remap](#renamed-repo-directories-kcap-remap)
  - Agents & daemon: [daemon](#daemon) · [agent](#local-agents-kcap-agent) · [repos](#repository-paths)
  - Account: [projects](#projects) · [profiles](#profiles) · [config](#configuration) · [uninstall](#uninstalling) · [other](#other-commands)
- [License](#license)

## Why Capacitor

- **Records everything, automatically.** Once set up, `kcap` runs silently in the background and captures every session — lifecycle, transcripts, subagent trees, tool calls, and token usage — with no change to how you work.
- **Real-time visibility.** Transcript data streams live over SignalR to a dashboard that shows repositories, sessions, and agents as they happen.
- **PR review with real context.** `kcap review` launches a reviewer equipped with MCP tools that query the actual implementation transcripts — ask *why* something changed, not just *what*.
- **Recall past work from your agent.** MCP servers let agents search and reuse prior sessions, durable team memories, and structured review flows without leaving the chat.
- **Works with your agents.** One install covers Claude Code, Codex, Cursor, GitHub Copilot, Gemini, Kiro, Pi, OpenCode, and Antigravity.

## Requirements

- **A Capacitor server URL** from your admin (e.g. `https://my-tenant.kcap.ai`), or sign in and let `kcap setup` discover/create your tenant.
- **Node.js + npm** to install the CLI globally (`npm install -g @kurrent/kcap`). The binary itself is a self-contained NativeAOT executable with no runtime dependency.
- **At least one supported coding agent** so there's something to record — Claude Code or Codex CLI on `PATH` at minimum (Cursor, Copilot, Gemini, Kiro, Pi, OpenCode, and Antigravity are detected too).
- **A supported platform:**

  | Platform | Architecture |
  |----------|-------------|
  | macOS | ARM64 (Apple Silicon) |
  | Linux | x64, ARM64 |
  | Linux (Alpine/musl) | x64, ARM64 |
  | Windows | x64 |

## Getting started

You need the server URL from your admin (e.g. `https://my-tenant.kcap.ai`).

### 1. Install the CLI

```bash
npm install -g @kurrent/kcap
```

npm automatically selects the right native binary for your [platform](#requirements). The CLI is compiled with NativeAOT — fast startup, no runtime dependency.

> **npm 11+ blocks install scripts by default.** You'll see a warning like
> `1 package has install scripts not yet covered by allowScripts`. The `kcap`
> binary works without the script; it only refreshes already-installed agent
> plugins (Claude / Codex / Cursor / Copilot / Gemini / Kiro / Pi) on upgrade. The warning suggests
> `npm approve-scripts @kurrent/kcap`, but that command rejects global installs
> (`EGLOBAL`) — a known npm UX bug. Instead, opt in one of two ways:
>
> ```bash
> # one-off
> npm install -g @kurrent/kcap --allow-scripts=@kurrent/kcap
> ```
>
> Or persistent — add this to `~/.npmrc` so every future `npm install -g`
> runs postinstall automatically:
>
> ```
> allow-scripts[]=@kurrent/kcap
> ```
>
> Without either, upgrade with **`kcap update`** instead of `npm install -g` — it
> runs the global npm upgrade and then refreshes your agent plugins itself, so it
> works regardless of the install-script gate. (You can also re-run `kcap plugin
> install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--skills] --if-installed` manually.)

> **Internal-tenant testers:** opt into pre-release builds with `kcap update
> --beta`; everyone else should stay on the default stable channel. See
> [`kcap update`](#other-commands) below.

### 2. Run setup

```bash
kcap setup                      # discovers your tenant — no URL needed
kcap setup <tenant>             # shorthand for a known tenant slug → https://<tenant>.kcap.ai
kcap setup --server-url <url>   # explicit server (self-hosted, or a full URL)
```

The setup wizard walks you through:

1. **Server** — with no `--server-url`/`<tenant>`, kcap **discovers** your tenant: it signs you in with your organization's single sign-on (pass `--github` to use GitHub instead), then lets you choose from the tenants you belong to. A bare `<tenant>` slug expands to `https://<tenant>.kcap.ai`; a full URL is used as-is. If you sign in with your organization's single sign-on and discovery finds no Capacitor tenant, `kcap setup` asks how to continue: create one for you (name + workspace URL, provisioned and waited for until it's live), point at a workspace you already belong to (enter its slug or URL — the same as `kcap setup <tenant>`), or cancel. That middle choice matters because SSO discovery only lists workspaces that use org SSO: a workspace whose members sign in with the GitHub App shows up here as "no tenant", so pick **I already have a workspace**, or re-run with `--github`.
2. **Login** — authenticates via your tenant's configured sign-in method; discovery completes the sign-in inline
3. **Default visibility** — choose how your sessions are visible to others
4. **Coding-agent hooks** — detects Claude Code and Codex CLI on `PATH`, Cursor by user-dir presence (`~/.cursor/`), GitHub Copilot CLI by `~/.copilot/` or `copilot` on `PATH`, Google Gemini CLI by `~/.gemini/` or `gemini` on `PATH`, AWS Kiro CLI by `~/.kiro/` or `kiro`/`kiro-cli` on `PATH`, Pi by `~/.pi/` or `pi` on `PATH`, SST OpenCode by `~/.config/opencode/` (or `~/.local/share/opencode/`) or `opencode` on `PATH`, and Google Antigravity by `~/.gemini/antigravity/` or `antigravity` on `PATH`, lists what it found, then asks **one** yes/no prompt to install kcap for every detected agent (hooks — or, for Pi/OpenCode/Antigravity, the live-ingest plugin — plus skills, instructions, and MCP) — plus a single shared set of agent skills under `~/.agents/skills/`, installed once when any of Codex, Cursor, Copilot, Gemini, Pi, or OpenCode is detected (Claude gets its skills from the bundled plugin; AWS Kiro and Google Antigravity read their own skills dirs — `~/.kiro/skills` and `~/.gemini/skills` respectively — so each gets its own copy there instead of the shared tree) — all user-wide. For Codex it also offers to enable **sandbox network access** for kcap (see below) — Codex blocks sandbox network by default, so the kcap skills can't reach the server without it. Each agent's own config-relocation environment variable is honored when set: `CLAUDE_CONFIG_DIR` (Claude), `CODEX_HOME` (Codex), `GEMINI_CLI_HOME` (Gemini — names the parent of `.gemini`), `KIRO_HOME` (Kiro), `COPILOT_HOME` (Copilot), `OPENCODE_CONFIG_DIR` (OpenCode), and `PI_CODING_AGENT_DIR` (Pi). Cursor's hooks path is fixed at `~/.cursor/hooks.json` and is not relocated.
5. **Daemon** — configure the daemon name for remote agent execution (the daemon verb is `kcap daemon`; `kcap agent` is a separate group that runs coding agents — see [Local agents](#local-agents-kcap-agent))
6. **Import past sessions** — offers (default yes) to import this repository's past sessions across every detected agent, equivalent to `kcap import --repo .`. Only shown when the current directory is a git repo with a resolvable origin remote and your authentication requirements are satisfied — which includes no-auth servers (auth provider `None`, no token needed); otherwise it's skipped with the usual `kcap import` hint. Opt out with `--skip-import`.

When setup finishes, `kcap` sends a best-effort POST to the server's `/api/users/me/cli-setup` endpoint so the dashboard can mark your CLI as registered and surface the import-past-sessions hint. The call is capped at 5 seconds and failures are silent — they do not affect setup completion.

> **Restart your coding agent for live recording to begin.** Hooks only load at session start, so a session that was already running when you ran setup keeps running without them and won't stream live. Start a new session (or `claude --continue`) to pick the hooks up — setup prints this reminder when it installs any hooks. A manual `kcap import` of the in-progress session only yields a frozen snapshot.

Verify with `kcap whoami` and `kcap status`. `kcap whoami` prints your identity and the profile it
resolved, then asks the server whether it actually accepts your token — it exits non-zero if the
server rejects it, or if the token was issued by a different server than the profile now targets
(re-run `kcap login`). If the server can't be reached it says so and still exits 0, so it stays
usable offline.

Setup closes with a **Next steps** box. Each item opens with a question, because neither step is for
everyone:

- **Did you create this Capacitor server?** Complete server setup — inviting teammates, and
  optionally Slack and your own AI keys — by following
  [Setup Server](https://capacitor.kurrent.io/docs/getting-started/setup-server/). Always listed:
  `kcap` can't tell whether you own the server, so you self-select.
- **New to Capacitor?** Prompt **"Start kcap guided tour"** in your coding agent (or, in Claude Code,
  `/kcap:guided-tour`) to see what your team has recorded and work through per-use-case tutorials for
  evals, session recall, PR review, and analytics. It's a prompt rather than a slash command because
  only Claude Code has slash commands — the skill ships with the plugin and is also installed for
  Codex and the other `~/.agents/skills/` agents (plus Kiro and Antigravity) as `kcap-guided-tour`.
  This item only appears when an agent was detected and one of them carries the skill.

For non-interactive environments:

```bash
kcap setup --server-url https://my-tenant.kcap.ai --default-visibility org_public --no-prompt
```

In `--no-prompt` mode, the wizard installs hooks for every detected agent by default. Opt out per agent with `--skip-claude-hooks`, `--skip-codex-hooks`, `--skip-cursor-hooks`, `--skip-copilot-hooks`, `--skip-gemini-hooks`, `--skip-kiro-hooks`, `--skip-pi-hooks`, `--skip-opencode-hooks`, and/or `--skip-antigravity-hooks`. When Codex hooks are installed, the wizard also enables Codex sandbox network access for your server(s) by default; pass `--skip-codex-network-access` to leave `~/.codex/config.toml` untouched.

> **Behavior change: `--no-prompt` now also imports this repo's history.** The Step 6 import (above) defaults to yes like every other prompt in the wizard, so `kcap setup --no-prompt` now uploads this repository's past sessions too — when run inside a git repo with an origin remote and authentication requirements are satisfied (including no-auth/provider-`None` servers). Existing unattended/scripted `kcap setup --no-prompt` invocations will start uploading current-repo session history unless you add `--skip-import`.

> **Need hooks for an agent installed after setup, or scoped to a single repo?**
> Run `kcap plugin install [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity]` (omit the flag for the Claude Code plugin), or pair Codex with `--project` for a per-repo install. Use `--skills` instead of `--codex` if you only want the agent skills without Codex hooks. Cursor uses user-scope only — `--project` has no effect with `--cursor`. After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). After a `--project` install, also run `codex` once in the repo and accept the workspace trust prompt. Re-running after a kcap upgrade is rarely needed for user-scope installs — the npm postinstall hook auto-refreshes them on every `npm install -g @kurrent/kcap`, and `kcap update` refreshes them too (npm 11+ blocks install scripts by default — `kcap update` works regardless, or add `allow-scripts[]=@kurrent/kcap` to `~/.npmrc` to opt the postinstall in once).

> **Need at least one agent to capture sessions:** the setup wizard runs to completion without an agent CLI on `PATH` (it'll still configure your profile, auth, and daemon), but kcap only records work once Claude Code or Codex CLI is installed and the hooks are in place.

> **Keep the daemon running:** `kcap daemon start -d` stops when the process dies (a crash, or an OS memory-pressure kill — macOS jetsam / Linux OOM). To auto-restart it and start it at login, install it as a per-user service: `kcap daemon service install`. See [Daemon](#daemon).

> **PR/MR auto-tagging is best-effort:** sessions on a branch with an open pull/merge request are automatically tagged with it, using the provider's own CLI — `gh` for GitHub and GitHub Enterprise, `glab` for GitLab. Neither is required to use kcap; if the matching CLI isn't installed or authenticated for the repo's host, the session is simply left untagged (no error, no retry).

### 3. Import existing sessions (optional)

```bash
kcap import                     # every detected agent (Claude, Codex, Cursor, Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity)
kcap import --org EventStore    # sessions whose git-remote owner is EventStore
kcap import --org               # pick an org from your discovered repos (and remember it)
kcap import --repo owner/repo   # sessions for one specific repo
kcap import --cursor            # only Cursor
kcap import --copilot           # only Copilot
kcap import --gemini            # only Gemini
kcap import --kiro              # only Kiro
kcap import --pi                # only Pi (badlogic/pi-mono)
kcap import --opencode          # only OpenCode
kcap import --antigravity       # only Antigravity
```

> **Pi** has no shell hooks, so live capture uses a shipped Pi extension rather than a hooks file: run `kcap plugin install --pi` (or accept the `kcap setup` prompt) to write `~/.pi/agent/extensions/kcap.ts`, which `pi` auto-loads and streams each session live. Because Pi also ships no built-in MCP, the same command installs an MCP-bridge extension (`~/.pi/agent/extensions/kcap-mcp.ts`, opt out `--skip-pi-mcp`) that exposes the kcap MCP servers as native Pi tools, plus a steering block in `~/.pi/agent/AGENTS.md` (opt out `--skip-pi-instructions`). Historical `kcap import --pi` works with or without any of it.

> **OpenCode** likewise has no shell hooks: live capture uses a shipped OpenCode plugin. Run `kcap plugin install --opencode` (or accept the `kcap setup` prompt) to write `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads and streams each session live (`vendor=opencode`). Subagents (the `task` tool / `@agent`) are captured too — the plugin fetches each child session via the SDK and streams it, so it nests under the parent in the trace. Historical `kcap import --opencode` reads OpenCode's SQLite database (`~/.local/share/opencode/opencode.db`) and imports every transitive descendant session (children, grandchildren, and so on — see [Loading historical sessions](#loading-historical-sessions)), so it backfills sessions from before the plugin was installed.

This backfills your past sessions from `~/.claude/projects/` (Claude), `~/.codex/sessions/` (Codex), `~/.cursor/projects/.../agent-transcripts/` (Cursor), `~/.copilot/session-state/` (Copilot), `~/.gemini/tmp/<project>/chats/` (Gemini), `~/.kiro/sessions/cli/` (Kiro), `~/.pi/agent/sessions/` (Pi), `~/.local/share/opencode/opencode.db` (OpenCode), and `~/.gemini/antigravity/brain/` (Antigravity) so they appear in the dashboard. All agents are discovered automatically — pass `--claude`, `--codex`, `--cursor`, `--copilot`, `--gemini`, `--kiro`, `--pi`, `--opencode`, or `--antigravity` (one or more) to narrow the run. All forms are idempotent — safe to run multiple times.

You must pick an explicit scope (`--all`, `--org`, or `--repo`) so personal/private repos aren't uploaded by accident. `--org <owner>` filters by the git-remote owner (GitHub org/user) detected on each session — independent of your profile name, so it behaves identically under GitHub and WorkOS sign-in. A bare `--org` lets you pick an owner from your discovered repos and remembers it for next time. Run with no scope on an interactive terminal to get a picker. See [Loading historical sessions](#loading-historical-sessions) for the full set of flags.

If your repo directories have been renamed or deleted on disk, the import prints a list of unresolved cwds up front. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) to recover those sessions.

### 4. Open the dashboard

Open the server URL in your browser. The dashboard shows repositories, sessions, and agents. It updates in real time as Claude Code sessions are active.

### Sessions and Flows MCP servers for agents

The `kcap mcp sessions` stdio server lets coding agents search and recall past Capacitor sessions without leaving the chat. `kcap setup` **registers it (with `kcap-review`) for Claude Code, Codex CLI, Cursor, GitHub Copilot CLI, Gemini CLI, SST OpenCode, Google Antigravity, and AWS Kiro CLI** — no manual `claude mcp add` or TOML/JSON edit. For Claude Code it's carried by the plugin's `.mcp.json`; for Codex CLI, `kcap setup` / `kcap plugin install --codex` write it into `~/.codex/config.toml`; for Cursor, `kcap setup` / `kcap plugin install --cursor` write it into `~/.cursor/mcp.json` (opt out with `--skip-cursor-mcp`); for Copilot, `kcap setup` / `kcap plugin install --copilot` write it into `~/.copilot/mcp-config.json` (opt out with `--skip-copilot-mcp`); for Gemini, `kcap setup` / `kcap plugin install --gemini` write it into the shared `~/.gemini/settings.json` (opt out with `--skip-gemini-mcp`); for OpenCode, into `~/.config/opencode/opencode.json` (opt out with `--skip-opencode-mcp`); for Antigravity, `kcap setup` / `kcap plugin install --antigravity` write it into `~/.gemini/config/mcp_config.json` (opt out with `--skip-antigravity-mcp`); for Kiro, `kcap setup` / `kcap plugin install --kiro` write it into `~/.kiro/settings/mcp.json` (opt out with `--skip-kiro-mcp`). The server is repo-aware: `cd` into a project before spawning your agent and `search_sessions` defaults to that repo's sessions.

The `kcap mcp flows` stdio server lets agents start and interact with AI-powered agent flows — any flow-definition catalog entry, not just reviews. The plugin **auto-registers it for Claude Code**, and `kcap setup` / the harness-specific plugin installers also register it for Codex, Cursor, Copilot, and Gemini. Codex registration is conservative: existing manual entries are preserved, and uninstall removes only unchanged kcap-owned entries. See the [Flows MCP server](#flows-mcp-server-for-agents) section for details.

The `kcap mcp flow-result` stdio server is the reviewer-side counterpart: the daemon injects it into hosted review-flow reviewer sessions so they can submit their result. It is not meant to be registered or run manually — see [Flow-result MCP server](#flow-result-mcp-server-hosted-reviewers).

The `kcap mcp memory` stdio server lets agents search, save, and update durable team memories — preferences, feedback, project facts, and references scoped to you, your team, or the org. `kcap setup` **auto-registers it for Claude Code** (via the plugin's `.mcp.json`), **Codex CLI** (in `~/.codex/config.toml`, alongside `kcap-sessions` / `kcap-review`), **Cursor** (in `~/.cursor/mcp.json`, alongside the other three servers), **GitHub Copilot CLI** (in `~/.copilot/mcp-config.json`, alongside the other three servers), **Gemini CLI** (in the shared `~/.gemini/settings.json`, alongside the other three servers), **SST OpenCode** (in `~/.config/opencode/opencode.json`, alongside the other three servers), **Google Antigravity** (in `~/.gemini/config/mcp_config.json`, alongside the other three servers), and **AWS Kiro CLI** (in `~/.kiro/settings/mcp.json`, alongside the other three servers); Codex's native plugin loader also picks it up via `.codex-mcp.json`. See the [Memory MCP server](#memory-mcp-server-for-agents) section for details.

Beyond registering the servers, `kcap setup` / `kcap plugin install` also installs a small kcap-owned **agent-instructions block** for harnesses that read a user-level instructions file (GitHub Copilot CLI's `~/.copilot/copilot-instructions.md`, and Gemini CLI's + Google Antigravity's shared `~/.gemini/GEMINI.md` today; more rolling out per harness). It's a marker-delimited, non-destructive note (preserves any instructions you've written) that steers the agent to prefer the kcap tools for "why / history / prior-work" questions over native `git`/GitHub/grep — registration alone doesn't make agents route to the tools. Opt out with `--skip-<harness>-instructions`.

Where a harness exposes a per-server trust knob, registration also marks the **read-only** kcap servers auto-approved so the agent doesn't stop to ask before every read: **Gemini** marks `kcap-review`, `kcap-sessions`, and `kcap-analytics` via `"trust": true` in `~/.gemini/settings.json`, and **Codex** marks the same three via `default_tools_approval_mode = "approve"` in `~/.codex/config.toml`. The write-capable `kcap-memory` (saves memories) and the work-launching `kcap-flows` (starts a *paid* hosted reviewer) are deliberately left prompting. **Cursor** and **Copilot** have no per-server auto-approve field in the config we write — auto-approve kcap's read tools there through the harness's own controls instead (Cursor's Auto-run mode or `cursor-agent --approve-mcps`; Copilot's `--allow-tool` / `--allow-all-tools`).

The `kcap mcp workitems` stdio server lets agents attach the current session (and its continuation chain) to a work item — by issue key, PR number, work item id, or a brand-new title — or list what a session is already attached to. The plugin **auto-registers it for Claude Code only**; it isn't offered for Cursor or Codex. See the [Work items MCP server](#work-items-mcp-server-for-agents) section for details.

The `kcap mcp analytics` stdio server lets agents answer analytics questions about the org's recorded coding sessions — spend, token/tool/model usage, outcomes, commits, PRs, evals — with governed read-only SQL over the server's curated analytics views. `kcap setup` **auto-registers it for Claude Code, Codex CLI, Cursor, GitHub Copilot CLI, Gemini CLI, SST OpenCode, Google Antigravity, and AWS Kiro CLI** — alongside the other repo-aware servers. It's repo-aware: it resolves its scope from the working directory, so `cd` into a project before spawning your agent. See the [Analytics MCP server](#analytics-mcp-server-for-agents) section for details.

## What it records

Once set up, Capacitor runs silently in the background. Every Claude Code (and Codex CLI, if you installed those hooks) session is captured automatically:

- **Session lifecycle** — start, end, interruptions, context compaction
- **Durable lifecycle delivery** — if the server is briefly unreachable when a `SessionStart`/`SessionEnd` hook (or a per-subagent `SubagentStop` carrying an `agent_id`) fires (for example during a deploy), the event is spooled to `~/.config/kcap/spool/` and automatically re-sent on the next hook, so sessions don't get stuck "active", lose their start record, or leave subagents stuck "running". No action needed; stale spool entries are reaped after 30 days.
- **Transcript data** — streamed in real time via a background watcher process over SignalR
- **Subagent activity** — full tree of spawned subagents with their own transcripts
- **Tool usage** — every tool call with timing and results
- **Token consumption** — input/output/cache token counts per interaction
- **Repository context** — git repo, branch, and PR linkage
- **In-agent upgrade prompts** — in Claude Code sessions, when the server is running a newer kcap release than the local CLI, additional context is injected into the session so the agent can offer the user an upgrade via `kcap update`. The stderr `kcap` update hint continues to fire for direct command-line use.
- **SessionStart context injection** — at every session start the server injects top evaluation-derived fact clusters for the current repo into Claude's `additionalContext`. The injected block is split into two sections: `## Known patterns` (repo/project facts relevant to any reader) and `## Guidance from past sessions` (agent-targeted action items derived from prior eval suggestions with `audience: "agent"`). Opt out by setting `disable_session_guidelines: true` in `~/.config/kcap/config.json` or via `kcap config set disable_session_guidelines true`.
- **SessionStart team-memory index** — at every session start (Claude Code, Codex CLI, GitHub Copilot CLI, Gemini CLI, AWS Kiro CLI, Google Antigravity, and Cursor CLI's `cursor-agent` — see the capability matrix below for the full per-harness rollout) `kcap` also fetches a compact index of durable [team memories](#memory-mcp-server-for-agents) visible for the current repo/machine and appends a `## Team memory` block to the session's injected context (`additionalContext` for Claude, Codex, Copilot, and Gemini, `additional_context` for Cursor, raw stdout for Kiro, `injectSteps`/`userMessage` for Antigravity): one `slug: description` line per memory, grouped **Org / Team / Yours**, with a nudge to call `get_memory` / `search_memories` for full content. Only the index is injected — never the bodies — so the cost stays roughly flat as the pool grows (mirrors a local `MEMORY.md`). Best-effort and fail-open (a slow or failed fetch injects nothing, never blocking the hook), and only ever injected once per conversation. Opt out with `disable_memory_index: true` in `~/.config/kcap/config.json` or `kcap config set disable_memory_index true`.
- **Crash resilience** — if a `kcap` command hits an unexpected error it records the exception (with stack trace) to `~/.config/kcap/crash.log` (honours `KCAP_CONFIG_DIR`; size-capped) and exits cleanly instead of aborting. Hook and detached-generator commands the coding agent spawns **fail open** (exit 0, nothing surfaced to the agent); other commands exit non-zero with a one-line stderr message pointing at the log.

The SessionStart memory foundation is deliberately separate from harness activation. Every row uses
the same typed fetch/render, lifecycle, fenced lease, and golden output contracts. Claude, Cursor,
Codex, Copilot, Gemini, Kiro, and Antigravity are wired; each remaining adapter is activated and
live-certified by its own AI-1456 child issue.

| Harness | Shared foundation | Hook/extension wired | Live receipt | Upstream status |
|---------|-------------------|----------------------|--------------|-----------------|
| Claude Code | yes | yes | existing baseline | available |
| Codex CLI | yes | yes (`SessionStart`'s `hookSpecificOutput.additionalContext`, combined with the `continue` handshake) | **certified** — gated live cert, `codex-cli 0.144.3`, needs kcap ≥ the release carrying this adapter | available |
| Cursor CLI (`cursor-agent`) | yes | yes | manual, recorded live-cert gate (needs `cursor-agent` + a reachable server + a memory in scope; not run in CI) | available — **supported** |
| Cursor IDE (Agent Window) | yes | yes (same adapter — `sessionStart`'s `additional_context`) | **upstream-degraded**: the hook emits the correct JSON, but whether the IDE's Agent Window actually surfaces it to the model is not guaranteed — a known Cursor IDE limitation, not a `kcap` defect | hook output correct; model receipt not guaranteed |
| GitHub Copilot CLI | yes | yes (`sessionStart`'s top-level `additionalContext`; silent when there is nothing to inject) | **certified** — gated live cert, `copilot 1.0.75` | available |
| Gemini CLI | yes | yes (`sessionStart`'s top-level `additionalContext`; falls back to the plain `{"continue":true}` allow payload when there is nothing to inject) | pending | available |
| Kiro CLI | yes | yes (`agentSpawn` raw stdout — no envelope; Kiro appends hook stdout to agent context verbatim) | **certified** — gated live cert, `kiro-cli 2.12.1`. The once-per-session dedupe is unit-covered only: a resumed `kiro-cli` invocation carries a different hook session id, so it cannot certify it | available — injects **once per session** despite `agentSpawn` firing every prompt |
| Pi | yes | no | pending | extension bridge required |
| OpenCode | yes | yes (plugin `experimental.chat.system.transform` — the fragment is appended as a new system entry, never replacing one; the CLI writes it raw on the start hook's stdout and zero bytes when there is nothing to inject) | **certified** — gated live cert, `opencode 1.18.9`, positive case plus negative control. Injection rides an **experimental** OpenCode API, so this cert is the only thing that would notice an upstream change silently ceasing delivery — it already earned that: it caught a start/first-request race no unit test could see, where a one-turn session got no index at all. Needs the plugin AND the `kcap` on `PATH` to be the same build; the positive case asks a model to echo 32 random hex characters, so a small free model flakes on transcription — the failure message distinguishes that from a delivery failure | available; transform contract measured on `opencode` 1.18.9. Appends on **every request** (OpenCode rebuilds the system array per request), while the fetch stays **once per session** behind the durable lease |
| Antigravity | yes | yes (`PreInvocation` → `{"injectSteps":[{"userMessage":…}]}`; zero bytes when there is no index) | pending — **IDE only**. The gated cert (`KCAP_ANTIGRAVITY_MEMORY_LIVE=1` + `KCAP_URL`) was run against `agy` 1.1.10 and the model did **not** receive the index: the CLI's print mode fires the hook but ignores the returned `injectSteps`. Our side is verified (hook fires; a correct 516–2518 byte payload is emitted; run completes well inside the hook budget), so the cert is retained as the regression test for when upstream honours it. Certification therefore needs the **manual IDE procedure** | available on the **IDE**; on the **CLI** the hook fires and captures, but the injection is not surfaced to the model (`agy` 1.1.10). Injects **once per conversation** despite `PreInvocation` firing every invocation |

## CLI commands

At a glance — each links to its section below:

| Command | What it does |
|---------|--------------|
| [`kcap setup`](#initial-setup) | Interactive wizard — server, auth, agent hooks, daemon |
| [`kcap import`](#loading-historical-sessions) | Backfill past sessions from every detected agent |
| [`kcap recap`](#session-recap) | AI summary + per-turn outline of a session |
| [`kcap validate-plan`](#plan-validation) | Check that every planned item was completed |
| [`kcap hide`](#hide-session) | Mark a session owner-only |
| [`kcap disable`](#disable-recording) | Stop recording and delete server-side data |
| [`kcap errors`](#error-extraction) | Extract tool-call errors from a session |
| [`kcap eval`](#session-evaluation-llm-as-judge) | Score a session with LLM-as-judge |
| [`kcap review`](#pr-review-with-full-context) | Launch a PR review with full transcript context |
| [`kcap mcp <server>`](#sessions-mcp-server-for-agents) | Run an MCP server (sessions / flows / memory / …) for agents |
| [`kcap curate apply`](#curate-guidelines) | Sync promoted guidelines into `CLAUDE.md` / `AGENTS.md` |
| [`kcap daemon …`](#daemon) | Run and manage the agent daemon |
| [`kcap agent`](#local-agents-kcap-agent) | Start, list, attach to, and stop daemon-hosted agents |
| [`kcap repos`](#repository-paths) | Manage known repo paths for the launch dialog |
| [`kcap projects` / `project`](#projects) | List and inspect projects |
| [`kcap profile` / `use`](#profiles) | Manage and switch between servers/profiles |
| [`kcap config`](#configuration) | Show and set configuration |
| [`kcap remap`](#renamed-repo-directories-kcap-remap) | Map renamed repo directories for import |
| [`kcap ignore`](#configuration) | Exclude paths from recording |
| [`kcap update`](#other-commands) | Upgrade the CLI and refresh agent plugins |
| [`kcap uninstall`](#uninstalling) | Remove kcap from this machine |
| [`kcap status` / `whoami` / `login` / `logout`](#other-commands) | Health, identity, and auth |

### Initial setup

```bash
kcap setup                                   # interactive wizard (discovers your tenant)
kcap setup <tenant>                          # shorthand: https://<tenant>.kcap.ai
kcap setup --server-url <url> --no-prompt    # CI / scripted
```

With no server argument, setup (and `kcap login`) runs **tenant discovery**: it signs you in with your organization's single sign-on, then lets you pick from the tenants you belong to. Pass `--github` to sign in with GitHub instead; `--discover` forces discovery even when a server is configured. In SSH / headless environments (no browser), discovery falls back to GitHub Device Flow, since SSO needs a local browser.

The setup wizard detects every supported coding agent, asks **one** yes/no prompt to install kcap (hooks, skills, instructions, MCP) for all of them, configures the daemon, and finishes with an offer to import this repository's past sessions. Claude Code and Codex CLI are detected via `PATH`; Cursor is detected by user-dir presence (`~/.cursor/`), so IDE users without the `cursor` shell command are covered; GitHub Copilot CLI is detected via `~/.copilot/` or `copilot` on `PATH`; Google Gemini CLI via `~/.gemini/` or `gemini` on `PATH`; AWS Kiro CLI via `~/.kiro/` or `kiro`/`kiro-cli` on `PATH`; Pi via `~/.pi/agent/` or `pi` on `PATH`; SST OpenCode via `~/.config/opencode/` (or `~/.local/share/opencode/`) or `opencode` on `PATH`; and Google Antigravity via `~/.gemini/antigravity/` or `antigravity` on `PATH` (Pi, OpenCode, and Antigravity have no shell hooks, so for those the wizard installs a live-ingest plugin rather than hook config). Re-run any time to update the configuration.

- **New tenant:** when signing in via Kurrent's hosted auth and you have no tenant yet, `setup` prompts to create one (organization name + `<slug>.kcap.ai` workspace URL) and waits for it to come online. Non-interactive runs (`--no-prompt`) skip this and exit with guidance.
- **Import past sessions:** the final step offers (default yes) to import this repository's past sessions across every detected agent — equivalent to `kcap import --repo .`. It only appears when the current directory is a git repo with a resolvable origin remote and your authentication requirements are satisfied — which includes no-auth servers (auth provider `None`); otherwise it's skipped with the usual `kcap import` hint. Opt out with `--skip-import`.

In `--no-prompt` mode, hooks install for every detected agent by default. Opt out per agent:

```bash
kcap setup --server-url <url> --no-prompt --skip-codex-hooks --skip-cursor-hooks   # only Claude
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-cursor-hooks  # only Codex
kcap setup --server-url <url> --no-prompt --skip-claude-hooks --skip-codex-hooks   # only Cursor
```

> **Behavior change:** `--no-prompt` now also auto-imports this repository's past sessions (the Step 6 import defaults to yes), subject to the same eligibility gate (git repo with an origin remote; authentication requirements satisfied, including no-auth/provider-`None` servers). Existing unattended/scripted `kcap setup --no-prompt` invocations will start uploading current-repo session history unless they add `--skip-import`.

After installing Codex hooks, the next `codex` launch prompts to trust the new hooks — accept once to trust them all (run `/hooks` inside Codex if you'd rather trust each entry individually). For project-scope installs (a single repo), use `kcap plugin install [--codex] --project` after setup.

Legacy `--plugin-scope <user|project|skip>` is retained for backwards compatibility:

- `user` — no-op (matches the new default)
- `project` — install the Claude Code plugin into `<repo>/.claude/settings.local.json`. Must be run from inside a git working tree; setup exits with an error otherwise.
- `skip` — alias for `--skip-claude-hooks`

New scripts should prefer `--skip-claude-hooks` / `--skip-codex-hooks` and `kcap plugin install --project` for project scope.

If you run `kcap setup` outside any git working tree, it still completes — hooks install user-scope and fire for every session — but a tip at the end reminds you that sessions recorded from non-repo directories won't capture owner/repo/branch/PR context. The Step 6 import is skipped too, since there's no origin remote to scope it to.

### Session recap

By default, shows a concise AI-generated summary — why the work was done, key decisions, and anything left unfinished — followed by a per-turn outline (one prose line per turn) and a `--get-turn <N>` pointer for drilling into any turn. Use `--full` for the complete transcript with all prompts, responses, and file changes.

```bash
kcap recap <sessionId>              # summary + per-turn outline (default)
kcap recap --full <sessionId>       # full transcript
kcap recap --chain <sessionId>      # summaries across continuation chain
kcap recap --chain --full <sessionId>  # full transcript across chain
kcap recap --per-turn <sessionId>   # compact per-turn index (prompt, tools, files, tokens, time)
kcap recap --get-turn <N> <sessionId>  # full event transcript for a single turn
```

`--per-turn` prints a one-block-per-turn index — useful for orienting in a long session before drilling into a specific turn with `--get-turn <N>` (the turn number shown in the `--per-turn` index). `--get-turn` takes the turn number as its value; the session id is the usual positional (or comes from the current session), so `kcap recap <sessionId> --get-turn <N>` works too.

The identifier can be a session GUID or a meta session slug. Find these from the dashboard or the current session's hook payloads. When run inside a Claude Code session with the kcap plugin, the session ID is set automatically.

If the kcap plugin is installed, you can also use the `/kcap:recap` skill inside Claude Code, or just ask:

```
Recap session c4de7fbe-cff5-4e2c-bf80-9858d02f58be and propose what should be done next.
```

### Plan validation

Verify that all items in a session's plan were completed.

```bash
kcap validate-plan <sessionId>
```

Fetches the session's discovered plan artifacts (`GET /api/sessions/{id}/plan-artifacts?chain=true`) — the primary artifact plus any other candidates the server found, in an ordered set — and pairs them with the existing `recap` call for the current session's file writes/edits and AI-generated "what's done" summaries. On an older server without the artifacts route (or a non-visible session), `validate-plan` falls back automatically to the previous recap-only behavior. A degraded, truncated, or unavailable plan renders an explicit marker line instead of failing silently; if the primary artifact's content can't be retrieved, the command reports that validation isn't possible and exits with status 2 (0 for a normal render or when no plan is found at all — absence is a valid answer).

With the plugin installed, use the `/kcap:validate-plan` skill or ask naturally:

```
Did I finish everything in the plan? Check what's left to do.
```

### Hide session

Mark a session as owner-only so other users no longer see it in the dashboard.

```bash
kcap hide                 # current session
kcap hide <sessionId>     # specific session
```

### Disable recording

Stop the watcher, silence future hooks, and delete server-side data for a session.

```bash
kcap disable                 # current session
kcap disable <sessionId>     # specific session
```

This is irreversible on the server side; the local transcript file is untouched.

### Error extraction

Scan a recorded session for tool call errors — failed bash commands, file read/write errors, agent failures, etc.

```bash
kcap errors <sessionId>              # single session
kcap errors <meta-session-slug>      # meta session
kcap errors --chain <sessionId>      # full continuation chain
```

Useful for post-session review: identify recurring mistakes, discover patterns to avoid, and update project instructions accordingly.

### Session evaluation (LLM-as-judge)

Score a recorded session against safety, plan adherence, quality, and efficiency criteria. Each of 13 questions (e.g. *"Did the agent run destructive commands?"*, *"Did it write tests when appropriate?"*, *"Were there repeated failed attempts at the same operation?"*) is answered by a separate headless Claude judge with **no filesystem or network tools**. Most judges reason from the compacted session trace embedded in the prompt; some questions — and any session whose trace is too large to embed — instead investigate the session on demand through a read-only, session-scoped MCP tool surface (summary, search, transcript, errors, recap, tool results). The embed-vs-tools threshold is tunable via `KCAP_EVAL_TRACE_TOKEN_BUDGET` (default ~200K estimated tokens).

```bash
kcap eval <sessionId>                      # default: sonnet judge
kcap eval --model opus <sessionId>         # stronger judge
kcap eval --chain <sessionId>              # include the full continuation chain
kcap eval --threshold 5000 <sessionId>     # keep more of each tool output before truncation
kcap eval --questions safety <sessionId>   # run only the 4 safety judges
kcap eval --skip efficiency <sessionId>    # run everything except efficiency
kcap eval --list-questions                 # print the question taxonomy
```

Output is a per-category + overall score (1-5, with `pass`/`warn`/`fail` verdicts), with a specific finding and supporting evidence per question. The aggregate is also persisted back to the session's stream as a `SessionEvalCompleted` event, so past evaluations can be queried from the dashboard or used to track quality trends across sessions.

Expect ~1-3 minutes total depending on the model and session size — judges run sequentially.

> **Server requirement:** `kcap eval` fetches its question catalog from the server (`GET /api/eval/catalog`) and posts results to `POST /api/sessions/{id}/evals/v3`. It fails fast with a clear error against a server that doesn't expose the catalog endpoint, so the server must be running a build that includes the eval catalog (Capacitor AI-9). Upgrade the server if `kcap eval` reports the catalog endpoint is unavailable.

### PR review with full context

```bash
kcap review <pr-url-or-owner/repo#number>
```

Accepts a GitHub PR URL (`https://github.com/owner/repo/pull/123`, any host including GitHub Enterprise), a GitLab MR URL (`https://gitlab.com/owner/repo/-/merge_requests/123`, including nested groups such as `https://gitlab.com/group/subgroup/repo/-/merge_requests/123`), or the shorthand `owner/repo#123` (single-level only).

Launches a Claude Code session equipped with MCP tools that query the implementation transcripts. Reviewers can ask *why* code was changed, understand design decisions, check what alternatives were considered, and verify test coverage — all grounded in what actually happened during development.

The same MCP server (`kcap-review`) is also auto-registered by the Kurrent Capacitor plugin and available in any Claude Code session, not just ones launched via `kcap review`. Each PR-scoped tool (`get_pr_summary`, `list_pr_files`, `get_file_context`, `search_context`, `list_sessions`) accepts an optional `pr` argument — pass `"owner/repo#123"` or a GitHub/GitLab URL to review any PR from any branch. When omitted, the server falls back to the PR passed at startup (set by `kcap review <pr>`) or to git auto-detection against the current branch. `get_transcript` keys off `session_id` and doesn't need a `pr` argument.

### Sessions MCP server (for agents)

```bash
kcap mcp sessions
```

Stdio MCP server that exposes past Capacitor sessions to coding agents (Claude Code, Codex, Cursor, Copilot, Gemini, Antigravity) so they can search and recall prior work without leaving the chat. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it (alongside `kcap-review`) directly in `~/.codex/config.toml` under `[mcp_servers]`, so there's nothing extra to do — launch Codex from your repo directory so the server resolves the right repo. Enabling the kcap plugin through Codex's native plugin manager (`codex plugin add`) also provides them via the plugin's `.codex-mcp.json` descriptor. **Cursor:** `kcap setup` / `kcap plugin install --cursor` register it (alongside the other three kcap servers) in `~/.cursor/mcp.json`; opt out with `--skip-cursor-mcp`. **GitHub Copilot CLI:** `kcap setup` / `kcap plugin install --copilot` register it (alongside the other three kcap servers) in `~/.copilot/mcp-config.json`; opt out with `--skip-copilot-mcp`. **Gemini CLI:** `kcap setup` / `kcap plugin install --gemini` register it (alongside the other three kcap servers) in the shared `~/.gemini/settings.json`; opt out with `--skip-gemini-mcp`. **Google Antigravity:** `kcap setup` / `kcap plugin install --antigravity` register it (alongside the other three kcap servers) in `~/.gemini/config/mcp_config.json` — Antigravity's own MCP file, not the Gemini CLI's `settings.json`; opt out with `--skip-antigravity-mcp`.

It provides four tools:

- **`search_sessions`** — free-text search over past sessions (and subagent transcripts), searching the current repo first and automatically widening to every visible repo when results come back thin (the response then carries `widened_to_all_repos: true`, and each hit includes its own repo). Pass `repo: "all"` to search across every repo you can see up front, or `repo: "owner/name"` for a different one — an explicit `repo` (including `"all"`) never auto-widens. Filter by `author` / `author_github_id`. Returns ranked hits with `session_id`, snippet, and (for transcript hits) `hit_event_index` + `agent_id` for drilling in.
- **`get_session_summary`** — concise `summary_text` + `plan` for a session. Use this to orient before reading the transcript.
- **`get_session_transcript`** — speaker-tagged events from a session. Pair `around_event` (and `agent_id` if the hit was in a subagent) with the values returned by `search_sessions` to fetch the exact decision context.
- **`get_turn`** — the full event transcript for one turn (user prompt, tool calls + results, assistant text) by `session_id` + `turn_index`. A turn is one user message and the assistant's full response up to the next user message.

The server is repo-aware — it resolves the current working directory to a repo hash at startup, and `search_sessions` defaults its `repo` filter to that hash, auto-widening to all repos only when that pinned search comes back thin. **If the current repo can't be resolved** (run outside a git checkout, or a missing/unparseable `origin` remote), `search_sessions` returns an error asking you to pass `repo: "owner/name"` or `repo: "all"` — it will not silently search across all repos.

### Flows MCP server (for agents)

```bash
kcap mcp flows
```

Stdio MCP server that lets coding agents start and interact with AI-powered agent flows — any entry in the server's flow-definition catalog, not just reviews — directly from within a session. The Kurrent Capacitor plugin **auto-registers it for Claude Code** (via `.mcp.json`), so there's nothing to do after `kcap setup` — the flows server derives the target repo from its launch working directory, and Claude Code always runs inside the repo, so one registration works for every repo. It's registered even with no daemon connected; the tools simply stay inert (and `start_flow` returns an error) until a daemon with the repo is available. `kcap setup` / `kcap plugin install --codex` register it for **Codex** in `~/.codex/config.toml`; existing manual/custom entries are never overwritten or claimed, and uninstall removes only unchanged kcap-owned entries. The native Codex plugin descriptor also includes it. The corresponding installers register it for **Cursor**, **GitHub Copilot CLI**, and **Gemini CLI** in their normal MCP configuration files. Because `kcap-flows` launches paid work, it is deliberately not marked read-only or auto-approved on any harness.

It provides four generic tools:

- **`start_flow`** — start a new flow from either `definition_id` (the server's flow-definition catalog, e.g. `spec-review`, `code-review`, or a custom definition) **or** `definition_yaml` (an inline dynamic flow definition — the full YAML, same schema as catalog definitions); provide exactly one of the two. Catalog starts use the guarded v2 protocol. A `definition_yaml` flow has extra constraints: every participant must declare `workspace: none` and a concrete, priced model (no `default`), the server clamps `limits`/`mcp` to its own caps rather than trusting the definition, may reject the whole thing with a coded error, and requires a server with dynamic flows enabled. Also provide `target_kind`, `target_ref`, `target_title`, and `context`. Requester context (session ID, cwd, repo root, owner, name) is resolved automatically from the environment. Returns a `flow_run_id`. A single-participant definition starts its first round eagerly; a multi-participant definition starts **round-less** — the response carries no round, and each declared role's agent launches lazily on its first `send_to_participant`. Optional `mode`: by default, when the daemon runs on the same machine and the selected vendor declares a borrowed-context containment strategy, the participant sees current tracked, dirty, and non-ignored untracked checkout content; the safety boundary is vendor-specific. Cursor runs in a daemon-owned snapshot refreshed before follow-up rounds, while direct borrowing is reserved for runtimes with a native read-only tool clamp. The capability is advertised for whatever build of the vendor CLI is installed — it is not gated on a per-version validation record, since a vendor auto-update would then silently drop the participant back onto a stale committed base. It IS gated per platform where the containment boundary has not been measured: **GitHub Copilot advertises borrowed review only on macOS/ARM64, and only on a daemon that can both enforce an OS sandbox (`sandbox-exec`) and broker a token** (`COPILOT_GITHUB_TOKEN`/`GH_TOKEN`/`GITHUB_TOKEN`) — widening its tool surface enough to read a snapshot also widens what a read tool can be pointed at, so the boundary is an OS sandbox rather than the vendor's own permission prompts, and that sandbox grants neither your keychain nor your Copilot state (see [Borrowed-context Copilot reviews](#borrowed-context-copilot-reviews)). Where any of the three is missing, a Copilot borrowed request returns `vendor_containment_unreadable` naming the daemon and the remedies; `mode="context-only"` works and is the remedy. Pass `mode="context-only"` to opt out. Claude review flows currently use owned worktrees because it declares no borrowed-review containment strategy. Optional `vendor`: for reserved `spec-review`/`code-review`, explicitly selects the reviewer independently of the driver; when omitted, resolution falls through to the definition's authored vendor when it declares one, then to your saved `flows.reviewer_vendor` preference (applied via one automatic retry, with the response saying so), and finally a coded `reviewer_vendor_required` if neither is set — ask the user which reviewer vendor to use, pass it explicitly, and offer to save it with `kcap config set flows.reviewer_vendor <vendor>`. Custom single-participant catalog definitions retain their authored vendor unless explicitly overridden. The server records requested/applied vendor plus selection source, and rejects an unavailable or uncertified vendor without silent fallback. Dynamic (`definition_yaml`) flows reject a top-level vendor override because each participant declares its own vendor. Optional `model`: a per-run reviewer **model** override for a single-participant catalog review — REQUIRES `vendor` (the model is interpreted against that vendor; there is no vendor→model table anywhere in the CLI, so `model` without `vendor` is rejected locally before any request is sent) and is rejected on a `definition_yaml` (dynamic) or multi-participant start (each participant already pins its own model). Pass the vendor's own model id or alias verbatim, case-sensitive — the CLI never translates, canonicalizes, or guesses it.
- **`send_to_participant`** — send a follow-up message to a participant role declared by the flow definition (single-participant definitions use `"reviewer"`; the server rejects an unknown role, naming the valid ones). One round runs at a time per role — a second send to a busy role is rejected naming the busy round, while other roles remain addressable. Returns the new round's findings.
- **`get_flow_status`** — get the current status (running, waiting, completed, failed) and last result of a flow run. Optional `wait` (boolean, default `false`): when `true`, blocks — via repeated bounded internal checks, never a raw long-poll — until the round is terminal or roughly 8 minutes pass, instead of returning the current snapshot immediately. A long-running round is expected, not an error; on the 8-minute cap this returns the same benign "still running" text an unset/false `wait` already returns on the round-submission path, so simply call it again with `wait: true`.
- **`close_flow`** — mark a completed flow run as closed.

Every flow response reports which **workspace** the reviewer actually used, so you can tell whether it saw your uncommitted work:

```
workspace: borrowed (the reviewer saw your working tree, uncommitted changes included)

workspace: fallback (not_colocated)
  ⚠ Your working tree was NOT borrowed — the reviewer did not see uncommitted work.

workspace: unknown
```

`fallback` carries the server's reason verbatim (`not_colocated`, `daemon_outdated`, `not_allowed`, `no_requesting_cwd`, `context_only_requested`, or any newer one — the CLI never translates or filters them). **`unknown` is not the same as borrowed**: it means no decision was disclosed — an older server, a multi-participant run, or a flow kind other than the reserved `spec-review`/`code-review` aliases. Treat it as "assume the reviewer did not see uncommitted work" and inline anything that matters. The line appears on start, every round, status and close.

Status and polled round-result output also cross-check the reviewer's **vendor**: if the `reviewer` participant's vendor disagrees with the run-level `applied_reviewer_vendor` (or the server flags `reviewer_vendor_mismatch`), the CLI renders a `⚠ reviewer vendor mismatch` warning telling the driver to treat the run's results as suspect, close the flow, and report it. Agreement renders nothing.

Responses from these tools may carry **`pending_messages`** — out-of-band notes participants push to the driver via `send_flow_message` (see the flow-result server below). The CLI acknowledges them to the server after rendering the response, so a message is normally shown once — but a failed acknowledgment redelivers it on a later call (at-least-once), so consumers should treat the `message_id` as the dedup key and react to each id only once.

While the server is rebuilding its flows read model (a projection replay after a server upgrade), flow tools can return a coded **`server_catching_up`** error (HTTP 409) with the replay's progress. It is temporary and retryable: wait a few minutes and retry, or ask the user how to proceed. The CLI renders the same guidance on every surface — start/submit, status/close, and the flow-result sidecar tools.

A start or round call can also hit two settlement-layer coded 409s — `flow_settlement_busy` and `reviewer_launch_incarnation_superseded` — raised while the daemon is still settling a prior launch. The CLI transparently auto-retries these on an exponential backoff with jitter (roughly 250–500 ms for the first retry, 5–10 s in steady state), so a short settlement wait is invisible to the caller. The retry budget is an **elapsed-time deadline of 3 minutes** measured from the first attempt — not an attempt count, and it includes each request's own duration, because a settlement-aware server absorbs the wait by holding the request open. Only these two codes retry on every path; every other coded error (`server_catching_up`, `budget_unverifiable`, `client_upgrade_required`, …) and every uncoded failure surfaces on the first attempt.

`submit_review_round` and `send_to_participant` also retry a third coded 409, `participant_unreachable` — a previously-assigned reviewer whose absence isn't durably proven yet (e.g. it idled out between rounds). The server declares it retryable and it converges once that absence is proven, so the CLI auto-retries it the same way, on the same 3-minute deadline. Starts never see it, so `start_review_flow`/`start_flow` don't retry on it.

If the deadline is exhausted the call does **not** hang or silently succeed: the tool returns an explicit error result (`isError: true`) in the usual `Error (code): message` shape, carrying how many attempts were made over how long, plus the last coded error it kept hitting — or, when the deadline cancelled the very first request before any coded response arrived, a note that the code shown is the client's default rather than something the server reported — for example `Error (flow_settlement_busy): gave up after 14 attempts over 3m — the daemon is still settling a prior launch and could not admit this one in time.` That outcome is retryable: try again in a minute, or check whether another flow is already running against the same daemon. The round **poll** loop has its own separate budget and its own graceful-cap message.

The four review tools — **`start_review_flow`**, **`submit_review_round`**, **`get_review_flow_status`**, **`close_review_flow`** — are aliases of the generic tools above: `start_review_flow`'s `kind` maps to `start_flow`'s `definition_id`, and `submit_review_round`'s `context` maps to `send_to_participant`'s `message` with the `reviewer` role targeted implicitly. Current clients always use flow protocol v2 for catalog starts; servers can reject legacy reserved-alias starts with `client_upgrade_required`. New integrations should prefer the generic tools; the review aliases remain convenient for a plain spec/code-review loop.

**Reviewer model override — capability gating, protocol, and semantics:**

- **Capability gating.** A daemon only advertises `SupportsReviewerModelResolution` (per vendor) when that vendor is installed, unattended-certified, AND has a runtime-owned model resolver — today that's **Claude** and **Codex** only. ACP-hosted vendors (Cursor, Copilot, Gemini, Kiro, Pi, OpenCode, Antigravity) advertise `false` for this field and keep their existing vendor-only (no model override) unattended support; the server refuses a `model` override for any vendor that doesn't advertise it, with no silent fallback. A resolver owns its own vendor's aliases/ids entirely — there is deliberately no shared, central vendor→model table anywhere in the CLI or daemon.
- **Protocol v3.** A `model`-bearing `start_review_flow`/`start_flow` call routes to `POST /api/flows/review/start/v3` (`client_flow_protocol_version: 3`) instead of the normal v2 route — exactly one request, never a v2 retry or fallback either way. `model` requires `vendor` and is rejected on a dynamic (`definition_yaml`) flow; both are checked locally, before any request is sent, so a bad pairing never reaches the server.
- **Exact error codes.** `reviewer_model_protocol_required` — the server predates the v3 protocol (404/405 on the v3 route, or an uncoded error body), or it accepted the start but didn't return the required ack fields (`applied_reviewer_model` + `reviewer_model_equivalence_key`); the CLI raises this one locally and, on the missing-ack case, best-effort closes the run it just started rather than leave an unvalidated reviewer running. Everything else is a genuine coded v3 verdict from the server, surfaced **verbatim** (never reformatted or intercepted): `reviewer_model_unavailable` (no advertised resolver on the selected vendor recognizes the model), `model_vendor_mismatch` (the model belongs to a different vendor than the one selected), `reviewer_model_safe_settlement_required` (the server can't prove a heal/relaunch would safely reapply the same model, so it refuses rather than risk a silent drift), and `reviewer_model_unpriceable` (the model has no resolvable pricing to budget-check against).
- **`requested`/`applied`/`resolved` semantics.** A v3 response's audit trail carries `requested_reviewer_model` (what was asked for), `applied_reviewer_model` (the canonical form the server matched), `resolved_reviewer_model` (the exact concrete value the daemon launched with), and `reviewer_model_source` (`"explicit"` for an override). These three legitimately differ as strings — a bare alias (`"opus"`) and the dated concrete id it resolves to are NOT compared textually. Validation is by opaque `reviewer_model_equivalence_key` **equality** end-to-end; the CLI never string-compares requested vs. applied vs. resolved.
- **Codex date-sensitivity.** The daemon always reports the verbatim model string it actually launched the agent with, never a date-suffixed model read back from session metadata. Claude's equivalence key is family-level and absorbs a dated id safely, but Codex's is slug-level and **date-sensitive** — a session-metadata model carrying a date suffix would compute a different equivalence key than the bare slug that was launched, and spuriously fail the server's post-launch echo validation.

Requires `kcap login` **and a running daemon with this repo checked out**. Discovery filters by the effective reviewer vendor before tie-breaking and requires the daemon's structured per-vendor capability before borrowing. Cursor and GitHub Copilot borrowed-context reviews both use a disposable owned snapshot — Copilot additionally requires macOS/ARM64, `sandbox-exec`, and a [brokered token](#borrowed-context-copilot-reviews); borrowed-checkout Claude review remains disabled, so those reviews use owned worktrees.

### Flow-result MCP server (hosted reviewers)

```bash
kcap mcp flow-result   # launched by the daemon — not meant to be run manually
```

Stdio MCP server the **daemon injects into hosted flow participant sessions** (Claude, Codex, Cursor, and GitHub Copilot participants). It exposes two tools: **`submit_review_result`** (`round_token`, `kind: "findings" | "clean"`, `findings`), which posts the participant's round result to the Capacitor server — the **only** delivery channel: the server does not read the participant's transcript, so ending a reply with `FINDINGS:`/`NO FINDINGS` markers delivers nothing (servers ≥ Flows Phase E-0) — and **`send_flow_message`** (`text`), which pushes a short out-of-band note to the flow driver between rounds (a notable observation, a blocking question); the driver sees it as `pending_messages` on its next flow call, so delivery is not immediate. Messages are retry-safe (client-generated `message_id`, deduplicated server-side) and are NOT a substitute for round results. It is deliberately separate from `kcap mcp flows` so an unattended reviewer can never start a nested flow, and it reads its identity from daemon-provided environment (`KCAP_FLOW_AGENT_ID`); run manually it just exits with an explanation. It's not necessarily the only server a reviewer gets, though: the flow definition's `mcp:` allowlist can additionally grant kcap-owned context servers (e.g. `kcap-sessions`), resolved against the same built-in registry — unknown names are skipped and any flow-starting server is always stripped regardless of listing, so a reviewer still can't start a nested flow.

Cursor reviewers run only in daemon-owned worktrees and launch with Cursor's native `--force --approve-mcps --trust` controls so command, MCP-server, and workspace-trust prompts are suppressed at the source. kcap does not auto-approve or route a fallback interaction to a human: any permission, elicitation, or unknown ACP interaction frame violates the zero-prompt contract and immediately reaps the reviewer. Copilot reviewers require an authenticated Copilot CLI with access to the requested model; the daemon preloads this MCP configuration and clamps Copilot's available tools to the validated flow allowlist. That clamp is exclusive, so an owned-worktree Copilot reviewer has no ambient file or shell tool at all. A **borrowed-context** Copilot reviewer is granted read/search tools (`view`, `grep`, `glob`) so it can read the snapshot, and is confined by an OS sandbox instead of by the clamp — see below. There is nothing to register or configure.

#### Borrowed-context Copilot reviews

A Copilot reviewer can read your working tree — uncommitted and untracked changes included — from a daemon-owned snapshot. Because that requires giving it real read tools, the containment boundary moves below the vendor into an OS sandbox, and the capability is only advertised where all three of these hold:

| Requirement | Why |
|---|---|
| **macOS on ARM64** | The only platform whose Copilot tool surface has been measured. Elsewhere the capability is not advertised. |
| **`sandbox-exec` present** | The reviewer process is confined to the snapshot. A host that cannot enforce that gets no borrowed review rather than an unconfined one. |
| **A brokered token** in the daemon's environment — `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, or `GITHUB_TOKEN` | The sandbox deliberately does **not** grant your login keychain, so the reviewer authenticates from a token the daemon passes through. `kcap` never reads, stores, or prompts for one. |

The token must be in the environment of the **daemon process itself** — not merely in the shell you start a review from. For a daemon you run yourself:

```bash
export GH_TOKEN="$(gh auth token)"   # or COPILOT_GITHUB_TOKEN / GITHUB_TOKEN
kcap daemon start -d
```

For a **supervised** daemon (`kcap daemon service install`), set `KCAP_COPILOT_TOKEN_CMD` instead — a command that *prints* a token:

```bash
export KCAP_COPILOT_TOKEN_CMD="gh auth token"
kcap daemon service install
```

The unit file stores the **command**, never a token, and the daemon runs it to obtain a fresh credential each time it launches a borrowed reviewer. That matters twice over: a service unit is a file on disk, so a token in it would be a token at rest; and resolving per launch means a rotated or revoked credential takes effect immediately instead of when the daemon next restarts.

Deliberately *not* supported: putting `GH_TOKEN` itself in the unit. `install` carries over only `PATH`, `KCAP_PROFILE`, `KCAP_URL`, `KCAP_CONFIG_DIR`, `KCAP_CLAUDE_PATH`, `KCAP_CODEX_PATH` and `KCAP_COPILOT_TOKEN_CMD` from your shell — credentials are excluded by design. (`install` also generates a `KCAP_DAEMON_SUPERVISED` marker of its own.) Unit files are written owner-only (`0600`).

The command runs under your shell, so anything that prints a token works — `gh auth token`, a `security find-generic-password` lookup, a secret-manager fetch. It runs **only when a borrowed review actually needs a token**, never at daemon startup: the daemon does not mint a credential nobody asked for. It is bounded at 10s and to one token-sized line, and its output is never logged, so a command that prints a secret on failure cannot leak it.

A daemon with a command *configured* advertises borrowed review, so a command that is broken fails at launch with the coded `borrowed_review_auth_unavailable` — the same honest rejection an unset variable produces. Verifying it up front would mean running it at every startup, which is the trade this deliberately does not make.

With any of the three requirements missing, a Copilot borrowed request is refused up front with `vendor_containment_unreadable` naming the daemon and the remedies, and `mode="context-only"` still works. Nothing silently falls back to reviewing a stale committed base.

The sandbox also gives the reviewer a **fresh, per-launch `HOME` and `TMPDIR`**, so it never sees your prior Copilot sessions, command history, or caches, and its own state is discarded when the review ends.

### Memory MCP server (for agents)

```bash
kcap mcp memory
```

Stdio MCP server that lets coding agents search, save, and update durable "memories" — short, reusable notes (preferences, feedback, project facts, references) scoped to you, your team, or the whole org, so lessons learned in one session are available in the next. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it in `~/.codex/config.toml` (alongside `kcap-sessions` / `kcap-review`), so there's nothing extra to do; enabling the plugin through Codex's native `codex plugin add` also provides it via the `.codex-mcp.json` descriptor. **Cursor:** `kcap setup` / `kcap plugin install --cursor` register it in `~/.cursor/mcp.json` (alongside the other three kcap servers); opt out with `--skip-cursor-mcp`. **GitHub Copilot CLI:** `kcap setup` / `kcap plugin install --copilot` register it in `~/.copilot/mcp-config.json` (alongside the other three kcap servers); opt out with `--skip-copilot-mcp`. **Gemini CLI:** `kcap setup` / `kcap plugin install --gemini` register it in the shared `~/.gemini/settings.json` (alongside the other three kcap servers); opt out with `--skip-gemini-mcp`.

It provides six tools:

- **`search_memories`** — hybrid semantic + keyword search over memories visible to you (your own, your teams', and org-wide). Agents are told to call this before saving a new memory.
- **`get_memory`** — fetch a memory's full content by id or slug.
- **`save_memory`** — save a new memory with an `audience` (`user`, `team`, or `org`), `slug`, `description`, `content`, and `kind`. Scoped to the current repo by default; pass `global: true` to save it repo-independent, or `machine_specific: true` (user audience only) to tag it to this machine.
- **`update_memory`** — update an existing memory's description, content, and/or kind.
- **`rescope_memory`** — change a memory's audience, e.g. promote a personal memory to the team or org.
- **`archive_memory`** — soft-delete a memory.

The server is repo- and machine-aware: it resolves the current working directory to a repo hash and the local persisted machine id at startup, and uses both to scope `save_memory` and to bias `search_memories` / `get_memory` results.

At SessionStart (Claude Code), `kcap` also injects a compact **index** of the memories visible for the current repo/machine into the session context, so the agent starts each session aware of what's saved without a search — see [SessionStart team-memory index](#what-it-records) above. Opt out with `kcap config set disable_memory_index true`.

### Work items MCP server (for agents)

```bash
kcap mcp workitems
```

Stdio MCP server that lets coding agents correlate the current session to the SDLC work item (issue/PR) it belongs to, or list what it's already correlated to. **Claude Code:** auto-registered via the plugin's `.mcp.json`; it isn't offered for Cursor or Codex.

It provides two tools:

- **`declare_work_item`** — attach the current session (and its continuation chain) to a work item. Pass exactly one of `issue_key` (e.g. `"AI-1234"`), `pr_number`, `work_item_id`, or `new_title` (creates a brand-new work item).
- **`get_session_work_items`** — list the work items the current session is attached to.

Both tools default `session_id` to the current kcap-hooked session (`KCAP_SESSION_ID`) when omitted. This is the manual-declare path alongside the server's own mechanical and LLM-assisted correlation — use it when an agent already knows which issue or PR a session belongs to.

### Analytics MCP server (for agents)

```bash
kcap mcp analytics
```

Stdio MCP server that lets coding agents answer analytics questions about the org's recorded AI coding sessions — spend, token/tool/model usage, session outcomes, commits, PRs, evals — by writing **governed read-only SQL** against the server's curated analytics views. Every statement is validated server-side (allowlisted views/columns, SELECT-only, row-capped, repo-scope enforced), so the agent gets the same governed data surface as the web UI's Analytics tab. It's repo-aware: it resolves its scope from the process working directory, so it rides the same registration path as `kcap-sessions` and is offered to every harness. **Claude Code:** auto-registered via the plugin's `.mcp.json`. **Codex CLI:** `kcap setup` / `kcap plugin install --codex` register it in `~/.codex/config.toml`; enabling the plugin through Codex's native `codex plugin add` also provides it via the `.codex-mcp.json` descriptor. **Cursor:** in `~/.cursor/mcp.json` (opt out with `--skip-cursor-mcp`). **GitHub Copilot CLI:** in `~/.copilot/mcp-config.json` (`--skip-copilot-mcp`). **Gemini CLI:** in the shared `~/.gemini/settings.json`, marked `"trust": true` (`--skip-gemini-mcp`). **SST OpenCode:** in `~/.config/opencode/opencode.json` (`--skip-opencode-mcp`). **Google Antigravity:** in `~/.gemini/config/mcp_config.json` (`--skip-antigravity-mcp`). **AWS Kiro CLI:** in `~/.kiro/settings/mcp.json` (`--skip-kiro-mcp`).

It provides two tools:

- **`get_analytics_schema`** — the governed schema document: the queryable views and columns, a terminology glossary, SQL rules, and worked examples. Agents are told to call this once before writing SQL.
- **`query_analytics`** — run one governed Postgres SELECT. Defaults to the current repository (resolved from the working directory); pass `scope: "global"` for org-wide questions, and `max_rows` to adjust the row cap. A rejected query returns the validator's reason so the agent can fix the SQL and retry.

Requires `kcap login` and a kcap-server new enough to expose the `/api/analytics` endpoints (older servers return a clear "upgrade kcap-server" message).

### Curate guidelines

Sync the repo's promoted curation guidelines into its `CLAUDE.md` and/or `AGENTS.md` via a managed block. The server tracks which guidelines have been promoted for the current repo; `curate apply` fetches them and writes (or updates) a `<!-- kcap:curated:start -->…<!-- kcap:curated:end -->` block in the relevant files. Content outside the markers is never touched.

```bash
kcap curate apply             # preview changes and confirm interactively
kcap curate apply --dry-run   # print what would change without writing anything
kcap curate apply --yes       # apply without prompting (CI / scripted)
kcap curate apply -y          # shorthand for --yes
```


### Loading historical sessions

Backfill older sessions from every detected coding agent in a single run. All seven agents ship per-session `.jsonl` transcripts (`~/.claude/projects/`, `~/.codex/sessions/`, `~/.cursor/projects/<sanitized-workspace>/agent-transcripts/`, `~/.copilot/session-state/`, `~/.gemini/tmp/<project>/chats/`, `~/.kiro/sessions/cli/`, `~/.pi/agent/sessions/`). They're discovered automatically and the command requires an explicit scope so personal/private repos aren't uploaded by accident:

```bash
kcap import --all                            # every discovered session from every agent
kcap import --org EventStore                 # sessions whose git-remote owner is EventStore
kcap import --org                            # pick an owner from discovered repos, then remember it
kcap import --repo owner/repo                # one specific repo
kcap import --repo .                         # the repo at the current cwd (must be a git repo with an origin remote)
```

Run `kcap import` with no scope on an interactive terminal to get a picker. Each run shows a confirmation summary (scope, matched count, repo samples, visibility) before uploading anything.

`--org` filters by the **git-remote owner** (GitHub org/user) detected on each session — not by your profile name. This makes it independent of how you signed in: under WorkOS the active profile is named after the tenant slug (which is not a GitHub org), so the owner to scope on is taken from the flag value or chosen from your discovered repos instead. Pass it explicitly as `--org <owner>`, or run a bare `kcap import --org` once on an interactive terminal to pick an owner from your discovered repos — the choice is saved to the active profile (`import_org`) and reused by later bare `--org` runs. Non-interactive runs (CI) must pass `--org <owner>` (or have a remembered org).

By default every available agent is imported. Pass one or more vendor filters to restrict the run:

```bash
kcap import --claude --org                   # only Claude transcripts
kcap import --codex --org                    # only Codex rollouts
kcap import --cursor --all                   # only Cursor — every discovered transcript
kcap import --cursor --cwd /path/to/proj     # only Cursor sessions whose workspace folder matches
kcap import --copilot --all                  # only Copilot — every discovered transcript
kcap import --gemini --all                   # only Gemini — every discovered transcript
kcap import --kiro --all                     # only Kiro — every session log under ~/.kiro/sessions/cli
kcap import --pi --all                       # only Pi — every discovered session
kcap import --opencode --all                 # only OpenCode — every session in opencode.db
```

Cursor historical import walks every JSONL transcript under `~/.cursor/projects/*/agent-transcripts/*/*.jsonl` and posts each line through the same `POST /hooks/transcript` route the live hook path uses, so live and historical ingest converge on one canonical event stream. The walker resolves each session's working directory by matching its sanitized workspace name against `~/Library/Application Support/Cursor/User/workspaceStorage/*/workspace.json` (on Linux: `~/.config/Cursor/User/...`); sessions whose workspace can't be resolved are still imported, just without `cwd` and git owner/repo enrichment.

Kiro historical import reads each session's append-only log at `~/.kiro/sessions/cli/{id}.jsonl` (plus the sibling `{id}.json` for cwd / model / title) and posts the lines through `POST /hooks/transcript` — the same lines the live watcher tails, so live and historical ingest converge. Set `KIRO_HOME` to point at a non-default location. Kiro persists no token counts, so imported Kiro sessions show no token usage (by design). Re-imports are idempotent — event ids are deterministic over `(session id, message/tool id, kind)`.

OpenCode historical import reads its SQLite database (`~/.local/share/opencode/opencode.db`, honouring `XDG_DATA_HOME`) and reconstructs the same `{info,parts}` lines the live plugin streams — so live and historical ingest converge on one canonical event stream (`vendor=opencode`). Unlike live capture, which only nests direct children, historical import walks every transitive descendant session (children, grandchildren, and so on, up to a depth of 8) and imports each one as a direct subagent of the top-level root (`/hooks/subagent-*`) — flattened because a session's stream key can't express deeper nesting. A descendant beyond the depth cap is never silently dropped: import prints a `[kcap] opencode: root <id> descendants_omitted=N (depth cap 8 exceeded)` diagnostic to stderr, appending `(lower bound — counting ceiling hit)` in the rare case where counting the omitted subtree itself hit an internal safety cap before finishing — the count is then a lower bound, not exact. Because the server exposes no completeness signal, kcap records each fully-imported session in a local ledger (`~/.cache/kcap/opencode-imported.json`, keyed by server) to skip it on re-run — the ledger key is a content fingerprint over the whole descendant tree (including omitted ids), so a newly-reachable descendant invalidates a stale entry; a session interrupted mid-import is repaired on the next run. Re-imports are idempotent — canonical event ids derive from OpenCode's stable `prt_` part ids.

Claude (`CLAUDE_CONFIG_DIR`), Codex (`CODEX_HOME`), Gemini (`GEMINI_CLI_HOME`, which names the parent of `.gemini`), OpenCode (`OPENCODE_CONFIG_DIR`), and Pi (`PI_CODING_AGENT_DIR`, which names the `~/.pi/agent` leaf) historical/live paths follow each agent's own config-relocation environment variable when it is set, so a relocated config is discovered automatically.

Additional flags:

```bash
kcap import --org EventStore --yes           # skip the confirmation prompt
kcap import --org EventStore --private        # mark every imported session as Only Visible to You
kcap import --org EventStore --since 2026-01-01  # only sessions on or after this date
kcap import --org EventStore --cwd /path/to/project  # filter by working directory
kcap import --org EventStore --session abc123    # single session
```

Non-interactive runs (no TTY, e.g. CI) must pass both a scope flag and `--yes`. The command is idempotent and resumable — re-running with the same scope only uploads what's missing or incomplete. A server-side tracker deduplicates events on `(stream, eventId)` so previously-imported turns don't get re-appended.

`--private` also covers sessions the run only *revisits*. Agents that record subagents (Cursor, Antigravity, Gemini) can attach a previously-missed subagent to a session that was already fully imported, so for those a `--private` re-run marks every session it touched private — not just the ones with brand-new top-level content. That is deliberate: a session's visibility can't be left to whether a re-run happened to find new content, and re-running with `--private` is the supported way to privatize sessions an earlier non-private import made visible.

After discovery, the import surfaces a one-shot report of any transcript working directories that no longer exist on disk. Sessions whose cwd was an ephemeral worktree (e.g. `~/dev/my-repo/.claude/worktrees/<slug>` or `~/dev/my-repo/.capacitor/worktrees/<slug>`) are transparently attributed to their parent project when that project still exists, so deleted-worktree paths drop out of the missing-cwds list. kcap's own background helper runs (the headless `claude -p` calls behind title generation and "what's done" summaries) record their transcripts in a throwaway temp directory that is removed the moment the run ends; these are never imported, and they're also excluded from the missing-cwds report so its dead temp paths don't drown out real ones. What remains is typically local repo dirs that have been renamed — those won't match an `--org` / `--repo` scope until you tell kcap how their old paths map to the new ones. See [Renamed repo directories (`kcap remap`)](#renamed-repo-directories-kcap-remap) below for the fix.

### Daemon

The daemon connects to the Capacitor server and runs Claude Code, Codex, or Cursor agents in isolated git worktrees, controlled from the dashboard. Hosted Claude and Codex agents run on macOS, Linux, **and Windows**; hosted Cursor (`cursor` vendor) is macOS and Linux only — choose the vendor from the dashboard's launch dialog. At startup the daemon probes `daemon.claude_path`, `daemon.codex_path`, and the Cursor CLI (`cursor-agent`, overridable via `KCAP_CURSOR_PATH` — see [Daemon config settings](#daemon-config-settings)) and advertises only the vendors it can actually spawn, so the launch dialog hides whichever agent isn't installed on the selected daemon.

> **Snapshotting a workspace that isn't a git repo:** when an agent targets a directory that is not a git
> repository with commits, the daemon takes a *standalone snapshot* — it copies the directory rather than
> creating a git worktree. Symlinks are recreated as links (never followed) and anything pointing outside
> the workspace is skipped, so the snapshot cannot pull in credentials or other out-of-tree content.
> **That guarantee assumes nothing else writes to the workspace while the agent is starting.** The check
> that classifies an entry and the read that copies it are not a single atomic operation, so a separate
> account or process that can swap a file for a symlink in between can still get the target's contents
> copied in. In practice: don't point a hosted agent at a directory that any account or process other than
> the daemon's own can write to during a launch. A normal single-user checkout is fine; a shared or
> world-writable directory is not.

> **Windows and hosted Codex:** needs Windows 10 1809 (build 17763) or newer — Windows 11 recommended — because that's the floor for Codex's own Windows sandbox. Older builds don't advertise the `codex` vendor at all. The sandbox *implementation* (`elevated`, which needs one-time admin-approved `winget` setup, vs `unelevated`) is whatever your `~/.codex/config.toml` `[windows] sandbox` says; the daemon inherits it rather than overriding it. Codex is found on `PATH` whether installed via `winget` (`codex.exe`) or npm (`codex.cmd`).

```bash
kcap daemon start                   # start in foreground (defaults --name to your OS username)
kcap daemon start -d                # start in background (daemonize)
kcap daemon start --name laptop -d  # run multiple daemons on the same machine by giving each a unique name
kcap daemon status                  # list all running daemons
kcap daemon status --name laptop    # show status of a specific daemon (incl. its running version)
kcap daemon stop --name laptop      # stop just that one
kcap daemon stop --yes              # stop all running daemons unattended (otherwise prompts on multi)
kcap daemon restart --name laptop              # restart now if idle; refuses while agents/evals run
kcap daemon restart --name laptop --when-idle  # queue the restart for the next idle moment
kcap daemon restart --name laptop --force      # restart now even if busy (tears down running agents)
kcap daemon doctor                  # diagnose lock-file state for every daemon name
kcap daemon doctor --clean          # also remove stale lock/pid files (held entries are never touched)
```

`agent` was this group's name before it was renamed to `daemon`, and it now belongs to a different group — [`kcap agent`](#local-agents-kcap-agent) runs coding agents. The two still share `start` and `stop`, so check which one you mean: `kcap daemon start` starts the daemon, `kcap agent start <vendor>` starts a coding agent. Daemon-only verbs typed against the wrong group (`kcap agent status`, `restart`, `logs`, `doctor`, `service`) answer with a pointer back here.

`KCAP_DAEMON_NAME` overrides the active profile's daemon name (superseded by an explicit `--name` flag).

For a running daemon, `kcap daemon status` reports the **version the daemon process is actually running** (read from a marker the daemon writes at startup, not the CLI's own version) — so you can confirm whether a self-update has taken effect:

```
Daemon 'laptop': running (PID 12345)
  version: 0.8.12
```

**Updating:** after `kcap update`, a running daemon on macOS/Linux detects the new binary and restarts itself once it's **idle** (no running hosted agents and no in-flight eval) — service-managed daemons exit so the supervisor relaunches the new binary; background (`-d`) daemons re-spawn themselves. `kcap daemon status` shows the running version (above) plus any pending restart, so you can tell the update apart from an as-yet-unrestarted daemon; `kcap daemon restart --force` applies it now. On **Windows**, stop the daemon (`kcap daemon stop` / `kcap daemon service stop`) before `kcap update` — a running daemon locks its binary, so the update can't replace it (the launcher detects this and aborts with instructions).

#### Run it as a service (auto-restart)

`kcap daemon start -d` runs only until the process dies — a crash, or an OS memory-pressure kill (macOS **jetsam** / Linux **OOM killer**) that sends an uncatchable `SIGKILL`. To have the daemon auto-restart and start at login, install it as a **per-user** OS service:

```bash
kcap daemon service install                # launchd (macOS) / systemd --user (Linux) / Scheduled Task (Windows)
kcap daemon service install --name laptop  # a service per daemon name
kcap daemon service status                 # installed / running state
kcap daemon service stop                   # stop the running service (stays installed)
kcap daemon service start                  # start it again
kcap daemon service uninstall              # stop and remove the service
```

`install` pins the active profile via `KCAP_PROFILE` and captures your current `PATH` into the unit, so the supervised daemon resolves the same server URL, `claude`/`codex` binaries, and profile settings it would from your shell. Pass `--profile P` to pin a different profile, `--max-agents N` to bake an override, or `--no-start` to register without starting. The service restarts the daemon on crash/`SIGKILL` but **not** on a clean stop.

What it carries over from your shell is a fixed allowlist — `PATH`, `KCAP_PROFILE`, `KCAP_URL`, `KCAP_CONFIG_DIR`, `KCAP_CLAUDE_PATH`, `KCAP_CODEX_PATH`, `KCAP_COPILOT_TOKEN_CMD`, plus the Google/Gemini configuration below — and **nothing else from your environment reaches the service**, credentials included, because the unit file lands on disk. (`install` additionally writes a generated `KCAP_DAEMON_SUPERVISED` marker, so the unit holds one key that did not come from your shell.) Unit files are written owner-only (`0600`). `KCAP_COPILOT_TOKEN_CMD` is on the list precisely because it is a *command* rather than a secret — see [borrowed-context Copilot review](#borrowed-context-copilot-reviews).

#### Hosted Gemini needs its project in the *daemon's* environment

Hosted Gemini agents use whatever credential you logged `gemini` in with — there is no API key to configure. But a Gemini login that is scoped to a Google Cloud project needs that project **where the daemon can see it**, and a supervised daemon sees none of your shell: launchd passes no shell environment, and a non-interactive shell never reads your profile. So `export GOOGLE_CLOUD_PROJECT=…` in `.zshrc` is invisible to it.

Get this wrong and Gemini reports it as a **tier** problem:

```
IneligibleTierError: This client is no longer supported for Gemini Code Assist for individuals.
```

That message names the wrong cause — the same text appears for a missing project id — so if you see it, check your project configuration before believing anything about your subscription.

`install` therefore captures, when set in the installing shell:

| Carried everywhere | Carried on macOS/Linux only | Never carried |
|---|---|---|
| `GOOGLE_CLOUD_PROJECT`, `GOOGLE_CLOUD_PROJECT_ID`, `GOOGLE_CLOUD_LOCATION`, `GOOGLE_GENAI_USE_VERTEXAI`, `GOOGLE_GENAI_USE_GCA` | `GOOGLE_APPLICATION_CREDENTIALS`, `GOOGLE_GEMINI_BASE_URL`, `GOOGLE_VERTEX_BASE_URL` | `GOOGLE_API_KEY`, `GOOGLE_CREDENTIALS` |

The middle column is secret-*capable* — a credential path says where your credential lives, and a base URL can carry a token in userinfo or a query string. On macOS and Linux that is bounded by a guarantee kcap enforces: unit files are written `0600`, the mode is re-checked on the open handle, and `install` refuses a group- or world-writable directory. On Windows the wrapper inherits your user profile's ACL, which kcap neither sets nor verifies, so those three are excluded there — the same reason `GH_TOKEN` is never carried. If you need Vertex-with-ADC on a Windows daemon, set it in the service's own environment yourself.

⚠️ **Capture happens at install time.** Exporting the project *after* `kcap daemon service install` leaves a unit without it. Set it first, or re-run `install` afterwards — and restart the daemon.

> Verified against `oauth-personal` (Gemini Code Assist) with a project. The `gemini-api-key`, `vertex-ai` and `gateway` auth methods are not verified; their variables are carried so they *can* work, which is not the same as knowing they do.

Because the service auto-restarts, stop a service-managed daemon with `kcap daemon service stop` (or `uninstall`) rather than `kcap daemon stop` — a raw stop would be relaunched immediately. `kcap daemon status` and `kcap daemon doctor` both report installed services.

Each daemon process holds an exclusive `flock` on `~/.config/kcap/daemons/<name>.lock` for its entire lifetime. The kernel releases the lock automatically when the daemon exits (including `SIGKILL` or power-off), so leftover lock files on disk are never a blocker — only a live process holding the kernel-level lock can prevent another daemon from acquiring the same name.

Two daemons with **different** `--name` values can run side-by-side. Two daemons under the **same name** on the same machine collide on the flock and the second one exits with code 2. Even if that guard is bypassed somehow, the server rejects the second daemon's `DaemonConnect` with a typed error and the second daemon exits with code 3 — no more silent slot-displacement oscillation.

#### Launch consent (`kcap daemon consent`)

Every server-driven launch — a hosted coding agent, a PR review, or a review-flow participant started from the dashboard — is checked against a daemon-owned consent policy (allow / deny / prompt-the-owner, with per-requester/kind/repo/vendor rules) before it runs; `kcap daemon consent` inspects and edits that policy from the CLI. A denial surfaces back to the server (and the requester) as the coded reason `launch_denied_by_owner`.

```bash
kcap daemon consent show                                        # print default, prompt timeout, and numbered rules
kcap daemon consent set-default deny                             # allow (default) | deny | prompt
kcap daemon consent allow --requester user_123 --kind review     # append a rule (at least one flag required)
kcap daemon consent deny --vendor codex --repo '/path/to/repo/*'   # deny by vendor + a repo glob
kcap daemon consent remove 0                                     # remove the rule at the index `show` printed
kcap daemon consent log -n 50                                    # tail consent-decisions.jsonl (works while stopped)
```

`show`/`set-default`/`allow`/`deny`/`remove` mutate the policy over the daemon's local socket and require the target daemon to be running; `log` reads `consent-decisions.jsonl` straight off disk, so it works even with the daemon stopped. All six take `--name <n>` like the rest of `kcap daemon`.

> **Two different gates, deliberately.** `daemon consent` authorises an individual launch and defaults to
> *allow*; the Gemini reviewer flag below is a **capability** gate that defaults to *off* and asks a
> different question — whether this daemon may run that vendor unattended **at all**. A per-launch deny is
> not a substitute: the reviewer's containment rests on a behaviour of the installed vendor build, so the
> safe default has to be off rather than on-until-denied.

#### Gemini as an unattended review-flow reviewer — off by default, and why

A **hosted** Gemini agent needs nothing beyond the project setting above. Using Gemini as an **unattended
review-flow reviewer** is a separate, deliberately opt-in decision, because of what an unattended review
grants:

```bash
export KCAP_GEMINI_UNATTENDED_REVIEWER=1     # this DAEMON's environment — not a server setting
```

Only an explicit `1`/`true`/`yes`/`on` enables it; anything else, a typo included, leaves it off. Enabling a
reviewer is a consent decision, so a misspelling must not read as consent.

> Earlier docs showed a `GeminiUnattendedReviewerEnabled` key in `~/.config/kcap/config.json`. Nothing read
> it — the flag was reachable only from a test constructor, so the Gemini unattended reviewer could not
> actually be turned on. The environment variable above is the working form.

**Read this before setting it.** An unattended reviewer runs in a daemon-owned worktree with this daemon's
own `HOME`, so repository content that steers the model into using its tools gets **code execution with your
daemon user's full authority**. Concretely, and not bounded to the review:

- **credentials** — the kcap token store and config directory are readable, and a copied token stays valid
  until it expires or is revoked; reaping the reviewer does not touch it;
- **integrity** — writes reach your *other* worktrees, the daemon's own state, the installed `kcap`, and your
  shell profiles, so one review can alter a later review or your own environment;
- **persistence** — a child process or scheduled job can outlive the review;
- **audit** — local session captures and logs are writable by the same authority, so after a compromise the
  local record is not evidence;
- **verdict** — steered content can simply ask the model to report `clean`. A review result is not
  authenticated review output.

This is a property of unattended review generally, not of Gemini specifically — enabling Gemini widens which
vendors can do it rather than introducing it. The one path that does *not* grant this is a sandboxed borrowed
review, which Gemini cannot use yet.

**Enabling this flag is your consent to the above**, which is why it is daemon-local (the person requesting a
review is not necessarily you) and defaults off.

Two further things it does *not* do:

- it does not bypass the **minimum version**. The reviewer's only containment is Gemini's exact-name MCP
  allowlist, which is a behaviour of the installed build, so the daemon records a minimum `gemini` version
  the first time you enable the reviewer. That recorded version is a **minimum, not an exact match**: any
  build at or above it runs, so **a Gemini upgrade needs no action from you**; an older one is refused, with
  a coded error naming both versions. Run `kcap daemon reviewer affirm --vendor gemini` to move the minimum
  to whatever is installed now — which is how you exclude a build you have found to be broken, and, if you
  run it while an older build is installed, how you deliberately lower the bar again.

  (Two earlier models are worth knowing about if you hit old docs. A maintainer-curated *certified version
  set* took the reviewer offline on every Gemini release until a new kcap shipped — a build one patch ahead
  of the certified one made the feature unreachable. Requiring an exact match to a version you affirmed
  removed the kcap-release coupling but kept the treadmill, just moved onto you. The minimum keeps the
  fail-closed direction for downgrades while letting routine upgrades through untouched.)
- it does not make Gemini a default reviewer. It is only ever reached by an explicit `vendor: "gemini"`.

#### Unattended Kiro reviews

```bash
export KCAP_KIRO_UNATTENDED_REVIEWER=1       # this DAEMON's environment — not a server setting
```

**Everything in the Gemini warning above applies**, with one difference in each direction.

*Tighter:* a Kiro reviewer runs with a **scoped** tool set — `fs_read`, `thinking`, and the tools of the MCP
servers the launch itself injects. `fs_write` and `execute_bash` are not trusted, and a permission request that
does not name exactly one of the injected tools ends the round rather than being auto-approved. Gemini's `yolo` approval mode
excludes nothing, so on tool surface Kiro is the narrower of the two.

> Kiro intermittently raises a permission prompt for a tool that *is* in its own trust list (an upstream
> trust-flag leak). The reviewer therefore approves prompts naming the tools this launch injected, and reaps
> on anything else — rather than reaping on every prompt, which would kill an unpredictable share of clean
> rounds on the call that delivers the result.

*Not tighter:* a trusted `fs_read` is **not path-scoped** — measured. It reads anything the daemon user can,
so the credential, integrity-of-*reads*, and verdict bullets above hold in full. Support is therefore limited
to a daemon whose operator and whose review requesters are in **one trust domain**.

A review launch also runs with a daemon-owned, empty `KIRO_HOME`, so your global
`~/.kiro/settings/mcp.json` servers — `kcap-flows` among them — do not reach the reviewer. Your own
interactive Kiro sessions are unaffected, and the file is never modified.

**Minimum version.** That suppression depends on the installed build honouring `KIRO_HOME`, so the daemon
records a minimum `kiro-cli` version the first time you enable the reviewer. It is a **minimum, not an exact
match**: any build at or above it runs, so **a `kiro-cli` upgrade needs no action from you**; a build older
than the recorded minimum is refused.

```bash
kcap daemon reviewer affirm --vendor kiro
```

Run that to move the minimum to whatever is installed now — which is how you exclude a build you have found
to be broken, and, if you run it while an older build is installed, how you deliberately lower the bar
again. The command records the version and nothing else — it does not enable the reviewer. No kcap release
is ever needed; Gemini and Antigravity use the same model and the same command (`--vendor gemini`,
`--vendor antigravity`).

POSIX only: the isolated home holds the reviewer's own transcript, and therefore the review context, and
cannot be created owner-only on Windows.

#### Unattended Antigravity reviews

```bash
export KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=1   # this DAEMON's environment — not a server setting
```

The prerequisite is the Antigravity **CLI** — the `agy` binary on `PATH` (or `KCAP_ANTIGRAVITY_PATH`
pointing at it). The Antigravity **IDE** alone is not enough: it ships no `agy`, and a daemon with only the
IDE installed never offers the vendor at all.

**Everything in the Gemini warning above applies.** Reviews run in a daemon-owned worktree under a
per-launch, owner-only `HOME` (removed when the reviewer is disposed), so your own `~/.gemini` config and
the kcap capture plugin inside it do not reach the reviewer, and your interactive Antigravity sessions are
unaffected. POSIX only, for the same reason as Kiro: that home holds the reviewer's own transcript, and
therefore the review context, and cannot be created owner-only on Windows.

**Give the daemon durable credentials.** An unattended reviewer's stdin is closed, so it cannot complete an
interactive login — an unauthenticated `agy` fails the launch with a coded
`antigravity_reviewer_auth_unavailable` rather than hanging. Application Default Credentials are the
supported setup:

```bash
gcloud auth application-default login
export GOOGLE_CLOUD_PROJECT=<your-project>
export AGY_ADC_AUTH=1                           # selects ADC; without it agy still demands an OAuth login
```

**Minimum version.** Containment here depends on the installed build honouring `HOME` and reading no other
global config source, so the daemon records a minimum `agy` version the first time you enable the reviewer.
It is a **minimum, not an exact match**: any build at or above it runs, so **an `agy` upgrade needs no
action from you** — which matters more here than for the other reviewers, since `agy` updates itself
(observed going 1.1.8 → 1.1.10 mid-session). A build older than the recorded minimum, or one whose
`agy --version` cannot be read, is withheld with a coded reason naming both versions.

```bash
kcap daemon reviewer affirm --vendor antigravity
```

Same command and same model as Kiro and Gemini: run it to move the minimum to whatever is installed now —
which is how you exclude a build you have found to be broken, and, if you run it while an older build is
installed, how you deliberately lower the bar again. It records the version and nothing else; it does not
enable the reviewer. No kcap release is ever needed.

Like Gemini and Kiro, this never makes Antigravity a default reviewer — it is only ever reached by an
explicit `vendor: "antigravity"`. Borrowed (in-place) review is not offered; a borrowed request falls back
to a daemon-owned worktree.

#### If your daemon runs as a service

All three consent flags above are read from the **daemon's own environment**, and a supervised daemon (`kcap daemon
service install` — launchd, systemd, or a Windows scheduled task) inherits nothing from the shell you
installed it from. Exporting a flag in your shell therefore does nothing for a service-installed daemon until
the unit itself carries it, so export it *first* and then reinstall:

```bash
export KCAP_GEMINI_UNATTENDED_REVIEWER=1
kcap daemon service install --name "$(whoami)"    # captures the flag into the unit
```

The Antigravity ADC variables (`GOOGLE_CLOUD_PROJECT`, `AGY_ADC_AUTH`, `GOOGLE_APPLICATION_CREDENTIALS`)
are captured by the same install, so a daemon installed *before* this shipped must be reinstalled from an
interactive shell to pick them up. `KCAP_ANTIGRAVITY_PATH` is **not** captured — like the other vendor path
overrides, set it where the unit can see it if you need a non-default value.

Install prints a `Consent:` line naming each reviewer flag it captured. That freeze is the point to notice:
the unit outlives the shell, so the reviewer stays enabled for that service until you reinstall without the
variable set — unsetting it in your shell later changes nothing.

If a reviewer is still not offered, the daemon says why in its own log at startup — one line per vendor that
is installed and unattended-capable but withheld, carrying the same text the launch path would have thrown
(consent not set, version unresolved, no minimum recorded, version below the minimum). It logs at `Information`, not
`Warning`: a daemon with Gemini, Kiro or the Antigravity CLI installed purely for interactive use and no
opt-in is in a perfectly normal state, so this is a line to find when you go looking, not an alert.

```bash
grep -i "is NOT offering it" ~/.config/kcap/daemon-*.log
```

Without that line the only symptom is the server's `reviewer_vendor_unavailable`, which reports that no
connected daemon advertises the vendor and cannot say why.

#### Hosted Codex agents

Hosted Codex agents require the Codex hook surface — if you said yes during `kcap setup`, you already have it. Otherwise install it manually:

Codex CLI 0.81+ exports `CODEX_THREAD_ID`; kcap reads it the same way it reads `KCAP_SESSION_ID` for Claude sessions — no manual session ID needed for any of the Codex skills (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`).

```bash
kcap plugin install --codex                          # user scope (~/.codex/hooks.json + ~/.agents/skills/)
kcap plugin install --codex --project                # project scope (<repo>/.codex/hooks.json), skills still user-wide
kcap plugin install --skills                         # skills only (~/.agents/skills/), no Codex hooks
kcap plugin install --skills --if-installed          # refresh only if skills were previously installed (used by npm postinstall, harmless to call by hand)
kcap plugin install --codex --if-installed           # refresh Codex hooks only if previously installed (used by npm postinstall)
kcap plugin install --if-installed                   # refresh Claude plugin registration only if previously installed (used by npm postinstall)
```

Installing with `--codex` (or `--skills`) writes seven skills under `~/.agents/skills/`:

| Skill | Wraps | Purpose |
|---|---|---|
| `kcap-recap` | `kcap recap` | Session summary / continuation chain / repo history |
| `kcap-errors` | `kcap errors` | Tool-call error extraction |
| `kcap-hide` | `kcap hide` | Mark session owner-only |
| `kcap-disable` | `kcap disable` | Stop recording + delete server data |
| `kcap-validate-plan` | `kcap validate-plan` | Verify plan items were completed |
| `kcap-review-flows` | `kcap mcp flows` | Structured iterative spec/code review loops |
| `kcap-guided-tour` | analytics + sessions MCP | Onboarding tour of what Capacitor has recorded |

The first five (`kcap-recap`, `kcap-errors`, `kcap-hide`, `kcap-disable`, `kcap-validate-plan`) auto-resolve the active session from `CODEX_THREAD_ID`; pass `<sessionId>` explicitly to operate on a different session. `kcap-review-flows` works differently — it operates via flow IDs through `kcap mcp flows` rather than session auto-resolution; see [Flows MCP server (for agents)](#flows-mcp-server-for-agents) for details. `kcap-guided-tour` shells out to `kcap whoami` and otherwise reads through the `kcap-analytics` and `kcap-sessions` MCP servers, so it needs those registered (setup does it) rather than a session id.

> **Codex sandbox network access (AI-794).** The skills shell out to `kcap …`, which talks to the Capacitor server — but Codex runs the agent's shell tool in a `workspace-write` sandbox that **blocks network by default**, so the skills fail (or demand escalation) until network access is allowed. Both `kcap setup` (one yes/no prompt after the Codex hooks step) and `kcap plugin install --codex` enable it for you. They write a constrained allowlist to `~/.codex/config.toml` rather than opening the network wholesale:
>
> ```toml
> [sandbox_workspace_write]
> network_access = true          # required — the proxy only enforces; it doesn't grant
>
> [features.network_proxy]
> enabled = true
> domains = { "**.kcap.ai" = "allow" }
> ```
>
> The `**.kcap.ai` wildcard covers every SaaS tenant — current and future — plus `auth.kcap.ai`, so switching profiles and adding tenants just work with no per-tenant edits. **Self-hosted** servers are added as exact-host entries, derived from every configured profile's `server_url` and refreshed on each `kcap setup`. Existing config is respected: if you already run a `network_proxy` policy, kcap's hosts are merged into your `domains` (yours preserved); if you've already opened the network (`network_access = true` with no proxy), nothing changes. Opt out with `--skip-codex-network-access` on either command (and the npm-postinstall `--if-installed` refresh never touches `config.toml`). A localhost dev server additionally needs `allow_local_binding = true`, which kcap does not set. Uninstall leaves these keys in place — they're your security posture, not kcap state.

**Sandbox and approval posture.** By default the daemon starts Codex with `--sandbox workspace-write` and `--ask-for-approval on-request`. This lets Codex edit files in the agent's worktree but escalates sensitive operations (e.g. network calls, shell commands outside the worktree) through the daemon's permission bridge to the dashboard.

The daemon also **accepts** a caller-selected posture for an **interactive** hosted launch (one that runs in a daemon-created worktree). Nothing selects one yet: the dashboard selector ships with a later Capacitor **server** release, and until then every launch uses the defaults above. The selectable values are:

| Flag | Selectable values |
|---|---|
| `--sandbox` | `read-only`, `workspace-write` (default), `danger-full-access` |
| `--ask-for-approval` | `untrusted`, `on-request` (default), `never` |

Two cases are **not** selectable, because their values are containment guarantees rather than preferences:

- A **borrowed** launch runs in your own real checkout, so it is always `read-only` — anything else would let a hosted agent modify the repo you are working in.
- A **review-flow reviewer** runs unattended, so it is always `--ask-for-approval never` — any prompting mode would wedge the round waiting for an approval no human is there to give.

PR-review launches also keep a fixed posture. A launch that supplies a posture for any of those three cases is **rejected** with a coded error (`codex_posture_not_overridable`) rather than having it silently ignored or silently applied; an unknown value, a half-specified pair, or the upstream-deprecated `on-failure` is rejected as `codex_posture_invalid`.

Two selections weaken the protections you get by default, in different ways:

- `--ask-for-approval never` **disables the permission bridge for that agent** — it never asks the dashboard before acting, so nothing pauses for your approval.
- `--sandbox danger-full-access` **removes the filesystem sandbox** — the agent can read and write outside its worktree. (Approvals are unaffected: with `on-request` it still escalates through the bridge.)

The daemon logs a warning naming the agent whenever an interactive launch resolves to either.

The daemon advertises posture support to the server at connect. A server that offers the selector refuses it against a daemon too old to advertise support, rather than sending a posture that would be quietly dropped — so update kcap on the daemon host if the dashboard says the selector is unavailable. This is all separate from local `kcap agent start codex -- …`, where you pass Codex flags yourself and they are not restricted (see [Local agents](#local-agents-kcap-agent)).

> **Upgrading from an earlier version of kcap?** Run `kcap update` (or `npm install -g @kurrent/kcap`) — the npm postinstall hook, and `kcap update` itself, refresh all user-scope kcap installations, so you always pick up the current CLI version's skills (`~/.agents/skills/kcap-*`), Codex hook commands (`~/.codex/hooks.json`), and Claude plugin registration (`~/.claude/settings.json`). Each refresh is gated on a marker file written by your previous setup — fresh systems that never opted in are left untouched. Project-scope installs (`--project`) are not auto-refreshed; re-run `kcap plugin install [--codex] --project` after upgrading if you want the latest config for a specific repo.
>
> npm 11+ blocks install scripts by default. Add `allow-scripts[]=@kurrent/kcap` to your `~/.npmrc` (or pass `--allow-scripts=@kurrent/kcap` on the install command line) to opt in to the auto-refresh; otherwise re-run the relevant `kcap plugin install ... --if-installed` commands manually after each upgrade. (`npm approve-scripts` does not work for global installs — that's a known npm UX bug.)
>
> **Restart your coding agent after upgrading, too — not just for hooks.** A harness reads each MCP server's tool schema **once**, when it connects, and caches it for the life of that session. Upgrading kcap replaces the binary on disk but cannot reach into a harness process that has already connected, so a session started before the upgrade keeps offering the **old** tool schema — missing any parameter the new version added. kcap deliberately does not try to hot-reload a cached schema; restarting is the supported recovery.
>
> This matters most for review flows. If `start_review_flow` is missing its `vendor` parameter, the agent driving it cannot name a reviewer, and resolution falls through to the flow definition's authored vendor (if it declares one) or the agent's saved `flows.reviewer_vendor` preference (if it doesn't) — so asking for "a review by Claude" can quietly get you a different reviewer. **The server cannot detect this**: an omitted `vendor` looks exactly like a caller who wanted whatever it resolves to, so the request carries no trace of the name you asked for. (The `client_upgrade_required`, `flow_client_protocol_required` and `flow_client_protocol_unsupported` rejections guard a different problem — *protocol* skew — and do not fire here.) The only guard in this window is the kcap flow skills, which instruct agents never to claim a named reviewer ran when they could not send `vendor`, and to tell you to restart instead.
>
> **Recovery:** upgrade kcap, then end the harness session and start a fresh one — or, in harnesses that expose it, reconnect the `kcap-flows` MCP server. To confirm the new schema is live, check that your client lists the server as connected (`claude mcp list`, `codex mcp get kcap-flows`, `gemini mcp list`, `cursor-agent mcp list`, `opencode mcp list`) and that `start_review_flow` now advertises `vendor`.

PR review is supported for hosted Codex agents as well as Claude — the same `kcap-review` MCP context is injected either way.

#### Cursor IDE hooks

Cursor is detected by the presence of `~/.cursor/` — you don't need the `cursor` shell command on `PATH`. If `kcap setup` found Cursor and you said yes, hooks are already in place. Installing also registers the four kcap MCP servers in `~/.cursor/mcp.json` (non-destructive, idempotent); pass `--skip-cursor-mcp` to opt out. To install or remove later:

```bash
kcap plugin install --cursor                # writes ~/.cursor/hooks.json + registers kcap MCP servers
kcap plugin install --cursor --skip-cursor-mcp  # hooks only, skip ~/.cursor/mcp.json
kcap plugin remove --cursor                 # remove Cursor hooks + kcap MCP servers
```

**Live capture is watcher-backed (AI-1382).** Every Cursor hook (any of the 8, not just
`sessionStart`) spawns — or heals — a per-session `kcap watch --vendor cursor` background watcher
that tails `~/.cursor/projects/<sanitized>/agent-transcripts/<sid>/<sid>.jsonl` and streams new
lines the moment Cursor writes them, instead of relying solely on the next hook's HTTP backfill.
Hooks are kept as belt-and-braces: `subagent-start`/`subagent-stop`/`session-end` POSTs still
fire, a spooled hook is still retried on the next invocation, and a per-hook backfill still runs
as a fallback — the watcher just makes capture continuous instead of hook-triggered. A subagent
gets its own child watcher, tailing its own transcript file, spawned only once its diverted
`subagent-start` is acknowledged by the server (so no subagent transcript line can ever arrive
before its lifecycle is opened) — a spooled (unacknowledged) start defers the spawn until a later
hook's spool drain delivers it. If Cursor is force-quit mid-turn, the watcher's shutdown drain
still recovers the last (possibly newline-less but complete) transcript line before exiting; if a
session goes idle past `KCAP_CURSOR_IDLE_CEILING_MINUTES` (default `60`) the watcher exits WITHOUT
posting `session-end` itself — end-of-session synthesis for Cursor stays owned by the
`sessionEnd` hook or, as a backstop, a server-side lease-gated sweep — and the next hook for that
session (any of the 8, not just `sessionStart`) reactivates a fresh watcher. A runtime rewrite
guard defends against Cursor ever rewriting a transcript in place (expected append-only, but not
hook-guaranteed): a detected rewrite discards the unsent batch and quarantines the session rather
than risk re-sending stale byte ranges. `kcap cursor-verify-appendonly --path <file>` is a hidden
diagnostic (not listed in `--help`) that samples a live transcript file for a bounded duration and
reports whether it stayed append-only — the empirical evidence behind the watcher promotion, not
something you need to run day to day.

#### GitHub Copilot CLI hooks

Copilot CLI is detected via `~/.copilot/` (created on Copilot's first run) or the `copilot` binary on `PATH`. kcap writes its own hooks file — Copilot merges every `*.json` under `~/.copilot/hooks/`, so your other hook files are never touched. Copilot loads hook config at startup: restart any running `copilot` session after installing.

```bash
kcap plugin install --copilot               # writes ~/.copilot/hooks/kcap.json
kcap plugin remove --copilot                # deletes ~/.copilot/hooks/kcap.json
```

Live sessions stream from `~/.copilot/session-state/<session-id>/events.jsonl` (`$COPILOT_HOME` is honoured); historical sessions import via `kcap import --copilot`, which also forwards Copilot's auto-generated session names as titles. Sessions resumed with `copilot --continue` / `--resume` reattach to the same recorded session.

#### Google Gemini CLI hooks

Gemini CLI is detected via `~/.gemini/` (created on Gemini's first run) or the `gemini` binary on `PATH`. Gemini keeps its hooks in the shared `~/.gemini/settings.json`, so kcap **merges** its entries into the `hooks` block and preserves your other settings and any hand-authored hook entries. Gemini loads hook config at startup: restart any running `gemini` session after installing.

```bash
kcap plugin install --gemini                # merges kcap hooks into ~/.gemini/settings.json
kcap plugin remove --gemini                 # removes only kcap's entries
```

Live sessions stream from the chat-recording JSONL Gemini names in each hook's `transcript_path` (`~/.gemini/tmp/<project>/chats/session-*.jsonl`); historical sessions import via `kcap import --gemini`. Sessions resumed with `gemini --resume` reattach to the same recorded session. (Historical import leaves working-directory / repo enrichment empty — Gemini doesn't record the cwd in a machine-readable header; live capture gets it from the hook payload.)

Spawned subagents are captured too: Gemini records each in a nested `chats/<session>/<subId>.jsonl`. Live capture (a child watcher per subagent) discovers direct children only. Historical `kcap import --gemini` goes further, recursively discovering every transitive descendant — a subagent's own subagents, and so on, up to a depth of 8 — under each descendant's own nested dir, and imports them all as direct subagents of the top-level session (flattened, since a session's stream key can't express deeper nesting). A descendant beyond the depth cap is never silently dropped: import prints a `descendants_omitted=N` diagnostic to stderr, appending `(lower bound — counting ceiling hit)` in the rare case where counting the omitted subtree itself hit an internal safety cap before finishing — the count is then a lower bound, not exact.

#### AWS Kiro CLI hooks

AWS Kiro CLI (the rebranded Amazon Q Developer CLI) is detected via `~/.kiro/` or the `kiro` / `kiro-cli` binary on `PATH`. Kiro hooks fire only for the **active** agent — there is no global hook — so to capture every session transparently, `install --kiro` **clones your current default agent** into `~/.kiro/agents/kcap.json` (preserving its tools; a minimal agent would lose tool access), adds kcap's `agentSpawn` hook, and makes it your default agent (`chat.defaultAgent` in `~/.kiro/settings/cli.json`). This needs `kiro-cli` on `PATH` to perform the clone. Restart any running `kiro` session after installing. `remove --kiro` restores your previous default agent and deletes `kcap.json`.

`install --kiro` (and `kcap setup`) also **registers the four kcap MCP servers** in Kiro's user-level `~/.kiro/settings/mcp.json` (a plain `mcpServers` merge, non-destructive — preserves your servers and their `disabled`/`autoApprove` fields; kcap leaves `autoApprove` unset). This is **independent of the agent clone**, so it still applies even if `kiro-cli` isn't present to clone the agent. Opt out with `--skip-kiro-mcp`; `remove --kiro` unregisters them.

It further installs the **kcap skills** into `~/.kiro/skills` (as `kcap-<name>/SKILL.md`). Kiro reads its skills from there — not the agent-agnostic `~/.agents/skills` — and the cloned agent's `resources` include `skill:///~/.kiro/skills/*/SKILL.md`, so the skills steer Kiro to prefer the kcap MCP tools for why/history/prior-work/review questions (registration alone doesn't make the model route to them). Opt out with `--skip-kiro-skills`; `remove --kiro` deletes the `kcap-*` skill folders.

```bash
kcap plugin install --kiro                  # clone default agent + add hook, set as default
kcap plugin remove --kiro                   # restore previous default, delete kcap.json
```

Kiro writes an append-only JSONL log per session at `~/.kiro/sessions/cli/{id}.jsonl` (plus a sibling `{id}.json` for cwd / model / title; honours `KIRO_HOME`), so the kcap watcher tails it like every other vendor. Lifecycle comes from Kiro's `agentSpawn` hook (fires every prompt → deduped server-side); since Kiro has **no session-end trigger**, the watcher synthesizes session-end on `kiro-cli` exit. Historical sessions import via `kcap import --kiro`. Kiro persists no token counts, so Kiro sessions show no token usage by design — but it does report a per-turn **context-fill %**, which the watcher now stamps onto each assistant message live (read from the sibling `{id}.json` at send time, best-effort). It's captured for a turn when that turn's metadata is present at send time — the common case, since the assistant message is a turn's last line. This mirrors the import path, which has always carried it; a session imported without ever being watched live gets it fully.

#### Pi extension

Pi (`badlogic/pi-mono`) is detected via `~/.pi/agent/` or the `pi` binary on `PATH`. Pi has **no shell hooks** and **no built-in MCP** — it exposes an in-process extension API — so `install --pi` writes dependency-free TypeScript extensions that `pi` auto-loads at startup:

- **`~/.pi/agent/extensions/kcap.ts`** — the live-ingest extension (shells out to `kcap hook --pi` on session start/end).
- **`~/.pi/agent/extensions/kcap-mcp.ts`** — the MCP bridge (opt out `--skip-pi-mcp`). At load it spawns each `kcap mcp <name>` server as a stdio subprocess, performs the MCP handshake, and registers every advertised tool as a native Pi tool `kcap_<server>_<tool>`, torn down on session exit.
- **`~/.pi/agent/AGENTS.md`** — a kcap-owned, marker-delimited steering block (opt out `--skip-pi-instructions`, non-destructive — your own instructions are preserved) nudging Pi to prefer the kcap tools.

`kcap` must be on `PATH` (both extensions shell out to it). Restart any running `pi` session after installing.

```bash
kcap plugin install --pi                    # write kcap.ts + kcap-mcp.ts + AGENTS.md block
kcap plugin install --pi --skip-pi-mcp      # ingest + steering only, no MCP bridge
kcap plugin remove --pi                     # delete both extensions + strip the AGENTS.md block
```

Live sessions stream from `~/.pi/agent/sessions/` (honours `PI_CODING_AGENT_DIR`); historical sessions import via `kcap import --pi`.

#### SST OpenCode plugin

SST OpenCode is detected via `~/.config/opencode/` (or `~/.local/share/opencode/`) or the `opencode` binary on `PATH`. OpenCode has **no shell hooks** — it exposes an in-process plugin API — so `install --opencode` writes a dependency-free plugin to `~/.config/opencode/plugins/kcap.ts`, which `opencode` auto-loads at startup. Restart any running `opencode` session after installing.

```bash
kcap plugin install --opencode              # write ~/.config/opencode/plugins/kcap.ts
kcap plugin remove --opencode               # delete it
```

Beyond the capture plugin, `install --opencode` (and `kcap setup`) also **registers the four kcap MCP servers** in `~/.config/opencode/opencode.json` — OpenCode's `mcp` block, each entry `type: "local"` with `command` as an array and `enabled: true` (non-destructive/idempotent, preserving `$schema` and any user servers; opt out `--skip-opencode-mcp`) — and installs a kcap-owned **steering block** into `~/.config/opencode/AGENTS.md` (opt out `--skip-opencode-instructions`). `remove --opencode` reverses all three. (OpenCode reads the agent-agnostic `~/.agents/skills/` for kcap's skills, so no separate skills copy is needed.)

On `session.created` the plugin runs `kcap hook --opencode` (POSTs lifecycle + spawns the watcher); on each `session.idle` it fetches the session's full messages via OpenCode's in-process SDK and appends them as native `{info, parts}` JSONL to a file the watcher tails (`vendor=opencode`) — so kcap must be on `PATH`. Since OpenCode has **no session-end event**, the watcher synthesizes session-end when the `opencode` process exits. OpenCode records per-message tokens/cost, so those flow through. Capture is **live-only** — OpenCode's on-disk format is unstable (SQLite, version-dependent), so there is no historical `kcap import --opencode`.

#### Google Antigravity plugin

Google Antigravity is a GUI agent IDE detected via `~/.gemini/antigravity/` (it shares the `~/.gemini` home with the Gemini CLI, honouring `GEMINI_CLI_HOME`) or the `antigravity` binary on `PATH`. Antigravity has **no shell hooks** — it runs *control hooks* configured by a **plugin** — so `install --antigravity` installs the kcap capture plugin to `~/.gemini/config/plugins/kcap/`: a `plugin.json` manifest (which the GUI requires to load the directory) plus a `hooks.json` `kcap` block (preserving any hook blocks you authored). The GUI only reads plugins under its config root — the `~/.gemini/antigravity-cli/` dir is the `agy` CLI's config and is invisible to the IDE. Restart Antigravity after installing so it reloads the plugin.

```bash
kcap plugin install --antigravity           # install the kcap plugin to ~/.gemini/config/plugins/kcap/
kcap plugin remove --antigravity            # remove the kcap plugin
```

Beyond the capture plugin, `install --antigravity` (and `kcap setup`) also **registers the four kcap MCP servers** in Antigravity's own `~/.gemini/config/mcp_config.json` — its OWN MCP file, not the Gemini CLI's `settings.json` (opt out `--skip-antigravity-mcp`); **installs the kcap steering block** into the shared `~/.gemini/GEMINI.md` (opt out `--skip-antigravity-instructions`); and **copies the kcap skills** into `~/.gemini/skills` — where Antigravity reads them, **not** `~/.agents/skills` (opt out `--skip-antigravity-skills`). All three are non-destructive and idempotent. `remove --antigravity` reverses them, but leaves the shared `~/.gemini/GEMINI.md` block in place when the Gemini CLI integration is still installed (that block is shared; `remove --gemini` owns it then).

Antigravity fires a distinct control hook per lifecycle/tool event; kcap acts on the first `PreInvocation` of a conversation (POSTs lifecycle + spawns a watcher tailing that conversation's `transcript_full.jsonl`, `vendor=antigravity`) — so kcap must be on `PATH`. Antigravity is a GUI whose process outlives any one conversation (like the Codex desktop app), so there is no per-conversation exit signal: the watcher ends a session after it goes idle (default 60 min; override with `KCAP_ANTIGRAVITY_IDLE_MINUTES`), and a later turn reactivates it. Token/model usage lives in each conversation's sibling SQLite db (`conversations/<id>.db`), not the JSONL, so the watcher decodes it and streams the per-generation cost (priced on read; cost is never stored). **Subagents** (Antigravity's nested agents) are separate conversations; both *live* capture and historical `kcap import --antigravity` nest them under the parent, derived from the `INVOKE_SUBAGENT` step in the parent's `transcript_full.jsonl` (the spawn-time linkage signal). Live capture POSTs a subagent-link as each child is spawned; import reads the same `INVOKE_SUBAGENT` steps across all conversations on disk. Historical import reads `~/.gemini/antigravity/brain/*/…/transcript_full.jsonl` and backfills sessions from before the hooks were installed; it's watermark-idempotent (safe to re-run) and leaves the working dir empty (Antigravity records no machine-readable cwd in the transcript — live capture gets it from the hook payload). Imported sessions currently carry content but not cost (cost injection on import is a follow-up). To import one conversation, `kcap import --antigravity --session <id>` accepts the id in **either** form — the dashed brain-dir conversation id or its dashless canonical form (the id kcap shows for the session) — because import canonicalizes to the same dashless id that live capture uses.

Cursor uses a single user-scope `hooks.json`; there is no project-scope variant.

`kcap setup` writes all 8 supported Cursor hook entries. Use `--skip-cursor-hooks` to opt out during setup:

```bash
kcap setup --server-url <url> --no-prompt --skip-cursor-hooks
```

#### Daemon config settings

Use `kcap config set` to configure the binary paths used by the daemon. The values are stored in the active profile and take effect the next time the daemon starts.

```bash
kcap config set daemon.claude_path /opt/claude/bin/claude
kcap config set daemon.codex_path  /opt/codex/bin/codex
```

| Key | Default | Description |
|-----|---------|-------------|
| `daemon.claude_path` | `"claude"` | Path to the Claude CLI binary. Resolved via `PATH` when not an absolute path. |
| `daemon.codex_path`  | `"codex"`  | Path to the Codex CLI binary. Resolved via `PATH` when not an absolute path. |

You can also override these at runtime with environment variables (take precedence over the profile):

```bash
KCAP_CLAUDE_PATH=/opt/claude/bin/claude kcap daemon
KCAP_CODEX_PATH=/opt/codex/bin/codex  kcap daemon
```

The Cursor CLI path (`cursor-agent` by default, used to spawn the `cursor` hosted-agent vendor) is env-only for now — there is no `daemon.cursor_path` profile key yet, so set it per-launch:

```bash
KCAP_CURSOR_PATH=/opt/cursor/bin/cursor-agent kcap daemon
```

`KCAP_CURSOR_MODEL` overrides the model a `cursor` hosted agent runs (default `claude-sonnet-4-5`; the per-launch model from the dashboard takes precedence). It is matched against the models the Cursor account actually offers, so a family name like `claude-sonnet-4-5` resolves to the exact available variant; an unrecognized value falls back to Cursor's own default.

```bash
KCAP_CURSOR_MODEL=claude-opus-4-8 kcap daemon
```

`KCAP_COPILOT_PATH` overrides the `copilot` binary the daemon spawns for **GitHub Copilot hosted agents** (`copilot --acp --stdio`), mirroring `KCAP_CURSOR_PATH` — the daemon hosts Claude, Codex, Cursor, Copilot, Kiro, Gemini, OpenCode and Antigravity. `KCAP_GEMINI_PATH` overrides the `gemini` binary the same way (`gemini --experimental-acp`), and applies to both hosted Gemini agents and the opt-in [unattended Gemini reviewer](#gemini-as-an-unattended-review-flow-reviewer--off-by-default-and-why) — whose build-affirmation check reads whichever binary it names. `KCAP_OPENCODE_PATH` overrides the `opencode` binary the daemon spawns for **OpenCode hosted agents** (`opencode acp`) — no longer reserved; see [Hosted OpenCode agents](#hosted-opencode-agents) below.

```bash
KCAP_COPILOT_PATH=/opt/copilot/bin/copilot kcap daemon
```

`KCAP_KIRO_PATH` overrides the AWS Kiro CLI binary the daemon spawns for **Kiro hosted agents**
(`kiro-cli acp`). It defaults to `kiro-cli` — the name a standard install puts on `PATH` — and only
that one name is probed, so point this at your binary if yours is named differently. As with Cursor,
if the daemon can't resolve it the `kiro` vendor is simply hidden from the launch dialog rather than
failing at launch.

```bash
KCAP_KIRO_PATH=/opt/kiro/bin/kiro-cli kcap daemon
```

`KCAP_KIRO_MODEL` overrides the model a `kiro` hosted agent runs, mirroring `KCAP_CURSOR_MODEL` —
with one deliberate difference: there is **no built-in default**, so with nothing set (and no
per-launch model from the dashboard, which takes precedence) a Kiro hosted agent runs whatever
Kiro's own default model is and kcap reports none. The value is matched against the models the Kiro
account actually offers (Kiro's ids are bare names like `claude-haiku-4.5`); an unrecognized value
falls back to Kiro's own default with none reported. Applied over ACP `session/set_model` —
verified to take effect at the turn level, not just accepted — because Kiro does not implement the
`session/set_config_option` call Cursor uses.

```bash
KCAP_KIRO_MODEL=claude-haiku-4.5 kcap daemon
```

One limit is worth knowing before you pick Kiro:

- **Interactive hosting only.** Kiro cannot yet be selected as an unattended review-flow reviewer.
  Kiro inherits the MCP servers from your global `~/.kiro/settings/mcp.json` into every ACP session,
  which is exactly what you want for a session you are driving yourself, but means an unattended
  reviewer would be handed the flow-starting `kcap-flows` server. Containment for that is tracked
  separately. (This also means a *pinned reviewer* model never reaches Kiro today — reviewer model
  overrides remain gated on the vendors that advertise resolver support.)

### Hosted OpenCode agents

`KCAP_OPENCODE_MODEL` overrides the model an `opencode` hosted agent runs, mirroring
`KCAP_KIRO_MODEL` — including the same deliberate absence of a built-in default: with nothing set
(and no per-launch model from the dashboard, which takes precedence) the agent runs whatever
OpenCode's own configured default is and kcap reports none. The value is matched against the models
your OpenCode account actually offers, whose ids are `provider/model`; a display label works too, so
`opencode/deepseek-v4-flash-free`, the `opencode/deepseek` prefix and `DeepSeek V4 Flash Free` all
resolve to the same model. An unrecognized value falls back to OpenCode's own default with none
reported.

```bash
KCAP_OPENCODE_MODEL=opencode/big-pickle kcap daemon
```

Two things are worth knowing before you pick OpenCode:

- **Your OpenCode plugins do not load in a hosted agent.** The daemon spawns the child with
  `OPENCODE_PURE=1`. This is not a preference: OpenCode is the one vendor where kcap has *two* capture
  paths, and kcap's own live-ingest plugin (`~/.config/opencode/plugins/kcap.ts`) loads inside the
  hosted child too — where it would start a second, independent recording of the very session the
  daemon is already recording, so the run would show up twice. Suppressing external plugins in the
  hosted child is what keeps a daemon-hosted session to exactly one recording. Sessions you start
  yourself are untouched: the plugin keeps its whole job there.
- **Unattended review is off by default** — see below.

#### OpenCode as an unattended review-flow reviewer — off by default, and why

`start_review_flow(vendor="opencode")` works only on a daemon whose operator has explicitly enabled
it:

```bash
KCAP_OPENCODE_UNATTENDED_REVIEWER=1 kcap daemon
```

The reviewer's tool surface is deliberately narrow. It gets `read`, `grep`, `glob` and `list` plus
the one MCP channel it reports results through — **no shell, no write, no edit, no network** — and
that is enforced by OpenCode's own permission table rather than by asking the model nicely.

What you are consenting to is the part that narrow surface does *not* cover: **those read tools are
not path-scoped.** They are whole-filesystem read primitives running as the daemon user, so a review
can read any file that user can read — its own credentials included — and a reviewer's findings text
goes back to whoever requested the review. Enable it only on a daemon whose operator and review
requesters are in one trust domain.

Two further things the launch does, which are worth knowing because they change what the reviewer
sees:

- **The reviewed branch's own configuration is ignored** (`OPENCODE_DISABLE_PROJECT_CONFIG`). A
  contributor-authored `opencode.json` / `.opencode/` and the repo's `AGENTS.md`/`CLAUDE.md` are
  inputs *from the thing being reviewed into the reviewer judging it*, so they are suppressed. The
  cost is that the reviewer does not see your repo's guidance documents.
- **Your global MCP servers are absent** (an empty per-launch `OPENCODE_CONFIG_DIR`). Otherwise a
  reviewer would inherit `kcap-flows` and could start review flows of its own.

Enabling it does **not** bypass the build check. The containment above is behaviour of the installed
`opencode` build, so the daemon records a **minimum** version and refuses anything older. Enabling
seeds that minimum from whatever is installed, so a later upgrade needs no action from you; to move
the floor to the currently-installed build (after a bad release, say), run:

```bash
kcap daemon reviewer affirm --vendor opencode
```

POSIX only: the containment is an *empty* config directory, which cannot be made owner-only on
Windows.

**Hosted Cursor agents run over ACP.** The `cursor` vendor is launched by the daemon as
`cursor-agent acp` (Cursor's Agent Client Protocol server) in an isolated worktree, driven from the
dashboard. This is **distinct from the Cursor recording hooks** (`~/.cursor/hooks.json`, which capture
sessions you run yourself in Cursor) — a hosted ACP agent is one the daemon *starts and supervises*.
It requires:

- The Cursor CLI (`cursor-agent`) installed and on `PATH`, or pointed at via `KCAP_CURSOR_PATH`. If the
  daemon can't find it, the `cursor` vendor is simply hidden from the launch dialog.
- A **logged-in Cursor account on a Team-tier (paid) subscription** — run `cursor-agent login` first.
  On the Free tier the agent refuses to run turns (it returns "Upgrade your plan to continue"); a
  logged-out CLI fails the handshake.

**Current limitations.** A hosted Cursor agent renders its transcript (messages, thinking, tool
calls/results) but does **not** support interactive local-attach input, special keys, or terminal
resize, and it emits no separate terminal-output stream — Cursor runs shell commands itself in the
worktree and they surface as `execute` tool results in the transcript. Capacitor advertises **no**
client filesystem or terminal capabilities to the agent (it performs those operations itself).

**Troubleshooting.**

- *`cursor` missing from the launch dialog* — the daemon didn't find `cursor-agent`; install it or set
  `KCAP_CURSOR_PATH`, then restart the daemon.
- *Agent connects but every turn fails with "Upgrade your plan to continue"* — the Cursor account is on
  the Free tier; hosted turns require a **Team-tier** subscription. (The handshake succeeds; the refusal
  is returned per-turn.)
- *Agent fails at startup / never establishes a session* — the CLI is likely logged out (`cursor-agent
  login`) or the binary is broken; the daemon logs the handshake failure.
- *Model not applied* — `KCAP_CURSOR_MODEL` (or the per-launch model) is matched against the models the
  account actually offers; an unrecognized value falls back to Cursor's default.

**Data handling.** A hosted Cursor agent runs as a local child process of the daemon with the daemon's
own filesystem/process access — the same unsandboxed-in-a-worktree posture as every hosted agent — and
its transcript (prompt text, tool arguments, tool results) is forwarded to the Capacitor server
verbatim, exactly like every other agent, with no Cursor/ACP-specific redaction.

**Codex session-end tuning.** Because Codex has no session-end hook, the watcher owns session-end via two triggers: parent `codex` process exit, and rollout-file idle timeout. The idle trigger is particularly important for the Codex desktop app, whose shared `codex app-server` process never exits per-conversation.

| Environment variable | Default | Description |
|----------------------|---------|-------------|
| `KCAP_CODEX_IDLE_MINUTES` | `60` | How long a Codex rollout file may be idle (no new rollout lines and no Codex tool call in flight) before the `kcap watch` background watcher ends the session (`reason: idle_timeout`). Increase for very long thinking/compute turns; decrease for faster cleanup of abandoned sessions. Invalid or non-positive values fall back to the 60-minute default. |
| `KCAP_PARENT_DEAD_CEILING_MINUTES` | `360` | Staged recovery ceiling for a watcher whose parent coding-agent PID was already dead at startup (a resolution glitch) and can't be re-resolved. The watcher first periodically re-resolves and re-arms the parent-exit watchdog; only if that keeps failing AND the transcript makes no progress for this long does it post `session-end` (`reason: parent_dead_ceiling`). Deliberately far above the idle timeout so a user parked at a Kiro/OpenCode prompt is never ended prematurely. Invalid or non-positive values fall back to 360 minutes (6h). |
| `KCAP_CURSOR_IDLE_CEILING_MINUTES` | `60` | How long a Cursor session's transcript watcher may go idle before it exits (AI-1382). Unlike Codex/Antigravity, this exit does NOT itself POST `session-end` — Cursor's end-of-session synthesis stays owned by the `sessionEnd` hook or, as a backstop, a server-side lease-gated sweep; the next hook for that session reactivates a fresh watcher. Invalid or non-positive values fall back to the 60-minute default. |

#### Review-flow reviewer backstops & crash-survivor reaping

Hosted review-flow reviewers are *unattended* and count against the daemon's `--max-agents` budget. To keep a stuck or abandoned reviewer from holding a slot forever, the daemon defends its own capacity in layers: OS-level containment at spawn time (this section) plus a managed record/scan/quarantine backstop for everything containment can't reach.

- **Lifetime / idle backstop.** The heartbeat reaps a review-flow reviewer that has run past a maximum lifetime or gone idle too long (the driver vanished, or its run went terminal on the server without the daemon hearing about it). Interactive agents are never touched by these bounds.
- **OS-level containment at spawn (native, immediate where the OS supports it).**
  - **Windows** — every hosted PTY is created already bound to a `KILL_ON_JOB_CLOSE` Job Object (no breakaway allowed): the OS itself kills the agent **and every descendant** the instant the job handle's last reference closes — clean shutdown, daemon crash, or an external kill, no exceptions. There is no survivor class to reap on Windows; the PID-record layer becomes pure bookkeeping.
  - **Linux** — a native spawn shim (`libpty_shim`) forks the agent, arms `PR_SET_PDEATHSIG`, and execs via `execveat` on a dedicated daemon-lifetime thread, so the agent **leader** dies immediately if the daemon dies — for a launch the shim proved was contained at initial exec (a non-privileged, non-deep-shebang binary on a kernel ≥ 3.19). Descendants and any uncontained-classified launch (a privileged binary, an old kernel, an unresolvable shebang) fall back to the crash-survivor record/scan reaping below.
  - **macOS** — there is no OS primitive for this at all (no PDEATHSIG, no job objects); a crash-surviving child is recovered *eventually*, at the next daemon boot/heartbeat, via the record layer below.
- **Crash-survivor reaping (the managed backstop — covers everything containment doesn't).** Each hosted child's pid + an exact OS-native start-identity is written to a durable per-daemon record under `{state-dir}/{name}/agents/` at spawn (Linux: kernel `boot_id`+`starttime`; macOS: a kernel-assigned, boot-scoped unique process id + boot-session UUID; Windows: an absolute start timestamp, moot once containment ships since there's no survivor to reap), and every child is stamped with `KCAP_AGENT_ID` / `KCAP_DAEMON_ID` / `KCAP_DAEMON_EPOCH` env markers. On the next boot (and on the heartbeat) the daemon reaps any child that outlived a **prior** incarnation of itself — matched by exact `(pid, start-identity)` from the record, or, for a recordless survivor, by the env markers (same daemon id, older epoch). A process is killed only when its identity is *proven*; anything ambiguous is spared (never a wrong kill). On Linux the env checks read `/proc/{pid}/environ`; on macOS process env is redacted from other processes entirely, so the record-based path is the effective mechanism there, and a record whose identity couldn't be captured (a rare private-ABI hiccup) is retained and logged each sweep rather than silently dropped — resolved automatically on Linux (the env-marker scan can still confirm it) or by a manual kill on macOS. Note: `kcap launch`'s private local-attach path spawns through the same OS-containment layer but writes no durable record at all (no server-side ownership to protect there), so it has no crash-survivor backstop beyond whatever OS containment applies.

| Environment variable | Default | Description |
|----------------------|---------|-------------|
| `KCAP_REVIEWER_MAX_LIFETIME` | `6h` (`21600`) | Max wall-clock lifetime, **in seconds**, for a hosted review-flow reviewer before the heartbeat reaps it. `0` disables the bound. |
| `KCAP_REVIEWER_IDLE_TIMEOUT` | `2h` (`7200`)  | Max time, **in seconds**, a reviewer may go without output before the heartbeat reaps it. `0` disables the bound. |

#### Daemon log verbosity

The daemon logs at `Information` by default. Raise the level for transport diagnostics — for example, per-tick `DaemonPing` round-trip times (logged at `Debug`) are useful for telling whether SignalR reconnects are caused by network/proxy latency. Set it either way:

```bash
kcap daemon start --log-level debug        # foreground or with -d; forwarded to the daemon
KCAP_DAEMON_LOG_LEVEL=debug kcap daemon    # env var; read directly, works in any launch mode (service, container)
```

Accepted values: `trace`, `debug`, `information` (default), `warning`, `error`, `critical`, `none`. The `--log-level` flag wins over the env var when both are set. `Debug` is verbose — it also enables the SignalR client's framework logs — so use it for a diagnostic window rather than steady state.

**Full ACP frame logging (`KCAP_ACP_DEBUG_FRAMES`).** Off by default. When set to `1`/`true`, a hosted Cursor (ACP) session logs full inbound/outbound JSON-RPC frames, raw unrecognized `session/update` payloads, and `cursor-agent` stderr at `Debug` (length-capped); with it off, only their shape (kind + length) is logged. **These frames can contain prompts, tool arguments, and file contents** — enable it only for a diagnostic window, never in a shared or persistently-logged environment. It needs `Debug` logging on to be visible:

```bash
KCAP_ACP_DEBUG_FRAMES=1 KCAP_DAEMON_LOG_LEVEL=debug kcap daemon
```

#### ACP crash reconnect (`KCAP_ACP_RECONNECT`)

When a hosted ACP agent's child process dies mid-session (a crash, an OOM kill — not a stop you
asked for), the daemon transparently resumes the session where the vendor supports it: it relaunches
the agent binary and restores the same session via ACP `session/load`, keeping the dashboard
session, transcript, and agent slot intact. A note appears in the transcript ("Agent process
restarted; the session was resumed"), and if a message was in flight at the crash it asks you to
resend it rather than guessing. Resume is attempted only for vendors verified to support it across a
crashed process (currently `cursor` and `copilot`; Kiro and Gemini refuse a crashed session's load,
so their agents end as before), and a session that keeps crashing stops being resumed after 5
recoveries. Set `KCAP_ACP_RECONNECT=0` to disable reconnect entirely — a child death then ends the
session immediately, the pre-reconnect behavior:

```bash
KCAP_ACP_RECONNECT=0 kcap daemon
```

#### Diagnosing a hard death

A daemon killed by an uncatchable `SIGKILL` (macOS **jetsam** / Linux **OOM**, `kill -9`, power loss) or a hard native crash can't log its own exit — the process is gone before any handler runs. Two things help tell those apart from a normal stop:

- **`~/.config/kcap/daemon.out.log`** — a background (`-d`) daemon reopens its stdout/stderr onto this file, so a runtime "Fatal error." dump or native crash message (which bypasses the normal `daemon.log` pipeline) is captured here. (`kcap daemon start -d` wires this up automatically by passing the daemon a `--stderr-file` flag; you don't set it yourself.) A service-managed daemon captures the same output via its service log. An empty file means nothing was written to stderr — i.e. a `SIGKILL`, not a crash.
- **Startup breadcrumb** — when a daemon starts and finds the previous instance's lock was left for the kernel to release (the signature of an uncatchable kill), it logs a `warning` to `daemon.log` naming the dead PID. If you see this recur, run the daemon as a service (`kcap daemon service install`) so it auto-restarts instead of staying down.

### Local agents (`kcap agent`)

Start a coding agent from your own terminal that the daemon hosts for you. Because the daemon owns the agent (not your terminal), you can **detach and the agent keeps running**, then **re-attach later** — like `tmux` for your coding agent.

```bash
kcap agent start claude                       # start Claude in the current directory, attached
kcap agent start claude -- --model opus       # everything after `--` is passed to the agent CLI verbatim
kcap agent start codex --worktree -- -m gpt-5 # run in an isolated git worktree instead of in place
kcap agent start claude -d                    # start without attaching; prints the agent id
```

- **`--` boundary:** flags before `--` are kcap's; everything after `--` is forwarded to the `claude`/`codex` CLI unchanged. kcap flags: `--worktree`, `--private`, `--daemon <name>`, `-d`/`--detach`.
- **Visibility:** by default the agent is **registered with the server**, so it appears in your own web UI immediately and you can drive it from the browser — start in the terminal, continue from anywhere. It is **visible only to you** until you share it. Pass `--private` to keep it purely local: unregistered, not streamed to the server, and not shown in the web UI.
- **Work location:** by default the agent runs **in place in your current directory** (it edits your real files). Pass `--worktree` to run in a throwaway git worktree instead.

- **What a worktree deliberately does NOT inherit:** an agent worktree is a checkout of whatever branch is
  being worked on, so anything committed there is content the agent's own author may not control. These are
  therefore neutralised when kcap creates one:

  - **Workspace MCP config is removed** — `.mcp.json`, `.cursor/mcp.json`, `.gemini/settings.json`,
    `.kiro/settings/mcp.json`, `.vscode/mcp.json`, `.github/copilot/mcp.json`, `.copilot/mcp.json`,
    `.codex/config.toml`. Some vendors execute the `command` these declare at session start — measured for
    Kiro, which spawns it with no prompt and no model involvement — which would be branch-controlled code
    running as the daemon user. Removals are logged. If your repo legitimately ships one, it will not apply
    inside an agent worktree; configure it for the daemon instead.
    Borrowed review snapshots keep those paths non-executable but do not hide the change from the reviewer:
    kcap supplies a private, read-only review-context tool containing the committed/staged **Git index**
    bytes. Unstaged and untracked MCP config is deliberately omitted and never read. The tool labels every
    returned path and content value as untrusted branch-authored evidence. A config too large to ship in
    full is declared to the reviewer by path, size and hash rather than failing the launch, so an oversized
    file can neither hide from the review nor block it. If kcap cannot extract, bound, or deliver that
    context safely, the borrowed reviewer fails closed instead of reporting a blind clean review.
  - **Git hooks are disabled for the creation commands** (`core.hooksPath=/dev/null`). With a relative
    `core.hooksPath` such as `.githooks`, the hook scripts are themselves branch content and git would run
    `post-checkout` during `worktree add`. This applies only to kcap's own creation commands; hooks in the
    agent's own later commits are unaffected.
  - **Clean/smudge filters are disabled for those same commands — all of them, including `lfs`.**
    `.gitattributes` is branch content and selects which filter driver applies, so a driver whose command is
    relative — `filter.x.smudge=./tools/f` — has the branch supply the executable. No command is inspected
    and no driver is exempt: four narrower designs were each defeated at their exemption, so there is
    deliberately nothing left to parse, resolve or impersonate. **No custom filter driver runs during the git
    commands kcap uses to create and populate a worktree** — that is the window in which branch content is
    first materialised, before an agent is running. The overrides are per-command, not persistent: git
    operations the agent itself runs inside the worktree afterwards use the repository's own configuration.
    What the disabling means for file contents depends on how the worktree is built:
    - *Owned worktrees* check out through git, so LFS-tracked files appear as pointer text.
    - *Standalone snapshots* copy the source directory's bytes and re-commit them with the clean filter
      disabled, so whatever the source already held — smudged content included — is what you get.
    - *Borrowed review snapshots* are rebuilt from the source working tree and carry its real content; they
      refuse to build if the source itself holds unsmudged LFS pointers.

    Disabled drivers are logged per worktree so the effect is visible rather than mysterious.

    Both the hook and the filter overrides are carried in git's environment (`GIT_CONFIG_COUNT` /
    `GIT_CONFIG_KEY_n`) rather than on the command line, so a config key that legally contains `=` — a filter
    driver named `evil=x` — stays intact instead of being cut at the `=` and left live. kcap measures at
    launch that the git it found honours those variables and refuses to build the worktree if not, rather
    than reporting containment it does not have: **creating an agent worktree needs git 2.31 or newer.**

- **Detach** without stopping the agent with the prefix key **`Ctrl-Q` then `d`**. The agent keeps running in the daemon.
- **Permissions:** for a registered agent, permission prompts appear in the web UI (the same dialog as hosted agents); with `--private`, prompts are answered natively in your terminal.

```bash
kcap agent                 # no subcommand — same as `kcap agent ls`
kcap agent ls              # list daemon-hosted agents (id, status, kind, repo)
kcap agent attach ab12     # re-attach your terminal (any unique id prefix works)
kcap agent stop ab12       # graceful /exit, then terminate
kcap agent stop --all -y   # stop every agent this daemon hosts, no prompt
```

Agent ids are long, so `attach` and `stop` accept **any unique prefix** — an ambiguous one lists the candidates instead of guessing. `stop --all` includes `--private` agents and prompts for confirmation unless you pass `--yes`/`-y`; a stop that cannot be confirmed prints a per-agent failure line and exits non-zero.

**Agents that aren't yours.** `kcap agent ls` shows a `KIND` column: `agent` for ones you started, `review` for PR-review agents, and `review-flow` for review-flow participants (with their role). The daemon protects the latter two, because they are driven by the flow protocol rather than by you:

- `kcap agent attach` on one is **read-only** — you see its output, your keystrokes are not delivered, and your terminal size is not applied to it.
- `kcap agent stop` on one is **refused** unless you pass `--force`, and `stop --all` skips them and says how many it skipped.

Enforcement lives in the daemon, so a current CLI can't bypass it by skipping the check or lying about `--force`. That guarantee has two version-skew exceptions: an old `kcap` sends the legacy `Stop` request, which has no `--force` concept — the daemon treats that as `--force`, so an old client can silently force-stop a review or review-flow agent. And against an old daemon, `ls`/`attach` degrade silently (no kind reported, every agent reads as `agent`), but `stop` doesn't degrade — it sends a newer request format the old daemon can't decode, so the connection closes and the CLI tells you to restart the daemon instead of stopping anything.

`agent start` auto-starts the daemon if one isn't already running, and needs a configured server for the daemon to record to. `ls`, `attach`, and `stop` only talk to the local socket, so they work without one. A locally-started agent appears in **your own** web UI (owner-only until you share it from the web UI); use `--private` to opt out of registration entirely. Unix only for now.

### Repository paths

Manage known repo paths for the agent launch dialog. Repos are automatically added when agents are launched, but you can also manage the list manually:

```bash
kcap repos                    # list known repos (sorted by last used)
kcap repos add .              # add current directory
kcap repos add ~/dev/project  # add a specific path
kcap repos remove ~/dev/old   # remove a path
```

Known repos are persisted to `~/.config/kcap/repos.json` and reported to the server when the daemon connects, so the launch dialog always shows previously-used repos even after restarts.

### Projects

List and inspect projects — a Team/Enterprise-plan grouping of repos and members that sessions can be scoped to (see `default_visibility project` below).

```bash
kcap projects            # table: slug, name, repos, members, your role
kcap project <slug>      # metadata, repo list, member list, and (owner/admin) pending join requests
```

Requires the Team or Enterprise plan — the server 403s on Free with a message telling you so.

### Profiles

Profiles let you work with multiple Capacitor servers — for example, a company server for work repos and a separate one for open-source projects. Each profile stores its own server URL, visibility settings, and daemon configuration.

```bash
kcap profile add work --server-url https://my-other-tenant.kcap.ai
kcap profile add oss --server-url https://cap.oss.dev --remote "github.com/myorg/*"
kcap profile list
kcap profile show work
kcap profile remove work
```

The `--remote` flag associates a profile with git remote patterns. When you open a repo whose remote matches a pattern, that profile activates automatically.

#### Switching profiles

```bash
kcap use work                  # bind 'work' profile to current repo/directory
kcap use work --global         # set 'work' as the global default
kcap use oss --save            # bind and write .kcap.json for team sharing
```

Without `--global`, `use` binds the profile to the current git repo root (or the current directory if not in a repo). With `--save`, it writes a `.kcap.json` file that can be committed so the whole team uses the same profile.

#### Profile resolution order

The CLI resolves which profile to use in this order:

1. `--server-url` CLI flag
2. `KCAP_URL` environment variable
3. `KCAP_PROFILE` environment variable
4. `.kcap.json` in the repo root (or current directory if not in a repo)
5. Git remote pattern matching from `--remote` flags
6. Directory binding from `kcap use`
7. Global active profile (or `default`)

### Configuration

```bash
kcap config show    # show current configuration
kcap config set <key> <value>
```

**Default session visibility** controls how your sessions appear to other users in the same Kurrent Capacitor account. Set during `kcap setup` or change at any time:

```bash
kcap config set default_visibility private      # only you can see your sessions
kcap config set default_visibility project      # visible to the repo's project members (see `kcap projects`)
kcap config set default_visibility org_public   # org repos visible, others private (default)
kcap config set default_visibility public       # all sessions visible to others in your account
```

**Repository exclusions** prevent specific repos from sending any data to the server — hooks are silently skipped, no session is recorded:

```bash
kcap config set excluded_repos "myorg/secret-project,personal/diary"
```

**Path exclusions** silently skip any session whose working directory is, or sits inside, a configured path — useful for ignoring scratch dirs, worktrees, or monorepo subtrees regardless of git remote:

```bash
kcap ignore .                       # ignore the current directory
kcap ignore ~/code/secret-project   # ignore a specific tree
kcap ignore --list                  # show all ignored paths
kcap ignore --remove ~/code/secret-project
```

Entries are stored on the **active profile**, so switching profiles with `kcap use` switches the ignore list too. Symlinks are resolved on both the stored entry and the session's reported cwd, so a worktree symlink and its target match.

**Provider API keys for headless calls.** Title generation, summaries, and judges shell out to `claude -p` / `codex exec` in the background. By default kcap scrubs `ANTHROPIC_API_KEY` and `OPENAI_API_KEY` from those spawns so your subscription login (claude.ai / ChatGPT account) is used — a globally-set key would otherwise override subscription auth and fail the call. If you intentionally authenticate via API key (PAYG), opt back in:

```bash
kcap config set use_provider_api_key true     # keep keys in headless spawns
KCAP_USE_PROVIDER_API_KEY=1 kcap recap …      # one-off override
```

`kcap setup` also prompts for this when it detects either key in the current environment. The env var (`1`/`true`/`yes`/`on` or `0`/`false`/`no`/`off`) wins over the profile setting.

#### Renamed repo directories (`kcap remap`)

Historic transcripts record the absolute working directory they ran in. If you've since renamed or moved that directory on disk (e.g. `~/dev/foo-cli → ~/dev/bar-cli`), `kcap import --org` / `--repo` can't resolve those sessions to a GitHub repo any more and silently drops them from the matched count.

Manage the rewrites with `kcap remap`:

```bash
kcap remap ~/dev/eventstore/foo-cli ~/dev/eventstore/bar-cli   # add or replace a mapping
kcap remap --list                                              # show all mappings
kcap remap --remove ~/dev/eventstore/foo-cli                   # drop one
```

Entries are stored at the top of `~/.config/kcap/config.json` under `cwd_remap` (a top-level JSON array of `{ "from": ..., "to": ... }` objects) — you can also edit the file directly for bulk changes.

Semantics:

- `from` / `to` are **path-prefix** rewrites with `~` expanding to the current user's home directory (`~\` is also accepted on Windows). The match requires a path boundary (`from` exactly equal, or `from` followed by `/` — or `\` on Windows), so `from: "~/dev/foo"` will **not** spuriously rewrite `~/dev/foo-cli`.
- Comparisons follow the host filesystem's case policy: case-insensitive on Windows, case-sensitive elsewhere.
- When multiple rules could apply to the same transcript cwd, the **longest** `from` wins.
- Rules are applied once (no chaining), so the result of one rule isn't fed into another.
- Remaps are global, not per-profile — same rename affects all profiles' imports.

After adding a remap, re-run `kcap import --org` (or whichever scope you use). The missing-cwd report at the top of the import will show what's still unresolved. Ephemeral worktree paths under `<project>/.<anything>/worktrees/<slug>` are auto-attributed to `<project>` when it still exists on disk, so deleted-worktree cwds don't need a remap entry.

### Uninstalling

To remove kcap from this machine, run:

```bash
kcap uninstall                  # interactive, user-scope removal
kcap uninstall --yes            # non-interactive
kcap uninstall --project --yes  # also strip project-scope hooks in cwd's repo
kcap uninstall --keep-config    # remove integrations, keep ~/.config/kcap
```

`uninstall` covers every supported agent: it stops running daemons and watcher processes, strips kcap entries from user-level Claude Code, Codex CLI, Cursor, and Copilot CLI hook files (preserving any non-kcap entries), deletes the Pi extensions (`~/.pi/agent/extensions/kcap.ts` + the `kcap-mcp.ts` bridge) and strips kcap's block from `~/.pi/agent/AGENTS.md`, deletes the OpenCode plugin (`~/.config/opencode/plugins/kcap.ts`), removes the kcap capture plugin from Antigravity's `~/.gemini/config/plugins/kcap/`, removes agent skills under `~/.agents/skills/` (plus the legacy `~/.codex/skills/kcap-*` folders), and deletes `~/.config/kcap/`.

`--project` additionally cleans up `<repo>/.claude/settings.local.json` and `<repo>/.codex/hooks.json` in the current git working tree (errors if you're not inside one). Cursor only has a user-scope `hooks.json`, so `--project` does not affect it. Project-scope hooks in other repos are not touched — re-run from each repo that has them.

Use `--keep-config` to preserve profiles, tokens, and ignore lists when you plan to reinstall. Per-agent selective cleanup is not exposed here — use `kcap plugin remove [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--skills]` for finer-grained removal.

### Other commands

```bash
kcap status         # server health check
kcap whoami         # show current identity + ask the server if it accepts your token
kcap login          # authenticate via OAuth (browser flow by default)
kcap login --device # force device-code flow (use in SSH / headless envs)
kcap update         # upgrade the CLI and refresh agent plugins (npm-global installs)
kcap update --beta  # switch to the beta channel and update to the latest beta
kcap update --stable # switch back to the stable channel (the default)
kcap logout         # delete stored tokens
```

> `kcap update` is the one-step upgrade for npm-global installs: it checks the
> registry, runs `npm install -g @kurrent/kcap@<tag>`, then refreshes your
> opted-in agent plugins — so it picks up new skills/hooks even when your package
> manager blocks install scripts. It exits early if you're already up to date,
> and tells you what to run instead for non-npm installs (e.g. Homebrew). Use
> `kcap update --check` for a machine-readable `{current, latest, newer}` probe.
>
> **Windows:** the update works even while Claude Code sessions (whose kcap MCP
> servers keep the binary locked) or the daemon are running — the old executable
> is moved aside so npm can replace it, and the leftover is cleaned up
> automatically later. Running processes keep the old version until they
> restart; a running daemon reports this and applies the new version on
> `kcap daemon restart`. A plain `npm install -g` / `npm update -g` does **not**
> do this and fails with `EBUSY` while any kcap process is running — prefer
> `kcap update`.
>
> **Beta channel (opt-in):** `kcap update --beta` switches the active profile to
> the beta release channel (npm dist-tag `beta`) and updates to the latest beta
> immediately. The choice is **persisted per profile**, so subsequent `kcap
> update` runs — and the passive stderr "update available" hint — keep tracking
> beta until you run `kcap update --stable`. A fresh config defaults to the
> **stable** channel (npm dist-tag `latest`); beta is strictly opt-in. Beta
> releases correspond to server versions rolled out to internal tenants first,
> so most users should stay on stable.

The v1 config format stored `server_url` as a bare host name without a
scheme. If `kcap` crashes with `An invalid request URI was provided`
after upgrading, your config still has the old format. Fix it with one
command:

    kcap config set server_url https://my-tenant.kcap.ai

Or remove the config file and re-run setup:

    rm ~/.config/kcap/config.json
    kcap setup

## License

[Kurrent License v1](LICENSE.md)
