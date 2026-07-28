using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

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
    public bool AttachesChildContentOnReplay => false; // chain-based: never routed

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var transcripts = CodexPaths.Discover(sessionsDir: _sessionsDir, since: filters.Since);

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
