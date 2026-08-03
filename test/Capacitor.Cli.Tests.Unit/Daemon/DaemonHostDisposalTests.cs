// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonHostDisposalTests.cs
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

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

    // ---- Teardown coordinator (DaemonRunner.RunTeardownAsync) ----
    // The daemon's finally-block teardown must never let one failing step (e.g. a disposal
    // throwing ObjectDisposedException) skip the later steps: under NativeAOT an escaped
    // teardown exception aborts the process (SIGABRT), and a skipped host-dispose leaves the
    // DI-owned UnixSpawnerThread's foreground thread parked forever.

    /// <summary>The production teardown step names, in the load-bearing order (explicit
    /// dispose before host stop — spawner-thread retirement, pinned by the tests above).</summary>
    static readonly string[] ProductionSteps =
        ["daemon-lock", "orchestrator", "server-connection", "host-stop", "host-dispose"];

    sealed class CaptureLogger : ILogger {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Test]
    public async Task Teardown_steps_run_in_the_exact_given_order_and_report_success() {
        var ran = new List<string>();

        var steps = new List<(string Name, Func<ValueTask> Action)>();

        foreach (var name in ProductionSteps) {
            steps.Add((name, () => { ran.Add(name); return ValueTask.CompletedTask; }));
        }

        var ok = await DaemonRunner.RunTeardownAsync(NullLogger.Instance, steps);

        await Assert.That(ok).IsTrue();
        await Assert.That(string.Join(",", ran)).IsEqualTo(string.Join(",", ProductionSteps));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task A_throwing_step_at_any_position_does_not_prevent_later_steps(int throwAt) {
        var ran = new List<string>();
        var log = new CaptureLogger();

        var steps = new List<(string Name, Func<ValueTask> Action)>();

        for (var i = 0; i < ProductionSteps.Length; i++) {
            var name = ProductionSteps[i];
            var idx  = i;

            steps.Add((name, () => {
                if (idx == throwAt) throw new ObjectDisposedException(name);

                ran.Add(name);

                return ValueTask.CompletedTask;
            }));
        }

        var ok = await DaemonRunner.RunTeardownAsync(log, steps);

        // Every step after (and before) the throwing one still ran, in order.
        await Assert.That(ok).IsFalse();
        await Assert.That(string.Join(",", ran))
            .IsEqualTo(string.Join(",", ProductionSteps.Where((_, i) => i != throwAt)));

        // The failing step is named in the log so an operator can tell WHICH disposal broke.
        await Assert.That(log.Messages.Any(m =>
            m.Contains($"Teardown step '{ProductionSteps[throwAt]}' failed", StringComparison.Ordinal))).IsTrue();
    }
}
