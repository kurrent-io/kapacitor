# DEV-1573 — Spectre.Console CLI UX: setup wizard + history import

## Summary

Adopt [Spectre.Console](https://github.com/spectreconsole/spectre.console) for the two most human-facing commands in the kapacitor CLI — `setup` and `history` — replacing ad-hoc `Console.Write*` calls with Spectre's rendering primitives (prompts, status spinners, rules, grids, and pinned progress). Scope is deliberately narrow: these two commands have the most prompts and the longest-running output, and they benefit the most from live feedback. All other commands (hook handlers, `watch`, daemon, MCP servers, etc.) remain untouched.

Non-goals:

- Adopting `Spectre.Console.Cli` (the command/binder framework). It is reflection-heavy, noisy under trim/AOT, and we have working hand-rolled arg parsing.
- Introducing an `IAnsiConsole` injection layer. Existing tests assert on file I/O and pure helpers — not stdout — so the static `AnsiConsole` API is sufficient.
- Reworking hook commands (`session-start`, `session-end`, `subagent-*`, `stop`, `notification`, `pre-compact`) or any command whose stdout is consumed by another process.

## Context

The CLI currently uses raw `Console.Write` / `Console.ReadLine` with manual `✓`/`✗` glyphs. The two commands that show the UX weakness most sharply:

- **`setup`** — a 5-step wizard (`src/kapacitor/Commands/SetupCommand.cs`) that asks the user to enter a server URL, log in via OAuth, pick default visibility (1/2/3 prompt), pick plugin scope (1/2/3 prompt), and name the daemon. Section headers, choice prompts, and a final summary — all suitable for Spectre primitives.
- **`history`** — a long-running import that discovers transcripts, processes them one by one, and runs background title/summary generation. A recent 1.5K-session import showed the current one-line-per-session output is functional but lacks a sense of progress, and the agent-inline import count only appears as a trailing line instead of surfacing during the work.

## Constraints

1. **AOT / trim safety.** The CLI publishes with `PublishAot=true` and `TrimMode=full`. After wiring Spectre, the publish build must remain clean of `IL2026` and `IL3050` warnings. Spectre.Console (core) is AOT-annotated as of current versions; `Spectre.Console.Cli` is not adopted for exactly this reason.
2. **Non-TTY correctness.** `history` is frequently piped (`| tee import.log`, CI logs, redirected output). `history`'s existing code already inspects `Console.IsInputRedirected` to short-circuit an interactive prompt. We extend the same discipline: when `Console.IsOutputRedirected` is true, fall back to the current plain one-line-per-session output. `setup` already has `--no-prompt` as the non-interactive escape hatch.
3. **Preserve existing `history` output format when piped.** Current lines like `Loading {sid}... {N} lines [new]` and `Skipping {sid} [too short: 4 lines < 10 minimum]` are preserved verbatim in the non-TTY path so existing scripts don't break.
4. **Tests unaffected.** `HistoryCommandTests` and `SetupCommandTests` exercise file I/O and pure helpers (metadata extraction, plugin installer JSON). They do not capture stdout. No injection layer required.

## Dependencies

Add a single package reference to `src/kapacitor/kapacitor.csproj`:

```xml
<PackageReference Include="Spectre.Console" Version="0.*" />
```

Do **not** add `Spectre.Console.Cli`.

## Setup wizard — design

File: `src/kapacitor/Commands/SetupCommand.cs`.

All interactive output routes through `AnsiConsole`. The flag-driven `--no-prompt` path uses the same rendering primitives (Rule, Grid) so CI logs are readable too; only the prompt calls are bypassed.

### Structure

- `AnsiConsole.Write(new FigletText("kapacitor").Color(Color.Green));` — one-time banner at the top (optional; omit if users find it noisy, easy to remove).
- Each step renders a section header: `AnsiConsole.Write(new Rule("[yellow]Step 1/5 — Server[/]").LeftJustified());`.

### Step 1 — Server

- Prompt:
  ```csharp
  serverUrl = AnsiConsole.Prompt(
      new TextPrompt<string>("Capacitor server URL:")
          .Validate(u => !string.IsNullOrWhiteSpace(u), "[red]URL cannot be empty[/]"));
  ```
- Reachability probe inside a status spinner:
  ```csharp
  await AnsiConsole.Status()
      .Spinner(Spinner.Known.Dots)
      .StartAsync("Checking server…", async _ => {
          provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
      });
  AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{provider}[/]");
  ```

### Step 2 — Login

- If `provider == "None"`: `AnsiConsole.MarkupLine("  [dim]Auth provider is None — no login required.[/]");`.
- Otherwise the existing `OAuthLoginFlow.LoginWithDiscoveryAsync` runs. It already writes user-visible output; we don't wrap that in a spinner (the OAuth flow needs the user's attention in a browser). After success: `AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [bold]{tokens.GitHubUsername}[/]");`.

### Step 3 — Default visibility

Replace the numbered prompt with a `SelectionPrompt<string>`:

```csharp
var visibility = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("How should your sessions be visible to others by default?")
        .AddChoices("org_public", "private", "public")
        .UseConverter(v => v switch {
            "private"    => "All private — only you can see your sessions",
            "org_public" => "Org repos public, others private (default)",
            "public"     => "All public — everyone can see all your sessions",
            _            => v
        }));
```

The value stored is the canonical short form (`private` / `org_public` / `public`) — matching the current persisted values so config remains compatible.

### Step 4 — Plugin scope

Same `SelectionPrompt<string>` shape with choices `user`, `project`, `skip`. Converter renders the long-form labels from today's code. Flow below the prompt (install vs. skip) is identical to current.

### Step 5 — Daemon name

`TextPrompt<string>` with `DefaultValue(Environment.UserName.ToLowerInvariant())` and `ShowDefaultValue(true)`.

### Final summary

Replace the trailing series of `Console.WriteLine` calls with a `Grid`:

```csharp
var grid = new Grid().AddColumn().AddColumn();
grid.AddRow("[bold]Server[/]",     serverUrl);
grid.AddRow("[bold]Visibility[/]", visibility);
grid.AddRow("[bold]Daemon[/]",     daemonName);
if (finalTokens is not null)
    grid.AddRow("[bold]Auth[/]",   $"{finalTokens.GitHubUsername} ({finalTokens.Provider})");
grid.AddRow("[bold]Config[/]",     AppConfig.GetConfigPath());

AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());
AnsiConsole.Write(grid);
AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the agent daemon with [cyan]kapacitor agent start -d[/]");
```

### `--no-prompt` behavior

Unchanged at the logic level. Outputs are still rendered through `AnsiConsole` (Rule, Grid) so non-interactive CI output still looks coherent, but no `Prompt()` calls are made. Failure modes (missing required flags) call `AnsiConsole.MarkupLine("[red]  --server-url is required with --no-prompt[/]");` and return non-zero.

## History import — design

File: `src/kapacitor/Commands/HistoryCommand.cs`, with a minor interface change in `src/kapacitor/Commands/SessionImporter.cs`.

### Display model

A single `AnsiConsole.Progress()` block wraps the session loop. Inside it, **one** top-level task is pinned as the footer. The task's `Description` is mutated as work progresses — we do NOT create one task per session (1.5K tasks would be hostile to Spectre and to the terminal scrollback).

Footer description template (where `{sessionId8}` and `{agentId8}` are the first 8 characters of the respective GUID):

```
Importing {done}/{total} · {sessionId8}: {linesDone}/{linesTotal} lines{agentSuffix}
```

`agentSuffix` is empty except while importing a subagent transcript inline, in which case it reads ` ↳ subagent {agentId8}`.

Completion lines are streamed above the pinned footer via `AnsiConsole.MarkupLine`. Formats preserved verbatim from today:

- `Loading {sid}... {linesSent} lines [new]`
- `Skipping {sid} [already loaded]`
- `Skipping {sid} [too short: {n} lines < {min} minimum]`
- `Skipping {sid} [server error: HTTP {code}]`
- `Skipping {sid} [server unreachable: {reason}]`

New streamed lines (replacing today's trailing `"  N agents imported inline"` summary line):

- `  ↳ imported subagent {agentId} ({N} lines)` — emitted once per subagent as it lands, not at the end of the parent session.

### Importer progress hook

Add an optional `IProgress<ImportProgress>? progress` parameter to `SessionImporter.ImportSessionAsync` and thread it into `SendTranscriptBatches` and `SendAgentTranscriptInline`. Events:

```csharp
public abstract record ImportProgress;
public sealed record BatchFlushed(int LinesAdded)                            : ImportProgress;
public sealed record SubagentStarted(string AgentId)                         : ImportProgress;
public sealed record SubagentFinished(string AgentId, int LinesSent)         : ImportProgress;
```

- `BatchFlushed` fires after each 100-line batch flush in `SendTranscriptBatches`.
- `SubagentStarted` fires when `SendAgentTranscriptInline` begins importing a subagent's transcript; `SubagentFinished` fires after the last batch.
- `progress` defaults to `null` so the importer remains usable outside `history` (e.g. any future caller) without forcing console dependencies on it.

The `HistoryCommand` consumer maps events to footer updates and streamed lines:

- `BatchFlushed` → update `task.Description`, incrementing the inner `linesDone` counter.
- `SubagentStarted` → set the agent suffix on the footer description.
- `SubagentFinished` → clear the agent suffix; emit a streamed line `  ↳ imported subagent {id} ({N} lines)`.

### Background phase (titles / summaries)

After the main import loop, if `backgroundTasks.Count > 0`, emit a separator and start a second `Progress` block:

```csharp
AnsiConsole.Write(new Rule($"[dim]── Waiting for {backgroundTasks.Count} background task(s) ──[/]").LeftJustified());
```

Inside this block, two tasks:

- `Titles 0/{N}`
- `Summaries 0/{M}`

Their `Increment(1)` is called from inside the existing `Task.Run` continuations (replacing the `Interlocked.Increment` counter-only tracking today). The counters (`titlesGenerated`, `summariesGenerated`, `titlesFailed`, `summariesFailed`) remain so the final summary line can print them.

**Failures stream individually** below the bar so the user can inspect them — each is a `MarkupLine`:

- `  [red]✗[/] title failed for {sessionId}: {reason}`
- `  [red]✗[/] summary failed for {sessionId}: {reason}`

Successes do not stream (too noisy at scale).

### Final summary

One line (current format, slightly colorized):

```
Done: [green]{loaded}[/] loaded, [green]{resumed}[/] resumed, {skipped} skipped[, [red]{errored}[/] errored]
```

Followed by the title/summary counts only when the background phase ran.

### Non-TTY fallback

When `Console.IsOutputRedirected` is true:

- Skip the Spectre `Progress` block entirely.
- Print the current plain lines (all the `Console.WriteLine` calls already there today), with the only addition being per-subagent completion lines: `  ↳ imported subagent {id} ({N} lines)`.
- The background phase header becomes a plain line: `Waiting for N background tasks (titles/summaries)...`.

This path must remain the default for CI, piped output, and users who prefer the current behavior.

## AOT verification

After implementation, the following must produce zero output (matching current CLAUDE.md guidance):

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

If any `IL2026` / `IL3050` warning appears, the specific Spectre API that triggered it is either replaced with a non-reflection alternative or the feature is dropped.

## Testing

- `HistoryCommandTests` and `SetupCommandTests` continue to pass unchanged (they do not assert on stdout).
- Manual verification:
  - `kapacitor setup` — run interactively, confirm arrow-key selection, status spinner, final grid.
  - `kapacitor setup --no-prompt --server-url https://example.com --default-visibility private --plugin-scope skip` — confirm rendering in a non-TTY context is still coherent.
  - `kapacitor history --min-lines 10` — confirm pinned footer updates, completion lines stream above, subagent `↳` indicator appears live, final summary matches today's counts.
  - `kapacitor history --min-lines 10 > out.log` — confirm the fallback path emits today's plain lines.
  - `kapacitor history --min-lines 10 --generate-summaries` — confirm background phase renders its own progress block and failures stream.

## Out of scope (explicit)

- `eval`, `review`, `status`, `repos`, `profile`, `config`, `agent start` — not touched in this change. Candidates for a follow-up scoped ticket if this pattern works well.
- Introducing a shared `IConsoleUx` abstraction.
- Localization / colorblind palette switches (Spectre respects `NO_COLOR` by default, which is sufficient for now).
