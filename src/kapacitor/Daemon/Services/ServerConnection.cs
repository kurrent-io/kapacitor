using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using kapacitor.Auth;
using kapacitor.Config;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

internal partial class ServerConnection : IAsyncDisposable {
    readonly HubConnection             _hub;
    readonly DaemonConfig              _config;
    readonly ILogger<ServerConnection> _logger;

    // Events for incoming commands from server
    public event Func<LaunchAgentCommand, Task>?    OnLaunchAgent;
    public event Func<string, Task>?                OnStopAgent; // agentId
    public event Func<SendInputCommand, Task>?      OnSendInput;
    public event Func<string, string, Task>?        OnSendSpecialKey; // agentId, key
    public event Func<ResizeTerminalCommand, Task>? OnResizeTerminal;

    // Per-phase eval handlers (DEV-1463 PR 2). These use SignalR's
    // client-result invocation — the server calls
    // HubConnection.InvokeAsync<T> which expects exactly one handler
    // returning the typed result, so we use settable properties rather
    // than multicast events. The SignalR 10.0.5 On<T1, TResult> overloads
    // don't expose a CancellationToken to the handler (verified against
    // Microsoft.AspNetCore.SignalR.Client reflection), so per-call
    // cancellation has to be driven by the daemon's own shutdown token —
    // the handler implementations link their work to _shutdownToken.
    public Func<PrepareEvalCommand,  Task<PrepareResult>>?  PrepareEvalHandler  { get; set; }
    public Func<RunQuestionCommand,  Task<QuestionResult>>? RunQuestionHandler  { get; set; }
    public Func<FinalizeEvalCommand, Task<FinalizeResult>>? FinalizeEvalHandler { get; set; }
    public Func<CancelEvalCommand,   Task>?                 CancelEvalHandler   { get; set; }

    /// <summary>
    /// Handler for the server's "do you have a checkout of this repo?" probe.
    /// Receives <c>(owner, repo, candidatePaths)</c> and returns confirmed git
    /// roots. Set by <see cref="AgentOrchestrator"/> at startup.
    /// </summary>
    public Func<FindRepoForRemoteRequest, Task<string[]>>? FindRepoForRemoteHandler { get; set; }

    /// <summary>
    /// Callback invoked at <see cref="RegisterDaemon"/> time to snapshot the
    /// agent IDs currently hosted by this daemon. The server uses this to
    /// reconcile its registry against the daemon's view. Set by
    /// <see cref="AgentOrchestrator"/> at startup; when null, an empty array
    /// is sent (tests don't need to wire the callback).
    /// </summary>
    public Func<string[]>? GetLiveAgentIds { get; set; }

    public ServerConnection(DaemonConfig config, ILogger<ServerConnection> logger) {
        _config = config;
        _logger = logger;

        _hub = new HubConnectionBuilder()
            .WithUrl(
                $"{config.ServerUrl.TrimEnd('/')}/hubs/sessions",
                options => {
                    options.AccessTokenProvider = async () => {
                        var tokens = await TokenStore.GetValidTokensAsync();

                        return tokens?.AccessToken;
                    };
                }
            )
            .WithAutomaticReconnect(new RetryPolicy())
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, KapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        _hub.On<LaunchAgentCommand>("LaunchAgent", cmd => OnLaunchAgent?.Invoke(cmd)                       ?? Task.CompletedTask);
        _hub.On<string>("StopAgent", agentId => OnStopAgent?.Invoke(agentId)                               ?? Task.CompletedTask);
        _hub.On<SendInputCommand>("SendInput", cmd => OnSendInput?.Invoke(cmd)                             ?? Task.CompletedTask);
        _hub.On<string, string>("SendSpecialKey", (agentId, key) => OnSendSpecialKey?.Invoke(agentId, key) ?? Task.CompletedTask);
        _hub.On<ResizeTerminalCommand>("ResizeTerminal", cmd => OnResizeTerminal?.Invoke(cmd) ?? Task.CompletedTask);

        // Client-result invocations for per-phase eval dispatch.
        _hub.On<PrepareEvalCommand, PrepareResult>("PrepareEval",
            cmd => PrepareEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new PrepareResult(false, "no handler", null, 0, 0, 0, 0, 0)));

        _hub.On<RunQuestionCommand, QuestionResult>("RunQuestion",
            cmd => RunQuestionHandler?.Invoke(cmd)
                ?? Task.FromResult(new QuestionResult(false, null, "no handler", 0, 0)));

        _hub.On<FinalizeEvalCommand, FinalizeResult>("FinalizeEval",
            cmd => FinalizeEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new FinalizeResult(false, "no handler", null)));

        _hub.On<CancelEvalCommand>("CancelEval",
            cmd => CancelEvalHandler?.Invoke(cmd) ?? Task.CompletedTask);

        // Server probe used by the "Review this PR" UI to discover which
        // checkouts on this daemon match the PR's owner/repo. Returns an empty
        // array when the orchestrator hasn't wired a handler yet (e.g. during
        // startup) so the server treats this daemon as having no matches.
        _hub.On<FindRepoForRemoteRequest, string[]>("FindRepoForRemote",
            req => FindRepoForRemoteHandler?.Invoke(req) ?? Task.FromResult(Array.Empty<string>()));

        _hub.Reconnected += OnReconnected;
        _hub.Closed      += OnClosed;
    }

    CancellationToken _ct;
    volatile bool     _disposed;
    Task?             _eventProcessorTask;

    public async Task ConnectAsync(CancellationToken ct) {
        _ct                 = ct;
        _eventProcessorTask = ProcessEventQueueAsync(ct);
        await ConnectWithRetryAsync(ct);
    }

    async Task ConnectWithRetryAsync(CancellationToken ct) {
        var delays  = new[] { 1, 2, 5, 10, 30 };
        var attempt = 0;

        while (!ct.IsCancellationRequested) {
            try {
                LogConnecting(_config.ServerUrl);
                await _hub.StartAsync(ct);
                await RegisterDaemon();
                LogConnected(_config.Name);

                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                var delay = delays[Math.Min(attempt, delays.Length - 1)];
                LogConnectionAttemptFailed(ex, attempt + 1, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                attempt++;
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    async Task OnClosed(Exception? ex) {
        if (_disposed || _ct.IsCancellationRequested) {
            return;
        }

        LogConnectionClosed(ex);

        try {
            await ConnectWithRetryAsync(_ct);
            OnReconnectedCallback?.Invoke();
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            // Shutting down, ignore
        }
    }

    async Task RegisterDaemon() {
        var platform  = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
        var repoPaths = await MergeRepoPathsAsync();
        var liveIds   = GetLiveAgentIds?.Invoke() ?? [];

        await _hub.InvokeAsync(
            "DaemonConnect",
            new DaemonConnect(_config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds),
            cancellationToken: _ct
        );
    }

    async Task<string[]> MergeRepoPathsAsync() {
        var persisted = await RepoPathStore.GetSortedPathsAsync();

        if (_config.AllowedRepoPaths.Length == 0)
            return persisted;

        // Union: persisted paths first (sorted by last_used desc), then config-only paths
        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(persisted, comparer);
        var merged = new List<string>(persisted);
        merged.AddRange(_config.AllowedRepoPaths.Select(p => p.TrimEnd('/', '*')).Where(seen.Add));

        return [..merged];
    }

    async Task OnReconnected(string? connectionId) {
        LogReconnected();
        await RegisterDaemon();
        OnReconnectedCallback?.Invoke();
    }

    public event Action? OnReconnectedCallback;

    public Task SendHeartbeatAsync()
        => _hub.SendAsync("DaemonHeartbeat", cancellationToken: _ct);

    public async Task UpdateRepoPathsAsync() {
        try {
            var repoPaths = await MergeRepoPathsAsync();
            await _hub.InvokeAsync("DaemonUpdateRepoPaths", repoPaths, cancellationToken: _ct);
        } catch (Exception ex) {
            LogRepoPathUpdateFailed(ex);
        }
    }

    // Outgoing messages to server
    public Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath)
        => _hub.InvokeAsync("AgentRegistered", new AgentRegistered(agentId, prompt, model, effort, repoPath), cancellationToken: _ct);

    public Task AgentStatusChangedAsync(string agentId, string status, string? sessionId)
        => _hub.InvokeAsync("AgentStatusChanged", new AgentStatusChanged(agentId, status, sessionId), cancellationToken: _ct);

    public Task AgentUnregisteredAsync(string agentId)
        => _hub.InvokeAsync("AgentUnregistered", new AgentUnregistered(agentId), cancellationToken: _ct);

    public Task LaunchFailedAsync(string agentId, string reason)
        => _hub.InvokeAsync("LaunchFailed", new LaunchFailed(agentId, reason), cancellationToken: _ct);

    /// <summary>
    /// Forwards a hosted-agent permission request to the server's <c>RequestPermission</c>
    /// hub method and returns the user's decision. Runs over the persistent SignalR
    /// connection so the long-poll isn't subject to the Cloudflare HTTP-request timeout
    /// that severs the equivalent <c>/hooks/permission-request</c> route at ~120s.
    /// The provided <paramref name="ct"/> should be the local hook caller's lifetime
    /// (the daemon's HTTP listener tracks the incoming request); cancellation propagates
    /// to the server which translates it into a synthesised "deny" decision.
    /// </summary>
    public Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct
        ) =>
        _hub.InvokeAsync<PermissionDecision>(
            "RequestPermission",
            sessionId,
            toolName,
            toolInput,
            suggestions,
            cancellationToken: ct
        );

    public Task SendTerminalOutputAsync(string agentId, string base64Data)
        => _hub.SendAsync("SendTerminalOutput", new TerminalOutput(agentId, base64Data), cancellationToken: _ct);

    // ── Eval progress events (DEV-1440) ────────────────────────────────────

    public Task EvalStartedAsync(string evalRunId, string sessionId, string judgeModel, int totalQuestions)
        => _hub.SendAsync("EvalStarted", new EvalStarted(evalRunId, sessionId, judgeModel, totalQuestions), cancellationToken: _ct);

    public Task EvalQuestionStartedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId)
        => _hub.SendAsync("EvalQuestionStarted", new EvalQuestionStarted(evalRunId, sessionId, index, total, category, questionId), cancellationToken: _ct);

    public Task EvalQuestionCompletedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, int score, string verdict)
        => _hub.SendAsync("EvalQuestionCompleted", new EvalQuestionCompleted(evalRunId, sessionId, index, total, category, questionId, score, verdict), cancellationToken: _ct);

    public Task EvalQuestionFailedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, string reason)
        => _hub.SendAsync("EvalQuestionFailed", new EvalQuestionFailed(evalRunId, sessionId, index, total, category, questionId, reason), cancellationToken: _ct);

    public Task EvalFinishedAsync(string evalRunId, string sessionId, int overallScore, string summary)
        => _hub.SendAsync("EvalFinished", new EvalFinished(evalRunId, sessionId, overallScore, summary), cancellationToken: _ct);

    public Task EvalFailedAsync(string evalRunId, string sessionId, string reason)
        => _hub.SendAsync("EvalFailed", new EvalFailed(evalRunId, sessionId, reason), cancellationToken: _ct);

    // ── Retrospective progress events (DEV-1470) ───────────────────────────

    public Task EvalRetrospectiveStartedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveStarted", new EvalRetrospectiveStarted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveCompletedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveCompleted", new EvalRetrospectiveCompleted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveFailedAsync(string sessionId, string evalRunId, string reason)
        => _hub.SendAsync("EvalRetrospectiveFailed", new EvalRetrospectiveFailed(sessionId, evalRunId, reason), cancellationToken: _ct);

    public Task AppendAgentRunEventAsync(string agentId, object evt) {
        _eventChannel.Writer.TryWrite(new PendingEvent(agentId, evt));

        return Task.CompletedTask;
    }

    readonly Channel<PendingEvent> _eventChannel = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest }
    );

    HttpClient? _httpClient;

    async Task ProcessEventQueueAsync(CancellationToken ct) {
        try {
            await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct)) {
                string payload;

                try {
                    var eventType = evt.Event.GetType().Name;
                    var data      = JsonSerializer.SerializeToNode(evt.Event, evt.Event.GetType(), KapacitorJsonContext.Default)!.AsObject();

                    var payloadObj = new JsonObject {
                        ["event_type"] = eventType,
                        ["data"]       = data
                    };
                    payload = payloadObj.ToJsonString();
                } catch (Exception ex) {
                    LogEventSerializationFailed(ex, evt.Event.GetType().Name, evt.AgentId);

                    continue;
                }

                var url        = $"{_config.ServerUrl.TrimEnd('/')}/api/agent-runs/{evt.AgentId}/events";
                var retryDelay = TimeSpan.FromSeconds(1);

                while (!ct.IsCancellationRequested) {
                    try {
                        _httpClient ??= new();
                        var tokens = await TokenStore.GetValidTokensAsync();

                        if (tokens?.AccessToken is not null) {
                            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
                        }

                        var response = await _httpClient.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                        response.EnsureSuccessStatusCode();

                        break;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        LogEventPostFailed(ex, retryDelay.TotalSeconds);

                        try {
                            await Task.Delay(retryDelay, ct);
                        } catch (OperationCanceledException) {
                            return;
                        }

#pragma warning disable IDE0059
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
#pragma warning restore IDE0059
                    }
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Graceful shutdown — channel read cancelled
        }
    }

    record PendingEvent(string AgentId, object Event);

    public async ValueTask DisposeAsync() {
        _disposed = true;
        _eventChannel.Writer.TryComplete();

        if (_eventProcessorTask is not null) {
            await _eventProcessorTask;
        }

        _httpClient?.Dispose();
        await _hub.DisposeAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {Url}...")]
    partial void LogConnecting(string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected and registered as '{Name}'")]
    partial void LogConnected(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection attempt {Attempt} failed, retrying in {Delay}s")]
    partial void LogConnectionAttemptFailed(Exception ex, int attempt, int delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SignalR connection closed, will reconnect")]
    partial void LogConnectionClosed(Exception? ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to server, re-registering daemon")]
    partial void LogReconnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to post agent run event, retrying in {Delay}s")]
    partial void LogEventPostFailed(Exception ex, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to serialize {EventType} for agent {AgentId}, dropping event")]
    partial void LogEventSerializationFailed(Exception ex, string eventType, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update repo paths on server")]
    partial void LogRepoPathUpdateFailed(Exception ex);

    class RetryPolicy : IRetryPolicy {
        static readonly TimeSpan[] Delays = [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);

            return Delays[index]; // Keeps retrying at 30s intervals after initial backoff
        }
    }
}
