using System.Runtime.InteropServices;
using System.Text.Json;
using kapacitor.Auth;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

public class ServerConnection : IAsyncDisposable {
    readonly HubConnection             _hub;
    readonly DaemonConfig              _config;
    readonly ILogger<ServerConnection> _logger;

    // Events for incoming commands from server
    public event Func<string, string?, string, string?, string, string[]?, string[]?, Task>? OnLaunchAgent;       // agentId, prompt, model, effort, repoPath, tools, attachmentIds
    public event Func<string, Task>?                                                         OnStopAgent;         // agentId
    public event Func<string, string, string[]?, Task>?                                      OnSendInput;         // agentId, text, attachmentIds
    public event Func<string, string, Task>?                                                 OnSendSpecialKey;    // agentId, key
    public event Action<string, int, int>?                                                   OnResizeTerminal;    // agentId, cols, rows

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
                options.PayloadSerializerOptions.TypeInfoResolverChain
                    .Insert(0, KapacitorJsonContext.Default);
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            })
            .Build();

        _hub.On<string, string?, string, string?, string, string[]?, string[]?>(
            "LaunchAgent",
            (
                    agentId,
                    prompt,
                    model,
                    effort,
                    repoPath,
                    tools,
                    attachmentIds
                ) =>
                OnLaunchAgent?.Invoke(agentId, prompt, model, effort, repoPath, tools, attachmentIds) ?? Task.CompletedTask
        );

        _hub.On<string>(
            "StopAgent",
            agentId => OnStopAgent?.Invoke(agentId) ?? Task.CompletedTask
        );

        _hub.On<string, string, string[]?>(
            "SendInput",
            (agentId, text, attachmentIds) => OnSendInput?.Invoke(agentId, text, attachmentIds) ?? Task.CompletedTask
        );

        _hub.On<string, string>(
            "SendSpecialKey",
            (agentId, key) => OnSendSpecialKey?.Invoke(agentId, key) ?? Task.CompletedTask
        );


        _hub.On<string, int, int>(
            "ResizeTerminal",
            (agentId, cols, rows) => OnResizeTerminal?.Invoke(agentId, cols, rows)
        );

        _hub.Reconnected += OnReconnected;
        _hub.Closed      += OnClosed;
    }

    CancellationToken _ct;
    volatile bool     _disposed;

    public async Task ConnectAsync(CancellationToken ct) {
        _ct = ct;
        await ConnectWithRetryAsync(ct);
    }

    async Task ConnectWithRetryAsync(CancellationToken ct) {
        var delays  = new[] { 1, 2, 5, 10, 30 };
        var attempt = 0;

        while (!ct.IsCancellationRequested) {
            try {
                _logger.LogInformation("Connecting to {Url}...", _config.ServerUrl);
                await _hub.StartAsync(ct);
                await RegisterDaemon();
                _logger.LogInformation("Connected and registered as '{Name}'", _config.Name);

                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                var delay = delays[Math.Min(attempt, delays.Length - 1)];
                _logger.LogWarning(ex, "Connection attempt {Attempt} failed, retrying in {Delay}s", attempt + 1, delay);
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

        _logger.LogWarning(ex, "SignalR connection closed, will reconnect");

        try {
            await ConnectWithRetryAsync(_ct);
            OnReconnectedCallback?.Invoke();
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            // Shutting down, ignore
        }
    }

    async Task RegisterDaemon() {
        var platform = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";

        await _hub.InvokeAsync(
            "DaemonConnect",
            _config.Name,
            platform,
            _config.AllowedRepoPaths,
            _config.MaxConcurrentAgents,
            cancellationToken: _ct
        );
    }

    async Task OnReconnected(string? connectionId) {
        _logger.LogInformation("Reconnected to server, re-registering daemon");
        await RegisterDaemon();
        OnReconnectedCallback?.Invoke();
    }

    public event Action? OnReconnectedCallback;

    // Outgoing messages to server
    public Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath)
        => _hub.InvokeAsync("AgentRegistered", agentId, prompt, model, effort, repoPath, cancellationToken: _ct);

    public Task AgentStatusChangedAsync(string agentId, string status, string? sessionId)
        => _hub.InvokeAsync("AgentStatusChanged", agentId, status, sessionId, cancellationToken: _ct);

    public Task AgentUnregisteredAsync(string agentId)
        => _hub.InvokeAsync("AgentUnregistered", agentId, cancellationToken: _ct);

    public Task LaunchFailedAsync(string agentId, string reason)
        => _hub.InvokeAsync("LaunchFailed", agentId, reason, cancellationToken: _ct);

    public Task SendTerminalOutputAsync(string agentId, string base64Data)
        => _hub.SendAsync("SendTerminalOutput", agentId, base64Data, cancellationToken: _ct);

    public Task AppendAgentRunEventAsync(string agentId, string eventType, System.Text.Json.Nodes.JsonObject data) {
        // Use HTTP for event persistence (same as MAUI client)
        var url = $"{_config.ServerUrl.TrimEnd('/')}/api/agent-runs/{agentId}/events";
        // Fire-and-forget HTTP POST
        _ = PostEventAsync(url, eventType, data);

        return Task.CompletedTask;
    }

    HttpClient? _httpClient;

    async Task PostEventAsync(string url, string eventType, System.Text.Json.Nodes.JsonObject data) {
        try {
            _httpClient ??= new HttpClient();
            var tokens = await TokenStore.GetValidTokensAsync();

            if (tokens?.AccessToken is not null) {
                _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
            }

            var payloadObj = new System.Text.Json.Nodes.JsonObject {
                ["event_type"] = eventType,
                ["data"] = data
            };
            var payload = payloadObj.ToJsonString();
            await _httpClient.PostAsync(url, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), _ct);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to post agent run event");
        }
    }

    public async ValueTask DisposeAsync() {
        _disposed = true;
        _httpClient?.Dispose();
        await _hub.DisposeAsync();
    }

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
