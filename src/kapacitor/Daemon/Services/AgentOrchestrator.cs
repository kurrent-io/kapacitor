using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using kapacitor.Auth;
using kapacitor.Config;
using kapacitor.Daemon.Pty;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

public record AgentInstance(
        string                  Id,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string                  RepoPath,
        IPtyProcess             Process,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    public string?              SessionId    { get; set; }
    public string               Status       { get; set; } = "Starting";
    public DateTime             CreatedAt    { get; }      = DateTime.UtcNow;
    public DateTime             LastOutputAt { get; set; } = DateTime.UtcNow;
    public TerminalOutputBuffer OutputBuffer { get; }      = new();
}

/// <summary>Ring buffer that keeps the last 2 MB of terminal output.</summary>
public class TerminalOutputBuffer {
    readonly List<byte[]> _chunks = [];
    int                   _totalBytes;
    const int             MaxBytes = 2 * 1024 * 1024;

    public void Append(byte[] data) {
        lock (_chunks) {
            _chunks.Add(data);
            _totalBytes += data.Length;

            while (_totalBytes > MaxBytes && _chunks.Count > 1) {
                _totalBytes -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    public List<byte[]> GetAll() {
        lock (_chunks) { return [.._chunks]; }
    }
}

internal partial class AgentOrchestrator : IAsyncDisposable {
    readonly ConcurrentDictionary<string, AgentInstance> _agents = new();
    readonly DaemonConfig                                _config;
    readonly ServerConnection                            _server;
    readonly WorktreeManager                             _worktreeManager;
    readonly IPtyProcessFactory                          _ptyFactory;
    readonly IHttpClientFactory                          _httpClientFactory;
    readonly ILogger<AgentOrchestrator>                  _logger;
    readonly PeriodicTimer                               _heartbeatTimer  = new(TimeSpan.FromSeconds(30));
    readonly PeriodicTimer                               _daemonHeartbeat = new(TimeSpan.FromMinutes(1));
    readonly CancellationTokenSource                     _shutdownCts     = new();

    public AgentOrchestrator(
            DaemonConfig               config,
            ServerConnection           server,
            WorktreeManager            worktreeManager,
            IPtyProcessFactory         ptyFactory,
            IHttpClientFactory         httpClientFactory,
            ILogger<AgentOrchestrator> logger
        ) {
        _config            = config;
        _server            = server;
        _worktreeManager   = worktreeManager;
        _ptyFactory        = ptyFactory;
        _httpClientFactory = httpClientFactory;
        _logger            = logger;

        // Wire up server commands
        _server.OnLaunchAgent         += HandleLaunchAgent;
        _server.OnStopAgent           += HandleStopAgent;
        _server.OnSendInput           += HandleSendInput;
        _server.OnSendSpecialKey      += HandleSendSpecialKey;
        _server.OnResizeTerminal      += HandleResizeTerminal;
        _server.OnReconnectedCallback += ReRegisterAgents;

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
    }

    int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var (agentId, prompt, model, effort, repoPath, tools, attachmentIds) = cmd;

        WorktreeInfo? worktree = null;

        try {
            if (ActiveCount >= _config.MaxConcurrentAgents) {
                await _server.LaunchFailedAsync(agentId, $"At max capacity ({_config.MaxConcurrentAgents} agents)");

                return;
            }

            if (!_config.IsRepoAllowed(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path not allowed: {repoPath}");

                return;
            }

            if (!Directory.Exists(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path does not exist: {repoPath}");

                return;
            }

            // "auto" means let the CLI decide — don't pass --effort at all
            if (string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
                effort = null;
            }

            // Validate effort level before expensive worktree setup
            if (!string.IsNullOrEmpty(effort) && !ValidEffortLevels.Contains(effort)) {
                await _server.LaunchFailedAsync(agentId, $"Invalid effort level: {effort}");

                return;
            }

            LogLaunching(agentId, repoPath, effort ?? "default", model);

            worktree = await _worktreeManager.CreateAsync(repoPath);

            // Overlay .claude/ local settings from source repo into worktree.
            // git worktree add creates tracked files (e.g. skills/), but gitignored
            // local files (.local.md, settings.local.json, MCP configs) are missing.
            // We merge them in without overwriting tracked files.
            // Best-effort: filesystem errors here should not block agent launch.
            try {
                var sourceClaudeDir = Path.Combine(repoPath, ".claude");
                var destClaudeDir   = Path.Combine(worktree.Path, ".claude");

                if (Directory.Exists(sourceClaudeDir)) {
                    OverlayDirectory(sourceClaudeDir, destClaudeDir);
                }

                // Symlink ~/.claude/projects/{worktree-path} → ~/.claude/projects/{source-path}
                // so that project-level permissions and settings carry over.
                SymlinkClaudeProjectDir(repoPath, worktree.Path);
            } catch (Exception ex) {
                LogOverlayFailed(ex, agentId);
            }

            // Write .mcp.json into the worktree so Claude discovers MCP servers.
            // Claude stores per-project MCP configs in ~/.claude.json keyed by the
            // literal CWD path, so worktrees don't inherit them. Writing a repo-level
            // .mcp.json is the simplest way to propagate them.
            try {
                WriteMcpConfig(repoPath, worktree.Path);
            } catch (Exception ex) {
                LogMcpConfigFailed(ex, agentId);
            }

            // Pre-trust the worktree path in ~/.claude.json. Without this, claude
            // blocks on the "Do you trust the files in this folder?" dialog for any
            // new cwd — and since the daemon drives claude over a PTY with no
            // interactive user, it would hang forever.
            try {
                TrustWorktreeInClaudeConfig(worktree.Path);
            } catch (Exception ex) {
                LogTrustWorktreeFailed(ex, agentId);
            }

            // Merge dialog-selected tools into the worktree's settings.local.json
            // instead of using --allowedTools (which overrides project permissions).
            // Best-effort: filesystem/JSON errors should not block agent launch.
            try {
                if (tools is { Length: > 0 }) {
                    MergeToolPermissions(worktree.Path, tools);
                }
            } catch (Exception ex) {
                LogToolPermissionsFailed(ex, agentId);
            }

            // Download attachments into worktree (best-effort)
            if (attachmentIds is { Length: > 0 }) {
                try {
                    var paths = await DownloadAttachmentsAsync(worktree.Path, attachmentIds);

                    if (paths.Count > 0) {
                        var suffix = $"\n\n[Attached files: {string.Join(", ", paths)}]";
                        prompt = string.IsNullOrEmpty(prompt) ? suffix.TrimStart() : prompt + suffix;
                    }
                } catch (Exception ex) {
                    LogAttachmentDownloadFailed(ex, agentId);
                }
            }

            // Build claude CLI args
            var args = new List<string>();

            if (!string.IsNullOrEmpty(effort)) {
                args.Add("--effort");
                args.Add(effort);
            }

            if (!string.IsNullOrEmpty(model)) {
                args.Add("--model");
                args.Add(model);
            }

            if (!string.IsNullOrEmpty(prompt)) {
                args.Add("--");
                args.Add(prompt);
            }

            var env = new Dictionary<string, string> {
                ["KAPACITOR_RENDERED_AGENT"] = "1",
                ["KAPACITOR_AGENT_ID"]       = agentId
            };

            if (!string.IsNullOrEmpty(_config.ServerUrl)) {
                env["KAPACITOR_URL"] = _config.ServerUrl;
            }

            var process = _ptyFactory.Spawn(_config.ClaudePath, args.ToArray(), worktree.Path, env);

            LogAgentSpawned(agentId, process.Pid, worktree.Path, _config.ClaudePath);

            var cts   = new CancellationTokenSource();
            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, process, worktree, cts);
            _agents[agentId] = agent;

            // Notify server
            await _server.AgentRegisteredAsync(agentId, prompt, model, effort, repoPath);

            _ = _server.AppendAgentRunEventAsync(
                agentId,
                new AgentRunStarted(prompt, model, effort, repoPath, worktree.Path)
            );

            // Persist repo path and notify server so launch dialog updates
            _ = Task.Run(async () => {
                try {
                    await RepoPathStore.AddAsync(repoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agentId);
                }
            });

            // Start reading output
            _ = ReadAgentOutputAsync(agent);
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            // Clean up worktree if it was created but agent didn't start
            if (worktree != null) {
                try { RemoveClaudeProjectSymlink(worktree.Path); } catch {
                    /* best-effort */
                }

                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }
            }

            await _server.LaunchFailedAsync(agentId, ex.Message);
        }
    }

    async Task ReadAgentOutputAsync(AgentInstance agent) {
        try {
            await foreach (var data in agent.Process.ReadOutputAsync(agent.ReadCts.Token)) {
                agent.LastOutputAt = DateTime.UtcNow;

                if (agent.Status == "Starting") {
                    agent.Status = "Running";
                    _            = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
                }

                agent.OutputBuffer.Append(data);
                var base64 = Convert.ToBase64String(data);
                _ = _server.SendTerminalOutputAsync(agent.Id, base64);
            }
        } catch (OperationCanceledException) {
            /* expected on stop */
        } catch (Exception ex) {
            LogOutputReadError(ex, agent.Id);
        } finally {
            try {
                // PTY output can end before waitpid reports the child as exited.
                // Wait briefly for the process to finalize so we get a real exit code.
                await agent.Process.WaitForExitAsync(TimeSpan.FromSeconds(5));

                var exitCode = agent.Process.ExitCode;

                var status = agent.Process.HasExited
                    ? exitCode is null or 0 ? "Completed" : "Failed"
                    : "Failed";
                var isEarlyExit = DateTime.UtcNow - agent.CreatedAt < EarlyExitWindow;

                if (agent.Status is not "Completed" and not "Failed") {
                    // If the process died shortly after spawn, treat it as a launch
                    // failure regardless of exit code. A process that exits within
                    // EarlyExitWindow never established a session, so even exit code 0
                    // means something went wrong (e.g., CLI config error, auth issue).
                    // We use elapsed time rather than status because the first output
                    // chunk flips status to "Running" before the process exits.
                    if (isEarlyExit) {
                        var output = ExtractTerminalText(agent.OutputBuffer);

                        var reason = !string.IsNullOrWhiteSpace(output)
                            ? output
                            : exitCode is null or 0
                                ? "Process exited before establishing a session"
                                : $"Process exited immediately (exit code {exitCode})";

                        status = "Failed";

                        LogStartupFailed(agent.Id, exitCode, reason);

                        _ = _server.LaunchFailedAsync(agent.Id, reason);
                    }

                    agent.Status = status;
                    _            = _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    _ = _server.AppendAgentRunEventAsync(
                        agent.Id,
                        new AgentRunStopped(stopReason, exitCode)
                    );
                }

                LogAgentExited(agent.Id, exitCode);

                // Clean up worktree and unregister from server
                await CleanupAgentAsync(agent.Id);
            } catch (Exception ex) {
                LogCleanupError(ex, agent.Id);
            }
        }
    }

    async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        try {
            LogStopping(agentId);

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            agent.Status = "Completed";
            _            = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
            _            = _server.AppendAgentRunEventAsync(agentId, new AgentRunStopped("user", null));

            // Cancel the read loop, then terminate the process.
            // The read loop's finally block will handle CleanupAgentAsync.
            await agent.ReadCts.CancelAsync();
            await agent.Process.TerminateAsync(TimeSpan.FromSeconds(10));
        } catch (Exception ex) {
            LogStopError(ex, agentId);
        }
    }

    async Task HandleSendInput(SendInputCommand cmd) {
        var (agentId, text, attachmentIds) = cmd;

        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        var message = text;

        if (attachmentIds is { Length: > 0 }) {
            var paths = await DownloadAttachmentsAsync(agent.Worktree.Path, attachmentIds);

            if (paths.Count > 0) {
                message = $"{text}\n\n[Attached files: {string.Join(", ", paths)}]";
            }
        }

        // Split text and Enter with delay (Claude CLI needs separate writes)
        await agent.Process.WriteAsync(message);
        await Task.Delay(50);
        await agent.Process.WriteAsync("\r");
    }

    async Task HandleSendSpecialKey(string agentId, string key) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        byte[] bytes = key switch {
            "Escape" => [0x1b],
            "Tab"    => [0x09],
            _        => []
        };

        if (bytes.Length > 0) {
            await agent.Process.WriteAsync(bytes);
        }
    }

    async Task<List<string>> DownloadAttachmentsAsync(string worktreePath, string[] attachmentIds) {
        var attachDir = Path.Combine(worktreePath, ".attached");
        Directory.CreateDirectory(attachDir);

        // Write .gitignore to prevent accidental commits
        var gitignorePath = Path.Combine(attachDir, ".gitignore");

        if (!File.Exists(gitignorePath)) {
            await File.WriteAllTextAsync(gitignorePath, "*\n");
        }

        var paths = new List<string>();

        foreach (var id in attachmentIds) {
            try {
                using var httpClient = _httpClientFactory.CreateClient("Attachments");

                var tokens = await TokenStore.GetValidTokensAsync();

                if (tokens is not null) {
                    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
                }

                var response = await httpClient.GetAsync($"/api/attachments/{id}");

                if (!response.IsSuccessStatusCode) {
                    LogAttachmentNotFound(id, response.StatusCode);

                    continue;
                }

                var rawFileName = response.Content.Headers.ContentDisposition?.FileNameStar
                 ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                 ?? $"attachment-{id[..8]}";

                // Sanitize: strip path separators to prevent directory traversal
                var fileName = Path.GetFileName(rawFileName);

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"attachment-{id[..8]}";

                var filePath = GetUniqueFilePath(attachDir, fileName);
                var fullPath = Path.GetFullPath(filePath);
                var safeDir  = Path.GetFullPath(attachDir) + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(safeDir)) {
                    LogAttachmentPathEscape(rawFileName);

                    continue;
                }

                await using var fs = File.Create(filePath);
                await response.Content.CopyToAsync(fs);

                paths.Add($".attached/{Path.GetFileName(filePath)}");
            } catch (Exception ex) {
                LogAttachmentError(ex, id);
            }
        }

        return paths;
    }

    static string GetUniqueFilePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path)) {
            return path;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        var counter        = 2;

        do {
            path = Path.Combine(directory, $"{nameWithoutExt}-{counter}{ext}");
            counter++;
        } while (File.Exists(path));

        return path;
    }

    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        if (_agents.TryGetValue(cmd.AgentId, out var agent)) {
            agent.Process.Resize((ushort)cmd.Cols, (ushort)cmd.Rows);
        }

        return Task.CompletedTask;
    }

    void ReRegisterAgents() {
        _ = ReRegisterAgentsAsync();

        return;

        async Task ReRegisterAgentsAsync() {
            foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
                try {
                    await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath);
                    await _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId);

                    // Replay terminal buffer so the UI has terminal history
                    foreach (var base64 in agent.OutputBuffer.GetAll().Select(Convert.ToBase64String)) {
                        await _server.SendTerminalOutputAsync(agent.Id, base64);
                    }
                } catch (Exception ex) {
                    LogReRegisterFailed(ex, agent.Id);
                }
            }
        }
    }

    static readonly TimeSpan              StartupTimeout    = TimeSpan.FromSeconds(90);
    static readonly TimeSpan              EarlyExitWindow   = TimeSpan.FromSeconds(30);
    static readonly JsonSerializerOptions IndentedJsonOpts  = new() { WriteIndented = true };
    static readonly HashSet<string>       ValidEffortLevels = ["low", "medium", "high", "max"];

    async Task RunHeartbeatLoopAsync(CancellationToken ct) {
        while (await _heartbeatTimer.WaitForNextTickAsync(ct)) {
            foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
                // Detect agents stuck in "Starting" with no output
                if (agent.Status                         == "Starting" &&
                    DateTime.UtcNow - agent.LastOutputAt > StartupTimeout) {
                    LogAgentStuck(agent.Id, (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds, agent.Process.Pid, agent.Process.HasExited);
                    _ = HandleStopAgent(agent.Id);

                    continue;
                }

                _ = _server.AppendAgentRunEventAsync(
                    agent.Id,
                    new AgentRunHeartbeat(agent.SessionId)
                );
            }
        }
    }

    async Task RunDaemonHeartbeatLoopAsync(CancellationToken ct) {
        while (await _daemonHeartbeat.WaitForNextTickAsync(ct)) {
            try {
                await _server.SendHeartbeatAsync();
            } catch (Exception ex) {
                LogHeartbeatFailed(ex);
            }
        }
    }

    async Task CleanupAgentAsync(string agentId) {
        if (!_agents.TryRemove(agentId, out var agent)) {
            return;
        }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Process.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

        try { RemoveClaudeProjectSymlink(agent.Worktree.Path); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing symlink", agentId); }

        try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }

        try { await _server.AgentUnregisteredAsync(agentId); } catch (Exception ex) { LogCleanupStepFailed(ex, "unregistering", agentId); }
    }

    public async ValueTask DisposeAsync() {
        await _shutdownCts.CancelAsync();

        foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
            try {
                await agent.ReadCts.CancelAsync();
                await agent.Process.TerminateAsync(TimeSpan.FromSeconds(5));
            } catch {
                /* best-effort */
            }
        }

        foreach (var agentId in _agents.Keys.ToList()) {
            await CleanupAgentAsync(agentId);
        }

        _heartbeatTimer.Dispose();
        _daemonHeartbeat.Dispose();
    }

    /// <summary>
    /// Merges dialog-selected tool names into the worktree's .claude/settings.local.json
    /// permissions.allow array. Existing granular rules (e.g. "Bash(git:*)") are preserved;
    /// dialog selections add broad tool-level entries (e.g. "Bash").
    /// </summary>
    internal static void MergeToolPermissions(string worktreePath, string[] tools) {
        var settingsPath = Path.Combine(worktreePath, ".claude", "settings.local.json");

        JsonNode? root = null;

        if (File.Exists(settingsPath)) {
            try {
                root = JsonNode.Parse(File.ReadAllText(settingsPath));
            } catch {
                // Malformed JSON — start fresh
            }
        }

        if (root is not JsonObject rootObj) {
            rootObj = [];
        }

        if (rootObj["permissions"] is not JsonObject permissions) {
            permissions            = [];
            rootObj["permissions"] = permissions;
        }

        if (permissions["allow"] is not JsonArray allow) {
            allow                = [];
            permissions["allow"] = allow;
        }

        var existing = new HashSet<string>(
            allow.Select(n => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                .Where(s => s != null)!
        );

        foreach (var tool in tools) {
            if (!existing.Contains(tool)) {
                allow.Add((JsonNode)JsonValue.Create(tool)!);
                existing.Add(tool);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        File.WriteAllText(settingsPath, rootObj.ToJsonString(IndentedJsonOpts));
    }

    /// <summary>
    /// Copies files from <paramref name="source"/> into <paramref name="dest"/>,
    /// creating directories as needed but never overwriting existing files.
    /// This preserves git-tracked files while adding gitignored local settings.
    /// </summary>
    static void OverlayDirectory(string source, string dest) {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source)) {
            var destFile = Path.Combine(dest, Path.GetFileName(file));

            if (!File.Exists(destFile)) {
                File.Copy(file, destFile);
            }
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            var destDir = Path.Combine(dest, Path.GetFileName(dir));
            OverlayDirectory(dir, destDir);
        }
    }

    /// <summary>
    /// Creates a symlink at ~/.claude/projects/{worktree-path-hash} pointing to
    /// ~/.claude/projects/{source-path-hash} so project-level permissions, settings,
    /// and memory are shared with the hosted agent.
    /// </summary>
    static void SymlinkClaudeProjectDir(string sourceRepoPath, string worktreePath) {
        if (!Directory.Exists(ClaudePaths.Projects)) {
            return;
        }

        var sourceProjDir = ClaudePaths.ProjectDir(sourceRepoPath);

        if (!Directory.Exists(sourceProjDir)) {
            return;
        }

        var worktreeProjDir = ClaudePaths.ProjectDir(worktreePath);

        // Don't clobber an existing directory or symlink
        if (Path.Exists(worktreeProjDir)) {
            return;
        }

        Directory.CreateSymbolicLink(worktreeProjDir, sourceProjDir);
    }

    /// <summary>
    /// Removes the ~/.claude/projects/{worktree-path-hash} symlink if it exists.
    /// Only removes symlinks, never real directories.
    /// </summary>
    static void RemoveClaudeProjectSymlink(string worktreePath) {
        var info = new DirectoryInfo(ClaudePaths.ProjectDir(worktreePath));

        if (info is { Exists: true, LinkTarget: not null }) {
            info.Delete();
        }
    }

    static void TrustWorktreeInClaudeConfig(string worktreePath) {
        // Workspace trust (the "Do you trust the files in this folder?" dialog)
        // is stored globally in ~/.claude.json under projects[path]. On a fresh
        // machine the file may not exist yet — create a minimal object so trust
        // is always persisted.
        var claudeJsonPath = Path.Combine(PathHelpers.HomeDirectory, ".claude.json");
        var root           = LoadJsonObject(claudeJsonPath);

        var projects = root["projects"] as JsonObject;

        if (projects is null) {
            projects         = new JsonObject();
            root["projects"] = projects;
        }

        var entry = projects[worktreePath] as JsonObject;

        if (entry is null) {
            entry                  = new JsonObject();
            projects[worktreePath] = entry;
        }

        var alreadyTrusted = entry["hasTrustDialogAccepted"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        if (!alreadyTrusted) {
            entry["hasTrustDialogAccepted"] = true;
            File.WriteAllText(claudeJsonPath, root.ToJsonString(IndentedJsonOpts));
        }

        // MCP server approval (the "New MCP server found in .mcp.json" dialog)
        // is stored per-project in {worktree}/.claude/settings.local.json, NOT in
        // ~/.claude.json. Claude requires BOTH fields: the blanket flag AND each
        // server listed by name (the blanket flag alone still shows a per-server
        // discovery prompt on first encounter).
        var serverNames = ReadMcpJsonServerNames(worktreePath);

        if (serverNames.Count == 0) return;

        var settingsDir  = Path.Combine(worktreePath, ".claude");
        var settingsPath = Path.Combine(settingsDir, "settings.local.json");

        Directory.CreateDirectory(settingsDir);

        var settings = LoadJsonObject(settingsPath);
        var sDirty   = false;

        var allEnabled = settings["enableAllProjectMcpServers"] is JsonValue ev
                      && ev.TryGetValue<bool>(out var eb)
                      && eb;

        if (!allEnabled) {
            settings["enableAllProjectMcpServers"] = true;
            sDirty                                 = true;
        }

        var known = new HashSet<string>();

        if (settings["enabledMcpjsonServers"] is JsonArray arr) {
            foreach (var item in arr) {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var s)) {
                    known.Add(s);
                }
            }
        }

        if (serverNames.Any(known.Add)) {
            // Rebuild the array via JSON parsing to avoid AOT issues with
            // JsonArray.Add(string) / JsonValue.Create<string>.
            var json = "[" + string.Join(",", known.Select(n => $"\"{JsonEncodedText.Encode(n)}\"")) + "]";
            settings["enabledMcpjsonServers"] = JsonNode.Parse(json);
            sDirty                            = true;
        }

        if (sDirty) {
            File.WriteAllText(settingsPath, settings.ToJsonString(IndentedJsonOpts));
        }
    }

    /// <summary>
    /// Loads a JSON object from disk, tolerating missing files and non-object roots
    /// by returning a fresh <see cref="JsonObject"/>. Ensures pre-trust logic never
    /// throws on an unexpected config shape and reintroduces the launch hang.
    /// </summary>
    static JsonObject LoadJsonObject(string path) {
        if (!File.Exists(path)) return new JsonObject();

        try {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        } catch {
            return new JsonObject();
        }
    }

    static List<string> ReadMcpJsonServerNames(string worktreePath) {
        var mcpJsonPath = Path.Combine(worktreePath, ".mcp.json");

        if (!File.Exists(mcpJsonPath)) return [];

        try {
            var parsed  = JsonNode.Parse(File.ReadAllText(mcpJsonPath));
            var servers = parsed?["mcpServers"]?.AsObject();

            return servers is null ? [] : [..servers.Select(kv => kv.Key)];
        } catch {
            return [];
        }
    }

    static void WriteMcpConfig(string sourceRepoPath, string worktreePath) {
        var claudeJsonPath = Path.Combine(PathHelpers.HomeDirectory, ".claude.json");

        if (!File.Exists(claudeJsonPath)) return;

        var root = JsonNode.Parse(File.ReadAllText(claudeJsonPath));
        var servers = root?["projects"]?[sourceRepoPath]?["mcpServers"]?.AsObject();

        if (servers is null || servers.Count == 0) return;

        var mcpJsonPath = Path.Combine(worktreePath, ".mcp.json");

        // Read existing .mcp.json if present (e.g. committed to git)
        JsonObject merged;

        if (File.Exists(mcpJsonPath)) {
            var existing = JsonNode.Parse(File.ReadAllText(mcpJsonPath));
            merged = existing?["mcpServers"]?.AsObject() ?? new JsonObject();
        } else {
            merged = new JsonObject();
        }

        // Add servers from ~/.claude.json (don't overwrite repo-committed ones)
        foreach (var (name, value) in servers) {
            if (!merged.ContainsKey(name) && value is not null) {
                var clone = value.DeepClone().AsObject();
                clone.Remove("env");
                merged[name] = clone;
            }
        }

        var wrapper = new JsonObject { ["mcpServers"] = merged };
        File.WriteAllText(mcpJsonPath, wrapper.ToJsonString(IndentedJsonOpts));
    }

    /// <summary>
    /// Extracts readable text from the terminal output buffer by decoding UTF-8
    /// and stripping ANSI escape sequences. Returns the last ~500 chars to keep
    /// the error message reasonable for the UI snackbar.
    /// </summary>
    static string ExtractTerminalText(TerminalOutputBuffer buffer) {
        var chunks = buffer.GetAll();

        if (chunks.Count == 0) {
            return "";
        }

        var combined = new byte[chunks.Sum(c => c.Length)];
        var offset   = 0;

        foreach (var chunk in chunks) {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        var raw     = Encoding.UTF8.GetString(combined);
        var cleaned = StripAnsiRegex().Replace(raw, "").Trim();

        return cleaned.Length > 500 ? cleaned[^500..] : cleaned;
    }

    // ── LoggerMessage source-generated methods ────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Launching agent {AgentId} for {Repo} (effort={Effort}, model={Model})")]
    partial void LogLaunching(string agentId, string repo, string effort, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, claude={ClaudePath})")]
    partial void LogAgentSpawned(string agentId, int pid, string worktree, string claudePath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} exited with code {ExitCode}")]
    partial void LogAgentExited(string agentId, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping agent {AgentId}")]
    partial void LogStopping(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .claude settings for agent {AgentId} (continuing)")]
    partial void LogOverlayFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to merge tool permissions for agent {AgentId} (continuing)")]
    partial void LogToolPermissionsFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write .mcp.json for agent {AgentId} (continuing)")]
    partial void LogMcpConfigFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
    partial void LogTrustWorktreeFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download launch attachments for agent {AgentId} (continuing)")]
    partial void LogAttachmentDownloadFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading output for agent {AgentId}")]
    partial void LogOutputReadError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} failed during startup (exit code {ExitCode}): {Reason}")]
    partial void LogStartupFailed(string agentId, int? exitCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download attachment {Id}: {Status}")]
    partial void LogAttachmentNotFound(string id, System.Net.HttpStatusCode status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attachment filename would escape directory: {FileName}")]
    partial void LogAttachmentPathEscape(string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error downloading attachment {Id}")]
    partial void LogAttachmentError(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to re-register agent {AgentId}")]
    partial void LogReRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating")]
    partial void LogAgentStuck(string agentId, double seconds, int pid, bool exited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error {Step} for agent {AgentId}")]
    partial void LogCleanupStepFailed(Exception ex, string step, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to launch agent {AgentId}")]
    partial void LogLaunchFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during cleanup of agent {AgentId}")]
    partial void LogCleanupError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error stopping agent {AgentId}")]
    partial void LogStopError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Daemon heartbeat failed")]
    partial void LogHeartbeatFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();
}
