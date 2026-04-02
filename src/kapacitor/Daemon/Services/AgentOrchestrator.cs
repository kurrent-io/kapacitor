using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using kapacitor.Auth;
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

public partial class AgentOrchestrator : IAsyncDisposable {
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

    async Task HandleLaunchAgent(
            string    agentId,
            string?   prompt,
            string    model,
            string?   effort,
            string    repoPath,
            string[]? tools,
            string[]? attachmentIds
        ) {
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

            _logger.LogInformation(
                "Launching agent {AgentId} for {Repo} (effort={Effort}, model={Model})",
                agentId,
                repoPath,
                effort ?? "default",
                model
            );

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
                _logger.LogWarning(ex, "Failed to overlay .claude settings for agent {AgentId} (continuing)", agentId);
            }

            // Merge dialog-selected tools into the worktree's settings.local.json
            // instead of using --allowedTools (which overrides project permissions).
            // Best-effort: filesystem/JSON errors should not block agent launch.
            try {
                if (tools is { Length: > 0 }) {
                    MergeToolPermissions(worktree.Path, tools);
                }
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Failed to merge tool permissions for agent {AgentId} (continuing)", agentId);
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
                    _logger.LogWarning(ex, "Failed to download launch attachments for agent {AgentId} (continuing)", agentId);
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

            _logger.LogInformation(
                "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, claude={ClaudePath})",
                agentId,
                process.Pid,
                worktree.Path,
                _config.ClaudePath
            );

            var cts   = new CancellationTokenSource();
            var agent = new AgentInstance(agentId, prompt, model, effort, repoPath, process, worktree, cts);
            _agents[agentId] = agent;

            // Notify server
            await _server.AgentRegisteredAsync(agentId, prompt, model, effort, repoPath);

            _ = _server.AppendAgentRunEventAsync(
                agentId,
                "AgentRunStarted",
                new System.Text.Json.Nodes.JsonObject {
                    ["prompt"]    = prompt, ["model"]           = model, ["effort"] = effort,
                    ["repo_path"] = repoPath, ["worktree_path"] = worktree.Path
                }
            );

            // Start reading output
            _ = ReadAgentOutputAsync(agent);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to launch agent {AgentId}", agentId);

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
            _logger.LogWarning(ex, "Error reading output for agent {AgentId}", agent.Id);
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

                        _logger.LogWarning(
                            "Agent {AgentId} failed during startup (exit code {ExitCode}): {Reason}",
                            agent.Id,
                            exitCode,
                            reason
                        );

                        _ = _server.LaunchFailedAsync(agent.Id, reason);
                    }

                    agent.Status = status;
                    _            = _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    _ = _server.AppendAgentRunEventAsync(
                        agent.Id,
                        "AgentRunStopped",
                        new() {
                            ["reason"]    = stopReason,
                            ["exit_code"] = exitCode
                        }
                    );
                }

                _logger.LogInformation("Agent {AgentId} exited with code {ExitCode}", agent.Id, exitCode);

                // Clean up worktree and unregister from server
                await CleanupAgentAsync(agent.Id);
            } catch (Exception ex) {
                _logger.LogError(ex, "Error during cleanup of agent {AgentId}", agent.Id);
            }
        }
    }

    async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        try {
            _logger.LogInformation("Stopping agent {AgentId}", agentId);

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            agent.Status = "Completed";
            _            = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
            _            = _server.AppendAgentRunEventAsync(agentId, "AgentRunStopped", new System.Text.Json.Nodes.JsonObject { ["reason"] = "user" });

            // Cancel the read loop, then terminate the process.
            // The read loop's finally block will handle CleanupAgentAsync.
            await agent.ReadCts.CancelAsync();
            await agent.Process.TerminateAsync(TimeSpan.FromSeconds(10));
        } catch (Exception ex) {
            _logger.LogError(ex, "Error stopping agent {AgentId}", agentId);
        }
    }

    async Task HandleSendInput(string agentId, string text, string[]? attachmentIds) {
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
                    _logger.LogWarning("Failed to download attachment {Id}: {Status}", id, response.StatusCode);

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
                    _logger.LogWarning("Attachment filename would escape directory: {FileName}", rawFileName);

                    continue;
                }

                await using var fs = File.Create(filePath);
                await response.Content.CopyToAsync(fs);

                paths.Add($".attached/{Path.GetFileName(filePath)}");
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Error downloading attachment {Id}", id);
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

    void HandleResizeTerminal(string agentId, int cols, int rows) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            return;
        }

        agent.Process.Resize((ushort)cols, (ushort)rows);
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
                    _logger.LogWarning(ex, "Failed to re-register agent {AgentId}", agent.Id);
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
                    _logger.LogWarning(
                        "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating",
                        agent.Id,
                        (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds,
                        agent.Process.Pid,
                        agent.Process.HasExited
                    );
                    _ = HandleStopAgent(agent.Id);

                    continue;
                }

                _ = _server.AppendAgentRunEventAsync(
                    agent.Id,
                    "AgentRunHeartbeat",
                    new() { ["session_id"] = agent.SessionId }
                );
            }
        }
    }

    async Task RunDaemonHeartbeatLoopAsync(CancellationToken ct) {
        while (await _daemonHeartbeat.WaitForNextTickAsync(ct)) {
            try {
                await _server.SendHeartbeatAsync();
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Daemon heartbeat failed");
            }
        }
    }

    async Task CleanupAgentAsync(string agentId) {
        if (!_agents.TryRemove(agentId, out var agent)) {
            return;
        }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Process.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Error disposing process for agent {AgentId}", agentId); }

        try { RemoveClaudeProjectSymlink(agent.Worktree.Path); } catch (Exception ex) { _logger.LogWarning(ex, "Error removing symlink for agent {AgentId}", agentId); }

        try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { _logger.LogWarning(ex, "Error removing worktree for agent {AgentId}", agentId); }

        try { await _server.AgentUnregisteredAsync(agentId); } catch (Exception ex) { _logger.LogWarning(ex, "Error unregistering agent {AgentId}", agentId); }
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
        var claudeProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects"
        );

        if (!Directory.Exists(claudeProjectsDir)) {
            return;
        }

        var sourceHash    = PathToProjectHash(sourceRepoPath);
        var sourceProjDir = Path.Combine(claudeProjectsDir, sourceHash);

        if (!Directory.Exists(sourceProjDir)) {
            return;
        }

        var worktreeHash    = PathToProjectHash(worktreePath);
        var worktreeProjDir = Path.Combine(claudeProjectsDir, worktreeHash);

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
        var claudeProjectsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects"
        );

        var worktreeHash    = PathToProjectHash(worktreePath);
        var worktreeProjDir = Path.Combine(claudeProjectsDir, worktreeHash);

        var info = new DirectoryInfo(worktreeProjDir);

        if (info is { Exists: true, LinkTarget: not null }) {
            info.Delete();
        }
    }

    /// <summary>
    /// Converts an absolute path to Claude Code's project directory hash format.
    /// e.g. "/Users/alexey/dev/myrepo" → "-Users-alexey-dev-myrepo"
    /// </summary>
    static string PathToProjectHash(string absolutePath) =>
        absolutePath.Replace(Path.DirectorySeparatorChar, '-');

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

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();
}
