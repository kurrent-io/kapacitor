using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

public class CodexImportTests {
    [Test]
    public async Task ExtractCodexSessionMetadata_pulls_cwd_model_provider_and_first_timestamp() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        // No turn_context here — the fallback path uses model_provider as
        // the model name. When turn_context is present, the real model
        // overrides this (see
        // ExtractCodexSessionMetadata_prefers_turn_context_model_over_model_provider).
        await File.WriteAllLinesAsync(path, [
            """{"timestamp":"2026-05-07T15:51:46.684Z","type":"session_meta","payload":{"id":"019e0322-05fc-7570-be65-75719c3ea861","timestamp":"2026-05-07T15:50:21.989Z","cwd":"/Users/alexey/dev/temp/Kurrent.Capacitor","originator":"codex-tui","cli_version":"0.128.0","model_provider":"openai","git":{"commit_hash":"abc","branch":"main","repository_url":"https://github.com/owner/repo"}}}""",
            """{"timestamp":"2026-05-07T15:51:46.686Z","type":"event_msg","payload":{"type":"task_started"}}""",
        ]);

        var meta = ImportCommand.ExtractCodexSessionMetadata(path);

        await Assert.That(meta.Cwd).IsEqualTo("/Users/alexey/dev/temp/Kurrent.Capacitor");
        await Assert.That(meta.Model).IsEqualTo("openai");
        await Assert.That(meta.SessionId).IsEqualTo("019e0322-05fc-7570-be65-75719c3ea861");
        await Assert.That(meta.FirstTimestamp).IsNotNull();
        await Assert.That(meta.FirstTimestamp!.Value).IsEqualTo(DateTimeOffset.Parse("2026-05-07T15:50:21.989Z", CultureInfo.InvariantCulture));
        await Assert.That(meta.Slug).IsNull();
    }

    [Test]
    public async Task ExtractCodexSessionMetadata_prefers_turn_context_model_over_model_provider() {
        // session_meta only carries model_provider ("openai"),
        // not the actual model. The real model name lives on turn_context.payload.model
        // (e.g. "gpt-5.5"). Prefer that over the provider so the session card and
        // details show "gpt-5.5" instead of "openai".
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"timestamp":"2026-05-07T15:51:46.684Z","type":"session_meta","payload":{"id":"019e0322-05fc-7570-be65-75719c3ea861","timestamp":"2026-05-07T15:50:21.989Z","cwd":"/x","model_provider":"openai"}}""",
            """{"timestamp":"2026-05-07T15:51:46.700Z","type":"event_msg","payload":{"type":"task_started"}}""",
            """{"timestamp":"2026-05-07T15:51:46.750Z","type":"turn_context","payload":{"turn_id":"abc","cwd":"/x","model":"gpt-5.5"}}""",
        ]);

        var meta = ImportCommand.ExtractCodexSessionMetadata(path);

        await Assert.That(meta.Model).IsEqualTo("gpt-5.5");
    }

    [Test]
    public async Task ExtractCodexSessionMetadata_ignores_turn_context_before_session_meta() {
        // Qodo regression (#52): the docstring resolves the model from the first
        // turn_context AFTER session_meta. If the rollout header is truncated or
        // corrupt and a turn_context appears without a session_meta, its model
        // should NOT be picked up — meta.Model stays null so callers treat the
        // rollout as malformed instead of silently importing it with a model
        // pulled from an unexpected line.
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"turn_context","payload":{"turn_id":"abc","cwd":"/x","model":"gpt-5.5"}}""",
            """{"type":"event_msg","payload":{"type":"task_started"}}""",
        ]);

        var meta = ImportCommand.ExtractCodexSessionMetadata(path);

        await Assert.That(meta.Model).IsNull();
    }

    [Test]
    public async Task ExtractCodexSessionMetadata_falls_back_to_model_provider_when_no_turn_context() {
        // Defensive: if a rollout has session_meta but never reaches a turn_context
        // (e.g. interrupted before the first turn), keep the legacy model_provider
        // fallback so the model field isn't blank.
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"x","cwd":"/x","model_provider":"openai"}}""",
        ]);

        var meta = ImportCommand.ExtractCodexSessionMetadata(path);

        await Assert.That(meta.Model).IsEqualTo("openai");
    }

    [Test]
    public async Task ExtractCodexSessionMetadata_returns_empty_when_first_line_is_not_session_meta() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"event_msg","payload":{"type":"task_started"}}""",
        ]);

        var meta = ImportCommand.ExtractCodexSessionMetadata(path);

        await Assert.That(meta.Cwd).IsNull();
        await Assert.That(meta.Model).IsNull();
    }

    [Test]
    public async Task ExtractCodexGitInfo_returns_git_block_when_present() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"x","cwd":"/x","git":{"commit_hash":"deadbeef","branch":"main","repository_url":"https://github.com/owner/repo"}}}""",
        ]);

        var git = ImportCommand.ExtractCodexGitInfo(path);

        await Assert.That(git).IsNotNull();
        await Assert.That(git!.RemoteUrl).IsEqualTo("https://github.com/owner/repo");
        await Assert.That(git.Branch).IsEqualTo("main");
        await Assert.That(git.CommitHash).IsEqualTo("deadbeef");
    }

    [Test]
    public async Task ExtractCodexGitInfo_returns_null_when_no_git_block() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"x","cwd":"/x"}}""",
        ]);

        var git = ImportCommand.ExtractCodexGitInfo(path);

        await Assert.That(git).IsNull();
    }

    [Test]
    public async Task ExtractCodexTitleContext_skips_environment_context_and_returns_first_real_user_text() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"x","cwd":"/x"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"<permissions instructions>..."}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>foo</environment_context>"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"review combined work in PR 573"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"I will inspect both PRs as one change set."}]}}""",
        ]);

        var (userText, assistantText) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsEqualTo("review combined work in PR 573");
        await Assert.That(assistantText).IsEqualTo("I will inspect both PRs as one change set.");
    }

    [Test]
    public async Task ExtractCodexTitleContext_skips_AGENTS_md_repo_context_prelude() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        // Reproduces the shape Codex emits when AGENTS.md auto-injection fires —
        // a role:"user" message whose input_text starts with the literal AGENTS.md
        // header. Picking this as the title context produces a title for the repo
        // overview, not the actual work request that follows.
        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"x","cwd":"/x"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for /Users/alexey/dev/eventuous/eventuous\n\n<INSTRUCTIONS>\n# Eventuous - AI Agent Context\n..."}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Review this PR https://github.com/Eventuous/eventuous/pull/502"}]}}""",
        ]);

        var (userText, _) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsEqualTo("Review this PR https://github.com/Eventuous/eventuous/pull/502");
    }

    [Test]
    public async Task ExtractCodexTitleContext_skips_turn_aborted_prelude() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<turn_aborted>\nThe user interrupted the previous turn..."}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"skip the tests. CI ran fine"}]}}""",
        ]);

        var (userText, _) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsEqualTo("skip the tests. CI ran fine");
    }

    [Test]
    public async Task ExtractCodexTitleContext_returns_null_user_when_only_environment_context_present() {
        using var tmp = TempDir.WithPathTo("rollout.tmp", out var path);

        await File.WriteAllLinesAsync(path, [
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>only</environment_context>"}]}}""",
        ]);

        var (userText, assistantText) = TitleGenerator.ExtractCodexTitleContext(path);

        await Assert.That(userText).IsNull();
        await Assert.That(assistantText).IsNull();
    }

    // Wire-shape: vendor field is omitted when null and serialized as "vendor":"codex" otherwise.
    // The server's INormalizerSelector keys off this exact field — see Kurrent.Capacitor#576.

    [Test]
    public async Task TranscriptBatch_omits_vendor_field_when_null() {
        var batch = new TranscriptBatch {
            SessionId = "abc",
            Lines     = ["{}"],
            Vendor    = null,
        };

        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

        await Assert.That(json.Contains("\"vendor\"")).IsFalse();
    }

    [Test]
    public async Task ClassifyAsync_returns_ProbeError_when_codex_filename_uuid_disagrees_with_session_meta_id() {
        using var server = WireMockServer.Start();
        // Stub /last-line in case the short-circuit ever regresses — the test should
        // fail loudly rather than time out on a real network call.
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var tmp = new TempDir();
        var dir = tmp.Path;

        var path = Path.Combine(dir, "rollout.jsonl");
        // Filename-derived sessionId we'll pass in: 019e0322...c3ea861 (dashless).
        // Inner payload.id is a different UUID — the validator must catch this.
        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"00000000-0000-0000-0000-000000000001","cwd":"/x"}}""",
        ]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("019e032205fc7570be6575719c3ea861", path, "")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            client,
            server.Url!,
            transcripts,
            minLines: 0,
            excludedRepos: null,
            CancellationToken.None,
            vendor: "codex"
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(result[0].ProbeErrorReason).IsNotNull();
        await Assert.That(result[0].ProbeErrorReason!).Contains("session id mismatch");
    }

    [Test]
    public async Task ClassifyAsync_accepts_codex_session_when_filename_and_session_meta_id_match() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var tmp = new TempDir();
        var dir = tmp.Path;

        var path = Path.Combine(dir, "rollout.jsonl");
        // Filename uuid (dashless) and session_meta payload.id (dashed) refer to
        // the same GUID — validator must let this through to the server probe.
        await File.WriteAllLinesAsync(path, [
            """{"type":"session_meta","payload":{"id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/x"}}""",
        ]);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            ("019e032205fc7570be6575719c3ea861", path, "")
        };

        using var client = new HttpClient();

        var result = await TranscriptFileClassification.ClassifyAsync(
            client,
            server.Url!,
            transcripts,
            minLines: 0,
            excludedRepos: null,
            CancellationToken.None,
            vendor: "codex"
        );

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    }

    [Test]
    public async Task TranscriptBatch_serializes_vendor_when_set() {
        var batch = new TranscriptBatch {
            SessionId = "abc",
            Lines     = ["{}"],
            Vendor    = "codex",
        };

        var json = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

        await Assert.That(json.Contains("\"vendor\":\"codex\"")).IsTrue();
    }
}
