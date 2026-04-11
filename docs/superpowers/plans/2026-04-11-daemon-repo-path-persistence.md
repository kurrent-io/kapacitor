# Daemon Repo Path Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist known repo paths on the daemon side so the agent launch dialog always shows previously-used repos, even after server/daemon restarts.

**Architecture:** A new `RepoPathStore` static class persists repo paths to `~/.config/kapacitor/repos.json`. The daemon merges persisted repos with configured `AllowedRepoPaths` when connecting. A new `kapacitor repos` CLI command manages the list manually. Auto-add on successful agent launch with live UI update via a new `DaemonUpdateRepoPaths` hub method.

**Tech Stack:** C# / .NET 10, AOT-safe source-generated JSON, SignalR

**Repos:** CLI changes in `/Users/alexey/dev/eventstore/kapacitor`, server changes in `/Users/alexey/dev/eventstore/kapacitor-server`

---

## File Structure

**CLI repo (`/Users/alexey/dev/eventstore/kapacitor`):**

| File | Action | Responsibility |
|------|--------|---------------|
| `src/kapacitor/Config/RepoPathStore.cs` | Create | Read/write `repos.json`, path normalization, add/remove/load |
| `src/kapacitor/Commands/ReposCommand.cs` | Create | `kapacitor repos [add|remove]` CLI command |
| `src/kapacitor/Resources/help-repos.txt` | Create | Help text for `kapacitor repos --help` |
| `src/kapacitor/Models.cs` | Modify | Add `RepoEntry` record, register in `KapacitorJsonContext` |
| `src/kapacitor/Program.cs` | Modify | Add `repos` to command routing and `offlineCommands` |
| `src/kapacitor/Daemon/Services/ServerConnection.cs` | Modify | Merge persisted repos in `RegisterDaemon()`, add `UpdateRepoPathsAsync()` |
| `src/kapacitor/Daemon/Services/AgentOrchestrator.cs` | Modify | Auto-add repo on launch, notify server |

**Server repo (`/Users/alexey/dev/eventstore/kapacitor-server`):**

| File | Action | Responsibility |
|------|--------|---------------|
| `src/Kurrent.Capacitor/Agents/DaemonRegistry.cs` | Modify | Add `UpdateRepoPaths()` method |
| `src/Kurrent.Capacitor/Sessions/CapacitorHub.cs` | Modify | Add `DaemonUpdateRepoPaths()` hub method |

---

## Task 1: Add `RepoEntry` model and JSON context

**Files:**
- Modify: `src/kapacitor/Models.cs`

- [ ] **Step 1: Add `RepoEntry` record**

In `src/kapacitor/Models.cs`, add the record before the `KapacitorJsonContext` class (around line 209):

```csharp
record RepoEntry {
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("last_used")]
    public required DateTimeOffset LastUsed { get; init; }
}
```

- [ ] **Step 2: Register in `KapacitorJsonContext`**

In `src/kapacitor/Models.cs`, add to the `KapacitorJsonContext` attributes (before the `[JsonSourceGenerationOptions]` line at line 243):

```csharp
[JsonSerializable(typeof(RepoEntry[]))]
```

- [ ] **Step 3: Verify AOT build**

Run: `dotnet publish src/kapacitor/src/kapacitor/kapacitor.csproj -c Release`
Expected: No IL3050/IL2026 warnings related to `RepoEntry`.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Models.cs
git commit -m "feat: add RepoEntry model for repo path persistence"
```

---

## Task 2: Create `RepoPathStore`

**Files:**
- Create: `src/kapacitor/Config/RepoPathStore.cs`

- [ ] **Step 1: Create the file**

Create `src/kapacitor/Config/RepoPathStore.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text.Json;

namespace kapacitor.Config;

public static class RepoPathStore {
    static readonly string StorePath = PathHelpers.ConfigPath("repos.json");

    static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static async Task<RepoEntry[]> LoadAsync() {
        if (!File.Exists(StorePath))
            return [];

        try {
            var json = await File.ReadAllTextAsync(StorePath);
            return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.RepoEntryArray) ?? [];
        } catch {
            return [];
        }
    }

    public static async Task AddAsync(string path) {
        var normalized = NormalizePath(path);
        var entries    = (await LoadAsync()).ToList();
        var existing   = entries.FindIndex(e => string.Equals(e.Path, normalized, PathComparison));

        if (existing >= 0) {
            entries[existing] = entries[existing] with { LastUsed = DateTimeOffset.UtcNow };
        } else {
            entries.Add(new RepoEntry { Path = normalized, LastUsed = DateTimeOffset.UtcNow });
        }

        await SaveAsync(entries);
    }

    public static async Task<bool> RemoveAsync(string path) {
        var normalized = NormalizePath(path);
        var entries    = (await LoadAsync()).ToList();
        var removed    = entries.RemoveAll(e => string.Equals(e.Path, normalized, PathComparison));

        if (removed == 0) return false;

        await SaveAsync(entries);
        return true;
    }

    static async Task SaveAsync(List<RepoEntry> entries) {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{StorePath}.tmp";
        var sorted   = entries.OrderByDescending(e => e.LastUsed).ToArray();
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(sorted, KapacitorJsonContext.Default.RepoEntryArray));
        File.Move(tempPath, StorePath, overwrite: true);
    }

    /// <summary>
    /// Returns all persisted repo paths sorted by last_used descending.
    /// </summary>
    public static async Task<string[]> GetSortedPathsAsync() {
        var entries = await LoadAsync();
        return entries.OrderByDescending(e => e.LastUsed).Select(e => e.Path).ToArray();
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/kapacitor/src/kapacitor/kapacitor.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/kapacitor/Config/RepoPathStore.cs
git commit -m "feat: add RepoPathStore for persisting known repo paths"
```

---

## Task 3: Create `kapacitor repos` CLI command

**Files:**
- Create: `src/kapacitor/Commands/ReposCommand.cs`
- Create: `src/kapacitor/Resources/help-repos.txt`
- Modify: `src/kapacitor/Program.cs`

- [ ] **Step 1: Create help text**

Create `src/kapacitor/Resources/help-repos.txt`:

```
Usage: kapacitor repos [add|remove] [path]

Manage known repository paths for the agent daemon launch dialog.

Subcommands:
  (none)         List known repos (sorted by last used)
  add <path>     Add a repo path (supports "." for current directory)
  remove <path>  Remove a repo path

Examples:
  kapacitor repos              # list known repos
  kapacitor repos add .        # add current directory
  kapacitor repos add ~/dev/my-project
  kapacitor repos remove ~/dev/old-project
```

- [ ] **Step 2: Create `ReposCommand.cs`**

Create `src/kapacitor/Commands/ReposCommand.cs`:

```csharp
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ReposCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2)
            return await List();

        var subcommand = args[1];

        return subcommand switch {
            "add" when args.Length >= 3    => await Add(args[2]),
            "add"                          => PrintAddUsage(),
            "remove" when args.Length >= 3 => await Remove(args[2]),
            "remove"                       => PrintRemoveUsage(),
            _                              => PrintUsage()
        };
    }

    static async Task<int> List() {
        var entries = await RepoPathStore.LoadAsync();

        if (entries.Length == 0) {
            await Console.Out.WriteLineAsync("No known repos. Use `kapacitor repos add .` to add the current directory.");
            return 0;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var entry in entries.OrderByDescending(e => e.LastUsed)) {
            var ago = FormatTimeAgo(now - entry.LastUsed);
            await Console.Out.WriteLineAsync($"  {entry.Path}   ({ago})");
        }

        return 0;
    }

    static async Task<int> Add(string path) {
        var resolved = Path.GetFullPath(path);

        if (!Directory.Exists(resolved)) {
            Console.Error.WriteLine($"Directory does not exist: {resolved}");
            return 1;
        }

        await RepoPathStore.AddAsync(resolved);
        await Console.Out.WriteLineAsync($"Added: {resolved}");

        return 0;
    }

    static async Task<int> Remove(string path) {
        var resolved = Path.GetFullPath(path);
        var removed  = await RepoPathStore.RemoveAsync(resolved);

        if (removed) {
            await Console.Out.WriteLineAsync($"Removed: {resolved}");
        } else {
            Console.Error.WriteLine($"Not found: {resolved}");
            return 1;
        }

        return 0;
    }

    static string FormatTimeAgo(TimeSpan elapsed) {
        if (elapsed.TotalMinutes < 1)  return "just now";
        if (elapsed.TotalHours   < 1)  return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays    < 1)  return $"{(int)elapsed.TotalHours}h ago";
        if (elapsed.TotalDays    < 30) return $"{(int)elapsed.TotalDays}d ago";

        return $"{(int)(elapsed.TotalDays / 30)}mo ago";
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor repos [add|remove] [path]");
        return 1;
    }

    static int PrintAddUsage() {
        Console.Error.WriteLine("Usage: kapacitor repos add <path>");
        return 1;
    }

    static int PrintRemoveUsage() {
        Console.Error.WriteLine("Usage: kapacitor repos remove <path>");
        return 1;
    }
}
```

- [ ] **Step 3: Wire up in `Program.cs`**

In `src/kapacitor/Program.cs`, add `"repos"` to the `offlineCommands` array (line 56):

```csharp
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update", "plugin", "profile", "use", "repos"];
```

Add the `repos` case in the `switch (command)` block (after the `config` case around line 177):

```csharp
    case "repos":
        return await ReposCommand.HandleAsync(args);
```

- [ ] **Step 4: Verify it compiles and runs**

Run: `dotnet build src/kapacitor/src/kapacitor/kapacitor.csproj && dotnet run --project src/kapacitor/src/kapacitor/kapacitor.csproj -- repos`
Expected: "No known repos. Use `kapacitor repos add .` to add the current directory."

- [ ] **Step 5: Test the add and list flow**

Run:
```bash
dotnet run --project src/kapacitor/src/kapacitor/kapacitor.csproj -- repos add .
dotnet run --project src/kapacitor/src/kapacitor/kapacitor.csproj -- repos
```
Expected: Shows current directory with "just now".

- [ ] **Step 6: Test the remove flow**

Run:
```bash
dotnet run --project src/kapacitor/src/kapacitor/kapacitor.csproj -- repos remove .
dotnet run --project src/kapacitor/src/kapacitor/kapacitor.csproj -- repos
```
Expected: "Removed: /path/to/cwd" then "No known repos."

- [ ] **Step 7: Commit**

```bash
git add src/kapacitor/Commands/ReposCommand.cs src/kapacitor/Resources/help-repos.txt src/kapacitor/Program.cs
git commit -m "feat: add kapacitor repos command for managing known repo paths"
```

---

## Task 4: Merge persisted repos in daemon `RegisterDaemon()`

**Files:**
- Modify: `src/kapacitor/Daemon/Services/ServerConnection.cs`

- [ ] **Step 1: Add `using` for `RepoPathStore`**

In `src/kapacitor/Daemon/Services/ServerConnection.cs`, add at the top:

```csharp
using kapacitor.Config;
```

- [ ] **Step 2: Update `RegisterDaemon()` to merge persisted repos**

Replace the `RegisterDaemon()` method (lines 108-116) with:

```csharp
    async Task RegisterDaemon() {
        var platform = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";

        var repoPaths = await MergeRepoPathsAsync();

        await _hub.InvokeAsync(
            "DaemonConnect",
            new DaemonConnect(_config.Name, platform, repoPaths, _config.MaxConcurrentAgents),
            cancellationToken: _ct
        );
    }

    async Task<string[]> MergeRepoPathsAsync() {
        var persisted = await RepoPathStore.GetSortedPathsAsync();

        if (_config.AllowedRepoPaths.Length == 0)
            return persisted;

        // Union: persisted paths first (sorted by last_used desc), then config-only paths
        var seen   = new HashSet<string>(persisted, StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(persisted);

        foreach (var p in _config.AllowedRepoPaths) {
            var clean = p.TrimEnd('/', '*');

            if (seen.Add(clean))
                merged.Add(clean);
        }

        return merged.ToArray();
    }
```

- [ ] **Step 3: Add `UpdateRepoPathsAsync()` method**

Add this method to `ServerConnection` (after `SendHeartbeatAsync`, around line 127):

```csharp
    public async Task UpdateRepoPathsAsync() {
        try {
            var repoPaths = await MergeRepoPathsAsync();
            await _hub.InvokeAsync("DaemonUpdateRepoPaths", repoPaths, cancellationToken: _ct);
        } catch (Exception ex) {
            LogRepoPathUpdateFailed(ex);
        }
    }
```

Add the log method with the other `LoggerMessage` methods at the bottom of the class:

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update repo paths on server")]
    partial void LogRepoPathUpdateFailed(Exception ex);
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build src/kapacitor/src/kapacitor/kapacitor.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Daemon/Services/ServerConnection.cs
git commit -m "feat: merge persisted repo paths in daemon RegisterDaemon"
```

---

## Task 5: Auto-add repo on agent launch

**Files:**
- Modify: `src/kapacitor/Daemon/Services/AgentOrchestrator.cs`

- [ ] **Step 1: Add `using` for `RepoPathStore`**

In `src/kapacitor/Daemon/Services/AgentOrchestrator.cs`, add at the top:

```csharp
using kapacitor.Config;
```

- [ ] **Step 2: Add auto-add after successful spawn**

In `HandleLaunchAgent()`, after the `await _server.AgentRegisteredAsync(...)` call (line 215), add:

```csharp
            // Persist repo path and notify server so launch dialog updates
            _ = Task.Run(async () => {
                try {
                    await RepoPathStore.AddAsync(repoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agentId);
                }
            });
```

Add the log method with the other `LoggerMessage` methods at the bottom of the class:

```csharp
    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/kapacitor/src/kapacitor/kapacitor.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Daemon/Services/AgentOrchestrator.cs
git commit -m "feat: auto-persist repo path on successful agent launch"
```

---

## Task 6: Server-side `DaemonUpdateRepoPaths` hub method

**Files:**
- Modify: `src/Kurrent.Capacitor/Agents/DaemonRegistry.cs` (in kapacitor-server repo)
- Modify: `src/Kurrent.Capacitor/Sessions/CapacitorHub.cs` (in kapacitor-server repo)

Note: These files are in the **server repo** at `/Users/alexey/dev/eventstore/kapacitor-server`.

- [ ] **Step 1: Add `UpdateRepoPaths` to `DaemonRegistry`**

In `DaemonRegistry.cs`, add this method after the `Touch` method (after line 63):

```csharp
    public void UpdateRepoPaths(string connectionId, string[] repoPaths) {
        if (_byConnectionId.TryGetValue(connectionId, out var entry)) {
            _byConnectionId[connectionId] = entry with { RepoPaths = repoPaths };
            OnChanged?.Invoke();
        }
    }
```

- [ ] **Step 2: Add `DaemonUpdateRepoPaths` hub method**

In `CapacitorHub.cs`, add this method after the `DaemonConnect` method (after line 323):

```csharp
    /// <summary>Called by daemon to update its advertised repo paths (e.g. after a new repo is used).</summary>
    public async Task DaemonUpdateRepoPaths(string[] repoPaths) {
        daemonRegistry.UpdateRepoPaths(Context.ConnectionId, repoPaths);
        await Clients.All.SendAsync("DaemonsChanged");
    }
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/Kurrent.Capacitor/Kurrent.Capacitor.csproj` (from kapacitor-server repo root)
Expected: Build succeeded.

- [ ] **Step 4: Commit (in kapacitor-server repo)**

```bash
git add src/Kurrent.Capacitor/Agents/DaemonRegistry.cs src/Kurrent.Capacitor/Sessions/CapacitorHub.cs
git commit -m "feat: add DaemonUpdateRepoPaths hub method for live repo list updates"
```

---

## Task 7: Verify AOT publish and end-to-end

- [ ] **Step 1: AOT publish CLI**

Run: `dotnet publish src/kapacitor/src/kapacitor/kapacitor.csproj -c Release`
Expected: No IL3050/IL2026 warnings related to `RepoEntry` or `RepoPathStore`.

- [ ] **Step 2: Install and test CLI**

```bash
cp src/kapacitor/src/kapacitor/bin/Release/net10.0/osx-arm64/publish/kapacitor ~/.local/bin/kapacitor
codesign --force --sign - ~/.local/bin/kapacitor
kapacitor repos
kapacitor repos add .
kapacitor repos
kapacitor repos remove .
```
Expected: List/add/remove all work correctly.

- [ ] **Step 3: Test daemon integration**

Start the daemon and verify it reports persisted repos on connect:

```bash
kapacitor repos add /Users/alexey/dev/eventstore/kapacitor-server
kapacitor agent stop 2>/dev/null; kapacitor agent start
```

Check the daemon log for the `DaemonConnect` message and verify the server's launch dialog shows the repo.

- [ ] **Step 4: Commit any fixes if needed**
