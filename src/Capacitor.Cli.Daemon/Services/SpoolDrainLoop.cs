using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Daemon-side periodic drain of the cross-vendor lifecycle + transcript spools.
///
/// <para>Complements the per-invocation drain wired into the CLI's <c>case "hook":</c> entry point
/// (<c>AgentHookPoster.DrainSpoolsAsync</c>): that only runs when a `kcap` process is invoked at all.
/// Several backlog-producing paths never fire a fresh hook process for the affected session —
/// Kiro/OpenCode's watcher-owned session-end (no session-end hook exists; the watcher detects the
/// vendor process exiting) and a GUI host's idle/parent-exit session-end (Antigravity/Codex desktop) —
/// so without an independent periodic sweep a backlog left over from an outage could sit spooled
/// indefinitely once the host stops firing hooks for that session. The daemon is long-running, so a
/// ~60s <see cref="PeriodicTimer"/> tick is a natural, self-throttling place for this.</para>
///
/// <para>Deliberately does NOT reuse <c>AgentHookPoster.DrainSpoolsAsync</c> (CLI-project-only,
/// `Capacitor.Cli.Commands`) — the daemon doesn't reference the CLI's exe project (and shouldn't: two
/// AOT exes referencing each other as libraries risks a duplicate-entry-point/bloated-trim-graph mess).
/// It composes the same Core primitives (<see cref="HookSpool"/>, <see cref="TranscriptSpool"/>,
/// <see cref="LifecycleSpoolDrain"/>) directly, and — mirroring the CLI's own
/// generate_whats_done wiring (BLOCKER-2) — passes the daemon's OWN pre-existing
/// <c>AgentOrchestrator.SpawnWhatsDoneGenerator</c> as the callback (the daemon already duplicates
/// that "spawn `kcap generate-whats-done`" subprocess logic rather than sharing
/// <c>WatcherManager</c>, for the same decoupling reason).</para>
/// </summary>
internal sealed class SpoolDrainLoop {
    readonly ConfigRoot                _configRoot;
    readonly ProfileContext            _profiles;
    readonly string                    _baseUrl;
    readonly HookSpool                 _lifecycle;
    readonly TranscriptSpool           _transcript;
    readonly ILogger                   _logger;
    readonly Action<string>?           _onWhatsDoneRequested;
    readonly CursorMarkers            _markers;

    // One tick's whole allowance: auth + every drain POST. Short because a tick that overruns the
    // timer's period just queues the next one behind it.
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public SpoolDrainLoop(
            ConfigRoot         configRoot,
            ProfileContext     profiles,
            string             baseUrl,
            HookSpool          lifecycle,
            TranscriptSpool    transcript,
            ILogger            logger,
            Action<string>?    onWhatsDoneRequested = null
        ) {
        _configRoot           = configRoot;
        _profiles             = profiles;
        _markers              = new CursorMarkers(configRoot);
        _baseUrl              = baseUrl;
        _lifecycle            = lifecycle;
        _transcript           = transcript;
        _logger               = logger;
        _onWhatsDoneRequested = onWhatsDoneRequested;
    }

    /// <summary>
    /// Total — never throws (modulo outer cancellation, which is expected at shutdown). Runs as an
    /// unobserved background Task; if a tick faulted the loop would die silently and the daemon would
    /// stop covering the watcher-owned / GUI-idle backlog gap described on the type.
    /// </summary>
    public async Task TickAsync(CancellationToken ct) {
        try {
            _lifecycle.ReapOlderThan(TimeSpan.FromDays(30));
            _transcript.ReapOlderThan(TimeSpan.FromDays(30));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Budget);

            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(_configRoot, _profiles, _baseUrl);

            using (client) {
                if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
                    _logger.LogDebug("Spool-drain tick: auth lapsed — skipping this pass");

                    return;
                }

                await LifecycleSpoolDrain.RunAsync(
                    _markers, client, _baseUrl, _lifecycle, _transcript, currentSessionId: null, Budget, cts.Token,
                    onWhatsDoneRequested: _onWhatsDoneRequested is null ? null : (sid, _) => _onWhatsDoneRequested(sid));
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Outer cancellation (process shutting down) — let the loop exit.
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Spool-drain tick faulted — continuing loop");
        }
    }
}
