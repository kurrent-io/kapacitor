using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The slice of an agent the title resolver reads; snapshotted per tick.</summary>
internal sealed record TitleAgentView(
    string Id, string Vendor, string? Prompt, string? SessionId, string? TranscriptPath, DateTime CreatedAt);

/// <summary>The server's title surface for one session: read the current title, push a locally
/// resolved one via the set-title path.</summary>
internal interface ITitleServerPort {
    Task<string?> GetTitleAsync(string sessionId, CancellationToken ct);
    Task<bool> PushTitleAsync(string sessionId, string title, CancellationToken ct);
}

/// <summary>
/// Resolves a display title per hosted agent, one ladder per tick: the vendor's native
/// transcript title first, the server's real title as the authority once one exists, and a
/// single local generation as the late fallback. A locally resolved title is pushed through
/// set-title when the session is recorded, so web and desktop converge on the same string.
///
/// <para>A server title that merely echoes the launch prompt is the watcher's initial
/// truncated-prompt title, not a real one: adopting it would overwrite a better native title
/// with the string the seed already shows, and treating it as real would block both the push
/// and the generation fallback.</para>
///
/// <para>The ladder never downgrades: a lane that stops producing (a transient read failure,
/// a server hiccup) keeps the last applied title rather than blanking it.</para>
/// </summary>
internal sealed class TitleResolveLoop {
    /// Generation costs a headless LLM call, and for a recorded session the watcher is already
    /// making one — it typically lands within a minute. Generate only after the server has
    /// stayed silent this long.
    static readonly TimeSpan GenerationGrace = TimeSpan.FromMinutes(5);

    sealed class AgentTitleState {
        public string? Applied;
        public string? Generated;
        /// Last title the server was OBSERVED holding after our push — suppresses re-pushes
        /// only while a successful read keeps confirming it; a read observing anything else
        /// (silence included) proves the confirmation stale and re-arms the push.
        public string? PushedTitle;
        /// Every title a push was ATTEMPTED with, confirmed or not. An attempt whose response
        /// was lost may still have committed and surface on a later read — even after newer
        /// attempts — so each must keep counting as "ours" when the server echoes it, or the
        /// loop would adopt its own stale title as independent authority. Bounded: a native
        /// title revises at most once per tick, and overflow trades memory for re-opening
        /// only a >32-revision-stale echo.
        public readonly HashSet<string> PushAttempts = new(StringComparer.Ordinal);
        /// The authoritative server title as of the last SUCCESSFUL read. Held across failed
        /// reads so an outage tick cannot demote the applied title down the ladder; cleared
        /// only by a successful read proving the server silent.
        public string? ServerTitle;
        public bool GenerationAttempted;
    }

    readonly Func<IReadOnlyList<TitleAgentView>> _agents;
    readonly Action<string, string> _apply;
    readonly ITitleServerPort _server;
    readonly Func<TitleAgentView, string?> _nativeLane;
    readonly Func<TitleAgentView, CancellationToken, Task<string?>> _generateLane;
    readonly TimeProvider _time;
    readonly ILogger _logger;
    readonly Dictionary<string, AgentTitleState> _states = [];

    public TitleResolveLoop(
            Func<IReadOnlyList<TitleAgentView>> agents,
            Action<string, string> apply,
            ITitleServerPort server,
            Func<TitleAgentView, string?> nativeLane,
            Func<TitleAgentView, CancellationToken, Task<string?>> generateLane,
            TimeProvider time,
            ILogger logger) {
        _agents       = agents;
        _apply        = apply;
        _server       = server;
        _nativeLane   = nativeLane;
        _generateLane = generateLane;
        _time         = time;
        _logger       = logger;
    }

    public async Task TickAsync(CancellationToken ct) {
        var agents = _agents();

        var live = new HashSet<string>(agents.Select(a => a.Id), StringComparer.Ordinal);
        foreach (var gone in _states.Keys.Where(id => !live.Contains(id)).ToList()) _states.Remove(gone);

        foreach (var agent in agents) {
            ct.ThrowIfCancellationRequested();

            if (!_states.TryGetValue(agent.Id, out var state)) _states[agent.Id] = state = new AgentTitleState();

            try {
                await ResolveOneAsync(agent, state, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title resolution failed for agent {AgentId} — keeping current title", agent.Id);
            }
        }
    }

    async Task ResolveOneAsync(TitleAgentView agent, AgentTitleState state, CancellationToken ct) {
        string? native = null;
        try {
            native = Normalize(_nativeLane(agent));
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Native title extraction failed for agent {AgentId}", agent.Id);
        }

        string? serverReal   = null;
        var     serverReadOk = agent.SessionId is null; // an unrecorded agent has no server to ask
        if (agent.SessionId is { } sessionId) {
            try {
                var serverTitle = Normalize(await _server.GetTitleAsync(sessionId, ct));
                serverReadOk = true;

                // A title the loop itself pushed (or may have pushed — an unacknowledged
                // attempt can still have committed) coming back is not an independent server
                // title: treating it as one would freeze the ladder on our own echo and a
                // later native revision could never advance past it.
                if (serverTitle is not null && !state.PushAttempts.Contains(serverTitle)
                 && !IsPromptEcho(serverTitle, agent.Prompt)) {
                    serverReal = serverTitle;
                }

                state.ServerTitle = serverReal;

                // Suppression holds only while the server is observed still holding the pushed
                // title; anything else — silence included — proves the confirmation stale, and
                // the local title must be able to converge again.
                if (state.PushedTitle is not null && serverTitle != state.PushedTitle) state.PushedTitle = null;
            } catch (Exception ex) {
                // An unreadable server is not a silent one: generation must not spend an LLM
                // call on a session whose watcher-made title merely couldn't be fetched.
                _logger.LogDebug(ex, "Server title read failed for session {SessionId}", sessionId);
            }
        }

        if (serverReadOk && serverReal is null && native is null && !state.GenerationAttempted
         && !string.IsNullOrWhiteSpace(agent.Prompt)
         && _time.GetUtcNow() - DateTime.SpecifyKind(agent.CreatedAt, DateTimeKind.Utc) >= GenerationGrace) {
            state.GenerationAttempted = true;
            try {
                state.Generated = Normalize(await _generateLane(agent, ct));
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title generation failed for agent {AgentId}", agent.Id);
            }
        }

        // On a failed read the last successfully-read authority stands in, so an outage tick
        // cannot demote the applied title down the ladder.
        var best = (serverReadOk ? serverReal : state.ServerTitle) ?? native ?? state.Generated;

        if (best is not null && best != state.Applied) {
            _apply(agent.Id, best);
            state.Applied = best;
        }

        // Converge: a locally resolved title reaches the server while it verifiably has no real
        // one. A failed read blocks the push too — an authoritative title whose presence merely
        // couldn't be checked must not be overwritten.
        var local = native ?? state.Generated;
        if (serverReadOk && serverReal is null && local is not null && local != state.PushedTitle
         && agent.SessionId is { } sid) {
            if (state.PushAttempts.Count >= 32) state.PushAttempts.Clear();
            state.PushAttempts.Add(local);

            var pushed = false;
            try {
                pushed = await _server.PushTitleAsync(sid, local, ct);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Title push failed for session {SessionId}", sid);
            }

            if (pushed) state.PushedTitle = local;
        }
    }

    static string? Normalize(string? title) {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var trimmed = title.Trim();
        return trimmed.Length > 120 ? trimmed[..120] : trimmed;
    }

    /// <summary>
    /// The watcher's initial title and the daemon's seed take exactly two forms: the launch
    /// prompt's first non-blank line verbatim (when short enough), or a prefix of it with a
    /// trailing ellipsis marking the cut. Only those forms are echoes — a bare prefix without
    /// the ellipsis can be a genuine generated title that happens to open like the prompt, and
    /// discarding it would trigger a duplicate local generation.
    /// </summary>
    internal static bool IsPromptEcho(string title, string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        var t         = title.TrimEnd();
        var truncated = false;
        if (t.EndsWith('…')) { t = t[..^1]; truncated = true; }
        else if (t.EndsWith("...", StringComparison.Ordinal)) { t = t[..^3]; truncated = true; }
        t = t.TrimEnd();

        if (t.Length == 0) return true;

        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            return truncated ? line.StartsWith(t, StringComparison.Ordinal) : line == t;
        }

        return false;
    }
}
