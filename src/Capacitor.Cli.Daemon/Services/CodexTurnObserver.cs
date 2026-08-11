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
        /// <summary>The length was still unreadable at the deadline (a deleted/moved rollout or a
        /// sustained stat failure) and no growth was ever seen — a measurement gap, NOT evidence
        /// that the reviewer ignored the input. The caller must not report this as "no turn".</summary>
        Unavailable,
        /// <summary>A newer round superseded this probe (the injected <c>isCurrent</c> predicate went
        /// false) before a verdict — the newer probe reports. Returned promptly so a superseded probe
        /// stops polling within one interval instead of running to the timeout.</summary>
        Superseded,
        /// <summary>The agent stopped or the daemon began shutting down before a verdict.</summary>
        Cancelled,
    }

    /// <summary>
    /// Polls <paramref name="currentLength"/> until it exceeds <paramref name="baseline"/> (⇒
    /// <see cref="Outcome.TurnObserved"/>), <paramref name="timeout"/> elapses (⇒
    /// <see cref="Outcome.NotObserved"/>, or <see cref="Outcome.Unavailable"/> when the final read
    /// signalled unavailability), or <paramref name="ct"/> fires (⇒ <see cref="Outcome.Cancelled"/>).
    /// The length is checked once up front (a fast turn may land before the first poll) and once more
    /// after the loop exits on the deadline, so a turn landing in the final interval is still credited
    /// rather than lost to an off-by-one.
    ///
    /// <para><paramref name="currentLength"/> returns a <b>negative</b> value to signal the length is
    /// momentarily unreadable. During polling that is simply "not grown yet" (a real growth always
    /// resolves to a value &gt; baseline first, so a transient read failure can never mask a turn we
    /// already saw); only a negative value at the final deadline check yields
    /// <see cref="Outcome.Unavailable"/> — distinguishing a genuine measurement failure from a real
    /// "no turn".</para>
    /// </summary>
    /// <param name="isCurrent">Optional single-flight predicate. Checked at the top of every poll
    /// (and once more at the deadline) BEFORE the length stat; the first time it returns false the
    /// probe stops with <see cref="Outcome.Superseded"/>. This is what makes a superseded probe stop
    /// polling within one interval rather than running to <paramref name="timeout"/> — the caller
    /// keeps the actual verdict authority (a generation), so this only manages the probe's lifetime.
    /// Null (the default) disables it.</param>
    public static async Task<Outcome> ObserveGrowthAsync(
        Func<long>        currentLength,
        long              baseline,
        TimeSpan          timeout,
        TimeSpan          pollInterval,
        TimeProvider      time,
        CancellationToken ct,
        Func<bool>?       isCurrent = null) {
        var start = time.GetTimestamp();

        try {
            while (time.GetElapsedTime(start) < timeout) {
                if (isCurrent is not null && !isCurrent()) return Outcome.Superseded;
                if (currentLength() > baseline)            return Outcome.TurnObserved;
                await Task.Delay(pollInterval, time, ct);
            }

            // Final check: a turn that landed during the last delay must not be missed just because
            // the loop condition fired first. A negative reading here is a genuine measurement gap,
            // not a "no turn" verdict.
            if (isCurrent is not null && !isCurrent()) return Outcome.Superseded;
            var finalLength = currentLength();
            if (finalLength < 0)        return Outcome.Unavailable;
            return finalLength > baseline ? Outcome.TurnObserved : Outcome.NotObserved;
        } catch (OperationCanceledException) {
            return Outcome.Cancelled;
        }
    }
}
