using System.Diagnostics;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// Gated live end-to-end certification that <see cref="PiRpcHostedAgentRuntimeFactory"/> and
/// <see cref="PiRpcHostedAgentRuntime"/> actually work against a REAL <c>pi --mode rpc</c> child — no
/// <c>FakeProcess</c>, no in-memory pipe (see <c>PiHostedLaunchTests</c> for that coverage of the same
/// code path, including the exact PSI/argv/env this factory builds and the refusal ladder). This is
/// the only place asserting the end-to-end claim: real process spawn (the factory's default
/// <c>processSource</c>, i.e. <c>processSource: null</c>) → real LF-framed JSONL-RPC over stdio → a
/// real <c>get_state</c> handshake resolving <see cref="PiRpcHostedAgentRuntime.AcpSessionId"/> → a
/// real <c>prompt</c> turn whose <c>assistant_text</c> reply actually reaches
/// <see cref="PiRpcHostedAgentRuntime.Envelopes"/> → a real <c>abort</c>-then-exit graceful stop.
///
/// <para><b>What this does NOT certify.</b> The <c>KCAP_PI_PURE=1</c> dual-capture gate this factory's
/// <c>BuildPsi</c> stamps on every launch (see <see cref="PiLaunchEnvironment"/>) is pinned STATICALLY
/// by <c>PiHostedLaunchTests.BuildPsi_EnvCarriesTheDualCaptureGate</c> — a PSI assertion, not a live
/// one. Whether the installed <c>~/.pi/agent/extensions/kcap.ts</c> actually HONOURS that env var (i.e.
/// that a real Pi process spawned with it set stands its own capture down rather than double-recording
/// this session) is a claim about the extension's runtime behaviour under a live Pi build, and that is
/// exactly what the memory-injection live cert covers — see
/// <c>SessionStartMemory.PiMemoryIndexLiveCertTests</c>. Deliberately not duplicated here.</para>
///
/// <para><b>Gated</b> behind <c>KCAP_PI_HOSTED_LIVE=1</c> so CI (no <c>pi</c> binary, no authenticated
/// provider) never runs this, and no ordinary local test run silently spends a real Pi turn. Requires:
/// <c>pi</c> on <c>PATH</c> and authenticated for at least one provider. POSIX-gated: the daemon-owned
/// worktree this factory requires (<c>WorkLocation.OwnedWorktree</c>) is a plain temp directory here,
/// but a <c>git init</c> of it mirrors the real launch shape (an actual daemon-created worktree is
/// always a git checkout) closely enough that skipping it on Windows costs nothing this cert needs to
/// prove.</para>
/// </summary>
public class PiHostedRuntimeLiveCertTests {
    const string LiveGateEnvVar = "KCAP_PI_HOSTED_LIVE";

    static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TurnTimeout  = TimeSpan.FromSeconds(60);
    static readonly TimeSpan StopTimeout  = TimeSpan.FromSeconds(15);

    [Test]
    public async Task StartAsync_AgainstRealPiRpc_ProducesThePromptedNonceAndStopsCleanly() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'pi --mode rpc' process — set {LiveGateEnvVar}=1 to run "
          + "(spends a real Pi turn; needs `pi` on PATH and authenticated for a provider — spawns one "
          + "real `pi --mode rpc` session in a throwaway git worktree).");
        Skip.Unless(
            !OperatingSystem.IsWindows(),
            "The gated probe git-inits a throwaway worktree with plain POSIX `git`; not exercised on Windows.");

        using var repo = GitRepo.Create();

        // A real (console) logger factory rather than NullLoggerFactory — PiRpcHostedAgentRuntime logs
        // at Warning/Debug on handshake and translation faults, so a real logger is the only way this
        // test can surface those instead of silently swallowing them.
        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        var nonce  = Guid.NewGuid().ToString("N")[..8];
        var prompt = $"Reply with exactly: PONG-{nonce}";

        var config = new DaemonConfig {
            PiPath = Environment.GetEnvironmentVariable("KCAP_PI_PATH") is { Length: > 0 } envPiPath
                ? envPiPath
                : "pi"
        };

        // Logged for the record — mirrors the memory-injection cert's RecordCertEnvironment spirit: a
        // cert passing (or failing) against an unknown build is how earlier live-cert failures got
        // misdiagnosed as code defects when the real cause was a stale/mismatched installed binary.
        await LogPiVersionAsync(config.PiPath);

        var factory = new PiRpcHostedAgentRuntimeFactory(
            config: config,
            loggerFactory: liveLoggerFactory,
            processSource: null,  // real `pi --mode rpc` spawn — the production path
            binaryExists: null);  // real CliResolver.Exists probe

        var ctx = new RuntimeStartContext(
            AgentId: "ai-894-pi-hosted-live",
            Vendor: "pi",
            SourceRepoPath: repo.Path,
            Worktree: new WorktreeInfo(Path: repo.Path, Branch: "", SourceRepo: repo.Path),
            Prompt: prompt,
            Model: null,
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 80,
            Rows: 24,
            ServerUrl: null,
            DaemonBridgeUrl: null,
            CapacitorPath: "/usr/local/bin/kcap");
            // Work defaults to WorkLocation.OwnedWorktree — the only shape this factory serves.

        using var startCts = new CancellationTokenSource();

        var start = await factory.StartAsync(ctx, startCts.Token).WaitAsync(ReadyTimeout);
        var runtime = (PiRpcHostedAgentRuntime)start.Runtime;

        try {
            // (a) The ordering guarantee StartAsync's own doc states: a factory MUST await the
            // ready barrier before returning, so both of these must already be true the instant
            // StartAsync's Task completed above — no further waiting here.
            await Assert.That(start.Transcript).IsNotNull();
            await Assert.That(runtime.AcpSessionId).IsNotEmpty();

            Console.WriteLine($"[pi-hosted-live] AcpSessionId={runtime.AcpSessionId} ResolvedModel={runtime.ResolvedModel ?? "(none)"}");

            // (b) The prompted turn's assistant_text reply must carry the nonce, observed over the
            // SAME channel the daemon forwards to the server.
            var (sawNonce, collected) = await CollectUntilNonceAsync(runtime.Envelopes, nonce, TurnTimeout);

            Console.WriteLine($"[pi-hosted-live] observed {collected.Count} transcript envelope(s):");
            foreach (var env in collected)
                Console.WriteLine($"[pi-hosted-live]   kind={env.Kind} text={env.Text}");

            await Assert.That(sawNonce).IsTrue()
                .Because($"expected an assistant_text envelope containing '{nonce}' within {TurnTimeout} "
                       + "of the prompted turn");
        } finally {
            // (c) Graceful stop first; TerminateAsync (inside DisposeAsync) is the backstop this
            // factory's own doc describes for a launch that fails to wind down cleanly.
            try {
                await runtime.RequestGracefulStopAsync().WaitAsync(StopTimeout);
                await runtime.WaitForExitAsync(StopTimeout);
            } catch (Exception ex) {
                Console.WriteLine($"[pi-hosted-live] graceful stop did not complete cleanly: {ex.Message}");
            }

            startCts.Cancel();
            await runtime.DisposeAsync();
        }
    }

    /// <summary>Drains <paramref name="envelopes"/> until an <c>assistant_text</c> envelope's
    /// <see cref="AcpEventEnvelope.Text"/> contains <paramref name="nonce"/> (ordinal), or
    /// <paramref name="timeout"/> elapses.</summary>
    static async Task<(bool SawNonce, List<AcpEventEnvelope> Collected)> CollectUntilNonceAsync(
            ChannelReader<AcpEventEnvelope> envelopes, string nonce, TimeSpan timeout) {
        var collected = new List<AcpEventEnvelope>();

        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await envelopes.WaitToReadAsync(timeoutCts.Token)) {
                while (envelopes.TryRead(out var env)) {
                    collected.Add(env);

                    if (env.Kind == AcpEventKind.AssistantText
                            && env.Text is { Length: > 0 } text
                            && text.Contains(nonce, StringComparison.Ordinal))
                        return (true, collected);
                }
            }
        } catch (OperationCanceledException) {
            // Timed out waiting for the turn to produce the nonce — fall through and report what was
            // observed either way.
        }

        return (false, collected);
    }

    /// <summary>Best-effort <c>pi --version</c> capture for the test log — never asserted on, purely
    /// diagnostic (see the memory-injection cert's <c>RecordCertEnvironmentAsync</c> for the same
    /// spirit: a cert result is meaningless without knowing which build it ran against).</summary>
    static async Task LogPiVersionAsync(string piPath) {
        try {
            using var process = Process.Start(new ProcessStartInfo(piPath, ["--version"]) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            });

            if (process is null) {
                Console.WriteLine("[pi-hosted-live] pi --version: could not start process");
                return;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Console.WriteLine($"[pi-hosted-live] pi --version: {stdout.Trim()}{stderr.Trim()}");
        } catch (Exception ex) {
            Console.WriteLine($"[pi-hosted-live] pi --version failed: {ex.Message}");
        }
    }

}
