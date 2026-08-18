using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Turn-completion state a Codex collab CHILD watcher folds from its own rollout lines to
/// decide when to post the LIVE <c>/hooks/subagent-stop</c>. Codex fires no
/// per-child stop hook and <c>sub_agent_activity</c> carries no completed kind, so before
/// this the only stop was the parent's session-end teardown
/// (<c>CodexSubagentTeardown</c>) — every finished child card stayed "in progress" for the
/// parent's whole lifetime (hours, for a hosted reviewer). The deterministic per-turn
/// signal is in the child's own rollout: each turn ends with an
/// <c>event_msg.task_complete</c> line.
///
/// A collab child is re-engageable (the parent can <c>send_message</c> again — observed
/// minutes after the first answer), but the server-side lifecycle is one-shot per
/// (session, agent): <c>AgentLifecycleDeterministicId</c> dedupes a second stop, and there
/// is no reopen. Two consequences shape this type:
///
/// <list type="bullet">
/// <item>The stop waits out a grace window after the last rollout activity, so a
/// same-round follow-up keeps the card honestly "in progress" instead of landing content
/// under an already-completed card. A child re-engaged AFTER the window still streams into
/// its subsession (appends are not lifecycle-gated) but its card stays completed —
/// accepted trade-off, far less wrong than spinning forever.</item>
/// <item><see cref="StopPosted"/> latches: once a stop succeeded, a later
/// <c>task_complete</c> never re-arms (the re-post would be a deduped no-op anyway). The
/// parent-end teardown remains the backstop and its duplicate stop dedupes the same
/// way.</item>
/// </list>
/// </summary>
public sealed class CodexSubagentTurnTracker {
    /// <summary>
    /// Default idle grace between the child's <c>task_complete</c> and the stop POST.
    /// Sized to absorb the observed same-round re-engagement gaps (the parent's follow-up
    /// <c>send_message</c> landed 2–6 minutes after a child's first answer in the
    /// production session this was diagnosed on).
    /// </summary>
    public static readonly TimeSpan DefaultStopGrace = TimeSpan.FromMinutes(5);

    /// <summary>The child rollout's last observed turn state: true once an
    /// <c>event_msg.task_complete</c> landed with no later turn activity.</summary>
    public bool TurnCompleted { get; private set; }

    /// <summary>Set by the watcher once a stop POST succeeded; permanently disarms
    /// <see cref="ShouldPostStop"/> (the lifecycle is one-shot — see class doc).</summary>
    public bool StopPosted { get; set; }

    /// <summary>
    /// Folds one rollout line. <c>event_msg.task_complete</c> marks the turn completed;
    /// real turn activity — any <c>response_item</c>, or an explicit
    /// <c>event_msg.task_started</c> — re-opens it (re-engagement). Everything else
    /// (<c>token_count</c> and other trailing <c>event_msg</c> noise, <c>turn_context</c>,
    /// <c>world_state</c>, malformed/non-JSON lines) leaves the state unchanged, so this is
    /// safe to call for every drained line.
    /// </summary>
    public void Observe(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            switch (root.Str("type")) {
                case "event_msg":
                    switch (root.Obj("payload")?.Str("type")) {
                        case "task_complete":
                            TurnCompleted = true;
                            break;

                        case "task_started":
                            TurnCompleted = false;
                            break;
                    }

                    break;

                case "response_item":
                    TurnCompleted = false;
                    break;
            }
        } catch {
            // Malformed / non-JSON lines never break the drain loop.
        }
    }

    /// <summary>
    /// True when the watcher should post the child's <c>subagent-stop</c> now: not yet
    /// posted, last turn completed, no tool call in flight (a long-running command produces
    /// no rollout line between its call and output — mirrors the idle-end guard), and the
    /// rollout has been quiet for <paramref name="grace"/> since
    /// <paramref name="lastActivityAt"/>.
    /// </summary>
    public bool ShouldPostStop(int pendingToolCallCount, DateTimeOffset lastActivityAt, DateTimeOffset now, TimeSpan grace) =>
        !StopPosted
     && TurnCompleted
     && pendingToolCallCount == 0
     && now - lastActivityAt >= grace;

    /// <summary>
    /// Resolves the stop grace from <c>KCAP_CODEX_SUBAGENT_IDLE_MINUTES</c>: non-negative
    /// minutes (<c>0</c> = post immediately at <c>task_complete</c>), anything
    /// unset/blank/non-numeric/negative falls back to <see cref="DefaultStopGrace"/>. Pure
    /// so the parsing is unit-testable (mirrors
    /// <c>WatchCommand.ResolveCodexIdleTimeout</c>).
    /// </summary>
    public static TimeSpan ResolveStopGrace(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes >= 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultStopGrace;
}
