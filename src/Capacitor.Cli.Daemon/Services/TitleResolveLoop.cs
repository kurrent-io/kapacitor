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
        public string? PushedTitle;
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

                if (serverTitle is not null && !IsPromptEcho(serverTitle, agent.Prompt)) serverReal = serverTitle;
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

        var best = serverReal ?? native ?? state.Generated;

        if (best is not null && best != state.Applied) {
            _apply(agent.Id, best);
            state.Applied = best;
        }

        // Converge: a locally resolved title reaches the server while it has no real one.
        var local = native ?? state.Generated;
        if (serverReal is null && local is not null && local != state.PushedTitle && agent.SessionId is { } sid) {
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
    /// The watcher's initial title and the daemon's seed are both prefix-truncations of the
    /// launch prompt's first non-blank line, so a server title that (ellipsis stripped) prefixes
    /// that line carries no information the seed doesn't already show.
    /// </summary>
    internal static bool IsPromptEcho(string title, string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return false;

        var t = title.TrimEnd();
        if (t.EndsWith('…')) t = t[..^1];
        else if (t.EndsWith("...", StringComparison.Ordinal)) t = t[..^3];
        t = t.TrimEnd();

        if (t.Length == 0) return true;

        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            return line.StartsWith(t, StringComparison.Ordinal);
        }

        return false;
    }
}
