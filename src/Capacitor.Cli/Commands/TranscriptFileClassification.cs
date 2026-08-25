using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// HEAD-probes the server for each transcript to decide New / Partial /
/// AlreadyLoaded / TooShort / Excluded / ProbeError. Used by both
/// ClaudeImportSource and CodexImportSource — the only difference between
/// them is the metadata extractor, which is selected internally via the
/// <c>vendor</c> string (today: "claude" or "codex").
/// </summary>
internal static class TranscriptFileClassification {
    public static async Task<List<ImportCommand.SessionClassification>> ClassifyAsync(
            ConfigRoot                                                   config,
            HttpClient                                                   httpClient,
            string                                                       baseUrl,
            List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            int                                                          minLines,
            string[]?                                                    excludedRepos,
            CancellationToken                                            ct,
            string                                                       vendor        = "claude",
            Action?                                                      onProbed      = null,
            string[]?                                                    excludedPaths = null
        ) {
        using var probeGate = new SemaphoreSlim(8);
        var       tasks     = new List<Task<ImportCommand.SessionClassification>>(transcripts.Count);

        foreach (var (sessionId, filePath, encodedCwd) in transcripts) {
            tasks.Add(ClassifyOneAsync(config, httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, excludedPaths, probeGate, vendor, onProbed, ct));
        }

        var results = await Task.WhenAll(tasks);

        return [.. results];
    }

    static async Task<ImportCommand.SessionClassification> ClassifyOneAsync(
            ConfigRoot        config,
            HttpClient        httpClient,
            string            baseUrl,
            string            sessionId,
            string            filePath,
            string            encodedCwd,
            int               minLines,
            string[]?         excludedRepos,
            string[]?         excludedPaths,
            SemaphoreSlim     probeGate,
            string            vendor,
            Action?           onProbed,
            CancellationToken ct
        ) {
        try {
            return await ClassifyOneCoreAsync(config, httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, excludedPaths, probeGate, vendor, ct);
        } finally {
            onProbed?.Invoke();
        }
    }

    static async Task<ImportCommand.SessionClassification> ClassifyOneCoreAsync(
            ConfigRoot        config,
            HttpClient        httpClient,
            string            baseUrl,
            string            sessionId,
            string            filePath,
            string            encodedCwd,
            int               minLines,
            string[]?         excludedRepos,
            string[]?         excludedPaths,
            SemaphoreSlim     probeGate,
            string            vendor,
            CancellationToken ct
        ) {
        var isCodex = vendor == "codex";
        var meta    = isCodex ? ImportCommand.ExtractCodexSessionMetadata(filePath) : ImportCommand.ExtractSessionMetadata(filePath);

        switch (isCodex) {
            // Short-circuit: kcap's own sub-sessions (title / what's-done) never get imported.
            // Codex rollouts have no analog, so the check is Claude-only.
            case false when TitleGenerator.IsCapacitorSubSession(filePath):
                return new() {
                    SessionId  = sessionId,
                    FilePath   = filePath,
                    EncodedCwd = encodedCwd,
                    Meta       = meta,
                    Status     = ImportCommand.ClassificationStatus.InternalSubSession,
                    Vendor     = vendor,
                };
            // Codex rollouts carry the session id in two places — the trailing UUID in the
            // filename (which the rest of the pipeline trusts as the canonical id used for
            // probe URLs and hook payloads) and `session_meta.payload.id`. Validate they
            // agree so a renamed/copied file can't import under the wrong server session.
            case true when meta.SessionId is { } innerId
             && Guid.TryParse(innerId, out var innerGuid)
             && innerGuid.ToString("N") != sessionId:
                return new() {
                    SessionId        = sessionId,
                    FilePath         = filePath,
                    EncodedCwd       = encodedCwd,
                    Meta             = meta,
                    Status           = ImportCommand.ClassificationStatus.ProbeError,
                    Vendor           = vendor,
                    ProbeErrorReason = "codex session id mismatch (filename vs session_meta.payload.id)",
                };
        }

        // Probe the server BEFORE scanning the file. On re-runs the probe returns
        // 204 (AlreadyLoaded) quickly and we never need to read the transcript.
        ImportCommand.ClassificationStatus status;
        var                                resumeFromLine   = 0;
        string?                            probeErrorReason = null;

        await probeGate.WaitAsync(ct);

        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", ct: ct);

            switch (resp.StatusCode) {
                case HttpStatusCode.NotFound:
                    status = ImportCommand.ClassificationStatus.New;

                    break;
                case HttpStatusCode.NoContent:
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;

                    break;
                default:
                    if (resp.IsSuccessStatusCode) {
                        var       json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc  = JsonDocument.Parse(json);

                        if (doc.RootElement.Num("last_line_number") is { } lastLine) {
                            resumeFromLine = (int)lastLine + 1;
                            status         = ImportCommand.ClassificationStatus.Partial;
                        } else {
                            status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                        }
                    } else {
                        status           = ImportCommand.ClassificationStatus.ProbeError;
                        probeErrorReason = $"HTTP {(int)resp.StatusCode}";
                    }

                    break;
            }
        } catch (HttpRequestException ex) {
            status           = ImportCommand.ClassificationStatus.ProbeError;
            probeErrorReason = ex.Message;
        } finally {
            probeGate.Release();
        }

        // Read enough of the local transcript to satisfy two checks at once:
        //   1. TooShort — fewer lines than minLines.
        //   2. False Partial — server says last_line_number = N but the local
        //      transcript has no lines past index N (resumeFromLine would be
        //      N+1 with nothing to send).
        // CountLinesUpTo early-exits at the threshold, so the read cost is
        // bounded by Math.Max(minLines, resumeFromLine + 1) lines.
        if (status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial) {
            var threshold = Math.Max(
                minLines,
                status == ImportCommand.ClassificationStatus.Partial ? resumeFromLine + 1 : 0
            );

            if (threshold > 0) {
                var observedLines = CountLinesUpTo(filePath, threshold);

                if (minLines > 0 && observedLines < minLines) {
                    return new() {
                        SessionId  = sessionId,
                        FilePath   = filePath,
                        EncodedCwd = encodedCwd,
                        Meta       = meta,
                        Status     = ImportCommand.ClassificationStatus.TooShort,
                        TotalLines = observedLines,
                        Vendor     = vendor,
                    };
                }

                // Server has lines >= the local transcript — nothing to resume.
                if (status == ImportCommand.ClassificationStatus.Partial && observedLines <= resumeFromLine) {
                    status         = ImportCommand.ClassificationStatus.AlreadyLoaded;
                    resumeFromLine = 0;
                }
            }
        }

        // Flag excluded repos/paths for New/Partial sessions. Resolution (include or skip?)
        // happens later in HandleImport, where we can batch prompts by key.
        string? excludedRepoKey = null;
        string? excludedPathKey = null;

        if (status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial) {
            var cwd = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(encodedCwd);

            if (cwd is not null) {
                if (excludedRepos is { Length: > 0 }) {
                    // Classification only needs owner/repo for the exclusion key — skip PR detection.
                    var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false);

                    if (repo?.Owner is not null && repo.RepoName is not null) {
                        var key = $"{repo.Owner}/{repo.RepoName}";

                        if (excludedRepos.Contains(key, StringComparer.OrdinalIgnoreCase)) {
                            excludedRepoKey = key;
                        }
                    }
                }

                if (excludedPathKey is null && excludedPaths is { Length: > 0 }) {
                    foreach (var entry in excludedPaths) {
                        if (PathExclusion.IsExcluded(cwd, [entry])) {
                            excludedPathKey = PathExclusion.Normalize(entry);
                            break;
                        }
                    }
                }
            }
        }

        // TotalLines is only meaningful for TooShort sessions (where we know the exact
        // count because it's below the threshold). Leave it at 0 for other statuses —
        // we only read enough of the file to confirm the TooShort filter didn't apply.
        return new ImportCommand.SessionClassification {
            SessionId        = sessionId,
            FilePath         = filePath,
            EncodedCwd       = encodedCwd,
            Meta             = meta,
            Status           = status,
            ResumeFromLine   = resumeFromLine,
            ProbeErrorReason = probeErrorReason,
            ExcludedRepoKey  = excludedRepoKey,
            ExcludedPathKey  = excludedPathKey,
            Vendor           = vendor,
        };
    }

    /// <summary>
    /// Count transcript lines with an early exit once <paramref name="threshold"/> lines
    /// have been observed. The caller only needs to distinguish "below threshold" from
    /// "at or above"; scanning further would be wasted I/O on large transcripts.
    /// Returns the exact count when below threshold, or exactly <paramref name="threshold"/>
    /// once the threshold is reached.
    /// </summary>
    static int CountLinesUpTo(string path, int threshold) {
        try {
            if (!File.Exists(path)) return 0;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;
            while (count < threshold && reader.ReadLine() is not null) count++;

            return count;
        } catch {
            // On transient I/O errors (locked file, permissions hiccup) treat the
            // transcript as "not too short" so the caller proceeds to probe/import
            // rather than silently classifying it as TooShort and skipping forever.
            return threshold;
        }
    }
}
