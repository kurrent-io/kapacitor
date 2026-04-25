using Spectre.Console;
using System.Net;
using System.Text;
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

static class HistoryCommand {
    readonly struct HistoryDisplay {
        public bool Tty { get; init; }

        public void Line(string plain, string? markup = null) {
            if (Tty) AnsiConsole.MarkupLine(markup ?? Markup.Escape(plain));
            else Console.WriteLine(plain);
        }

        public void BeginPhase(string title) {
            if (Tty) {
                AnsiConsole.Write(new Rule($"[yellow]{Markup.Escape(title)}[/]").LeftJustified());
            } else {
                Console.WriteLine();
                Console.WriteLine($"== {title} ==");
            }
        }

        public void WritePlanGrid(ClassificationCounts c) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]New[/]", c.New.ToString());
                grid.AddRow("[bold]Resumable[/]", c.Partial.ToString());
                grid.AddRow("[bold]Already loaded[/]", c.AlreadyLoaded.ToString());
                grid.AddRow("[bold]Too short[/]", c.TooShort.ToString());
                grid.AddRow("[bold]Excluded[/]", c.Excluded.ToString());
                if (c.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{c.ProbeError}[/]");
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine($"  New               {c.New}");
                Console.WriteLine($"  Resumable         {c.Partial}");
                Console.WriteLine($"  Already loaded    {c.AlreadyLoaded}");
                Console.WriteLine($"  Too short         {c.TooShort}");
                Console.WriteLine($"  Excluded          {c.Excluded}");
                if (c.ProbeError > 0) Console.WriteLine($"  Probe errors      {c.ProbeError}");
            }
        }

        public void WriteDoneGrid(FinalCounts f) {
            if (Tty) {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]Loaded[/]", f.Loaded.ToString());
                grid.AddRow("[bold]Resumed[/]", f.Resumed.ToString());
                grid.AddRow("[bold]Already loaded[/]", f.AlreadyLoaded.ToString());
                if (f.TooShort   > 0) grid.AddRow("[bold]Too short[/]", f.TooShort.ToString());
                if (f.Excluded   > 0) grid.AddRow("[bold]Excluded[/]", f.Excluded.ToString());
                if (f.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{f.ProbeError}[/]");
                if (f.Errored    > 0) grid.AddRow("[bold]Errored[/]", $"[red]{f.Errored}[/]");

                if (f.RanBackground) {
                    grid.AddRow("[bold]Titles[/]", $"{f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        grid.AddRow("[bold]Summaries[/]", $"{f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }

                AnsiConsole.Write(new Rule("[green]Done[/]").LeftJustified());
                AnsiConsole.Write(grid);
            } else {
                Console.WriteLine();
                Console.WriteLine("== Done ==");
                Console.WriteLine($"  Loaded              {f.Loaded}");
                Console.WriteLine($"  Resumed             {f.Resumed}");
                Console.WriteLine($"  Already loaded      {f.AlreadyLoaded}");
                if (f.TooShort   > 0) Console.WriteLine($"  Too short           {f.TooShort}");
                if (f.Excluded   > 0) Console.WriteLine($"  Excluded            {f.Excluded}");
                if (f.ProbeError > 0) Console.WriteLine($"  Probe errors        {f.ProbeError}");
                if (f.Errored    > 0) Console.WriteLine($"  Errored             {f.Errored}");

                if (f.RanBackground) {
                    Console.WriteLine($"  Titles              {f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        Console.WriteLine($"  Summaries           {f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }
            }
        }

        public static HistoryDisplay Create() => new() { Tty = !Console.IsOutputRedirected };
    }

    internal enum ClassificationStatus {
        /// <summary>Session does not exist on the server — needs a full import.</summary>
        New,

        /// <summary>Session exists on the server with a partial line count — resume from ResumeFromLine.</summary>
        Partial,

        /// <summary>Session is fully loaded on the server — no work to do.</summary>
        AlreadyLoaded,

        /// <summary>Transcript line count is below the minLines threshold.</summary>
        TooShort,

        /// <summary>Session's repository is in the user's excluded list and the user declined to include it.</summary>
        Excluded,

        /// <summary>The last-line probe failed (HTTP error, network error).</summary>
        ProbeError,

        /// <summary>Kapacitor-spawned sub-session (title generation, what's-done summary) — never imported.</summary>
        InternalSubSession,
    }

    internal sealed record SessionClassification {
        public required string               SessionId  { get; init; }
        public required string               FilePath   { get; init; }
        public required string               EncodedCwd { get; init; }
        public required SessionMetadata      Meta       { get; init; }
        public required ClassificationStatus Status     { get; init; }

        /// <summary>Only populated when Status == Partial.</summary>
        public int ResumeFromLine { get; init; }

        /// <summary>Only populated when Status == ProbeError. Short human-readable reason.</summary>
        public string? ProbeErrorReason { get; init; }

        /// <summary>Populated when the session is a continuation in the continuation map.</summary>
        public string? PreviousSessionId { get; init; }

        /// <summary>
        /// Populated when Status == Excluded OR when the session would otherwise be New/Partial
        /// but its cwd maps to an excluded repo and the user has not yet been consulted.
        /// Format: "{Owner}/{RepoName}".
        /// </summary>
        public string? ExcludedRepoKey { get; init; }

        /// <summary>Total transcript line count (cached so we don't re-read the file downstream).</summary>
        public int TotalLines { get; init; }
    }

    internal sealed record ClassificationCounts(
            int New,
            int Partial,
            int AlreadyLoaded,
            int TooShort,
            int Excluded,
            int ProbeError
        );

    internal sealed record FinalCounts(
            int  Loaded,
            int  Resumed,
            int  AlreadyLoaded,
            int  TooShort,
            int  Excluded,
            int  ProbeError,
            int  Errored,
            int  TitlesGenerated,
            int  TitlesSkipped,
            int  TitlesFailed,
            int  SummariesGenerated,
            int  SummariesFailed,
            bool RanBackground,
            bool RequestedSummaries
        );

    public static async Task<int> HandleHistory(string baseUrl, string? filterCwd, string? filterSession = null, int minLines = 15, bool generateSummaries = false) {
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       display    = HistoryDisplay.Create();

        // --- Discover ---
        display.BeginPhase("Discovering");
        var projectsDir = ClaudePaths.Projects;

        if (!Directory.Exists(projectsDir)) {
            display.Line("No Claude Code projects directory found.");

            return 0;
        }

        var transcriptFiles = DiscoverTranscripts(projectsDir);

        if (transcriptFiles.Count == 0) {
            display.Line("No transcript files found.");

            return 0;
        }

        if (filterSession is not null) {
            var normalized = NormalizeGuid(filterSession);
            transcriptFiles = [.. transcriptFiles.Where(t => t.SessionId == normalized)];

            if (transcriptFiles.Count == 0) {
                await Console.Error.WriteLineAsync($"Session not found: {normalized}");

                return 1;
            }
        }

        if (filterCwd is not null) {
            var normalizedFilter = filterCwd.TrimEnd('/');

            transcriptFiles = [
                .. transcriptFiles.Where(t => {
                        var cwd = ExtractCwdFromTranscript(t.FilePath);

                        return cwd?.TrimEnd('/').Equals(normalizedFilter, StringComparison.Ordinal) == true;
                    }
                )
            ];
        }

        var projectCount = transcriptFiles.Select(t => t.EncodedCwd).Distinct().Count();
        display.Line($"Found {transcriptFiles.Count} session{(transcriptFiles.Count == 1 ? "" : "s")} in {projectCount} project{(projectCount == 1 ? "" : "s")}");

        // --- Classify (parallel probes) ---
        var                         excludedRepos = (await AppConfig.Load())?.ExcludedRepos;
        List<SessionClassification> classifications;

        if (display.Tty) {
            var tmp = new List<SessionClassification>();

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar     = ctx.AddTask("[yellow]Probing[/]", maxValue: transcriptFiles.Count);
                        var results = await ClassifyAsync(
                            httpClient,
                            baseUrl,
                            transcriptFiles,
                            minLines,
                            excludedRepos,
                            CancellationToken.None,
                            onProbed: () => bar.Increment(1)
                        );
                        tmp.AddRange(results);
                    }
                );
            classifications = tmp;
        } else {
            display.Line($"Probing {transcriptFiles.Count} sessions...");
            classifications = await ClassifyAsync(httpClient, baseUrl, transcriptFiles, minLines, excludedRepos, CancellationToken.None);
        }

        // --- Resolve excluded-repo prompts (TTY only; non-TTY auto-skips) ---
        var excludedByKey = classifications
            .Where(c => c.ExcludedRepoKey is not null)
            .GroupBy(c => c.ExcludedRepoKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (excludedByKey.Count > 0) {
            var includedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Only prompt when both stdin and stdout are interactive. Writing prompts to stderr
            // keeps them visible even when stdout is redirected, but we still can't ReadLine
            // meaningfully without a TTY on stdin.
            var canPrompt = display.Tty && !Console.IsInputRedirected;

            if (canPrompt) {
                foreach (var (key, sessions) in excludedByKey) {
                    Console.Error.Write($"Repository {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} from it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedKeys.Add(key);
                }
            } else {
                Console.Error.WriteLine($"Auto-skipping {excludedByKey.Values.Sum(v => v.Count)} session(s) from {excludedByKey.Count} excluded repo(s) (non-interactive).");
            }

            for (var i = 0; i < classifications.Count; i++) {
                var c = classifications[i];

                if (c.ExcludedRepoKey is null) continue;

                if (!includedKeys.Contains(c.ExcludedRepoKey)) {
                    classifications[i] = c with { Status = ClassificationStatus.Excluded };
                }
            }
        }

        // --- Plan grid ---
        var planCounts = new ClassificationCounts(
            New: classifications.Count(c => c.Status           == ClassificationStatus.New),
            Partial: classifications.Count(c => c.Status       == ClassificationStatus.Partial),
            AlreadyLoaded: classifications.Count(c => c.Status == ClassificationStatus.AlreadyLoaded),
            TooShort: classifications.Count(c => c.Status      == ClassificationStatus.TooShort),
            Excluded: classifications.Count(c => c.Status      == ClassificationStatus.Excluded),
            ProbeError: classifications.Count(c => c.Status    == ClassificationStatus.ProbeError)
        );

        display.BeginPhase("Plan");
        display.WritePlanGrid(planCounts);

        // --- Build chains + set continuation predecessors ---
        var continuationMap = BuildContinuationMapFromClassifications(classifications);

        classifications = [
            .. classifications.Select(c =>
                continuationMap.TryGetValue(c.SessionId, out var prev) ? c with { PreviousSessionId = prev } : c
            )
        ];
        var chains = BuildImportChains(classifications);

        // --- Import ---
        // ConcurrentBag rather than List: OnTitleTaskReady / OnSessionEnded
        // callbacks fire from parallel chain-worker threads, so a plain List's
        // Add would race. No ordering guarantees needed — we only enumerate at
        // the end via Task.WhenAll.
        var       backgroundTasks    = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var       titleTaskCount     = 0;
        var       summaryTaskCount   = 0;
        using var concurrencyLimit   = new SemaphoreSlim(3);
        var       titlesGenerated    = 0;
        var       titlesSkipped      = 0;
        var       titlesFailed       = 0;
        var       summariesGenerated = 0;
        var       summariesFailed    = 0;
        var       titleFailures      = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();
        var       summaryFailures    = new System.Collections.Concurrent.ConcurrentBag<(string SessionId, string Reason)>();

        var events = new ChainWorkerEvents {
            OnLineCompleted = s => display.Line(s, $"[green]✓[/] {Markup.Escape(s)}"),
            OnSubagentFinished = (_, aid, lines) => display.Line(
                $"  ↳ imported subagent {aid} ({lines} lines)",
                $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(aid)}[/] ({lines} lines)"
            ),
            OnSessionErrored = (sid, reason) => display.Line(
                $"Skipping {sid} [{reason}]",
                $"[red]✗[/] Skipping [cyan]{Markup.Escape(sid)}[/] [{Markup.Escape(reason)}]"
            ),
            OnTitleTaskReady = t => {
                var (sid, fp, _) = t;
                Interlocked.Increment(ref titleTaskCount);

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var result = await GenerateTitleForImportAsync(httpClient, baseUrl, sid, fp);

                                switch (result) {
                                    case TitleResult.Generated: Interlocked.Increment(ref titlesGenerated); break;
                                    case TitleResult.Skipped:   Interlocked.Increment(ref titlesSkipped); break;
                                    case TitleResult.Failed:
                                        Interlocked.Increment(ref titlesFailed);
                                        titleFailures.Add((sid, "generation error"));

                                        break;
                                }
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
            OnSessionEnded = t => {
                if (!t.GenerateWhatsDone || !generateSummaries) return;

                Interlocked.Increment(ref summaryTaskCount);
                var sid = t.SessionId;

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var rc = await WhatsDoneCommand.GenerateForSessionAsync(baseUrl, sid, _ => { });

                                if (rc == 0) Interlocked.Increment(ref summariesGenerated);
                                else {
                                    Interlocked.Increment(ref summariesFailed);
                                    summaryFailures.Add((sid, $"exit {rc}"));
                                }
                            } catch (Exception ex) {
                                Interlocked.Increment(ref summariesFailed);
                                summaryFailures.Add((sid, ex.Message));
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
        };

        ImportChainsResult importResult;

        if (chains.Count > 0) {
            display.BeginPhase($"Importing {chains.Sum(c => c.Count)} sessions");

            if (display.Tty) {
                var r = default(ImportChainsResult);

                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var bar = ctx.AddTask("[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                            var wrappedEvents = events with {
                                OnLineCompleted = s => {
                                    events.OnLineCompleted(s);
                                    bar.Increment(1);
                                },
                            };
                            r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None);
                        }
                    );
                importResult = r!;
            } else {
                importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None);
            }
        } else {
            importResult = new ImportChainsResult(0, 0, 0);
        }

        // --- Background phase (titles / summaries) ---
        var ranBackground = backgroundTasks.Count > 0;

        if (ranBackground) {
            display.BeginPhase("Titles & summaries");

            if (display.Tty) {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var titleTask   = titleTaskCount   > 0 ? ctx.AddTask("[cyan]Titles[/]", maxValue: titleTaskCount) : null;
                            var summaryTask = summaryTaskCount > 0 ? ctx.AddTask("[cyan]Summaries[/]", maxValue: summaryTaskCount) : null;
                            var seenT       = 0;
                            var seenS       = 0;

                            while (backgroundTasks.Any(t => !t.IsCompleted)) {
                                if (titleTask is not null) titleTask.Value     = titlesGenerated    + titlesFailed + titlesSkipped;
                                if (summaryTask is not null) summaryTask.Value = summariesGenerated + summariesFailed;
                                var tList                                      = titleFailures.ToList();
                                var sList                                      = summaryFailures.ToList();

                                for (var i = seenT; i < tList.Count; i++) {
                                    var (sid, reason) = tList[i];
                                    AnsiConsole.MarkupLine($"  [red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenT = tList.Count;

                                for (var i = seenS; i < sList.Count; i++) {
                                    var (sid, reason) = sList[i];
                                    AnsiConsole.MarkupLine($"  [red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenS = sList.Count;
                                await Task.Delay(250);
                            }

                            try { await Task.WhenAll(backgroundTasks); } catch {
                                /* per-task try/catch */
                            }

                            if (titleTask is not null) titleTask.Value     = titlesGenerated    + titlesFailed + titlesSkipped;
                            if (summaryTask is not null) summaryTask.Value = summariesGenerated + summariesFailed;
                        }
                    );
            } else {
                display.Line($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");

                try { await Task.WhenAll(backgroundTasks); } catch {
                    /* per-task */
                }

                foreach (var (sid, reason) in titleFailures)
                    display.Line($"  ✗ title failed for {sid}: {reason}");

                foreach (var (sid, reason) in summaryFailures)
                    display.Line($"  ✗ summary failed for {sid}: {reason}");
            }
        }

        // --- Done ---
        var final = new FinalCounts(
            Loaded: importResult.Loaded,
            Resumed: importResult.Resumed,
            AlreadyLoaded: planCounts.AlreadyLoaded,
            TooShort: planCounts.TooShort,
            Excluded: planCounts.Excluded,
            ProbeError: planCounts.ProbeError,
            Errored: importResult.Errored,
            TitlesGenerated: titlesGenerated,
            TitlesSkipped: titlesSkipped,
            TitlesFailed: titlesFailed,
            SummariesGenerated: summariesGenerated,
            SummariesFailed: summariesFailed,
            RanBackground: ranBackground,
            RequestedSummaries: summaryTaskCount > 0
        );
        display.WriteDoneGrid(final);

        return 0;
    }

    /// <summary>
    /// Timestamp used to order a continuation chain. Prefers the transcript's first
    /// observed message timestamp; falls back to the file's last-write mtime when
    /// the transcript has no parseable timestamp in its first 50 lines (best-effort
    /// `ExtractSessionMetadata` leaves `FirstTimestamp` null in that case).
    /// <br/>
    /// Without this fallback, timestamp-less transcripts would all sort at
    /// `DateTimeOffset.MinValue` and could displace real predecessors — corrupting
    /// `previous_session_id` links in the session-start hook. The mtime lookup is
    /// wrapped so a file deleted between discovery and chain building can't crash
    /// the whole history run; ordering is best-effort.
    /// </summary>
    static DateTimeOffset ChainTimestamp(SessionClassification c) {
        if (c.Meta.FirstTimestamp is { } ts) return ts;

        try {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(c.FilePath), TimeSpan.Zero);
        } catch {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Build a sessionId → previousSessionId map from classifications, grouping by slug.
    /// Replaces BuildContinuationMap which read transcripts again.
    /// </summary>
    static Dictionary<string, string> BuildContinuationMapFromClassifications(List<SessionClassification> classifications) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var bySlug = classifications
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal);

        foreach (var group in bySlug) {
            var chain = group
                .OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();

            for (var i = 1; i < chain.Count; i++)
                map[chain[i].SessionId] = chain[i - 1].SessionId;
        }

        return map;
    }

    internal static SessionMetadata ExtractSessionMetadata(string filePath) {
        var meta = new SessionMetadata();

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 50) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Skip file-history-snapshot entries
                    if (root.Str("type") == "file-history-snapshot") {
                        continue;
                    }

                    // Extract cwd from metadata
                    meta.Cwd ??= root.Str("cwd");

                    // Extract model from assistant message
                    meta.Model ??= root.Obj("message")?.Str("model");

                    // Extract slug from metadata
                    meta.Slug ??= root.Str("slug");

                    // Extract sessionId
                    meta.SessionId ??= root.Str("sessionId");

                    // Extract first timestamp for continuation ordering
                    if (meta.FirstTimestamp is null
                     && root.Str("timestamp") is { } tsStr
                     && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        meta.FirstTimestamp = ts;
                    }

                    // Stop early once we have all metadata
                    if (meta.Cwd is not null && meta.Model is not null && meta.Slug is not null) {
                        break;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort metadata extraction
        }

        return meta;
    }

    internal static DateTimeOffset? ExtractLastTimestamp(string filePath) {
        try {
            // Read backward from end of file to find the last timestamp without loading everything into memory.
            // Strategy: read the last ~64KB chunk which covers well over 50 JSONL lines.
            using var fs        = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int chunkSize = 64 * 1024;
            var       offset    = Math.Max(0, fs.Length - chunkSize);
            fs.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);

            // If we seeked mid-file, skip the first partial line
            if (offset > 0) reader.ReadLine();

            // Collect the last 50 non-empty lines
            var tail = new List<string>(50);

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    tail.Add(line);

                    if (tail.Count > 50) tail.RemoveAt(0);
                }
            }

            // Scan from the end
            for (var i = tail.Count - 1; i >= 0; i--) {
                try {
                    using var doc  = JsonDocument.Parse(tail[i]);
                    var       root = doc.RootElement;

                    if (root.Str("timestamp") is { } tsStr
                     && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        return ts;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    static string? ExtractCwdFromTranscript(string filePath) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 20) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc = JsonDocument.Parse(line);

                    if (doc.RootElement.Str("cwd") is { } cwd) {
                        return cwd;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Enumerate ~/.claude/projects/*/*.jsonl transcripts, deduplicating directories
    /// by their resolved path (so symlinked project dirs don't scan the same files
    /// twice). Returns one entry per transcript with the normalized session id.
    /// </summary>
    internal static List<(string SessionId, string FilePath, string EncodedCwd)> DiscoverTranscripts(string projectsDir) {
        var results = new List<(string, string, string)>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(projectsDir)) return results;

        foreach (var cwdDir in Directory.GetDirectories(projectsDir)) {
            var realPath = new DirectoryInfo(cwdDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
             ?? Path.GetFullPath(cwdDir);

            if (!seen.Add(realPath)) continue;

            var encodedCwd = Path.GetFileName(cwdDir);

            results.AddRange(
                from jsonlFile in Directory.GetFiles(cwdDir, "*.jsonl")
                let sessionId = NormalizeGuid(Path.GetFileNameWithoutExtension(jsonlFile))
                select (sessionId, jsonlFile, encodedCwd)
            );
        }

        return results;
    }

    /// <summary>
    /// Group the import-bound subset (New + Partial) into ordered chains by slug.
    /// A chain is a list of classifications sharing the same slug, ordered by
    /// FirstTimestamp ascending. Sessions without a slug (or with a unique slug)
    /// become chains of length 1. Chain order (across chains) is stable by slug
    /// string so re-runs import in the same order.
    /// </summary>
    internal static List<List<SessionClassification>> BuildImportChains(List<SessionClassification> classifications) {
        var importable = classifications
            .Where(c => c.Status == ClassificationStatus.New || c.Status == ClassificationStatus.Partial)
            .ToList();

        var chains = new List<List<SessionClassification>>();

        var withSlug = importable
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in withSlug) {
            var ordered = group
                .OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();
            chains.Add(ordered);
        }

        foreach (var solo in importable.Where(c => c.Meta.Slug is null).OrderBy(c => c.SessionId, StringComparer.Ordinal)) {
            chains.Add([solo]);
        }

        return chains;
    }

    /// <summary>
    /// Normalize a GUID string to dashless format (matching the live CLI's NormalizeGuidField).
    /// Non-GUID strings are returned as-is.
    /// </summary>
    static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;

    static async Task<TitleResult> GenerateTitleForImportAsync(HttpClient httpClient, string baseUrl, string sessionId, string filePath) {
        try {
            var (userText, assistantText) = TitleGenerator.ExtractTitleContext(filePath);

            if (userText is null) {
                return TitleResult.Skipped;
            }

            var result = await TitleGenerator.GenerateAsync(userText, assistantText, _ => { });

            if (result is null) {
                return TitleResult.Skipped;
            }

            var payload = new SessionTitlePayload {
                SessionId        = sessionId,
                Title            = result.Result,
                Model            = result.Model,
                InputTokens      = result.InputTokens,
                OutputTokens     = result.OutputTokens,
                CacheReadTokens  = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens
            };

            var       payloadJson = JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.SessionTitlePayload);
            using var content     = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var titleResp   = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-title", content);

            return TitleResult.Generated;
        } catch {
            return TitleResult.Failed;
        }
    }

    enum TitleResult { Generated, Skipped, Failed }

    internal sealed record ImportChainsResult(int Loaded, int Resumed, int Errored);

    internal sealed record ChainWorkerEvents {
        public required Action<string>                                                         OnLineCompleted    { get; init; }
        public required Action<string, string, int>                                            OnSubagentFinished { get; init; }
        public required Action<string, string>                                                 OnSessionErrored   { get; init; }
        public required Action<(string SessionId, string FilePath, string? PreviousSessionId)> OnTitleTaskReady   { get; init; }
        public required Action<(string SessionId, bool GenerateWhatsDone)>                     OnSessionEnded     { get; init; }
    }

    /// <summary>
    /// Dispatch chains across 4 parallel workers; sessions within a chain run
    /// serially. Thread-safe: counters use Interlocked, callbacks must be
    /// thread-safe (production wiring uses AnsiConsole + ConcurrentBag).
    /// </summary>
    internal static async Task<ImportChainsResult> ImportChainsAsync(
            HttpClient                        httpClient,
            string                            baseUrl,
            List<List<SessionClassification>> chains,
            ChainWorkerEvents                 events,
            CancellationToken                 ct
        ) {
        var loaded  = 0;
        var resumed = 0;
        var errored = 0;

        // Parallel.ForEachAsync caps outstanding tasks at MaxDegreeOfParallelism,
        // which matters when we have thousands of singleton chains. A hand-rolled
        // chains.Select(Task.Run) would queue one Task per chain, all blocked on
        // a semaphore — significant overhead at scale.
        var options = new ParallelOptions {
            MaxDegreeOfParallelism = 4,
            CancellationToken      = ct,
        };

        await Parallel.ForEachAsync(
            chains,
            options,
            async (chain, token) => {
                foreach (var session in chain) {
                    // Belt-and-suspenders: ImportSingleSessionAsync already catches
                    // expected failures; a bare catch here guarantees one rogue
                    // session can't fault the worker and abort the iteration.
                    SessionImportOutcome r;

                    try {
                        r = await ImportSingleSessionAsync(httpClient, baseUrl, session, events, token);
                    } catch (Exception ex) {
                        events.OnSessionErrored(session.SessionId, ex.Message);
                        r = SessionImportOutcome.Errored;
                    }

                    switch (r) {
                        case SessionImportOutcome.Loaded:  Interlocked.Increment(ref loaded); break;
                        case SessionImportOutcome.Resumed: Interlocked.Increment(ref resumed); break;
                        case SessionImportOutcome.Errored: Interlocked.Increment(ref errored); break;
                    }
                }
            }
        );

        return new ImportChainsResult(loaded, resumed, errored);
    }

    enum SessionImportOutcome { Loaded, Resumed, Errored }

    static async Task<SessionImportOutcome> ImportSingleSessionAsync(
            HttpClient            httpClient,
            string                baseUrl,
            SessionClassification session,
            ChainWorkerEvents     events,
            CancellationToken     ct
        ) {
        IProgress<ImportProgress> perSessionProgress = new CallbackProgress(ev => {
                if (ev is SubagentFinished sf) {
                    events.OnSubagentFinished(session.SessionId, sf.AgentId, sf.LinesSent);
                }
            }
        );

        if (session.Status == ClassificationStatus.Partial) {
            try {
                var linesSent = await SessionImporter.SendTranscriptBatches(
                    httpClient,
                    baseUrl,
                    session.SessionId,
                    session.FilePath,
                    agentId: null,
                    startLine: session.ResumeFromLine,
                    progress: perSessionProgress
                );
                events.OnLineCompleted($"Loading {session.SessionId}... {linesSent} lines [resuming from line {session.ResumeFromLine}]");

                return SessionImportOutcome.Resumed;
            } catch (HttpRequestException ex) {
                events.OnSessionErrored(session.SessionId, $"server unreachable: {ex.Message}");

                return SessionImportOutcome.Errored;
            } catch (Exception ex) {
                events.OnSessionErrored(session.SessionId, ex.Message);

                return SessionImportOutcome.Errored;
            }
        }

        // status == New: session-start → import → session-end → enqueue background tasks
        var meta = session.Meta;
        var cwd  = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(session.EncodedCwd);

        var startHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["source"]          = "Startup",
            ["hook_event_name"] = "session_start",
            ["model"]           = meta.Model,
        };
        if (meta.FirstTimestamp is not null) startHook["started_at"]                = meta.FirstTimestamp.Value.ToString("O");
        if (session.PreviousSessionId is not null) startHook["previous_session_id"] = session.PreviousSessionId;
        if (meta.Slug is not null) startHook["slug"]                                = meta.Slug;

        if (cwd is not null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repo is not null) {
                var repoNode                                           = new System.Text.Json.Nodes.JsonObject();
                if (repo.UserName is not null) repoNode["user_name"]   = repo.UserName;
                if (repo.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;
                if (repo.RemoteUrl is not null) repoNode["remote_url"] = repo.RemoteUrl;
                if (repo.Owner is not null) repoNode["owner"]          = repo.Owner;
                if (repo.RepoName is not null) repoNode["repo_name"]   = repo.RepoName;
                if (repo.Branch is not null) repoNode["branch"]        = repo.Branch;
                startHook["repository"] = repoNode;
            }
        }

        try {
            using var startContent = new StringContent(startHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var startResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-start", startContent, ct: ct);

            if (!startResp.IsSuccessStatusCode) {
                events.OnSessionErrored(session.SessionId, $"session-start failed: HTTP {(int)startResp.StatusCode}");

                return SessionImportOutcome.Errored;
            }
        } catch (HttpRequestException ex) {
            events.OnSessionErrored(session.SessionId, $"server unreachable: {ex.Message}");

            return SessionImportOutcome.Errored;
        }

        ImportResult importResult;

        try {
            importResult = await SessionImporter.ImportSessionAsync(
                httpClient,
                baseUrl,
                session.FilePath,
                session.SessionId,
                meta,
                session.EncodedCwd,
                perSessionProgress
            );
        } catch (Exception ex) {
            // Catches file-IO, JSON parsing, and anything else ImportSessionAsync can throw.
            // Sending session-end on a half-imported session would be misleading; let the
            // errored count reflect reality and move on to the next session in the chain.
            events.OnSessionErrored(session.SessionId, ex.Message);

            return SessionImportOutcome.Errored;
        }

        events.OnLineCompleted($"Loading {session.SessionId}... {importResult.LinesSent} lines [new]");

        var lastTs = ExtractLastTimestamp(session.FilePath);

        var endHook = new System.Text.Json.Nodes.JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["reason"]          = "Other",
            ["hook_event_name"] = "session_end",
        };
        if (lastTs is not null) endHook["ended_at"] = lastTs.Value.ToString("O");

        var generateWhatsDone = false;

        try {
            using var endContent = new StringContent(endHook.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var endResp    = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-end", endContent, ct: ct);

            if (endResp.IsSuccessStatusCode) {
                try {
                    var body = await endResp.Content.ReadAsStringAsync(ct);
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body);
                    generateWhatsDone = node?["generate_whats_done"]?.GetValue<bool>() == true;
                } catch {
                    /* best effort */
                }
            }
        } catch {
            /* best effort */
        }

        events.OnTitleTaskReady((session.SessionId, session.FilePath, session.PreviousSessionId));
        events.OnSessionEnded((session.SessionId, generateWhatsDone));

        return SessionImportOutcome.Loaded;
    }

    sealed class CallbackProgress(Action<ImportProgress> onReport) : IProgress<ImportProgress> {
        public void Report(ImportProgress value) => onReport(value);
    }

    /// <summary>
    /// Probe each transcript against the server's last-line API and classify
    /// what the import phase should do with it. Probes run concurrently via
    /// SemaphoreSlim(8) — idempotent GETs, safe to parallelize.
    /// </summary>
    internal static async Task<List<SessionClassification>> ClassifyAsync(
            HttpClient                                                   httpClient,
            string                                                       baseUrl,
            List<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            int                                                          minLines,
            string[]?                                                    excludedRepos,
            CancellationToken                                            ct,
            Action?                                                      onProbed = null
        ) {
        using var probeGate = new SemaphoreSlim(8);
        var       tasks     = new List<Task<SessionClassification>>(transcripts.Count);

        foreach (var (sessionId, filePath, encodedCwd) in transcripts) {
            tasks.Add(ClassifyOneAsync(httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, probeGate, onProbed, ct));
        }

        var results = await Task.WhenAll(tasks);

        return [.. results];
    }

    static async Task<SessionClassification> ClassifyOneAsync(
            HttpClient        httpClient,
            string            baseUrl,
            string            sessionId,
            string            filePath,
            string            encodedCwd,
            int               minLines,
            string[]?         excludedRepos,
            SemaphoreSlim     probeGate,
            Action?           onProbed,
            CancellationToken ct
        ) {
        try {
            return await ClassifyOneCoreAsync(httpClient, baseUrl, sessionId, filePath, encodedCwd, minLines, excludedRepos, probeGate, ct);
        } finally {
            onProbed?.Invoke();
        }
    }

    static async Task<SessionClassification> ClassifyOneCoreAsync(
            HttpClient        httpClient,
            string            baseUrl,
            string            sessionId,
            string            filePath,
            string            encodedCwd,
            int               minLines,
            string[]?         excludedRepos,
            SemaphoreSlim     probeGate,
            CancellationToken ct
        ) {
        var meta = ExtractSessionMetadata(filePath);

        // Short-circuit: kapacitor's own sub-sessions (title / what's-done) never get imported.
        if (TitleGenerator.IsKapacitorSubSession(filePath)) {
            return new SessionClassification {
                SessionId  = sessionId,
                FilePath   = filePath,
                EncodedCwd = encodedCwd,
                Meta       = meta,
                Status     = ClassificationStatus.InternalSubSession,
            };
        }

        // Probe the server BEFORE scanning the file. On re-runs the probe returns
        // 204 (AlreadyLoaded) quickly and we never need to read the transcript.
        ClassificationStatus status;
        int                  resumeFromLine   = 0;
        string?              probeErrorReason = null;

        await probeGate.WaitAsync(ct);

        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/last-line", ct: ct);

            switch (resp.StatusCode) {
                case HttpStatusCode.NotFound:
                    status = ClassificationStatus.New;

                    break;
                case HttpStatusCode.NoContent:
                    status = ClassificationStatus.AlreadyLoaded;

                    break;
                default:
                    if (resp.IsSuccessStatusCode) {
                        var       json = await resp.Content.ReadAsStringAsync(ct);
                        using var doc  = System.Text.Json.JsonDocument.Parse(json);

                        if (doc.RootElement.Num("last_line_number") is { } lastLine) {
                            resumeFromLine = (int)lastLine + 1;
                            status         = ClassificationStatus.Partial;
                        } else {
                            status = ClassificationStatus.AlreadyLoaded;
                        }
                    } else {
                        status           = ClassificationStatus.ProbeError;
                        probeErrorReason = $"HTTP {(int)resp.StatusCode}";
                    }

                    break;
            }
        } catch (HttpRequestException ex) {
            status           = ClassificationStatus.ProbeError;
            probeErrorReason = ex.Message;
        } finally {
            probeGate.Release();
        }

        // Apply the TooShort filter only for sessions that would otherwise be imported.
        // This avoids scanning the whole transcript when AlreadyLoaded / ProbeError.
        if (minLines > 0 && (status == ClassificationStatus.New || status == ClassificationStatus.Partial)) {
            var observedLines = CountLinesUpTo(filePath, minLines);

            if (observedLines < minLines) {
                return new SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = filePath,
                    EncodedCwd = encodedCwd,
                    Meta       = meta,
                    Status     = ClassificationStatus.TooShort,
                    TotalLines = observedLines,
                };
            }
        }

        // Flag excluded repos for New/Partial sessions. Resolution (include or skip?)
        // happens later in HandleHistory, where we can batch prompts by repo key.
        string? excludedRepoKey = null;

        if ((status == ClassificationStatus.New || status == ClassificationStatus.Partial)
         && excludedRepos is { Length: > 0 }) {
            var cwd = meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(encodedCwd);

            if (cwd is not null) {
                var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

                if (repo?.Owner is not null && repo.RepoName is not null) {
                    var key = $"{repo.Owner}/{repo.RepoName}";

                    if (excludedRepos.Contains(key, StringComparer.OrdinalIgnoreCase)) {
                        excludedRepoKey = key;
                    }
                }
            }
        }

        // TotalLines is only meaningful for TooShort sessions (where we know the exact
        // count because it's below the threshold). Leave it at 0 for other statuses —
        // we only read enough of the file to confirm the TooShort filter didn't apply.
        return new SessionClassification {
            SessionId        = sessionId,
            FilePath         = filePath,
            EncodedCwd       = encodedCwd,
            Meta             = meta,
            Status           = status,
            ResumeFromLine   = resumeFromLine,
            ProbeErrorReason = probeErrorReason,
            ExcludedRepoKey  = excludedRepoKey,
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
