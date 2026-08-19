using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>
/// Discover + classify Codex rollouts under <c>~/.codex/sessions/</c>. Discovery
/// wraps <see cref="CodexPaths.Discover(string?, DateOnly?)"/> (honouring the
/// <c>--since</c> filter at directory-prune time) and applies <c>--cwd</c> /
/// <c>--session</c> via the same helpers as <see cref="ClaudeImportSource"/>.
/// Classification delegates to <see cref="TranscriptFileClassification.ClassifyAsync"/>
/// with <c>vendor = "codex"</c>. <see cref="ImportSessionAsync"/> is a stub —
/// the orchestrator will wire chain workers in E2.
/// </summary>
internal sealed class CodexImportSource(string? rootOverride = null) : IImportSource {
    readonly string _sessionsDir = rootOverride ?? CodexPaths.Sessions;

    public string Vendor => "codex";

    public bool IsAvailable => Directory.Exists(_sessionsDir);

    public bool SupportsTitleGeneration => true;

    // `--since` prunes whole day directories here and never opens a rollout, so the directory IS the
    // date it compares.
    public DateTimeOffset? DiscoveryAge(DiscoveredSession session) {
        var path = DiscoveredSessionFile.PathOf(session);

        return path is null
            ? session.FirstTimestamp
            : CodexDiscoveryAge.DayFromPath(path) ?? DiscoveredSessionFile.LastWrite(path);
    }
    public bool AttachesChildContentOnReplay => false; // chain-based: never routed

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var transcripts = CodexPaths.Discover(sessionsDir: _sessionsDir, since: filters.Since);

        // Collab subagent rollouts (Codex 0.146+, session_meta thread_source == "subagent")
        // are NOT top-level sessions: they are imported nested under their parent by
        // SessionImporter.ImportSessionAsync's codex descendant walk. Leaving them here would
        // import each child as an unrelated top-level session — and the child's session_meta
        // `session_id` field even holds the PARENT's id (its own id is in `id`), so nothing
        // downstream may ever key a child by `session_id`. Only a DEFINITIVE NotSubagent
        // verdict passes: an Indeterminate header (empty/truncated — a session actively
        // starting while this import runs) is skipped for THIS pass rather than risking a
        // child imported top-level now and nested under its parent on the next run; the next
        // import picks it up once the header is flushed.
        transcripts = [.. transcripts.Where(t => CodexSubagentDiscovery.ReadHeader(t.FilePath).Outcome == CodexSubagentDiscovery.RolloutHeader.NotSubagent)];

        // --session filter — normalize to dashless GUID then exact-match the discovered id.
        if (filters.FilterSession is { } sessionFilter) {
            var normalized = ImportCommand.NormalizeGuid(sessionFilter);
            transcripts = [.. transcripts.Where(t => t.SessionId == normalized)];
        }

        // --cwd filter — Codex stores cwd inside session_meta.payload.cwd; the
        // helper reads it the same way ImportCommand.HandleImport does today.
        if (filters.FilterCwd is { } cwdFilter) {
            var normalizedCwd = cwdFilter.TrimEnd('/');

            transcripts = [
                .. transcripts.Where(t => {
                        var cwd = ImportCommand.ExtractCwdFromTranscript(t.FilePath, codex: true);

                        return cwd?.TrimEnd('/').Equals(normalizedCwd, StringComparison.Ordinal) == true;
                    }
                )
            ];
        }

        IReadOnlyList<DiscoveredSession> result = [
            .. transcripts.Select(t => new DiscoveredSession(
                    SessionId: t.SessionId,
                    Vendor: Vendor,
                    Cwd: null,
                    FirstTimestamp: null,
                    SourceMeta: new Dictionary<string, object?> {
                        ["FilePath"]   = t.FilePath,
                        ["EncodedCwd"] = t.EncodedCwd,
                    }
                )
            )
        ];

        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                  ctx,
            CancellationToken                ct
        ) {
        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)>(sessions.Count);

        foreach (var s in sessions) {
            var filePath   = s.SourceMeta.TryGetValue("FilePath", out var fp) ? fp as string   ?? "" : "";
            var encodedCwd = s.SourceMeta.TryGetValue("EncodedCwd", out var ec) ? ec as string ?? "" : "";
            transcripts.Add((s.SessionId, filePath, encodedCwd));
        }

        return await TranscriptFileClassification.ClassifyAsync(
            ctx.HttpClient,
            ctx.BaseUrl,
            transcripts,
            ctx.MinLines,
            ctx.ExcludedRepos?.ToArray(),
            ct,
            vendor: "codex",
            excludedPaths: ctx.ExcludedPaths?.ToArray()
        );
    }

    public Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) =>
        throw new NotImplementedException("Wired up via ImportChainsAsync in E2.");
}
