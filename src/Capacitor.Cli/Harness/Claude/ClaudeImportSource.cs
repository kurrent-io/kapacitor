using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Discover + classify Claude Code transcripts under <c>~/.claude/projects/</c>.
/// Discovery wraps <see cref="ImportCommand.DiscoverTranscripts(string)"/> and
/// applies the <c>--cwd</c> / <c>--session</c> filters via the existing helpers.
/// Classification delegates to <see cref="TranscriptFileClassification.ClassifyAsync"/>
/// with <c>vendor = "claude"</c>. <see cref="ImportSessionAsync"/> is a stub —
/// the orchestrator will wire chain workers in E2.
/// </summary>
internal sealed class ClaudeImportSource(string? rootOverride = null) : IImportSource {
    readonly string _projectsDir = rootOverride ?? ClaudePaths.Projects;

    public string Vendor => "claude";

    public bool IsAvailable => Directory.Exists(_projectsDir);

    public bool SupportsTitleGeneration => true;
    public bool AttachesChildContentOnReplay => false; // chain-based: never routed

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        var transcripts = ImportCommand.DiscoverTranscripts(_projectsDir);

        // --session filter — normalize to dashless GUID then exact-match the discovered id.
        if (filters.FilterSession is { } sessionFilter) {
            var normalized = ImportCommand.NormalizeGuid(sessionFilter);
            transcripts = [.. transcripts.Where(t => t.SessionId == normalized)];
        }

        // --cwd filter — read the first few transcript lines to recover the cwd
        // (the encoded directory name isn't always trustworthy on its own).
        if (filters.FilterCwd is { } cwdFilter) {
            var normalizedCwd = cwdFilter.TrimEnd('/');

            transcripts = [
                .. transcripts.Where(t => {
                        var cwd = ImportCommand.ExtractCwdFromTranscript(t.FilePath, codex: false);

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
            vendor: "claude",
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
