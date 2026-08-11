namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Codex turn-start diagnostic. After a follow-up round's input is delivered to a hosted <b>Codex</b> reviewer, the
/// daemon log stops at <c>"SendInput delivered"</c> — it cannot say whether Codex then began a turn
/// or silently ignored the input, which is the entire open question when a round-2 review times out.
///
/// <para>Unlike an ACP runtime (explicit turn boundaries, logged by <c>LogTurnStarted</c>/
/// <c>LogTurnEnded</c> in <c>AcpHostedAgentRuntime</c>), a PTY Codex runtime exposes no turn signal
/// to the daemon: the read loop advances the activity clock on <em>every</em> PTY output chunk,
/// including TUI redraws, so "there was output" is not a turn gate. Codex's own rollout JSONL
/// (<c>~/.codex/sessions/**.jsonl</c>) is the one clean signal — it grows only when Codex commits
/// response items. This observer watches that file's length for growth after input, turning a
/// round-2 timeout into a one-log-line diagnosis instead of a forensic session.</para>
///
/// <para>Pure and unit-testable: the growth source and the clock are injected; only the caller
/// (<c>AgentOrchestrator</c>) touches the filesystem.</para>
/// </summary>
internal static class CodexTurnObserver {
    internal enum Outcome {
        /// <summary>The rollout grew past its post-input baseline — Codex began a turn.</summary>
        TurnObserved,
        /// <summary>The timeout elapsed with no growth — Codex received the input but produced no
        /// turn (the "input delivered, no turn" failure signature).</summary>
        NotObserved,
        /// <summary>The agent stopped or the daemon began shutting down before a verdict.</summary>
        Cancelled,
    }

    /// <summary>
    /// Polls <paramref name="currentLength"/> until it exceeds <paramref name="baseline"/> (⇒
    /// <see cref="Outcome.TurnObserved"/>), <paramref name="timeout"/> elapses (⇒
    /// <see cref="Outcome.NotObserved"/>), or <paramref name="ct"/> fires (⇒
    /// <see cref="Outcome.Cancelled"/>). The length is checked once up front (a fast turn may land
    /// before the first poll) and once more after the loop exits on the deadline, so a turn landing
    /// in the final interval is still credited rather than lost to an off-by-one.
    /// </summary>
    public static async Task<Outcome> ObserveGrowthAsync(
        Func<long>        currentLength,
        long              baseline,
        TimeSpan          timeout,
        TimeSpan          pollInterval,
        TimeProvider      time,
        CancellationToken ct) {
        var start = time.GetTimestamp();

        try {
            while (time.GetElapsedTime(start) < timeout) {
                if (currentLength() > baseline) return Outcome.TurnObserved;
                await Task.Delay(pollInterval, time, ct);
            }

            // Final check: a turn that landed during the last delay must not be missed just
            // because the loop condition fired first.
            return currentLength() > baseline ? Outcome.TurnObserved : Outcome.NotObserved;
        } catch (OperationCanceledException) {
            return Outcome.Cancelled;
        }
    }
}
