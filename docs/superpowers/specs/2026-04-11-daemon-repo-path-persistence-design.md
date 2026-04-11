# Daemon Repo Path Persistence

## Problem

When the Capacitor server restarts, the in-memory `DaemonRegistry` loses all daemon registrations. Daemons reconnect and re-register, but the `RepoPaths` array they send is built solely from `DaemonConfig.AllowedRepoPaths`, which defaults to empty. This means the agent launch dialog shows no repo paths until agents have been manually configured with allowed paths.

Even without server restarts, new daemon instances start with an empty repo list unless the user has configured `AllowedRepoPaths` — which most users don't do.

## Design

Persist known repo paths on the daemon side in `~/.config/kapacitor/repos.json`. The daemon merges this persisted list with its configured `AllowedRepoPaths` when connecting to the server, so the launch dialog always shows previously-used repos. New repos are auto-added on successful agent launch.

### Data model

`~/.config/kapacitor/repos.json`:

```json
[
  { "path": "/Users/alexey/dev/eventstore/kapacitor-server", "last_used": "2026-04-11T14:30:00Z" },
  { "path": "/Users/alexey/dev/eventstore/some-other-repo", "last_used": "2026-04-10T09:15:00Z" }
]
```

Each entry has an absolute `path` and a `last_used` UTC timestamp. The list is sorted by `last_used` descending (most recently used first) when sent to the server, so the launch dialog shows the most relevant repos at the top.

### Separation of concerns

`AllowedRepoPaths` (from daemon config) remains a **security whitelist** — when non-empty, only listed paths can be launched. The persisted repo store is a separate **known repos list** for the UI. They are merged for the `RepoPaths` sent to the server but `IsRepoAllowed()` only checks `AllowedRepoPaths`, unchanged.

### Components

#### `RepoPathStore` (`Config/RepoPathStore.cs`)

Static class following existing patterns (`TokenStore`, `AppConfig`). AOT-safe with source-generated JSON serialization.

- `LoadAsync()` → `RepoEntry[]` — reads and deserializes the file, returns empty array if missing/malformed
- `AddAsync(string path)` → adds entry with `last_used = UtcNow` if not already present; if present, updates `last_used`. Writes file.
- `RemoveAsync(string path)` → removes entry if present. Writes file.

Path normalization: `Path.GetFullPath()` + trim trailing directory separators. Deduplication is case-insensitive on macOS/Windows, case-sensitive on Linux (using `StringComparison` based on `RuntimeInformation`).

`RepoEntry` record: `{ string Path, DateTimeOffset LastUsed }` with `[JsonPropertyName]` attributes. Registered in `KapacitorJsonContext` for AOT.

#### Daemon integration

**`ServerConnection.RegisterDaemon()`** — currently a sync method; becomes async to call `RepoPathStore.LoadAsync()`. Merges `AllowedRepoPaths` with persisted repos. Union, deduplicate, sort by `last_used` descending (config-only paths get `DateTimeOffset.MinValue` so they sort last). Send merged list as `RepoPaths` in `DaemonConnect`.

**`AgentOrchestrator.HandleLaunchAgent()`** — after successful spawn, fire-and-forget `RepoPathStore.AddAsync(repoPath)`. Then call a new `ServerConnection.UpdateRepoPathsAsync()` method that sends the refreshed list to the server via a new `DaemonUpdateRepoPaths` hub method, so the UI updates immediately without requiring a reconnect.

#### Server-side hub method

**`CapacitorHub.DaemonUpdateRepoPaths(string[] repoPaths)`** — updates the daemon's `RepoPaths` in `DaemonRegistry` and broadcasts `DaemonsChanged`. `DaemonRegistry` gets an `UpdateRepoPaths(string connectionId, string[] repoPaths)` method that replaces paths on the existing entry.

#### CLI command (`Commands/ReposCommand.cs`)

```
kapacitor repos                  # list known repos (sorted by last_used desc)
kapacitor repos add <path>       # add repo (resolves relative paths, supports ".")
kapacitor repos remove <path>    # remove repo
```

Added to `Program.cs` switch statement and `offlineCommands` array (no server connection needed).

`kapacitor repos add .` resolves `.` to the current working directory via `Path.GetFullPath(".")`.

Output format for `kapacitor repos`:
```
/Users/alexey/dev/eventstore/kapacitor-server   (last used: 2h ago)
/Users/alexey/dev/eventstore/some-other-repo    (last used: 1d ago)
```

### Files changed

**CLI repo (`kurrent-io/kapacitor`):**
- `src/kapacitor/Config/RepoPathStore.cs` — new: persistence logic
- `src/kapacitor/Commands/ReposCommand.cs` — new: CLI command
- `src/kapacitor/Models.cs` — add `RepoEntry` record and register in `KapacitorJsonContext`
- `src/kapacitor/Program.cs` — add `repos` command routing + `offlineCommands`
- `src/kapacitor/Daemon/Services/ServerConnection.cs` — merge persisted repos in `RegisterDaemon()`, add `UpdateRepoPathsAsync()`
- `src/kapacitor/Daemon/Services/AgentOrchestrator.cs` — auto-add repo on launch, notify server

**Server repo (`kurrent-io/kapacitor-server`):**
- `src/Kurrent.Capacitor/Sessions/CapacitorHub.cs` — add `DaemonUpdateRepoPaths` method
- `src/Kurrent.Capacitor/Agents/DaemonRegistry.cs` — add `UpdateRepoPaths` method

### What doesn't change

- `DaemonConfig.AllowedRepoPaths` — still config-only, still controls `IsRepoAllowed()`
- `DaemonConfig.IsRepoAllowed()` — unchanged security check
- `DaemonConnect` record — unchanged (already carries `string[] RepoPaths`)
- Server-side `DaemonRegistry` — remains in-memory, no server-side persistence
