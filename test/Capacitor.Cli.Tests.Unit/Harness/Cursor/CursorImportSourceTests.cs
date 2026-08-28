using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

// several tests here share hardcoded session ids ("11111111-…",
// "22222222-…") across BOTH ClassifyAsync and ImportSessionAsync calls, and CursorMarkers writes
// its quarantine marker file under the per-test config root. Both ClassifyAsync and
// ImportSessionAsync read that marker, so a quarantine-toggling test
// (classify_skips_a_correlated_child_when_its_parent_is_quarantined) running in parallel with any
// other test sharing the same id could flip its outcome.
// [NotInParallel] serializes this whole class against itself (mirrors MachineIdFileTests) —
// still parallel with every other test class.
[NotInParallel(nameof(CursorImportSourceTests))]
public class CursorImportSourceTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    CursorMarkers Markers => new(Config.Root);

    [Test]
    public async Task vendor_is_cursor() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.Vendor).IsEqualTo(HarnessId.Cursor);
    }

    [Test]
    public async Task does_not_support_title_generation() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.SupportsTitleGeneration).IsFalse();
    }

    [Test]
    public async Task session_start_payload_carries_pr_fields_when_repo_has_a_pr() {
        // Cursor is the one import source whose synthetic session-start carries a `repository`
        // node via BuildRepositoryNode — including pr_* when a PR is detected. Guard
        // that the payload propagates those fields (a regression dropped them when Cursor's repo
        // detector was switched to skip PR detection during the import-latency work).
        var repo = new RepositoryPayload {
            Owner = "o", RepoName = "r", Host = "github.com", Branch = "main",
            PrNumber = 7, PrTitle = "t", PrUrl = "https://github.com/o/r/pull/7", PrHeadRef = "main"
        };
        var repoNode = RepositoryDetection.BuildRepositoryNode(repo);

        var payload = CursorImportSource.BuildSessionStartPayload(
            "sid", "/ws", "/t.jsonl", DateTimeOffset.UtcNow, repoNode);

        var repository = payload["repository"] as JsonObject;
        await Assert.That(repository).IsNotNull();
        await Assert.That(repository!["pr_number"]!.GetValue<int>()).IsEqualTo(7);
        await Assert.That(repository["pr_url"]!.GetValue<string>()).IsEqualTo("https://github.com/o/r/pull/7");
    }

    [Test]
    public async Task is_available_when_projects_dir_exists() {
        using var fx  = new ProjectsDirFixture();
        var       src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        await Assert.That(src.IsAvailable).IsTrue();
    }

    [Test]
    public async Task is_unavailable_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-cursor-missing-{Guid.NewGuid():N}");
        var src     = new CursorImportSource(Config.Root, missing, missing);
        await Assert.That(src.IsAvailable).IsFalse();
    }

    [Test]
    public async Task normalize_cursor_session_id_strips_dashes() {
        await Assert.That(CursorImportSource.NormalizeCursorSessionId("abc-1234-5678"))
            .IsEqualTo("abc12345678");
    }

    [Test]
    public async Task encode_workspace_path_strips_leading_slash_and_replaces_separators() {
        await Assert.That(CursorImportSource.EncodeWorkspacePath("/Users/me/dev/foo-bar"))
            .IsEqualTo("Users-me-dev-foo-bar");
    }

    [Test]
    public async Task discover_returns_empty_when_projects_dir_missing() {
        var missing = Path.Combine(Path.GetTempPath(), $"kcap-cursor-missing-{Guid.NewGuid():N}");
        var src     = new CursorImportSource(Config.Root, missing, missing);
        var result  = await src.DiscoverAsync(Filters(), CancellationToken.None);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discover_walks_jsonl_files() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"x\":1}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(2);
        await Assert.That(got.Select(s => s.SessionId)).Contains("11111111111111111111111111111111");
        await Assert.That(got.Select(s => s.SessionId)).Contains("22222222222222222222222222222222");
        await Assert.That(got.All(s => s.Vendor == HarnessId.Cursor)).IsTrue();
    }

    [Test]
    public async Task discover_resolves_cwd_via_workspace_storage_when_sanitized_matches() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/foo");
        fx.AddSession("Users-me-dev-foo", "33333333-3333-3333-3333-333333333333", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsEqualTo("/Users/me/dev/foo");
    }

    [Test]
    public async Task discover_leaves_cwd_null_when_sanitized_not_in_workspace_storage() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-someone-else-proj", "44444444-4444-4444-4444-444444444444", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsNull();
    }

    [Test]
    public async Task discover_applies_session_filter_dashless() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "55555555-5555-5555-5555-555555555555", "{}\n");
        fx.AddSession("Users-me-proj", "66666666-6666-6666-6666-666666666666", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        // Pass the dashed form; the filter must normalize to dashless before matching.
        var got = await src.DiscoverAsync(Filters(filterSession: "55555555-5555-5555-5555-555555555555"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].SessionId).IsEqualTo("55555555555555555555555555555555");
    }

    [Test]
    public async Task discover_applies_cwd_filter_against_resolved_workspace_folder() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/match");
        fx.AddWorkspaceJson("hash-bbb", "file:///Users/me/dev/other");
        fx.AddSession("Users-me-dev-match", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "{}\n");
        fx.AddSession("Users-me-dev-other", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(filterCwd: "/Users/me/dev/match"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].SessionId).IsEqualTo("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    [Test]
    public async Task classify_marks_new_when_server_has_no_state() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        );
        using var client = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(client, minLines: 1), CancellationToken.None);

        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        // FilePath stays empty so ImportCommand routes Cursor through the
        // routed phase (ImportSessionAsync) instead of the Claude/Codex chain
        // worker. Transcript path lives in SourceMeta.
        await Assert.That(classified[0].FilePath).IsEqualTo("");
        await Assert.That((string)classified[0].SourceMeta!["TranscriptPath"]!).IsEqualTo(jsonl);
        await Assert.That(classified[0].TotalLines).IsEqualTo(3);
    }

    [Test]
    public async Task classify_keeps_file_path_empty_so_orchestrator_routes_to_ImportSessionAsync() {
        // Qodo P1 regression test: ImportCommand splits classifications into
        // file-based (chain worker, /hooks/session-start sans vendor suffix —
        // server defaults to claude) and routed (ImportSessionAsync —
        // Cursor's path). Non-empty FilePath misroutes Cursor sessions
        // through the Claude-shaped lifecycle.
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n{}\n");

        var       src     = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 0),
            CancellationToken.None
        );

        foreach (var c in classified) {
            await Assert.That(c.FilePath).IsEqualTo("");
        }
    }

    [Test]
    public async Task classify_marks_already_loaded_when_server_at_or_past_last_non_blank_line() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        // Three non-blank lines at indexes 0,1,2 → last_line_number=2 means fully loaded.
        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"last_line_number":2}""")
            }
        );
        using var client = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task classify_marks_partial_with_resume_from_when_server_mid_file() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"last_line_number":0}""")
            }
        );
        using var client = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(1);
    }

    [Test]
    public async Task classify_marks_too_short_below_min_lines() {
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n");
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 5),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task classify_returns_probe_error_when_watermark_returns_5xx() {
        using var fx = new ProjectsDirFixture();

        fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n"
        );
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
    }

    // a session already quarantined by the live watcher's runtime rewrite
    // guard must never be fed back through `kcap import` either.
    [Test]
    public async Task classify_skips_a_quarantined_standalone_session() {
        using var fx = new ProjectsDirFixture();

        // A fresh GUID per test — NOT the well-known "1111...1111" id several OTHER tests in this
        // file share via SetupParentChildAsync/fixed literals — so quarantining it here can never
        // leak into (and break) an unrelated test later in the same run (CursorMarkers writes a
        // real, non-test-scoped marker file that outlives this test).
        var sessionIdWithDashes = Guid.NewGuid().ToString();
        var sessionId           = CursorImportSource.NormalizeCursorSessionId(sessionIdWithDashes);
        fx.AddSession("Users-me-proj", sessionIdWithDashes, "{\"a\":1}\n{\"b\":2}\n");
        Markers.Quarantine(sessionId, "transcript rewrite detected");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified.Count).IsEqualTo(1);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(classified[0].ProbeErrorReason).Contains("quarantined");
    }

    // A non-quarantined session must classify normally — the quarantine check must not
    // false-positive for every session.
    [Test]
    public async Task classify_does_not_skip_a_non_quarantined_session() {
        using var fx = new ProjectsDirFixture();
        // Fresh GUID, not the shared "1111...1111" fixture id — defensive against any other
        // test's quarantine marker for that id (see classify_skips_a_quarantined_standalone_session).
        fx.AddSession("Users-me-proj", Guid.NewGuid().ToString(), "{\"a\":1}\n{\"b\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 1),
            CancellationToken.None
        );

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    }

    // quarantine is always keyed on the FAMILY (parent) identity, since
    // CursorRewriteGuard is constructed from the watcher process's own sessionId argument, which
    // for a spawned child watcher IS the parent id (WatcherManager.BuildSpawnArgs). A correlated
    // child must therefore be filtered under its PARENT's quarantine marker, not its own — an
    // import that only checked the child's own id would let the child through even though its
    // family was quarantined.
    [Test]
    public async Task classify_skips_a_correlated_child_when_its_parent_is_quarantined() {
        using var fx = new ProjectsDirFixture();
        var (parentId, childId, _, src) = await SetupParentChildAsync(fx);

        // The child's OWN id is never quarantined directly — only the parent's, mirroring how
        // CursorRewriteGuard is actually keyed for a spawned child watcher. SetupParentChildAsync
        // hardcodes these ids and is shared by several OTHER tests in this file, so the marker
        // MUST be cleaned up afterward — CursorMarkers writes a real, non-test-scoped file that
        // would otherwise leak into (and break) every later test reusing the same parent id.
        Markers.Quarantine(parentId, "transcript rewrite detected");

        try {
            using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var client   = new HttpClient(handler);

            var reclassified = await src.ClassifyAsync(
                await src.DiscoverAsync(Filters(), CancellationToken.None),
                Ctx(client, minLines: 1),
                CancellationToken.None
            );

            var parentClass = reclassified.Single(c => c.SessionId == parentId);
            var childClass   = reclassified.Single(c => c.SessionId == childId);

            await Assert.That(parentClass.Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
            await Assert.That(parentClass.ProbeErrorReason).Contains("quarantined");
            await Assert.That(childClass.Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
            await Assert.That(childClass.ProbeErrorReason).Contains("quarantined");
        } finally {
            try { File.Delete(Markers.QuarantinePath(parentId)); } catch { }
        }
    }

    [Test]
    public async Task import_session_posts_lifecycle_then_transcript_then_session_end() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        var outcome = await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(posted.Count).IsEqualTo(3);

        // Order matters: session-start before transcript, session-end after.
        await Assert.That(posted[0].Path).IsEqualTo("/hooks/session-start/cursor");
        await Assert.That(posted[1].Path).IsEqualTo("/hooks/transcript");
        await Assert.That(posted[2].Path).IsEqualTo("/hooks/session-end/cursor");

        var startNode = JsonNode.Parse(posted[0].Body)!;
        await Assert.That(startNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(startNode["hook_event_name"]!.GetValue<string>()).IsEqualTo("sessionStart");
        await Assert.That(startNode["workspace_roots"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("/Users/me/dev/proj");
        await Assert.That(startNode["transcript_path"]!.GetValue<string>()).IsEqualTo(jsonl);

        var transcriptNode = JsonNode.Parse(posted[1].Body)!;
        await Assert.That(transcriptNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(transcriptNode["vendor"]!.GetValue<string>()).IsEqualTo("cursor");
        await Assert.That(transcriptNode["lines"]!.AsArray().Count).IsEqualTo(3);

        var endNode = JsonNode.Parse(posted[2].Body)!;
        await Assert.That(endNode["session_id"]!.GetValue<string>()).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(endNode["hook_event_name"]!.GetValue<string>()).IsEqualTo("sessionEnd");
        await Assert.That(endNode["reason"]!.GetValue<string>()).IsEqualTo("historical-import");
    }

    [Test, ExcludeOn(OS.Linux)]
    public async Task import_session_populates_started_at_and_ended_at_from_file_times() {
        // synthetic lifecycle hooks must carry the JSONL file's
        // creation/last-write time so the server records canonical
        // SessionStarted/SessionEnded with the real timestamps, not
        // import-time wall clock.
        //
        // Linux ext4 has no usable birth-time round-trip via .NET's File
        // APIs — SetCreationTimeUtc is a no-op, GetCreationTimeUtc returns
        // mtime — so on Linux started_at and ended_at collapse to the same
        // value. The test verifies the macOS/Windows contract; Linux gets
        // degraded-but-functional behavior in production.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var created  = new DateTime(2026, 3, 14, 9, 27, 0, DateTimeKind.Utc);
        var modified = new DateTime(2026, 3, 14, 10, 27, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(jsonl, created);
        File.SetLastWriteTimeUtc(jsonl, modified);

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        var endNode   = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-end/cursor").Body)!;

        var startedAt = DateTimeOffset.Parse(startNode["started_at"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var endedAt   = DateTimeOffset.Parse(endNode["ended_at"]!.GetValue<string>(), CultureInfo.InvariantCulture);

        await Assert.That(startedAt.UtcDateTime).IsEqualTo(created);
        await Assert.That(endedAt.UtcDateTime).IsEqualTo(modified);
        await Assert.That((long)endNode["duration_ms"]!).IsEqualTo(3_600_000L); // 1h
    }

    [Test]
    public async Task import_session_returns_failed_when_session_start_post_fails() {
        // Reviewer P2a: lifecycle POST failure must hard-fail the import so
        // the user re-runs. Otherwise transcript success + lifecycle failure
        // leaves the session permanently lifecycle-less on the server.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();

        using var handler = new StubHandler(
            postCapture: (req, _) => {
                var path = req.RequestUri!.AbsolutePath;
                posted.Add(path);

                return path == "/hooks/session-start/cursor"
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Failed);
        // Transcript MUST NOT be posted when session-start failed —
        // otherwise the watermark advances and next-run sees AlreadyLoaded.
        await Assert.That(posted).DoesNotContain("/hooks/transcript");
    }

    [Test]
    public async Task import_session_returns_failed_when_session_end_post_fails() {
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            postCapture: (req, _) => req.RequestUri!.AbsolutePath == "/hooks/session-end/cursor"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Failed);
    }

    [Test]
    public async Task import_session_emits_lifecycle_only_for_already_loaded_status() {
        // Reviewer P2a (backfill case): re-running import on an already-loaded
        // Cursor session re-asserts lifecycle without resending transcript.
        // The orchestrator's routed-phase filter now includes AlreadyLoaded
        // for this exact case; ImportSessionAsync must short-circuit the
        // transcript batch but still emit session-start + session-end.
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();

        using var handler = new StubHandler(
            postCapture: (req, _) => {
                posted.Add(req.RequestUri!.AbsolutePath);

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = "11111111111111111111111111111111",
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
                TotalLines = 3,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(outcome.SentChildContent).IsFalse(); // no SubagentChildren attached
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");
        await Assert.That(posted).DoesNotContain("/hooks/transcript");
    }

    // --- round-2 review fix (finding 1): SentChildContent signal ---

    [Test]
    public async Task already_loaded_parent_reports_sent_child_content_when_attaching_a_new_child() {
        // Round-2 finding 1: an AlreadyLoaded parent (nothing past its OWN watermark, so its own
        // outcome is Resumed/Skipped) can still attach a previously-unloaded nested child inline.
        // That IS real new work — ImportSessionAsync must surface it via SentChildContent so
        // ImportCommand's IsLifecycleOnlyRoutedReplay doesn't wrongly suppress it (which would
        // drop the parent from importedSessionIds and leave a --private run's parent still public).
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound), // child subsession watermark: nothing sent yet
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        // The parent's OWN transcript has nothing new past its watermark...
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        // ...but the new child DID send content, and that must be visible to the caller.
        await Assert.That(result.SentChildContent).IsTrue();
    }

    [Test]
    public async Task already_loaded_parent_with_already_loaded_child_reports_no_sent_child_content() {
        // Counterpart: an AlreadyLoaded parent whose correlated child is ALSO already fully
        // ingested (subsession watermark already covers the whole child transcript) sends zero
        // new bytes for the child too — a genuine lifecycle-only replay, SentChildContent stays
        // false.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            // Subsession watermark already covers both child lines (0-indexed last_line_number=1).
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"last_line_number\":1}", System.Text.Encoding.UTF8, "application/json"),
            },
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsFalse();
    }

    // --- round-4 review fix (P1): probe failure is indeterminate, defaults privacy-safe ---

    [Test]
    public async Task already_loaded_parent_with_failing_child_watermark_probe_conservatively_reports_sent_child_content_to_preserve_privacy() {
        // Round-4 P1 fix: the round-3 fix above (blanket-suppressing SentChildContent whenever the
        // watermark probe failed) over-corrected into a privacy regression. A failed probe is
        // INDETERMINATE, not proof the fail-open full resend is a duplicate — when the child is
        // genuinely NEW and the watermark endpoint merely 500s transiently, the fail-open resend
        // really does POST new content, yet the caller was told nothing new happened. For an
        // AlreadyLoaded parent, that meant the parent was excluded from importedSessionIds and
        // NOT privatized under `--private`, even though a real new child had just been attached to
        // it — a privacy leak, not just a cosmetic miscount. So probe failure must NOT force
        // SentChildContent=false; the conservative, privacy-safe default is to report
        // SentChildContent=true whenever the fail-open resend actually posted lines, so the parent
        // is counted as loaded and IS privatized. (Known, separately-tracked trade-off: this can
        // cosmetically double-count an already-complete child that gets fail-open-resent — its
        // AlreadyLoaded parent lands in both the Loaded and AlreadyLoaded buckets. That's accepted:
        // privacy correctness wins over count precision.)
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();

        using var handler = new StubHandler(
            // The child subsession watermark probe fails transiently (5xx) — a fail-open trigger.
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            postCapture: (req, _) => {
                posted.Add(req.RequestUri!.AbsolutePath);

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        // The fail-open repost happens (idempotent server-side)...
        await Assert.That(posted).Contains("/hooks/transcript");
        // ...and since the probe failure is indeterminate (not proof of "no new content"), the
        // posted resend conservatively reports SentChildContent=true — the parent is counted as
        // loaded and would be privatized under `--private`.
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();
    }

    [Test]
    public async Task already_loaded_parent_with_known_child_watermark_and_lines_beyond_it_reports_sent_child_content() {
        // Counterpart to the fail-open case above: when the watermark probe SUCCEEDS (no
        // exception) and returns a real value with genuinely new lines beyond it, that IS real new
        // content and must still report SentChildContent=true — the fix must not blanket-suppress
        // every resend, only the fail-open, indeterminate ones.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        // 3 child lines; the probe reports last_line_number=0 (only the first line previously
        // ingested), so lines 1 and 2 are genuinely new.
        var childJsonl = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n{\"z\":3}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"last_line_number\":0}", System.Text.Encoding.UTF8, "application/json"),
            },
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();
    }

    // --- Watermark probe 2-attempt cap ---

    [Test]
    public async Task cursor_watermark_probe_retries_once_on_transient_500_then_succeeds() {
        // A single transient 500 no longer skips straight to fail-open on the first failure:
        // the status-bearing retry engages, the second attempt succeeds with a real watermark,
        // and only the genuinely-new lines beyond it are sent.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        // 3 child lines; watermark (once known) reports last_line_number=0, so lines 1 and 2
        // are genuinely new.
        var childJsonl = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n{\"z\":3}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var getCalls = 0;
        var posted   = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            getResponse: _ => {
                getCalls++;
                return getCalls == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK) {
                        Content = new StringContent("{\"last_line_number\":0}", System.Text.Encoding.UTF8, "application/json"),
                    };
            },
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        // Exactly 2 probe attempts: the retryable 500, then the successful retry — no fail-open
        // triggered (the watermark WAS resolved).
        await Assert.That(getCalls).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();

        var transcriptPost = posted.Single(p => p.Path == "/hooks/transcript");
        var lineNumbers     = JsonNode.Parse(transcriptPost.Body)!["line_numbers"]!.AsArray()
            .Select(n => n!.GetValue<int>()).ToArray();
        // The exact startLine used: only lines 1 and 2 (beyond the resolved watermark) were sent.
        await Assert.That(lineNumbers).IsEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task cursor_watermark_probe_falls_open_after_exactly_2_attempts_on_sustained_500() {
        // A sustained failure exhausts the fixed 2-attempt cap before falling open — not before,
        // not after. The fail-open resend still happens (idempotent server-side) and still
        // conservatively reports SentChildContent=true since lines were actually posted.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n{\"z\":3}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var getCalls = 0;
        var posted   = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            getResponse: _ => {
                getCalls++;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            },
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        // Exactly 2 attempts made — the fixed cap, no more and no less — before falling open.
        await Assert.That(getCalls).IsEqualTo(2);
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();

        var transcriptPost = posted.Single(p => p.Path == "/hooks/transcript");
        var lineNumbers     = JsonNode.Parse(transcriptPost.Body)!["line_numbers"]!.AsArray()
            .Select(n => n!.GetValue<int>()).ToArray();
        // Fail-open: the watermark was never resolved, so the whole child is resent from 0.
        await Assert.That(lineNumbers).IsEquivalentTo(new[] { 0, 1, 2 });
    }

    [Test]
    public async Task cursor_watermark_probe_does_not_retry_a_404() {
        // A definitive 4xx (here reached via the existing NotFound "no watermark yet" shortcut)
        // is not retried — the first attempt's result stands, matching today's behavior for a
        // never-before-imported child.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n{\"z\":3}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var getCalls = 0;

        using var handler = new StubHandler(
            getResponse: _ => {
                getCalls++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            },
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(getCalls).IsEqualTo(1); // no second attempt
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();
    }

    [Test]
    public async Task cursor_watermark_probe_does_not_retry_a_429() {
        // 429 is explicitly excluded from the outer retry (a rate-limit signal, not a transient
        // blip) — no second attempt, fail-open triggers immediately, same treatment as a
        // non-retryable 4xx.
        using var fx = new ProjectsDirFixture();

        var parentJsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{\"x\":1}\n{\"y\":2}\n{\"z\":3}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var getCalls = 0;

        using var handler = new StubHandler(
            getResponse: _ => {
                getCalls++;
                return new HttpResponseMessage((HttpStatusCode)429);
            },
            postCapture: (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        );
        using var client = new HttpClient(handler);

        var parentClass = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
            TotalLines = 3,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]   = parentJsonl,
                ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                    new("22222222222222222222222222222222", childJsonl, "generalPurpose"),
                },
            },
        };

        var result = await src.ImportSessionAsync(
            parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(getCalls).IsEqualTo(1); // no second attempt
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
        await Assert.That(result.SentChildContent).IsTrue();
    }

    // The statusless case is tested directly against the pure retry-decision shell
    // (FetchServerLastLineWithCappedRetryAsync) rather than end-to-end through ImportSessionAsync:
    // a genuinely statusless HttpRequestException can only come out of GetWithRetryAsync by
    // exhausting its own real ~30s internal backoff loop (a normally-returned non-2xx status,
    // used by the four tests above, never enters that loop at all — only a THROWN
    // HttpRequestException does, and GetWithRetryAsync retries every thrown HttpRequestException
    // it sees regardless of status). Driving that for real would make this one test take ~30s of
    // wall-clock time for no extra coverage; testing the shell directly with an injected probe
    // keeps it instant while still exercising the exact acceptance criterion (finding 4): a
    // statusless failure gets no second attempt.

    [Test]
    public async Task fetch_server_last_line_with_capped_retry_does_not_retry_a_statusless_transport_failure() {
        var probeCalls = 0;

        Task<int?> Probe(CancellationToken _) {
            probeCalls++;

            throw new HttpRequestException("connection refused"); // no HttpStatusCode — statusless
        }

        await Assert.That(async () =>
            await CursorImportSource.FetchServerLastLineWithCappedRetryAsync(Probe, CancellationToken.None)
        ).Throws<HttpRequestException>();

        await Assert.That(probeCalls).IsEqualTo(1); // no second attempt
    }

    [Test]
    public async Task fetch_server_last_line_with_capped_retry_retries_a_status_bearing_5xx_once() {
        var probeCalls = 0;

        Task<int?> Probe(CancellationToken _) {
            probeCalls++;

            if (probeCalls == 1)
                throw new HttpRequestException("boom", null, HttpStatusCode.InternalServerError);

            return Task.FromResult<int?>(7);
        }

        var result = await CursorImportSource.FetchServerLastLineWithCappedRetryAsync(Probe, CancellationToken.None);

        await Assert.That(result).IsEqualTo(7);
        await Assert.That(probeCalls).IsEqualTo(2);
    }

    [Test]
    public async Task fetch_server_last_line_with_capped_retry_propagates_cancellation_without_retrying() {
        var probeCalls = 0;
        using var cts  = new CancellationTokenSource();

        Task<int?> Probe(CancellationToken _) {
            probeCalls++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        await Assert.That(async () =>
            await CursorImportSource.FetchServerLastLineWithCappedRetryAsync(Probe, cts.Token)
        ).Throws<OperationCanceledException>();

        await Assert.That(probeCalls).IsEqualTo(1);
    }

    [Test]
    [Arguments(500, true)]
    [Arguments(503, true)]
    [Arguments(408, true)]
    [Arguments(404, false)]
    [Arguments(429, false)]
    [Arguments(401, false)]
    public async Task is_retryable_watermark_probe_status_classifies_correctly(int statusCode, bool expected) {
        var result = CursorImportSource.IsRetryableWatermarkProbeStatus((HttpStatusCode)statusCode);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task is_retryable_watermark_probe_status_is_false_for_a_null_statusless_code() {
        var result = CursorImportSource.IsRetryableWatermarkProbeStatus(null);
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task import_session_attaches_repository_from_detected_workspace_repo() {
        // the import/backfill path must attach a `repository` node to the
        // synthetic sessionStart so historical Cursor sessions emit RepositoryDetected
        // server-side and group under their repo (not just "All repos"). Detected from
        // the workspace folder via the repo detector (git remote parse).
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n{}\n");

        var src = new CursorImportSource(Config.Root, 
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(
                new RepositoryPayload {
                    Owner     = "kurrent-io",
                    RepoName  = "kcap-server",
                    Branch    = "main",
                    RemoteUrl = "git@github.com:kurrent-io/kcap-server.git",
                }
            )
        );

        var posted = new List<(string Path, string Body)>();
        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        var repo      = startNode["repository"];
        await Assert.That(repo).IsNotNull();
        await Assert.That(repo!["owner"]!.GetValue<string>()).IsEqualTo("kurrent-io");
        await Assert.That(repo!["repo_name"]!.GetValue<string>()).IsEqualTo("kcap-server");
    }

    [Test]
    public async Task import_session_omits_repository_when_workspace_repo_undetected() {
        // Fail-open: non-git workspace (detector returns null) → no repository node,
        // session stays unattributed rather than erroring.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, 
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(null)
        );

        var posted = new List<(string Path, string Body)>();
        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = "/Users/me/dev/proj",
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted.First(p => p.Path == "/hooks/session-start/cursor").Body)!;
        await Assert.That(startNode["repository"]).IsNull();
    }

    // Builds a parent (Task prompt) + child (first user_query == prompt) session pair in the
    // same workspace and returns their (parentId, childId, discovered, classified).
    async Task<(string ParentId, string ChildId, IReadOnlyList<ImportCommand.SessionClassification> Classified, CursorImportSource Src)> SetupParentChildAsync(ProjectsDirFixture fx) {
        const string prompt = "EXPLORE the repo and report back";
        var childUserText = System.Text.Json.JsonSerializer.Serialize("<user_query>\n" + prompt + "\n</user_query>");
        var taskPrompt    = System.Text.Json.JsonSerializer.Serialize(prompt);

        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"description\":\"d\",\"prompt\":" + taskPrompt + ",\"subagent_type\":\"generalPurpose\"}}]}}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" + childUserText + "}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var getHandler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var getClient  = new HttpClient(getHandler);
        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(getClient, minLines: 1), CancellationToken.None);
        return ("11111111111111111111111111111111", "22222222222222222222222222222222", classified, src);
    }

    [Test]
    public async Task parent_import_nests_subagent_child_before_its_own_session_end() {
        // the PARENT's import ingests its subagent child under the parent's
        // AgentSubsession stream (subagent-start + transcript-with-agent_id + subagent-stop),
        // and — critically — BEFORE the parent's own session-end, so a late subagent-start
        // can't reactivate the ended parent (review finding on ordering).
        using var fx = new ProjectsDirFixture();
        var (parentId, childId, classified, src) = await SetupParentChildAsync(fx);

        var parentClass = classified.Single(c => c.SessionId == parentId);
        // Parent classification carries its children; parent is not itself a child.
        await Assert.That(parentClass.SourceMeta!.ContainsKey("SubagentChildren")).IsTrue();
        await Assert.That(parentClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsFalse();

        var posted = new List<string>();
        var bodies = new List<(string Path, string Body)>();
        using var postHandler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound), // subsession watermark → New
            postCapture: (req, body) => { posted.Add(req.RequestUri!.AbsolutePath); bodies.Add((req.RequestUri!.AbsolutePath, body)); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var postClient = new HttpClient(postHandler);

        var outcome = await src.ImportSessionAsync(parentClass, new ImportContext(postClient, "http://localhost", ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);

        // Parent lifecycle + the child's subagent lifecycle are all present.
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");
        await Assert.That(posted).Contains("/hooks/subagent-start");
        await Assert.That(posted).Contains("/hooks/subagent-stop");

        // Ordering guard (the review finding): subagent-start/-stop must precede the parent's session-end.
        var endIdx        = posted.IndexOf("/hooks/session-end/cursor");
        var subStartIdx   = posted.IndexOf("/hooks/subagent-start");
        var subStopIdx    = posted.LastIndexOf("/hooks/subagent-stop");
        await Assert.That(subStartIdx >= 0 && subStartIdx < endIdx).IsTrue();
        await Assert.That(subStopIdx  >= 0 && subStopIdx  < endIdx).IsTrue();

        var startNode = JsonNode.Parse(bodies.First(b => b.Path == "/hooks/subagent-start").Body)!;
        await Assert.That(startNode["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(startNode["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
        await Assert.That(startNode["agent_type"]!.GetValue<string>()).IsEqualTo("generalPurpose");
        // HookBase-required fields (server binding rejects the hook without them) + fail-closed.
        await Assert.That(startNode["cwd"]).IsNotNull();
        await Assert.That(startNode["transcript_path"]).IsNotNull();
        await Assert.That(startNode["strict"]!.GetValue<bool>()).IsTrue();

        var subTranscript = bodies.First(b => b.Path == "/hooks/transcript" && JsonNode.Parse(b.Body)!["agent_id"] is not null);
        var tNode = JsonNode.Parse(subTranscript.Body)!;
        await Assert.That(tNode["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(tNode["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
        // Strict so server-side normalization failures fail the import, not just HTTP errors.
        await Assert.That(tNode["strict"]!.GetValue<bool>()).IsTrue();

        // Full SubagentStopHook + HookBase shape (review finding): the server binds all fields.
        var stopNode = JsonNode.Parse(bodies.First(b => b.Path == "/hooks/subagent-stop").Body)!;
        await Assert.That(stopNode["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(stopNode["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
        await Assert.That(stopNode["cwd"]).IsNotNull();
        await Assert.That(stopNode["transcript_path"]).IsNotNull();
        await Assert.That(stopNode["stop_hook_active"]).IsNotNull();
        await Assert.That(stopNode["agent_transcript_path"]!.GetValue<string>().EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(stopNode["last_assistant_message"]).IsNotNull();
        await Assert.That(stopNode["strict"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task parent_import_fails_when_a_subagent_transcript_post_is_rejected() {
        // Review finding (fail-closed): a rejected child transcript POST must abort the parent
        // import (return Failed) BEFORE the parent session-end, so a re-run repairs it — rather
        // than posting subagent-stop + session-end over a child whose transcript wasn't accepted.
        using var fx = new ProjectsDirFixture();
        var (parentId, childId, classified, src) = await SetupParentChildAsync(fx);
        var parentClass = classified.Single(c => c.SessionId == parentId);

        var posted = new List<string>();
        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                var path = req.RequestUri!.AbsolutePath;
                posted.Add(path);
                // Reject only the subagent transcript (agent_id present).
                if (path == "/hooks/transcript" && JsonNode.Parse(body)!["agent_id"] is not null)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(parentClass, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Failed);
        // The parent's session-end must NOT be posted after a failed child transcript.
        await Assert.That(posted).DoesNotContain("/hooks/session-end/cursor");
    }

    [Test]
    public async Task subagent_child_import_is_a_noop_handled_by_the_parent() {
        // A child's own routed import must no-op (Skipped) — the parent imports it. Otherwise
        // the child would create a standalone top-level session AND post subagent lifecycle
        // out of order with the parent.
        using var fx = new ProjectsDirFixture();
        var (parentId, childId, classified, src) = await SetupParentChildAsync(fx);

        var childClass = classified.Single(c => c.SessionId == childId);
        await Assert.That(childClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsTrue();

        var posted = new List<string>();
        using var postHandler = new StubHandler(postCapture: (req, body) => { posted.Add(req.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var postClient = new HttpClient(postHandler);

        var outcome = await src.ImportSessionAsync(childClass, new ImportContext(postClient, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(posted.Count).IsEqualTo(0); // nothing posted — the parent owns this child
    }

    // ImportSessionAsync must re-check quarantine FRESH, before ANY
    // lifecycle/transcript delivery, rather than trusting only ClassifyAsync's earlier check. This
    // simulates the runtime rewrite guard tripping SOMETIME AFTER classification (repo probing, an
    // interactive confirmation prompt, or simply queueing behind other sessions in the same
    // import run) by quarantining the session directly and calling ImportSessionAsync WITHOUT ever
    // going through ClassifyAsync at all — the fix must still stop everything.
    [Test]
    public async Task import_session_posts_nothing_when_quarantined_between_classify_and_import() {
        using var fx = new ProjectsDirFixture();
        var sessionIdWithDashes = Guid.NewGuid().ToString();
        var sessionId           = CursorImportSource.NormalizeCursorSessionId(sessionIdWithDashes);
        var jsonl = fx.AddSession("Users-me-proj", sessionIdWithDashes, "{\"a\":1}\n{\"b\":2}\n");

        Markers.Quarantine(sessionId, "transcript rewrite detected");
        try {
            var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

            var posted = new List<string>();
            using var handler = new StubHandler(
                postCapture: (req, _) => { posted.Add(req.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.OK); });
            using var client = new HttpClient(handler);

            var outcome = await src.ImportSessionAsync(
                new ImportCommand.SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = "",
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.New,
                    Vendor     = HarnessId.Cursor,
                    // QuarantineIdentity as ClassifyAsync would have stamped it for a plain
                    // top-level (non-child) session — itself.
                    SourceMeta = new Dictionary<string, object?> {
                        ["TranscriptPath"]     = jsonl,
                        ["QuarantineIdentity"] = sessionId,
                    },
                },
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Skipped);
            await Assert.That(posted.Count).IsEqualTo(0);
        } finally { try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { } }
    }

    // A non-quarantined session must import normally even WITH a QuarantineIdentity stamped —
    // the fresh re-check must not false-positive.
    [Test]
    public async Task import_session_proceeds_normally_when_quarantine_identity_is_stamped_but_not_quarantined() {
        using var fx = new ProjectsDirFixture();
        var jsonl = fx.AddSession(
            "Users-me-proj", "11111111-1111-1111-1111-111111111111", "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var sessionId = "11111111111111111111111111111111";

        var posted = new List<string>();
        using var handler = new StubHandler(
            postCapture: (req, _) => { posted.Add(req.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);

        var outcome = await src.ImportSessionAsync(
            new ImportCommand.SessionClassification {
                SessionId  = sessionId,
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> {
                    ["TranscriptPath"]     = jsonl,
                    ["QuarantineIdentity"] = sessionId,
                },
            },
            new ImportContext(client, "http://localhost", ForcePrivate: false),
            CancellationToken.None
        );

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(posted).Contains("/hooks/session-start/cursor");
        await Assert.That(posted).Contains("/hooks/transcript");
        await Assert.That(posted).Contains("/hooks/session-end/cursor");
    }

    // a quarantine that appears WHILE the
    // sessionStart POST is in flight must still be caught before any transcript content is sent;
    // the session already legitimately exists server-side by then, so it's best-effort closed
    // with session-end rather than left hanging "active" forever, and the outcome signals Failed
    // so a re-run is attempted (which will hit the pre-flight check and cleanly Skip from then on).
    [Test]
    public async Task import_session_aborts_transcript_but_still_closes_the_session_when_quarantined_during_the_session_start_post() {
        using var fx = new ProjectsDirFixture();
        var sessionIdWithDashes = Guid.NewGuid().ToString();
        var sessionId           = CursorImportSource.NormalizeCursorSessionId(sessionIdWithDashes);
        var jsonl = fx.AddSession("Users-me-proj", sessionIdWithDashes, "{\"a\":1}\n{\"b\":2}\n");
        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();
        using var handler = new StubHandler(postCapture: (req, _) => {
            var path = req.RequestUri!.AbsolutePath;
            posted.Add(path);
            if (path == "/hooks/session-start/cursor") {
                // The runtime guard trips WHILE this POST is "in flight" (simulated side effect).
                Markers.Quarantine(sessionId, "test");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        try {
            var outcome = await src.ImportSessionAsync(
                new ImportCommand.SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = "",
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.New,
                    Vendor     = HarnessId.Cursor,
                    SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
                },
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
            await Assert.That(posted).Contains("/hooks/session-start/cursor");
            await Assert.That(posted).DoesNotContain("/hooks/transcript");
            await Assert.That(posted).Contains("/hooks/session-end/cursor");
        } finally { try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { } }
    }

    // the round-2 fix above only re-checked quarantine
    // immediately BEFORE SendTranscriptBatches; that method itself streamed the (mutable) file and
    // posted one request per 100 lines with NO quarantine check at all, so a quarantine written by
    // the live watcher's runtime rewrite guard AFTER the first transcript POST still let every
    // remaining batch post. This test drives a transcript large enough to need TWO batches (150
    // lines: one 100-line flush, one 50-line final flush) and quarantines the session the instant
    // the FIRST batch lands, proving the second is aborted rather than delivered.
    [Test]
    public async Task import_session_aborts_the_remaining_batch_when_quarantine_is_written_between_batches() {
        using var fx = new ProjectsDirFixture();
        var sessionIdWithDashes = Guid.NewGuid().ToString();
        var sessionId           = CursorImportSource.NormalizeCursorSessionId(sessionIdWithDashes);
        var lines = string.Concat(Enumerable.Range(0, 150).Select(i => $$"""{"n":{{i}}}""" + "\n"));
        var jsonl = fx.AddSession("Users-me-proj", sessionIdWithDashes, lines);
        var src   = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted         = new List<string>();
        var transcriptPosts = 0;
        using var handler = new StubHandler(postCapture: (req, _) => {
            var path = req.RequestUri!.AbsolutePath;
            posted.Add(path);
            if (path == "/hooks/transcript") {
                transcriptPosts++;
                if (transcriptPosts == 1) {
                    // The live watcher's runtime rewrite guard trips WHILE the first 100-line
                    // batch is "in flight" (simulated side effect, mirroring the sessionStart test
                    // above but at the transcript-batch boundary instead).
                    Markers.Quarantine(sessionId, "test");
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        try {
            var outcome = await src.ImportSessionAsync(
                new ImportCommand.SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = "",
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.New,
                    Vendor     = HarnessId.Cursor,
                    SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
                },
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
            // Exactly ONE transcript batch posted (the first 100 lines) — the second (remaining
            // 50 lines) must never be sent once the quarantine marker appears.
            await Assert.That(transcriptPosts).IsEqualTo(1);
            await Assert.That(posted).Contains("/hooks/session-start/cursor");
            // Best-effort closed rather than left hanging "active" forever (same contract as the
            // two boundary checks — pre-flight and post-sessionStart — above).
            await Assert.That(posted).Contains("/hooks/session-end/cursor");
        } finally { try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { } }
    }

    // the round-3 fix above only proved the SECOND of TWO
    // batches gets aborted; it never exercised a transcript that is a SINGLE (final and only)
    // batch, which is exactly the gap SendTranscriptBatches' round-3 wiring left open (no "next"
    // pre-POST check ever runs for a one-batch send). This drives a transcript of 30 lines
    // (well under the 100-line flush threshold) and quarantines the instant that one-and-only
    // transcript POST lands, proving it is still observed and routed through the same
    // best-effort close-and-fail contract.
    [Test]
    public async Task import_session_aborts_when_quarantine_is_written_during_the_only_batch_post() {
        using var fx = new ProjectsDirFixture();
        var sessionIdWithDashes = Guid.NewGuid().ToString();
        var sessionId           = CursorImportSource.NormalizeCursorSessionId(sessionIdWithDashes);
        var lines = string.Concat(Enumerable.Range(0, 30).Select(i => $$"""{"n":{{i}}}""" + "\n"));
        var jsonl = fx.AddSession("Users-me-proj", sessionIdWithDashes, lines);
        var src   = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted           = new List<string>();
        var transcriptPosts  = 0;
        using var handler = new StubHandler(postCapture: (req, _) => {
            var path = req.RequestUri!.AbsolutePath;
            posted.Add(path);
            if (path == "/hooks/transcript") {
                transcriptPosts++;
                // The live watcher's runtime rewrite guard trips WHILE this — the ONLY — batch is
                // "in flight" (simulated side effect). There is no second batch to catch this on a
                // pre-POST check; the fix must observe it immediately AFTER this POST instead.
                Markers.Quarantine(sessionId, "test");
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        try {
            var outcome = await src.ImportSessionAsync(
                new ImportCommand.SessionClassification {
                    SessionId  = sessionId,
                    FilePath   = "",
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.New,
                    Vendor     = HarnessId.Cursor,
                    SourceMeta = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
                },
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
            // Exactly ONE transcript POST — the only batch this transcript ever needed.
            await Assert.That(transcriptPosts).IsEqualTo(1);
            await Assert.That(posted).Contains("/hooks/session-start/cursor");
            // Best-effort closed rather than left hanging "active" forever.
            await Assert.That(posted).Contains("/hooks/session-end/cursor");
        } finally { try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { } }
    }

    // a quarantine trip during a CHILD's own transcript
    // delivery must propagate to the parent's best-effort close-and-fail path — the same contract
    // as a trip during the parent's OWN transcript. Before the fix, SendSubagentLifecycleAsync's
    // catch-all swallowed the typed TranscriptDeliveryAbortedException into a bare `false`, so
    // ImportSessionAsync returned Failed WITHOUT ever posting the parent's session-end — even
    // though the child's subagent-start had already landed, leaving the parent/subsession stuck
    // Active forever (the quarantine marker makes the next run Skip at preflight instead of
    // repairing it).
    [Test]
    public async Task import_session_closes_and_fails_when_a_child_transcript_quarantine_trips_mid_delivery() {
        using var fx = new ProjectsDirFixture();
        var parentIdWithDashes = Guid.NewGuid().ToString();
        var parentId           = CursorImportSource.NormalizeCursorSessionId(parentIdWithDashes);
        var childIdWithDashes  = Guid.NewGuid().ToString();
        var childId            = CursorImportSource.NormalizeCursorSessionId(childIdWithDashes);

        var parentJsonl = fx.AddSession("Users-me-proj", parentIdWithDashes, "{\"a\":1}\n");
        var childJsonl  = fx.AddSession("Users-me-proj", childIdWithDashes, "{\"b\":1}\n{\"c\":2}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<string>();
        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound), // subsession watermark → New
            postCapture: (req, body) => {
                var path = req.RequestUri!.AbsolutePath;
                posted.Add(path);
                if (path == "/hooks/transcript" && JsonNode.Parse(body)!["agent_id"] is not null) {
                    // The child's OWN transcript delivery is what trips the guard mid-flight.
                    Markers.Quarantine(parentId, "test");
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        try {
            var classification = new ImportCommand.SessionClassification {
                SessionId  = parentId,
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> {
                    ["TranscriptPath"]    = parentJsonl,
                    ["SubagentChildren"]  = new List<CursorImportSource.CursorSubagentChild> {
                        new(childId, childJsonl, "task"),
                    },
                },
            };

            var outcome = await src.ImportSessionAsync(
                classification,
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
            // The child's subagent-start WAS already posted before the quarantine tripped mid its
            // OWN transcript delivery.
            await Assert.That(posted).Contains("/hooks/subagent-start");
            // The abort happens before subagent-stop — the child is left correctly unfinished.
            await Assert.That(posted).DoesNotContain("/hooks/subagent-stop");
            // The parent must still be best-effort closed instead of left hanging Active forever
            // (this is exactly the case the pre-fix bare-`false` return skipped).
            await Assert.That(posted).Contains("/hooks/session-end/cursor");
        } finally { try { File.Delete(Markers.QuarantinePath(parentId)); } catch { } }
    }

    // never START a NEW child once the family is found
    // quarantined, even when that quarantine was NOT tripped by this child's own delivery (a
    // concurrent trip — the live watcher runs alongside this import — discovered only when it's
    // this child's turn in the loop). Two children are configured in a fixed, known order; the
    // quarantine lands the instant child A's OWN lifecycle finishes cleanly (its subagent-stop),
    // simulating a trip unrelated to anything child B does. Child B must never be started.
    [Test]
    public async Task import_session_never_starts_a_later_child_once_the_family_is_found_quarantined() {
        using var fx = new ProjectsDirFixture();
        var parentIdWithDashes = Guid.NewGuid().ToString();
        var parentId           = CursorImportSource.NormalizeCursorSessionId(parentIdWithDashes);
        var childAIdWithDashes = Guid.NewGuid().ToString();
        var childAId           = CursorImportSource.NormalizeCursorSessionId(childAIdWithDashes);
        var childBIdWithDashes = Guid.NewGuid().ToString();
        var childBId           = CursorImportSource.NormalizeCursorSessionId(childBIdWithDashes);

        var parentJsonl = fx.AddSession("Users-me-proj", parentIdWithDashes, "{\"a\":1}\n");
        var childAJsonl = fx.AddSession("Users-me-proj", childAIdWithDashes, "{\"b\":1}\n");
        var childBJsonl = fx.AddSession("Users-me-proj", childBIdWithDashes, "{\"c\":1}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var bodies = new List<(string Path, string Body)>();
        using var handler = new StubHandler(
            getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            postCapture: (req, body) => {
                var path = req.RequestUri!.AbsolutePath;
                bodies.Add((path, body));
                if (path == "/hooks/subagent-stop" && JsonNode.Parse(body)!["agent_id"]!.GetValue<string>() == childAId) {
                    // A trip unrelated to child A's own (clean) delivery — e.g. the live watcher
                    // concurrently detected a rewrite — lands right as child A finishes.
                    Markers.Quarantine(parentId, "test");
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        try {
            var classification = new ImportCommand.SessionClassification {
                SessionId  = parentId,
                FilePath   = "",
                EncodedCwd = "",
                Meta       = new SessionMetadata(),
                Status     = ImportCommand.ClassificationStatus.New,
                Vendor     = HarnessId.Cursor,
                SourceMeta = new Dictionary<string, object?> {
                    ["TranscriptPath"]   = parentJsonl,
                    ["SubagentChildren"] = new List<CursorImportSource.CursorSubagentChild> {
                        new(childAId, childAJsonl, "task"),
                        new(childBId, childBJsonl, "task"),
                    },
                },
            };

            var outcome = await src.ImportSessionAsync(
                classification,
                new ImportContext(client, "http://localhost", ForcePrivate: false),
                CancellationToken.None
            );

            await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);

            var subagentStartAgentIds = bodies
                .Where(b => b.Path == "/hooks/subagent-start")
                .Select(b => JsonNode.Parse(b.Body)!["agent_id"]!.GetValue<string>())
                .ToList();

            // Child A started and completed normally before the quarantine appeared.
            await Assert.That(subagentStartAgentIds).Contains(childAId);
            // Child B must NEVER be started once the family is found quarantined — even though
            // nothing about child B's OWN delivery caused the trip.
            await Assert.That(subagentStartAgentIds).DoesNotContain(childBId);
            await Assert.That(bodies.Any(b => b.Path == "/hooks/session-end/cursor")).IsTrue();
        } finally { try { File.Delete(Markers.QuarantinePath(parentId)); } catch { } }
    }

    // subagentLinks is only ever computed from the sessions THIS batch
    // discovered. A --session <child> filter excludes the parent from that batch entirely, so the
    // in-batch correlator can never produce the family link. The persisted CursorLiveSubagentLinker
    // marker (written by the live hook dispatcher, independent of any one CLI invocation's
    // discovery) is the fallback that still resolves the family/quarantine identity.
    [Test]
    public async Task classify_resolves_parent_quarantine_via_the_persisted_live_marker_when_session_filter_excludes_the_parent() {
        using var fx = new ProjectsDirFixture();
        var childIdWithDashes = Guid.NewGuid().ToString();
        var childId           = CursorImportSource.NormalizeCursorSessionId(childIdWithDashes);
        var parentId          = Guid.NewGuid().ToString("N");

        fx.AddSession("Users-me-proj", childIdWithDashes, "{\"a\":1}\n{\"b\":2}\n");
        // Mirrors CursorHookCommand.SaveLink at the child's own sessionStart — written by the LIVE
        // path independent of what any one `kcap import` invocation happens to discover.
        CursorLiveSubagentLinker.SaveLink(Config.Root, childId, parentId, "task");
        Markers.Quarantine(parentId, "transcript rewrite detected");

        try {
            var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
            using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var client  = new HttpClient(handler);

            // --session <child>: the parent's transcript doesn't even exist on disk in this test
            // (it was never discovered), so the in-batch correlator has nothing to correlate
            // against — only the persisted marker fallback can resolve the family identity.
            var discovered = await src.DiscoverAsync(Filters(filterSession: childIdWithDashes), CancellationToken.None);
            var classified = await src.ClassifyAsync(discovered, Ctx(client, minLines: 1), CancellationToken.None);

            await Assert.That(classified.Count).IsEqualTo(1);
            await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
            await Assert.That(classified[0].ProbeErrorReason).Contains("quarantined");
        } finally {
            try { File.Delete(Markers.QuarantinePath(parentId)); } catch { }
        }
    }

    [Test]
    public async Task ResolveQuarantineIdentity_falls_back_to_the_persisted_live_marker_when_not_in_subagentLinks() {
        var child  = Guid.NewGuid().ToString("N");
        var parent = Guid.NewGuid().ToString("N");
        CursorLiveSubagentLinker.SaveLink(Config.Root, child, parent, "task");

        var empty = new Dictionary<string, CursorSubagentCorrelator.SubagentLink>();
        await Assert.That(CursorImportSource.ResolveQuarantineIdentity(Config.Root, child, empty)).IsEqualTo(parent);
    }

    [Test]
    public async Task ResolveQuarantineIdentity_falls_back_to_self_when_no_link_anywhere() {
        var sid   = Guid.NewGuid().ToString("N");
        var empty = new Dictionary<string, CursorSubagentCorrelator.SubagentLink>();
        await Assert.That(CursorImportSource.ResolveQuarantineIdentity(Config.Root, sid, empty)).IsEqualTo(sid);
    }

    [Test]
    public async Task cross_workspace_prompt_match_does_not_link_child_to_parent_in_a_different_workspace() {
        // Qodo fix regression test: same-workspace discovery only exists to give a filtered/scoped
        // import VISIBILITY into a parent — it must also CONSTRAIN correlation to that workspace.
        // A child in workspace A whose first user_query happens to canonically match a Task prompt
        // issued by a parent in a DIFFERENT workspace B must stay standalone; correlating across the
        // union of every workspace touched by this classify call would wrongly nest it.
        using var fx = new ProjectsDirFixture();

        const string prompt      = "EXPLORE the repo and report back";
        var          childUserText = System.Text.Json.JsonSerializer.Serialize("<user_query>\n" + prompt + "\n</user_query>");
        var          taskPrompt    = System.Text.Json.JsonSerializer.Serialize(prompt);

        // Parent (Task prompt) lives in workspace B.
        fx.AddSession("Workspace-B", "11111111-1111-1111-1111-111111111111",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"go\"}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Task\",\"input\":{\"description\":\"d\",\"prompt\":" + taskPrompt + ",\"subagent_type\":\"generalPurpose\"}}]}}\n");
        // Child (matching first user_query) lives in a DIFFERENT workspace A — unrelated to the
        // parent above other than the coincidental prompt match.
        fx.AddSession("Workspace-A", "22222222-2222-2222-2222-222222222222",
            "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":" + childUserText + "}]}}\n" +
            "{\"role\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None);
        var classified = await src.ClassifyAsync(discovered, Ctx(client, minLines: 1), CancellationToken.None);

        var parentClass = classified.Single(c => c.SessionId == "11111111111111111111111111111111");
        var childClass  = classified.Single(c => c.SessionId == "22222222222222222222222222222222");

        // The child must NOT be linked to the cross-workspace parent...
        await Assert.That(childClass.SourceMeta!.ContainsKey("IsSubagentChild")).IsFalse();
        // ...and the parent must NOT list the cross-workspace session as one of its children.
        await Assert.That(parentClass.SourceMeta!.ContainsKey("SubagentChildren")).IsFalse();
    }

    [Test]
    public async Task classify_throws_promptly_when_cancelled_during_same_workspace_scan() {
        // Qodo fix regression test: the same-workspace transcript-path scan
        // (DiscoverSameWorkspaceSessionPaths) must consult the CancellationToken passed to
        // ClassifyAsync rather than running an uncancelable, synchronous directory walk to
        // completion — otherwise Ctrl-C hangs on a large/slow filesystem.
        using var fx = new ProjectsDirFixture();
        fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");
        fx.AddSession("Users-me-proj", "22222222-2222-2222-2222-222222222222", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var discovered = await src.DiscoverAsync(Filters(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => src.ClassifyAsync(discovered, Ctx(client, minLines: 1), cts.Token)
        );
    }

    [Test]
    public async Task import_session_omits_workspace_roots_when_cwd_unresolved() {
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("unknown-workspace", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId  = "11111111111111111111111111111111",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.New,
            Vendor     = HarnessId.Cursor,
            SourceMeta = new Dictionary<string, object?> {
                ["TranscriptPath"]  = jsonl,
                ["WorkspaceFolder"] = null,
            },
        };

        await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        var startNode = JsonNode.Parse(posted[0].Body)!;
        await Assert.That(startNode["workspace_roots"]).IsNull();
    }

    [Test]
    public async Task import_session_resumes_from_resume_line_for_partial_status() {
        using var fx = new ProjectsDirFixture();

        var jsonl = fx.AddSession(
            "Users-me-proj",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"
        );

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);

        var posted = new List<(string Path, string Body)>();

        using var handler = new StubHandler(
            postCapture: (req, body) => {
                posted.Add((req.RequestUri!.AbsolutePath, body));

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );
        using var client = new HttpClient(handler);

        var classification = new ImportCommand.SessionClassification {
            SessionId      = "11111111111111111111111111111111",
            FilePath       = "",
            EncodedCwd     = "",
            Meta           = new SessionMetadata(),
            Status         = ImportCommand.ClassificationStatus.Partial,
            ResumeFromLine = 1,
            Vendor         = HarnessId.Cursor,
            SourceMeta     = new Dictionary<string, object?> { ["TranscriptPath"] = jsonl },
        };

        var outcome = await src.ImportSessionAsync(classification, new ImportContext(client, "http://localhost", ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Resumed);

        var transcriptPost = posted.First(p => p.Path == "/hooks/transcript");
        var node           = JsonNode.Parse(transcriptPost.Body)!;
        var lines          = node["lines"]!.AsArray();
        var lineNumbers    = node["line_numbers"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]!.GetValue<string>()).IsEqualTo("{\"b\":2}");
        await Assert.That((int)lineNumbers[0]!).IsEqualTo(1);
    }

    [Test]
    public async Task classify_sets_excluded_repo_key_when_workspace_repo_matches_excluded_list() {
        // Qodo bug #1 regression test: Cursor sessions must surface
        // ExcludedRepoKey so ImportCommand's auto-skip/prompt logic applies.
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/secret");

        fx.AddSession(
            "Users-me-dev-secret",
            "11111111-1111-1111-1111-111111111111",
            "{\"a\":1}\n{\"b\":2}\n"
        );

        var src = new CursorImportSource(Config.Root, 
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => Task.FromResult<RepositoryPayload?>(
                new RepositoryPayload { Owner = "acme", RepoName = "secret" }
            )
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        var classified = await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            new ClassifyContext(
                client,
                "http://localhost",
                MinLines: 1,
                ExcludedRepos: new[] { "acme/secret" },
                ExcludedPaths: null,
                Home: Home
            ),
            CancellationToken.None
        );

        await Assert.That(classified[0].ExcludedRepoKey).IsEqualTo("acme/secret");
    }

    [Test]
    public async Task classify_does_not_invoke_repo_detection_when_excluded_repos_empty() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/foo");
        fx.AddSession("Users-me-dev-foo", "11111111-1111-1111-1111-111111111111", "{}\n");

        var detectorCalls = 0;

        var src = new CursorImportSource(Config.Root, 
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => {
                detectorCalls++;

                return Task.FromResult<RepositoryPayload?>(null);
            }
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            Ctx(client, minLines: 0),
            CancellationToken.None
        );

        await Assert.That(detectorCalls).IsEqualTo(0);
    }

    [Test]
    public async Task classify_caches_repo_detection_per_workspace_across_sessions() {
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/shared");
        fx.AddSession("Users-me-dev-shared", "11111111-1111-1111-1111-111111111111", "{}\n");
        fx.AddSession("Users-me-dev-shared", "22222222-2222-2222-2222-222222222222", "{}\n");
        fx.AddSession("Users-me-dev-shared", "33333333-3333-3333-3333-333333333333", "{}\n");

        var detectorCalls = 0;

        var src = new CursorImportSource(Config.Root, 
            fx.ProjectsDir,
            fx.WorkspaceStorageDir,
            repoDetector: _ => {
                detectorCalls++;

                return Task.FromResult<RepositoryPayload?>(new RepositoryPayload { Owner = "o", RepoName = "r" });
            }
        );

        using var handler = new StubHandler(getResponse: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client  = new HttpClient(handler);

        await src.ClassifyAsync(
            await src.DiscoverAsync(Filters(), CancellationToken.None),
            new ClassifyContext(
                client,
                "http://localhost",
                MinLines: 0,
                ExcludedRepos: new[] { "o/r" },
                ExcludedPaths: null,
                Home: Home
            ),
            CancellationToken.None
        );

        await Assert.That(detectorCalls).IsEqualTo(1);
    }

    [Test, RunOn(OS.MacOs | OS.Windows)]
    public async Task discover_cwd_filter_matches_case_insensitively_on_macos_and_windows() {
        // cwd comparison must be case-insensitive where the filesystem is; Linux keeps Ordinal.
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-aaa", "file:///Users/me/dev/MyProj");
        fx.AddSession("Users-me-dev-MyProj", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        // Caller passes a lower-cased cwd, e.g. from a shell tab-completion.
        var got = await src.DiscoverAsync(Filters(filterCwd: "/users/me/dev/myproj"), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
    }

    [Test, ExcludeOn(OS.Linux)]
    public async Task discover_since_filter_uses_file_creation_time_not_last_write() {
        // A session whose JSONL was created BEFORE the cutoff but appended to AFTER it must still
        // be excluded by --since. Cursor JSONL has no in-band timestamps, so the file creation
        // time is the closest proxy for session-start.
        //
        // Linux ext4 doesn't expose btime through .NET's File APIs:
        // SetCreationTimeUtc is a silent no-op and GetCreationTimeUtc falls
        // back to mtime. This test exercises a macOS/Windows-only filesystem
        // contract; on Linux the production code degrades to gating on mtime,
        // which is documented but not asserted here.
        using var fx    = new ProjectsDirFixture();
        var       jsonl = fx.AddSession("Users-me-proj", "11111111-1111-1111-1111-111111111111", "{}\n");

        // Set creation = 30 days ago, last write = today.
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        File.SetCreationTimeUtc(jsonl, thirtyDaysAgo);
        File.SetLastWriteTimeUtc(jsonl, DateTime.UtcNow);

        var src   = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var got   = await src.DiscoverAsync(Filters(since: since), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discover_marks_cwd_null_when_two_workspaces_encode_to_the_same_sanitized_key() {
        // Qodo bug #3 regression test: EncodeWorkspacePath is lossy
        // ("/foo/bar" and "/foo-bar" both → "foo-bar"); on collision we leave
        // cwd null rather than misattributing to one of them.
        using var fx = new ProjectsDirFixture();
        fx.AddWorkspaceJson("hash-a", "file:///foo/bar");
        fx.AddWorkspaceJson("hash-b", "file:///foo-bar");
        fx.AddSession("foo-bar", "11111111-1111-1111-1111-111111111111", "{}\n");

        var src = new CursorImportSource(Config.Root, fx.ProjectsDir, fx.WorkspaceStorageDir);
        var got = await src.DiscoverAsync(Filters(), CancellationToken.None);

        await Assert.That(got.Count).IsEqualTo(1);
        await Assert.That(got[0].Cwd).IsNull();
    }

    static DiscoveryFilters Filters(string? filterCwd = null, string? filterSession = null, DateOnly? since = null, int minLines = 0) =>
        new(FilterCwd: filterCwd, FilterSession: filterSession, Since: since, MinLines: minLines);

    ClassifyContext Ctx(HttpClient http, int minLines = 0) =>
        new(http, "http://localhost", minLines, ExcludedRepos: null, ExcludedPaths: null, Home: Home);

    sealed class ProjectsDirFixture : IDisposable {
        public string Root                { get; }
        public string ProjectsDir         => Path.Combine(Root, ".cursor", "projects");
        public string WorkspaceStorageDir => Path.Combine(Root, "workspaceStorage");

        public ProjectsDirFixture() {
            Root = Path.Combine(Path.GetTempPath(), $"kcap-cursor-walker-{Guid.NewGuid():N}");
            Directory.CreateDirectory(ProjectsDir);
            Directory.CreateDirectory(WorkspaceStorageDir);
        }

        public string AddSession(string sanitized, string sessionId, string jsonlContent) {
            var dir = Path.Combine(ProjectsDir, sanitized, "agent-transcripts", sessionId);
            Directory.CreateDirectory(dir);
            var jsonl = Path.Combine(dir, sessionId + ".jsonl");
            File.WriteAllText(jsonl, jsonlContent);

            return jsonl;
        }

        public void AddWorkspaceJson(string hashDir, string folderUri) {
            var dir = Path.Combine(WorkspaceStorageDir, hashDir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "workspace.json"), $$"""{"folder":"{{folderUri}}"}""");
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    sealed class StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage>?         getResponse = null,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture = null
        )
        : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get)
                return getResponse?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            return postCapture?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
