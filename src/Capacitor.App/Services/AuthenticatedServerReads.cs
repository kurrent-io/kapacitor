using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

public sealed class AuthenticatedServerReads<TChannel> : IAsyncDisposable where TChannel : class {
    public delegate Task<(HttpClient Client, AuthStatus Status)> ClientFactory(ConfigRoot config, ProfileContext profiles, string url, CancellationToken ct);
    sealed class Lease(HttpClient http, TChannel channel, string url) {
        internal HttpClient Http { get; } = http;
        internal TChannel Channel { get; } = channel;
        internal string Url { get; } = url;
        internal int Borrowers;
        internal bool Retired;
        internal bool Disposed;
    }
    readonly ConfigRoot _config;
    readonly ProfileContext? _profiles;
    readonly ClientFactory _factory;
    readonly Func<HttpClient, string, TChannel> _channelFactory;
    readonly SemaphoreSlim _build = new(1, 1);
    readonly Lock _lock = new();
    readonly CancellationTokenSource _dispose = new();
    readonly List<Task> _active = [];
    Lease? _lease;
    ServiceProvider? _lane;
    readonly bool _allowAutoRedirect;
    long _generation;
    bool _disposed;

    public AuthenticatedServerReads(ConfigRoot config, ProfileContext? profiles, Func<HttpClient, string, TChannel> channelFactory,
        ClientFactory? factory = null, bool allowAutoRedirect = true) {
        _config = config;
        _profiles = profiles;
        _channelFactory = channelFactory;
        _allowAutoRedirect = allowAutoRedirect;
        _factory = factory ?? RegisteredLaneAsync;
    }
    async Task<(HttpClient Client, AuthStatus Status)> RegisteredLaneAsync(
        ConfigRoot config, ProfileContext profiles, string url, CancellationToken ct) {
        _lane ??= new ServiceCollection()
            .AddSingleton(config)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(url, config, profiles))
            .AddCapacitorHttp()
            .BuildValidated();
        var clients = _lane.GetRequiredService<ICapacitorHttpClient>();
        var attempt = _allowAutoRedirect
            ? await clients.ForWaitAsync(ct).ConfigureAwait(false)
            : await clients.ForProtectedReadAsync(ct).ConfigureAwait(false);
        return (attempt.Client, attempt.Status);
    }
    public Task<T> ReadAsync<T>(Func<TChannel, CancellationToken, Task<T>> read, Func<T, bool> signedOut, T noAuth, T disposed, CancellationToken ct) {
        Task<T> task;
        lock (_lock) {
            if (_disposed) return Task.FromResult(disposed);
            task = ReadCoreAsync(read, signedOut, noAuth, disposed, ct);
            _active.Add(task);
        }
        _ = task.ContinueWith(completed => { lock (_lock) _active.Remove(completed); }, TaskScheduler.Default);
        return task;
    }
    async Task<T> ReadCoreAsync<T>(Func<TChannel, CancellationToken, Task<T>> read, Func<T, bool> signedOut, T noAuth, T disposed, CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispose.Token);
        if (_profiles is null || _profiles.Resolution.ServerUrl is not { Length: > 0 } url) return noAuth;
        Lease? lease = null;
        try {
            lease = await BorrowAsync(url, linked.Token).ConfigureAwait(false);
            if (lease is null) return noAuth;
            var result = await read(lease.Channel, linked.Token).ConfigureAwait(false);
            if (signedOut(result)) Retire(lease);
            return result;
        } catch (OperationCanceledException) when (_dispose.IsCancellationRequested && !ct.IsCancellationRequested) { return disposed; }
        finally { if (lease is not null) Release(lease); }
    }
    async Task<Lease?> BorrowAsync(string url, CancellationToken ct) {
        await _build.WaitAsync(ct).ConfigureAwait(false);
        try {
            Lease? old;
            long generation;
            lock (_lock) {
                if (_lease is { Retired: false } live && live.Url == url) { live.Borrowers++; return live; }
                old = _lease;
                if (old is not null) old.Retired = true;
                _lease = null;
                generation = _generation;
            }
            if (old is not null) DisposeIfUnused(old);
            var (http, status) = await _factory(_config, _profiles!, url, ct).ConfigureAwait(false);
            if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) { http.Dispose(); return null; }
            TChannel channel;
            try { channel = _channelFactory(http, url); }
            catch { http.Dispose(); throw; }
            var lease = new Lease(http, channel, url) { Borrowers = 1 };
            lock (_lock) {
                if (!_disposed && generation == _generation) { _lease = lease; return lease; }
                lease.Retired = true;
                lease.Borrowers = 0;
            }
            DisposeIfUnused(lease);
            return null;
        } finally { _build.Release(); }
    }
    void Retire(Lease lease) {
        lock (_lock) { lease.Retired = true; if (ReferenceEquals(_lease, lease)) _lease = null; }
    }
    public void Invalidate() {
        Lease? lease;
        lock (_lock) {
            _generation++;
            lease = _lease;
            _lease = null;
            if (lease is not null) lease.Retired = true;
        }
        if (lease is not null) DisposeIfUnused(lease);
    }
    void Release(Lease lease) {
        lock (_lock) lease.Borrowers--;
        DisposeIfUnused(lease);
    }
    void DisposeIfUnused(Lease lease) {
        lock (_lock) {
            if (!lease.Retired || lease.Borrowers != 0 || lease.Disposed) return;
            lease.Disposed = true;
        }
        (lease.Channel as IDisposable)?.Dispose();
        lease.Http.Dispose();
    }
    public async ValueTask DisposeAsync() {
        Task[] active;
        Lease? lease;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            active = _active.ToArray();
            lease = _lease;
            _lease = null;
            if (lease is not null) lease.Retired = true;
        }
        _dispose.Cancel();
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch (Exception) { }
        if (lease is not null) DisposeIfUnused(lease);
        if (_lane is not null) await _lane.DisposeAsync().ConfigureAwait(false);
        _dispose.Dispose();
        _build.Dispose();
    }
}
