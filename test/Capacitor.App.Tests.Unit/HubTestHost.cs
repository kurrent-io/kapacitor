using System.Text.Json;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.App.Tests.Unit;

/// A scriptable stand-in for the server's sessions hub: loopback Kestrel on an OS-assigned
/// port, snake_case hub JSON (the real server's policy), no auth. Handlers are static because
/// SignalR constructs a fresh hub instance per invocation.
public sealed class HubTestHost : IAsyncDisposable {
    WebApplication? _app;
    public string Url { get; private set; } = "";

    public static Func<List<DaemonInfo>> DaemonsHandler { get; set; } = () => [];
    public static Func<JsonElement, string> LaunchHandler { get; set; } = _ => "agent-1";
    static int _launchCalls;
    public static int LaunchCalls => _launchCalls;

    public static async Task<HubTestHost> StartAsync() {
        DaemonsHandler = () => [];
        LaunchHandler = _ => "agent-1";
        _launchCalls = 0;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        // A short cap so StopAsync forcibly severs a still-open hub connection instead of
        // waiting out the default 30s graceful-drain — a connected-then-closed test would
        // otherwise sit for tens of seconds before the client ever sees the drop.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(500));

        var app = builder.Build();
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var host = new HubTestHost { _app = app };
        host.Url = app.Urls.First();
        return host;
    }

    public Task BroadcastAsync(string method, params object?[] args) =>
        _app!.Services.GetRequiredService<IHubContext<SessionsHub>>()
            .Clients.All.SendCoreAsync(method, args);

    public Task StopAsync() => _app!.StopAsync();

    public async ValueTask DisposeAsync() {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public sealed class SessionsHub : Hub {
        public List<DaemonInfo> GetConnectedDaemons() => DaemonsHandler();

        public string RequestLaunchAgentV2(JsonElement payload) {
            Interlocked.Increment(ref _launchCalls);
            return LaunchHandler(payload);
        }
    }
}
