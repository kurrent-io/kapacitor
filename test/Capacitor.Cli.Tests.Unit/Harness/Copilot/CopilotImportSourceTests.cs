using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Discovery-phase tests for <see cref="CopilotImportSource"/> against fake
/// <c>session-state/</c> trees: scaffolding-dir skipping, current-root
/// precedence over the legacy root, workspace.yaml metadata extraction, and
/// the --session / --cwd / --since filters.
/// </summary>
public class CopilotImportSourceTests {
    const string Sid1 = "1053bee4-574f-40e3-84ca-463bd7a82dc2";
    const string Sid2 = "4ae28a73-dd66-46ac-81d2-be94b5e87079";

    [Test]
    public async Task discovery_skips_scaffolding_dirs_without_events_jsonl() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        // Failed-startup scaffolding: workspace.yaml but no events.jsonl.
        tmp.CreateDir(Sid2);
        tmp.CreateFile([Sid2, "workspace.yaml"], $"id: {Sid2}\ncwd: /work/b\n");

        var source   = new CopilotImportSource(tmp.Path, legacyDirOverride: tmp.PathTo("none"));
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(sessions[0].Vendor).IsEqualTo("copilot");
    }

    [Test]
    public async Task discovery_reads_workspace_yaml_metadata() {
        using var tmp = new TempDir();
        WriteSession(
            tmp.Path, Sid1,
            cwd: "/work/a",
            name: "Create a file hello.txt containing 'hello world'",
            createdAt: "2026-06-10T20:23:25.556Z");

        var source   = new CopilotImportSource(tmp.Path, legacyDirOverride: tmp.PathTo("none"));
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z", CultureInfo.InvariantCulture));
        await Assert.That(sessions[0].SourceMeta["Name"]).IsEqualTo("Create a file hello.txt containing 'hello world'");
    }

    [Test]
    public async Task discovery_prefers_current_root_over_legacy_for_same_session() {
        using var tmp = new TempDir();
        var current = tmp.PathTo("session-state");
        var legacy  = tmp.PathTo("history-session-state");

        WriteSession(current, Sid1, cwd: "/work/current");
        WriteSession(legacy, Sid1, cwd: "/work/legacy");
        WriteSession(legacy, Sid2, cwd: "/work/legacy-only");

        var source   = new CopilotImportSource(current, legacy);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(2);

        var migrated = sessions.Single(s => s.SessionId == Sid1.Replace("-", ""));
        await Assert.That(migrated.Cwd).IsEqualTo("/work/current");
        await Assert.That(sessions.Any(s => s.SessionId == Sid2.Replace("-", ""))).IsTrue();
    }

    [Test]
    public async Task session_filter_matches_dashless_and_dashed_input() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        WriteSession(tmp.Path, Sid2, cwd: "/work/b");

        var source = new CopilotImportSource(tmp.Path, legacyDirOverride: tmp.PathTo("none"));

        var byDashed = await source.DiscoverAsync(new DiscoveryFilters(null, Sid1, null, 0), CancellationToken.None);
        await Assert.That(byDashed.Count).IsEqualTo(1);
        await Assert.That(byDashed[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));

        var byDashless = await source.DiscoverAsync(
            new DiscoveryFilters(null, Sid1.Replace("-", ""), null, 0), CancellationToken.None);
        await Assert.That(byDashless.Count).IsEqualTo(1);
    }

    [Test]
    public async Task cwd_filter_excludes_other_workspaces_and_sessions_without_cwd() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        WriteSession(tmp.Path, Sid2, cwd: null);   // no workspace.yaml cwd

        var source  = new CopilotImportSource(tmp.Path, legacyDirOverride: tmp.PathTo("none"));
        var matched = await source.DiscoverAsync(new DiscoveryFilters("/work/a", null, null, 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
    }

    [Test]
    public async Task since_filter_gates_on_session_start() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a", createdAt: "2026-06-01T10:00:00Z");
        WriteSession(tmp.Path, Sid2, cwd: "/work/b", createdAt: "2026-06-09T10:00:00Z");

        var source  = new CopilotImportSource(tmp.Path, legacyDirOverride: tmp.PathTo("none"));
        var matched = await source.DiscoverAsync(
            new DiscoveryFilters(null, null, new DateOnly(2026, 6, 5), 0), CancellationToken.None);

        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    [Test]
    public async Task workspace_yaml_parser_tolerates_missing_file_and_colon_values() {
        using var tmp = new TempDir();

        await Assert.That(CopilotWorkspaceYaml.TryRead(tmp.PathTo("absent.yaml"))).IsNull();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: 4ae28a73-dd66-46ac-81d2-be94b5e87079
            cwd: /private/tmp/work
            name: Fix the bug: timestamps are wrong
            user_named: false
            created_at: 2026-06-10T20:23:25.556Z
            updated_at: 2026-06-10T20:23:37.838Z
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Cwd).IsEqualTo("/private/tmp/work");
        // Values containing ": " must not be truncated at the second colon.
        await Assert.That(meta.Name).IsEqualTo("Fix the bug: timestamps are wrong");
        await Assert.That(meta.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:25.556Z", CultureInfo.InvariantCulture));
        await Assert.That(meta.UpdatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:37.838Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task workspace_yaml_parser_strips_scalar_quotes_from_names() {
        // Copilot quotes the name when it contains YAML-special sequences —
        // the captured fixture has `name: 'Reply with exactly: ok'`. Without
        // unquoting, the literal quotes leak into imported session titles.
        using var tmp = new TempDir();

        var path = tmp.PathTo("workspace.yaml");
        await File.WriteAllTextAsync(path, """
            id: e18d0fe8-95e1-4bd3-87e1-54318e4f3c54
            cwd: '/private/tmp/quoted dir'
            name: 'Reply with exactly: ok'
            created_at: '2026-06-10T20:23:21.000Z'
            """);

        var meta = CopilotWorkspaceYaml.TryRead(path);

        await Assert.That(meta).IsNotNull();
        await Assert.That(meta!.Name).IsEqualTo("Reply with exactly: ok");
        await Assert.That(meta.Cwd).IsEqualTo("/private/tmp/quoted dir");
        await Assert.That(meta.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-06-10T20:23:21.000Z", CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments("'Reply with exactly: ok'", "Reply with exactly: ok")]
    [Arguments("'It''s done: finally'", "It's done: finally")]
    [Arguments("\"Fix the \\\"bug\\\": now\"", "Fix the \"bug\": now")]
    [Arguments("plain value", "plain value")]
    [Arguments("'unterminated", "'unterminated")]
    [Arguments("''", "")]
    public async Task unquote_handles_yaml_scalar_quote_forms(string raw, string expected) {
        await Assert.That(CopilotWorkspaceYaml.Unquote(raw)).IsEqualTo(expected);
    }

    // The server watermark is the max persisted canonical $lineNumber, and
    // every Copilot transcript ends with lines the server normalizer skips
    // (session.shutdown + the sessionEnd hook's own hook.start/hook.end pair).
    // Classification must compare against the last RELEVANT line or complete
    // imports re-classify Partial forever.
    [Test]
    [Arguments("""{"type":"session.start","data":{"sessionId":"x","context":{"cwd":"/w"}},"id":"1","timestamp":"2026-06-10T20:23:49Z","parentId":null}""", true)]
    [Arguments("""{"type":"user.message","data":{"content":"hi","transformedContent":"<x/>hi"},"id":"2","timestamp":"2026-06-10T20:23:50Z","parentId":null}""", true)]
    [Arguments("""{"type":"user.message","data":{"content":"","attachments":[]},"id":"3","timestamp":"2026-06-10T20:23:50Z","parentId":null}""", false)]
    [Arguments("""{"type":"assistant.message","data":{"content":"ok","toolRequests":[],"outputTokens":5},"id":"4","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","toolRequests":[{"toolCallId":"t1","name":"write","arguments":{}}]},"id":"5","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","reasoningText":"thinking","toolRequests":[]},"id":"6","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", true)]
    [Arguments("""{"type":"assistant.message","data":{"content":"","toolRequests":[]},"id":"7","timestamp":"2026-06-10T20:23:55Z","parentId":null}""", false)]
    [Arguments("""{"type":"tool.execution_complete","data":{"toolCallId":"t1","success":true,"result":{"content":"done"}},"id":"8","timestamp":"2026-06-10T20:23:56Z","parentId":null}""", true)]
    [Arguments("""{"type":"tool.execution_start","data":{"toolCallId":"t1","toolName":"write"},"id":"9","timestamp":"2026-06-10T20:23:56Z","parentId":null}""", false)]
    [Arguments("""{"type":"session.shutdown","data":{"shutdownType":"routine","totalPremiumRequests":1},"id":"10","timestamp":"2026-06-10T20:24:40Z","parentId":null}""", false)]
    [Arguments("""{"type":"hook.start","data":{"hookInvocationId":"h1","hookType":"sessionEnd","input":{}},"id":"11","timestamp":"2026-06-10T20:24:41Z","parentId":null}""", false)]
    [Arguments("""{"type":"hook.end","data":{"hookInvocationId":"h1","hookType":"sessionEnd","success":true},"id":"12","timestamp":"2026-06-10T20:24:41Z","parentId":null}""", false)]
    [Arguments("""{"type":"assistant.turn_end","data":{"turnId":"0"},"id":"13","timestamp":"2026-06-10T20:23:59Z","parentId":null}""", false)]
    [Arguments("not json", false)]
    public async Task import_relevance_mirrors_the_server_normalizer_mapping(string line, bool expected) {
        await Assert.That(CopilotImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }

    static void WriteSession(string root, string dashedSid, string? cwd, string? name = null, string? createdAt = null) {
        var dir = Path.Combine(root, dashedSid);
        Directory.CreateDirectory(dir);

        File.WriteAllText(
            Path.Combine(dir, "events.jsonl"),
            """{"type":"session.start","data":{"sessionId":"x"},"id":"11111111-1111-4111-8111-111111111111","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}"""
          + "\n");

        var yaml = $"id: {dashedSid}\n";
        if (cwd is not null) yaml       += $"cwd: {cwd}\n";
        if (name is not null) yaml      += $"name: {name}\n";
        if (createdAt is not null) yaml += $"created_at: {createdAt}\n";
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), yaml);
    }
}
