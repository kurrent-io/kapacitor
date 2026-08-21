using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// Regression coverage for the production shutdown hang: a DI-owned <see cref="UnixSpawnerThread"/>
/// starts a foreground (<c>IsBackground = false</c>) OS thread that parks on its queue until
/// <see cref="UnixSpawnerThread.Dispose"/> runs. <see cref="IHost.StopAsync"/> only stops registered
/// <see cref="IHostedService"/>s — it never disposes the ServiceProvider — so a shutdown path that
/// calls <c>StopAsync</c> without also disposing the host leaves the thread (and therefore the whole
/// process) alive forever. These tests build a minimal host with the same
/// <c>AddSingleton&lt;UnixSpawnerThread&gt;()</c> registration <c>DaemonRunner.RunAsync</c> uses and
/// drive the exact StopAsync/dispose sequence to prove: (1) StopAsync alone is not enough, and
/// (2) <see cref="DaemonRunner.DisposeHostAsync"/> — the fix — retires the thread.
/// </summary>
public class DaemonHostDisposalTests {
    [Test]
    public async Task StopAsync_alone_leaves_the_spawner_thread_alive() {
        if (OperatingSystem.IsWindows()) return;

        var host    = BuildHost();
        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();

        await host.StartAsync();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        await host.StopAsync();

        // The bug: StopAsync stops IHostedServices, not plain AddSingleton<T> IDisposables.
        // Without a subsequent host disposal the foreground thread is still parked on its
        // BlockingCollection, exactly as it is in production between WaitForShutdownAsync
        // returning and a StopAsync-only cleanup path.
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        // Clean up so the test process itself can exit.
        await DaemonRunner.DisposeHostAsync(host);
    }

    [Test]
    public async Task DisposeHostAsync_after_StopAsync_retires_the_spawner_thread() {
        if (OperatingSystem.IsWindows()) return;

        var host    = BuildHost();
        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();

        await host.StartAsync();
        await Assert.That(spawner.IsThreadAlive).IsTrue();

        await host.StopAsync();
        await DaemonRunner.DisposeHostAsync(host);

        // The fix: disposing the host disposes the ServiceProvider, which disposes the
        // UnixSpawnerThread singleton, which calls CompleteAdding() and joins the thread.
        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    static IHost BuildHost() {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<UnixSpawnerThread>();
        return builder.Build();
    }

    [Test]
    public async Task Spawner_thread_dispose_is_idempotent() {
        if (OperatingSystem.IsWindows()) return;

        var spawner = new UnixSpawnerThread();

        spawner.Dispose();
        // Second pass = the host-dispose DI walk re-disposing the instance the explicit
        // spawner-retire teardown step already retired. Without a run-once guard this hit
        // CompleteAdding on the already-disposed BlockingCollection (ObjectDisposedException).
        spawner.Dispose();

        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    // ---- Production teardown (DaemonRunner.RunDaemonTeardownAsync) ----
    // The daemon's teardown must never let one failing step (e.g. a disposal throwing
    // ObjectDisposedException) skip the later steps: under NativeAOT an escaped teardown
    // exception aborts the process (SIGABRT), and a skipped spawner retirement leaves the
    // DI-owned UnixSpawnerThread's foreground thread parked forever. These tests drive the SAME
    // step list RunAsync executes (BuildDaemonTeardownSteps via RunDaemonTeardownAsync), so a
    // production divergence — a reordered or removed step — fails them.

    /// <summary>The production teardown step names, in the load-bearing order (explicit
    /// dispose + spawner retirement before host stop/dispose — pinned by the tests above).</summary>
    static readonly string[] ProductionSteps =
        ["daemon-lock", "orchestrator", "server-connection", "spawner-retire", "host-stop", "host-dispose"];

    sealed class CaptureLogger : ILogger {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    sealed class SpyLock(List<string> ran, bool throws = false) : IDisposable {
        public void Dispose() {
            if (throws) throw new InvalidOperationException("daemon-lock boom");

            ran.Add("daemon-lock");
        }
    }

    sealed class SpyAsyncDisposable(List<string> ran, string name, bool throws = false) : IAsyncDisposable {
        public ValueTask DisposeAsync() {
            if (throws) throw new InvalidOperationException($"{name} boom");

            ran.Add(name);

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Records the spawner-retire step's UnixSpawnerThread lookup (returning null, so
    /// the step no-ops after recording) — IServiceProvider is the only seam that step touches.</summary>
    sealed class SpyServiceProvider(List<string> ran, bool throws = false) : IServiceProvider {
        public object? GetService(Type serviceType) {
            if (serviceType != typeof(UnixSpawnerThread)) return null;

            if (throws) throw new InvalidOperationException("spawner-retire boom");

            ran.Add("spawner-retire");

            return null;
        }
    }

    sealed class SpyHost(List<string> ran, IServiceProvider services, bool stopThrows = false, bool disposeThrows = false)
        : IHost, IAsyncDisposable {
        public IServiceProvider Services => services;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct = default) {
            if (stopThrows) throw new InvalidOperationException("host-stop boom");

            ran.Add("host-stop");

            return Task.CompletedTask;
        }

        // DisposeHostAsync prefers the IAsyncDisposable path; the sync Dispose is never hit.
        public void Dispose() { }

        public ValueTask DisposeAsync() {
            if (disposeThrows) throw new InvalidOperationException("host-dispose boom");

            ran.Add("host-dispose");

            return ValueTask.CompletedTask;
        }
    }

    static Task<bool> RunProductionTeardownAsync(ILogger logger, List<string> ran, int throwAt = -1)
        => DaemonRunner.RunDaemonTeardownAsync(
            logger,
            new SpyLock(ran, throws: throwAt == 0),
            new SpyAsyncDisposable(ran, "orchestrator", throws: throwAt == 1),
            new SpyAsyncDisposable(ran, "server-connection", throws: throwAt == 2),
            new SpyHost(ran, new SpyServiceProvider(ran, throws: throwAt == 3),
                stopThrows: throwAt == 4, disposeThrows: throwAt == 5));

    [Test]
    public async Task Production_teardown_runs_the_six_real_steps_in_order() {
        var ran = new List<string>();

        var ok = await RunProductionTeardownAsync(NullLogger.Instance, ran);

        await Assert.That(ok).IsTrue();
        await Assert.That(string.Join(",", ran)).IsEqualTo(string.Join(",", ProductionSteps));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    public async Task A_throwing_production_step_at_any_position_does_not_prevent_later_steps(int throwAt) {
        var ran = new List<string>();
        var log = new CaptureLogger();

        var ok = await RunProductionTeardownAsync(log, ran, throwAt);

        // Every step after (and before) the throwing one still ran, in order.
        await Assert.That(ok).IsFalse();
        await Assert.That(string.Join(",", ran))
            .IsEqualTo(string.Join(",", ProductionSteps.Where((_, i) => i != throwAt)));

        // The failing step is named in the log so an operator can tell WHICH disposal broke.
        await Assert.That(log.Messages.Any(m =>
            m.Contains($"Teardown step '{ProductionSteps[throwAt]}' failed", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_never_resolved_orchestrator_is_skipped_and_every_other_step_still_runs() {
        // The partially-started shape: SIGTERM landed inside host.StartAsync, so the
        // orchestrator local was never assigned. Teardown must be null-safe and complete.
        var ran = new List<string>();

        var ok = await DaemonRunner.RunDaemonTeardownAsync(
            NullLogger.Instance,
            new SpyLock(ran),
            orchestrator: null,
            new SpyAsyncDisposable(ran, "server-connection"),
            new SpyHost(ran, new SpyServiceProvider(ran)));

        await Assert.That(ok).IsTrue();
        await Assert.That(string.Join(",", ran))
            .IsEqualTo(string.Join(",", ProductionSteps.Where(s => s != "orchestrator")));
    }

    sealed class ThrowingDisposable : IDisposable {
        public void Dispose() => throw new InvalidOperationException("DI walk boom");
    }

    [Test]
    public async Task A_throwing_DI_disposable_during_host_dispose_cannot_strand_the_spawner_thread() {
        if (OperatingSystem.IsWindows()) return;

        // A DI-tracked singleton whose Dispose throws mid-walk aborts the container's INTERNAL
        // dispose walk, skipping every disposable it had not reached yet. Created AFTER the
        // spawner (the walk runs in reverse creation order), the throwing singleton is disposed
        // FIRST — so without the explicit spawner-retire step the UnixSpawnerThread would be
        // skipped and its foreground thread would keep the "shut down" process alive forever.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<UnixSpawnerThread>();
        builder.Services.AddSingleton<ThrowingDisposable>();
        var host = builder.Build();

        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();
        _ = host.Services.GetRequiredService<ThrowingDisposable>();

        await host.StartAsync();

        var ran = new List<string>();
        var log = new CaptureLogger();

        var ok = await DaemonRunner.RunDaemonTeardownAsync(
            log, new SpyLock(ran), null, new SpyAsyncDisposable(ran, "server-connection"), host);

        // host-dispose reported the aborted DI walk…
        await Assert.That(ok).IsFalse();
        await Assert.That(log.Messages.Any(m =>
            m.Contains("Teardown step 'host-dispose' failed", StringComparison.Ordinal))).IsTrue();

        // …but the spawner thread was still retired by the explicit step.
        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    sealed class BlockingStartService : IHostedService {
        public Task StartAsync(CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        public Task StopAsync(CancellationToken  ct) => Task.CompletedTask;
    }

    [Test]
    public async Task Cancellation_during_host_start_is_contained_and_teardown_still_runs() {
        if (OperatingSystem.IsWindows()) return;

        // SIGTERM while a hosted service is still starting surfaces as TaskCanceledException out
        // of host.StartAsync (dotnet/runtime#111013 behavior). Guarded startup must treat that as
        // the requested shutdown — clean exit, full teardown (daemon lock + host dispose) — not a
        // fault that escapes Main and aborts the NativeAOT process.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<UnixSpawnerThread>();
        builder.Services.AddHostedService<BlockingStartService>();
        var host = builder.Build();

        var spawner = host.Services.GetRequiredService<UnixSpawnerThread>();
        var ran     = new List<string>();

        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exit = await DaemonRunner.RunGuardedStartupAsync(
            NullLogger.Instance,
            stopping.Token,
            startupAndWait: async () => {
                await host.StartAsync(stopping.Token); // throws TaskCanceledException mid-start

                return 0; // never reached
            },
            teardown: () => DaemonRunner.RunDaemonTeardownAsync(
                NullLogger.Instance, new SpyLock(ran), orchestrator: null,
                new SpyAsyncDisposable(ran, "server-connection"), host));

        // Cooperative shutdown, not a fault — and the full teardown ran: the daemon lock was
        // released and host disposal retired the spawner's foreground thread.
        await Assert.That(exit).IsNull();
        await Assert.That(ran).Contains("daemon-lock");
        await Assert.That(spawner.IsThreadAlive).IsFalse();
    }

    [Test]
    public async Task A_cancellation_without_a_shutdown_request_still_propagates() {
        var teardownRan = false;

        await Assert.That(async () => await DaemonRunner.RunGuardedStartupAsync(
                NullLogger.Instance,
                CancellationToken.None, // no shutdown was requested — fail-loud is preserved
                startupAndWait: () => throw new OperationCanceledException(),
                teardown: () => { teardownRan = true; return Task.FromResult(true); }))
            .Throws<OperationCanceledException>();

        // Even the fail-loud path tears down (finally).
        await Assert.That(teardownRan).IsTrue();
    }
}
