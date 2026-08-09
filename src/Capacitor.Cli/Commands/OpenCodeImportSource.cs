using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Historical import of OpenCode sessions from its SQLite db. Routed source
/// (FilePath="" → ImportSessionAsync), modeled on PiImportSource/GeminiImportSource.
/// Roots are main sessions; child sessions (parent_id set) are imported as subagents
/// of their parent. Completeness is tracked client-side (the import ledger) since the
/// server exposes no ended signal; repair replays above the server HWM (idempotent by
/// prt_ id). See docs/superpowers/specs/2026-06-26-opencode-import-design.md.
/// </summary>
internal sealed class OpenCodeImportSource : IImportSource {
    readonly string               _dbPath;
    readonly OpenCodeImportLedger _ledger;
    readonly object               _ledgerLock = new(); // routed imports may run concurrently

    public OpenCodeImportSource(string? dbPathOverride = null, string? ledgerPathOverride = null) {
        _dbPath = dbPathOverride ?? Path.Combine(OpenCodePaths.DataDir(), "opencode.db");
        _ledger = OpenCodeImportLedger.Load(ledgerPathOverride ?? OpenCodeImportLedger.DefaultPath());
    }

    public string Vendor => "opencode";
    public bool   IsAvailable => File.Exists(_dbPath);
    public bool   SupportsTitleGeneration => false; // routed; native title forwarded via /hooks/set-title
    public bool   AttachesChildContentOnReplay => false; // AlreadyLoaded early-returns before any POST

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static string Norm(string p) {
        try { return Path.GetFullPath(p).TrimEnd('/', '\\'); } catch { return p.TrimEnd('/', '\\'); }
    }

    // OpenCode stores epoch MILLISECONDS (observed ~1.78e12). Guard against a future
    // seconds-based column: a value too small to be plausible ms is read as seconds.
    static DateTimeOffset FromEpoch(long v) =>
        v < 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeSeconds(v)
            : DateTimeOffset.FromUnixTimeMilliseconds(v);

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        if (!File.Exists(_dbPath)) return Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);

        var normalizedCwd = filters.FilterCwd is { } cwd ? Norm(cwd) : null;
        var sinceMs = filters.Since is { } s
            ? new DateTimeOffset(s.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
            : (long?)null;

        var result = new List<DiscoveredSession>();
        try {
            // A corrupt / schema-drifted opencode.db must NOT crash the whole `kcap import`
            // (other vendors share the run). On open/query failure, warn and skip OpenCode.
            using var db = new OpenCodeDb(_dbPath);
            foreach (var row in db.QueryRoots()) {
                ct.ThrowIfCancellationRequested();
                if (filters.FilterSession is { } fs && !string.Equals(row.Id, fs, StringComparison.Ordinal)) continue;
                if (normalizedCwd is not null &&
                    (row.Directory is null || !Norm(row.Directory).Equals(normalizedCwd, PathComparison))) continue;
                // A null TimeCreated ("unknown") is best-effort KEPT — only a KNOWN-older
                // row is skipped by the since filter.
                if (sinceMs is { } cutoff && row.TimeCreated is { } tc && tc < cutoff) continue;

                result.Add(new DiscoveredSession(
                    SessionId:      row.Id, // raw ses_… — no GUID normalization
                    Vendor:         Vendor,
                    Cwd:            row.Directory,
                    FirstTimestamp: row.TimeCreated is { } created ? FromEpoch(created) : null,
                    SourceMeta:     new Dictionary<string, object?> {
                        ["Title"]       = row.Title,
                        ["TimeUpdated"] = row.TimeUpdated,
                    }));
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] OpenCode discovery skipped ({_dbPath}): {SurfaceCause(ex)}");
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);
        }
        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
        IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) {
        // DiscoverAsync defensively swallows db-open failures (returns []), but Classify
        // is still invoked for every source. Don't open the db for an empty slice — a
        // single vendor's unreadable db must skip OpenCode, not crash the whole import run.
        if (sessions.Count == 0) return [];

        // Discovery succeeded earlier, but the db may have become unreadable since (deleted,
        // perms, WAL sidecars, or an on-demand native-lib fetch failure on this second open).
        // Fail only OpenCode — mark the slice as probe errors instead of throwing out of the
        // multi-vendor probe task.
        OpenCodeDb opened;
        try {
            opened = new OpenCodeDb(_dbPath);
        } catch (Exception ex) {
            var reason = $"opencode.db open failed: {SurfaceCause(ex)}";
            return [.. sessions.Select(s =>
                Make(s, MetaFor(s), ImportCommand.ClassificationStatus.ProbeError, 0, reason))];
        }
        using var db = opened;
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        foreach (var s in sessions) {
            ct.ThrowIfCancellationRequested();

            var meta = MetaFor(s);

            int total, importable; string fingerprint; bool countTruncated;
            try {
                (total, importable, fingerprint, countTruncated) = ComputeClassificationInfo(db, s.SessionId);
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, 0, "transcript read failed"));
                continue;
            }

            if (countTruncated) {
                // The below-cap counting/signature walk hit MaxCountingNodes, so the fingerprint
                // can't see the WHOLE omitted subtree — a ledger AlreadyLoaded hit can no longer
                // be trusted to reflect completeness. Surface it and fall through past the
                // ledger check below (never silent).
                Console.Error.WriteLine(
                    $"[kcap] opencode: root {s.SessionId} descendant-count ceiling " +
                    $"({OpenCodeDb.MaxCountingNodes}) hit — omitted-subtree signature is a lower " +
                    "bound; skipping the completeness ledger for this session.");
            }

            if (importable < ctx.MinLines) {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.TooShort, total));
                continue;
            }

            // Completeness is tracked client-side (the ledger): a hit on this server with a
            // matching content fingerprint (parent transcript + children) means we already
            // fully imported it AND it hasn't changed since — skip. Never trusted when the
            // below-cap counting walk was truncated (see above). --reimport bypasses the
            // ledger entirely so a session the ledger wrongly believes complete (e.g. deleted
            // server-side after import) re-classifies as New/Partial and re-sends; the resend
            // is idempotent via canonical prt_ ids, and a successful import refreshes the entry.
            if (!ctx.Reimport) {
                lock (_ledgerLock) {
                    if (!countTruncated && _ledger.IsComplete(ctx.BaseUrl, s.SessionId, fingerprint)) {
                        results.Add(Make(s, meta, ImportCommand.ClassificationStatus.AlreadyLoaded, total));
                        continue;
                    }
                }
            }

            // Not in the ledger → New (no server watermark) or Partial-repair (watermark
            // present → replay ABOVE the HWM, idempotent via canonical prt_ ids). Carry the
            // fingerprint so ImportSessionAsync records it verbatim once session-end succeeds.
            int? hwm;
            try {
                hwm = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, agentId: null, ct);
            } catch (OperationCanceledException) {
                throw; // cancellation is not a probe error
            } catch {
                results.Add(Make(s, meta, ImportCommand.ClassificationStatus.ProbeError, total, "watermark probe failed"));
                continue;
            }

            var meta2 = WithFingerprint(s.SourceMeta!, fingerprint);
            var c = (hwm is { } h
                ? Make(s, meta, ImportCommand.ClassificationStatus.Partial, total) with { ResumeFromLine = h }
                : Make(s, meta, ImportCommand.ClassificationStatus.New, total)) with { SourceMeta = meta2 };
            results.Add(c);
        }
        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
        ImportCommand.SessionClassification c, ImportContext ctx, CancellationToken ct) {
        if (c.Status == ImportCommand.ClassificationStatus.AlreadyLoaded) return ImportOutcome.Skipped;

        var title  = c.SourceMeta!.TryGetValue("Title", out var t) ? t as string : null;
        var repair = c.Status == ImportCommand.ClassificationStatus.Partial;
        // Repair replays the FULL transcript with line numbers offset above the server
        // HWM (ResumeFromLine), so previously-accepted content dedupes by prt_ id and the
        // gap lands. New imports send from 0.
        var lineOffset = repair ? checked(c.ResumeFromLine + 1) : 0;

        // 1. session-start (lifecycle-before-transcript; idempotent server-side).
        var startPayload = BuildSessionStartPayload(c.SessionId, c.Meta.Cwd, c.Meta.FirstTimestamp, ctx.ForcePrivate);
        // Step 3 visibility stamp — New-only, and never overrides the existing forcePrivate
        // "private" stamp above (mutually exclusive: this only fires when !ctx.ForcePrivate).
        if (!ctx.ForcePrivate && c.Status == ImportCommand.ClassificationStatus.New && ctx.DefaultVisibility is not null) {
            startPayload["default_visibility"] = ctx.DefaultVisibility;
        }

        if (!await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-start/opencode", startPayload, ct))
            return ImportOutcome.Failed;

        // 2. parent transcript (synthesize to a temp file; SendTranscriptBatches needs a path).
        int sent;
        var tmpFile = Path.Combine(Path.GetTempPath(), $"kcap-oc-{c.SessionId}-{Guid.NewGuid():N}.jsonl");
        try {
            using var db = new OpenCodeDb(_dbPath);
            await using (var w = new StreamWriter(tmpFile)) {
                foreach (var line in db.SynthesizeLines(c.SessionId)) await w.WriteLineAsync(line);
            }
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient: ctx.HttpClient, baseUrl: ctx.BaseUrl, sessionId: c.SessionId,
                filePath: tmpFile, agentId: null, startLine: 0, vendor: Vendor,
                lineNumberOffset: lineOffset, failOnError: true);
        } catch (OperationCanceledException) {
            throw; // cancellation is not an import failure — let it propagate
        } catch {
            // Strict: abort before session-end. A partial send may have advanced the HWM,
            // but the ledger is written only after session-end — which we never reach — so
            // the session is NOT recorded, and a re-run classifies it Partial and repairs.
            return ImportOutcome.Failed;
        } finally {
            try { File.Delete(tmpFile); } catch { }
        }

        // 3. descendants as subagents — BEFORE session-end so SubagentCompleted precedes
        //    SessionEnded. A subagent-start posted here may REACTIVATE an already-Ended root
        //    (Model A always routes SubagentStarted through
        //    EnsureSessionExists(isReactivation:true)) — so per the finally-style re-close
        //    contract, the session-end re-assertion below must run regardless of whether this
        //    step throws. An early `Failed` return here would otherwise leave a reactivated root
        //    stuck Active with no re-close ever posted.
        //
        //    Cancellation is handled the SAME way: a cancellation that arrives after a
        //    descendant lifecycle already reactivated the root (e.g. right after a successful
        //    subagent-start/content send, observed at the NEXT loop iteration's
        //    ct.ThrowIfCancellationRequested()) must not skip the re-close below — it's recorded
        //    here and rethrown only AFTER step 5 runs, never before.
        var descendantsOk           = true;
        OperationCanceledException? cancellation = null;
        try {
            await ImportDescendantsAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, ct);
        } catch (OperationCanceledException oce) {
            cancellation = oce;
        } catch {
            descendantsOk = false;
        }

        // 4. native title (best-effort, like Copilot/Kiro). Skipped on cancellation — no new
        //    outbound work once cancelled — but the terminal re-close in step 5 still runs.
        if (cancellation is null && !string.IsNullOrWhiteSpace(title))
            await PostSetTitleAsync(ctx.HttpClient, ctx.BaseUrl, c.SessionId, title!, ct);

        // 5. session-end — posted regardless of step 3's outcome, INCLUDING cancellation (see
        //    the finally contract above). Uses CancellationToken.None deliberately: `ct` may
        //    already be cancelled, and the whole point of this call is to re-assert the
        //    terminal state even so — PostHookAsync/PostWithRetryAsync still bound this to a
        //    fixed wall-clock budget (~30s), so it cannot hang.
        var endOk = await PostHookAsync(ctx.HttpClient, ctx.BaseUrl, "session-end/opencode",
            BuildSessionEndPayload(c.SessionId, c.Meta.Cwd, c.Meta.LastTimestamp), CancellationToken.None);

        // `cancellation` above only catches an OCE THROWN BY step 3. A cancellation that arrives
        // AFTER descendants finish — most notably while step 4's PostSetTitleAsync is awaited,
        // which catches OperationCanceledException internally (see its comment) — would
        // otherwise never be observed: this call to session-end deliberately uses
        // CancellationToken.None, and nothing downstream re-checked `ct`. Now that the
        // uncancellable re-close has run (preserving the contract above), check `ct` one more
        // time and propagate — preferring the already-captured exception (with its original
        // throw site) when one exists — BEFORE evaluating endOk or writing completeness, so a
        // run cancelled at any point never marks the ledger complete.
        if (cancellation is not null) {
            ExceptionDispatchInfo.Capture(cancellation).Throw();
        } else if (ct.IsCancellationRequested) {
            throw new OperationCanceledException(ct);
        }

        if (!endOk) return ImportOutcome.Failed;
        if (!descendantsOk) return ImportOutcome.Failed;

        // 6. Record completeness in the ledger — ONLY now, after a fully successful import.
        //    The fingerprint was computed at classify time and carried on SourceMeta.
        if (c.SourceMeta!.TryGetValue("Fingerprint", out var fpObj) && fpObj is string fp) {
            lock (_ledgerLock) {
                _ledger.MarkComplete(ctx.BaseUrl, c.SessionId, fp);
                _ledger.Save();
            }
        }

        return repair ? ImportOutcome.Resumed : (sent == 0 ? ImportOutcome.Skipped : ImportOutcome.Loaded);
    }

    /// <summary>
    /// Imports every TRANSITIVE descendant (recursive <c>parent_id</c> walk, depth-capped) as a
    /// DIRECT subagent of the top-level root — the existing flatten shape; Model A's stream key
    /// can't express deeper nesting, and flattening preserves content that today is silently
    /// dropped beyond the first level. Descendants beyond the depth cap are surfaced (never
    /// silent) via a stderr diagnostic. Throws on the first descendant whose lifecycle can't be
    /// posted (caller wraps this in a finally-style re-close).
    /// </summary>
    async Task ImportDescendantsAsync(HttpClient client, string baseUrl, string rootId, CancellationToken ct) {
        using var db = new OpenCodeDb(_dbPath);
        var (descendants, omitted, _, countTruncated) = db.QueryDescendants(rootId); // BFS, per-level ordered like QueryChildren

        if (omitted > 0) {
            // Once the below-cap counting ceiling is hit, `omitted` is a LOWER BOUND, not an
            // exact count — say so, rather than implying completeness.
            var lowerBoundNote = countTruncated ? " (lower bound — counting ceiling hit)" : "";
            Console.Error.WriteLine(
                $"[kcap] opencode: root {rootId} descendants_omitted={omitted}{lowerBoundNote} " +
                $"(depth cap {OpenCodeDb.MaxDescendantDepth} exceeded)");
        }

        foreach (var d in descendants) {
            ct.ThrowIfCancellationRequested();
            var child     = d.Row;
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(child.Id);
            var agentType = ResolveAgentType(db, child.Id);

            // No per-child completeness gate: a complete parent is skipped wholesale via the
            // ledger, so descendants are only reached during an incomplete parent's import.
            // Offset above the descendant's (rootId, agentId) HWM so a repair replays above
            // ingested content.
            var chwm = await FetchServerLastLineAsync(client, baseUrl, rootId, agentId, ct);
            var childOffset = chwm is { } v ? checked(v + 1) : 0;

            var tmp = Path.Combine(Path.GetTempPath(), $"kcap-oc-{child.Id}-{Guid.NewGuid():N}.jsonl");
            try {
                await using (var w = new StreamWriter(tmp)) {
                    foreach (var line in db.SynthesizeLines(child.Id)) await w.WriteLineAsync(line);
                }

                // fail-closed: no content unless the subagent registered first.
                var startOk = await PostHookAsync(client, baseUrl, "subagent-start",
                    OpenCodeSubagentDiscovery.BuildStartPayload(rootId, agentId, agentType, tmp), ct);
                if (!startOk) throw new HttpRequestException($"subagent-start failed for {child.Id}");

                await SessionImporter.SendTranscriptBatches(
                    httpClient: client, baseUrl: baseUrl, sessionId: rootId,
                    filePath: tmp, agentId: agentId, startLine: 0, vendor: Vendor,
                    lineNumberOffset: childOffset, failOnError: true);

                if (!await PostHookAsync(client, baseUrl, "subagent-stop",
                        OpenCodeSubagentDiscovery.BuildStopPayload(rootId, agentId, agentType, tmp), ct))
                    throw new HttpRequestException($"subagent-stop failed for {child.Id}");
            } finally {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    static string ResolveAgentType(OpenCodeDb db, string childId) {
        foreach (var line in db.SynthesizeLines(childId)) {
            try {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("info", out var info) &&
                    info.TryGetProperty("agent", out var a) && a.GetString() is { Length: > 0 } agent)
                    return agent;
            } catch { }
        }
        return "subagent";
    }

    static SessionMetadata MetaFor(DiscoveredSession s) => new() {
        SessionId      = s.SessionId,
        Cwd            = s.Cwd,
        FirstTimestamp = s.FirstTimestamp,
        LastTimestamp  = s.SourceMeta!.TryGetValue("TimeUpdated", out var tu) && tu is long tums
            ? FromEpoch(tums) : null,
    };

    /// <summary>
    /// Surface the actionable cause of a SQLite open failure, not "a type initializer threw
    /// an exception". Prefer the on-demand native-lib failure (SqliteNativeResolver throws a
    /// DllNotFoundException with download/mirror guidance) anywhere in the chain; otherwise the
    /// base exception (e.g. a genuinely corrupt db's SQLite error — GetBaseException would
    /// otherwise over-unwrap a download failure to its inner DNS/socket error).
    /// </summary>
    static string SurfaceCause(Exception ex) {
        Exception cause = ex.GetBaseException();
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is DllNotFoundException) { cause = e; break; }
        return cause.Message;
    }

    static ImportCommand.SessionClassification Make(
        DiscoveredSession s, SessionMetadata meta, ImportCommand.ClassificationStatus status,
        int totalLines, string? probeError = null) => new() {
        SessionId        = s.SessionId,
        FilePath         = "",   // routed phase
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = "opencode",
        ProbeErrorReason = probeError,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    // Fingerprint-schema version. The fingerprint used to cover only the parent transcript +
    // its DIRECT QueryChildren, so an AlreadyLoaded ledger hit could skip a root wholesale
    // forever even though its grandchildren were never imported (single-level discovery
    // silently dropped them). The fingerprint is now recursive over EVERY descendant AND the
    // omitted-subtree signature beyond the import cap (see below), and this version marker is
    // fed into the hash too — bump it whenever the fingerprint's shape/inputs change, so every
    // pre-upgrade ledger entry is cache-busted and the first post-upgrade import re-classifies
    // and picks up whatever the old fingerprint couldn't see.
    const string FingerprintSchemaVersion = "v3-recursive-descendants-with-omitted-signature";

    // Parent total reconstructed lines, importable lines (gates MinLines), a content
    // fingerprint over the parent transcript AND every transitive descendant — the ledger key,
    // so a same-line-count mutation (tool completing, in-place edit, changed/added/removed
    // descendant at any depth) re-imports — and whether the below-cap counting walk was
    // truncated: when true, the fingerprint's omitted-subtree signature is a LOWER BOUND, and
    // the caller must not trust a ledger match as complete.
    static (int Total, int Importable, string Fingerprint, bool CountTruncated) ComputeClassificationInfo(OpenCodeDb db, string sessionId) {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Feed(string s) { hash.AppendData(Encoding.UTF8.GetBytes(s)); hash.AppendData("\n"u8); }

        Feed(" fpv:" + FingerprintSchemaVersion);

        int total = 0, importable = 0;
        foreach (var line in db.SynthesizeLines(sessionId)) {  // parent reader fully drained before descendant queries
            total++;
            if (OpenCodeDb.IsImportRelevantLine(line)) importable++;
            Feed(line);
        }
        var (descendants, _, omittedIds, countTruncated) = db.QueryDescendants(sessionId);
        // Fed unconditionally, including an EMPTY descendant set, so the fingerprint always
        // reflects the descendant edge shape, not merely its presence.
        Feed(" descendants:" + descendants.Count);
        foreach (var d in descendants) {
            Feed(" child:" + d.Row.Id + ":" + d.Depth);
            foreach (var line in db.SynthesizeLines(d.Row.Id)) Feed(line);
        }
        // Fed too, by id — a descendant BEYOND the import cap is never imported, but its
        // presence/absence must still invalidate the ledger: the old fingerprint hashed only
        // the in-cap descendant list, so a newly-reachable capped
        // descendant left the fingerprint unchanged and an AlreadyLoaded hit skipped the session
        // wholesale forever — silently, since ImportDescendantsAsync (and its
        // descendants_omitted diagnostic) never even ran.
        Feed(" omitted:" + omittedIds.Count);
        foreach (var oid in omittedIds) Feed(" omitted_child:" + oid);
        return (total, importable, Convert.ToHexString(hash.GetHashAndReset()), countTruncated);
    }

    static Dictionary<string, object?> WithFingerprint(IReadOnlyDictionary<string, object?> src, string fingerprint) {
        var d = new Dictionary<string, object?>(src) { ["Fingerprint"] = fingerprint };
        return d;
    }

    static async Task<int?> FetchServerLastLineAsync(
        HttpClient http, string baseUrl, string sessionId, string? agentId, CancellationToken ct) {
        var url = $"{baseUrl}/api/sessions/{sessionId}/last-line" + (agentId is not null ? $"?agentId={agentId}" : "");
        using var resp = await http.GetWithRetryAsync(url, ct: ct);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32() : null;
    }

    static JsonObject BuildSessionStartPayload(string sid, string? cwd, DateTimeOffset? startedAt, bool forcePrivate) {
        var p = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sid,
            ["source"]          = "startup",
        };
        if (cwd is not null) p["cwd"] = cwd;
        // Fail-open git-root discovery, mirroring ImportChainsAsync
        // so routed imports carry the same workspace_root the file-based path does.
        if (cwd is not null && GitRepository.FindRoot(cwd) is { } workspaceRoot) p["workspace_root"] = workspaceRoot;
        if (startedAt is { } ts) p["started_at"] = ts.ToString("O");
        if (forcePrivate) p["default_visibility"] = "private";
        p["origin"] = ImportOrigins.Historical;
        return p;
    }

    static JsonObject BuildSessionEndPayload(string sid, string? cwd, DateTimeOffset? endedAt) {
        var p = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sid,
            ["reason"]          = "opencode-import",
        };
        if (cwd is not null) p["cwd"] = cwd;
        if (endedAt is { } ts) p["ended_at"] = ts.ToString("O");
        p["origin"] = ImportOrigins.Historical;
        return p;
    }

    static async Task<bool> PostHookAsync(HttpClient client, string baseUrl, string route, JsonObject payload, CancellationToken ct) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{route}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw; // don't mask cancellation as a hook failure
        } catch { return false; }
    }

    static async Task PostSetTitleAsync(HttpClient client, string baseUrl, string sid, string title, CancellationToken ct) {
        if (title.Length > 120) title = title[..120];
        var payload = new JsonObject { ["session_id"] = sid, ["title"] = title };
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _ = await client.PostWithRetryAsync($"{baseUrl}/hooks/set-title", content, ct: ct);
        } catch { /* best effort */ }
    }
}
