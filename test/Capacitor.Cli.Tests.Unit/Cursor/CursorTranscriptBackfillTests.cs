using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

public class CursorTranscriptBackfillTests {
    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task Backfill_holds_unterminated_final_line_mid_session() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        // Two complete lines + a half-written third with NO trailing newline.
        await File.WriteAllTextAsync(transcript,
            "{\"role\":\"user\",\"message\":{\"content\":[]}}\n{\"role\":\"assistant\",\"message\":{\"content\":[]}}\n{\"role\":\"user\",\"mess");

        string? postedBody = null;
        using var handler = new RecordingHandler(
            getResponse: r => r.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent) : null, // resume from 0
            postCapture: (_, b) => { postedBody = b; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://s", NewSessionId(), transcript, () => false, CancellationToken.None, finalDrain: false);

        var lines = JsonNode.Parse(postedBody!)!["lines"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(2); // the half-written line was HELD
        await Assert.That(stats.LinesPosted).IsEqualTo(2);
    }

    [Test]
    public async Task Backfill_consumes_unterminated_final_line_on_finalDrain_when_complete() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        // Two complete lines + a THIRD complete JSON record with no trailing newline —
        // finalDrain (sessionEnd pre-end drain) must consume it via ConsumeIfComplete rather
        // than stranding a valid final record just because the agent process died before the
        // watcher (whichever component is down) flushed a trailing newline.
        await File.WriteAllTextAsync(transcript,
            "{\"role\":\"user\",\"message\":{\"content\":[]}}\n{\"role\":\"assistant\",\"message\":{\"content\":[]}}\n{\"role\":\"user\",\"message\":{\"content\":[]}}");

        string? postedBody = null;
        using var handler = new RecordingHandler(
            getResponse: r => r.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NoContent) : null,
            postCapture: (_, b) => { postedBody = b; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://s", NewSessionId(), transcript, () => false, CancellationToken.None, finalDrain: true);

        var lines = JsonNode.Parse(postedBody!)!["lines"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(3); // the complete-but-unterminated final line was CONSUMED
        await Assert.That(stats.LinesPosted).IsEqualTo(3);
    }

    [Test]
    public async Task Backfill_noops_when_quarantined() {
        var sessionId = NewSessionId();
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllTextAsync(transcript, "{\"role\":\"user\",\"message\":{\"content\":[]}}\n");
        CursorMarkers.Quarantine(sessionId, "rewrite detected");

        var postCount = 0;
        using var client = new HttpClient(new RecordingHandler(_ => null, (_, _) => { postCount++; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var stats = await CursorTranscriptBackfill.RunAsync(client, "http://s", sessionId, transcript, () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(postCount).IsEqualTo(0);
    }

    [Test]
    public async Task Backfill_holds_when_barrier_pending() {
        var sessionId = NewSessionId();
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllTextAsync(transcript, "{\"role\":\"user\",\"message\":{\"content\":[]}}\n");
        CursorMarkers.CreateBarrier(sessionId, DateTimeOffset.UtcNow);

        var postCount = 0;
        using var client = new HttpClient(new RecordingHandler(
            r => r.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.NoContent) : null,
            (_, _) => { postCount++; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var stats = await CursorTranscriptBackfill.RunAsync(client, "http://s", sessionId, transcript, () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(postCount).IsEqualTo(0);
    }

    // the markers must be re-checked IMMEDIATELY at the delivery
    // boundary (right before the POST), not only before the watermark GET. Simulate a
    // concurrent quarantine landing DURING the watermark probe — after the early check already
    // passed, but before the POST — by setting the marker as a side effect of the GET call
    // itself. Only the late (pre-POST) recheck can catch this; the early check alone would miss
    // it and the corrupted tail would still be posted.
    [Test]
    public async Task Backfill_rechecks_quarantine_immediately_before_the_POST_even_if_set_during_the_watermark_probe() {
        var sessionId = NewSessionId();
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllTextAsync(transcript, "{\"role\":\"user\",\"message\":{\"content\":[]}}\n");

        var postCount = 0;
        using var handler = new RecordingHandler(
            getResponse: r => {
                if (r.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal)) {
                    // A concurrent watcher-side rewrite-guard trip lands exactly here — between
                    // the early check (already passed, since the marker didn't exist yet) and
                    // the POST below.
                    CursorMarkers.Quarantine(sessionId, "rewrite detected mid-flight");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return null;
            },
            postCapture: (_, _) => { postCount++; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(client, "http://s", sessionId, transcript, () => false, CancellationToken.None);

        await Assert.That(postCount).IsEqualTo(0);
        await Assert.That(stats.LinesPosted).IsEqualTo(0);
    }

    [Test]
    public async Task Backfill_rechecks_barrier_immediately_before_the_POST_even_if_created_during_the_watermark_probe() {
        var sessionId = NewSessionId();
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllTextAsync(transcript, "{\"role\":\"user\",\"message\":{\"content\":[]}}\n");

        var postCount = 0;
        using var handler = new RecordingHandler(
            getResponse: r => {
                if (r.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal)) {
                    // A concurrent beforeSubmitPrompt hook creates its ordering barrier exactly
                    // here — after the early check, before the POST.
                    CursorMarkers.CreateBarrier(sessionId, DateTimeOffset.UtcNow);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return null;
            },
            postCapture: (_, _) => { postCount++; return new HttpResponseMessage(HttpStatusCode.OK); });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(client, "http://s", sessionId, transcript, () => false, CancellationToken.None);

        await Assert.That(postCount).IsEqualTo(0);
        await Assert.That(stats.LinesPosted).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_returns_zero_when_transcript_path_null() {
        using var tmp = new TempDir();
        using var handler = new RecordingHandler();
        using var client  = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: null, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(handler.Sent).IsEmpty();
    }

    [Test]
    public async Task RunAsync_resumes_from_last_line_number_plus_one_and_posts_single_batch() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["line0", "line1", "line2", "line3"]);

        string? postedBody = null;
        string? postedPath = null;
        using var handler = new RecordingHandler(
            getResponse: req => req.RequestUri!.AbsolutePath.EndsWith("/last-line", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("""{"last_line_number":1}""")
                }
                : null,
            postCapture: (req, body) => {
                postedPath = req.RequestUri!.AbsolutePath;
                postedBody = body;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(postedPath).IsEqualTo("/hooks/transcript");

        var node = JsonNode.Parse(postedBody!)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(node["vendor"]!.GetValue<string>()).IsEqualTo("cursor");
        var lines = node["lines"]!.AsArray();
        var lineNumbers = node["line_numbers"]!.AsArray();
        await Assert.That(lines.Count).IsEqualTo(2);
        await Assert.That(lines[0]!.GetValue<string>()).IsEqualTo("line2");
        await Assert.That(lines[1]!.GetValue<string>()).IsEqualTo("line3");
        await Assert.That(lineNumbers[0]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(lineNumbers[1]!.GetValue<int>()).IsEqualTo(3);
    }

    [Test]
    public async Task RunAsync_treats_204_NoContent_watermark_as_resume_from_zero() {
        // /api/sessions/{sid}/last-line returns 204 when the session stream
        // exists but no lines have been accepted yet. Treat as resumeFrom=0,
        // not as failure.
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["line0", "line1"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NoContent),
            postCapture: (_, _) => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(2);
        await Assert.That(stats.Failed).IsFalse();
    }

    [Test]
    public async Task RunAsync_does_not_post_when_budget_already_expired() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b", "c"]);

        var postCount = 0;
        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NotFound),
            postCapture: (_, _) => {
                postCount++;
                return new(HttpStatusCode.OK);
            });
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript,
            budget: () => true,
            CancellationToken.None);

        // Budget already burnt before the batch POST — nothing posted, no failure.
        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsFalse();
        await Assert.That(postCount).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_returns_failed_on_POST_non_2xx() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b", "c"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.NotFound),
            postCapture: (_, _) => new(HttpStatusCode.InternalServerError));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsTrue();
    }

    [Test]
    public async Task RunAsync_fails_open_on_watermark_GET_5xx() {
        using var tmp = new TempDir();
        var transcript = Path.Combine(tmp.Path, "t.jsonl");
        await File.WriteAllLinesAsync(transcript, ["a", "b"]);

        using var handler = new RecordingHandler(
            getResponse: _ => new(HttpStatusCode.InternalServerError),
            postCapture: (_, _) => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        var stats = await CursorTranscriptBackfill.RunAsync(
            client, "http://localhost", sessionId: "abc",
            transcriptPath: transcript, budget: () => false, CancellationToken.None);

        await Assert.That(stats.LinesPosted).IsEqualTo(0);
        await Assert.That(stats.Failed).IsTrue();
    }

    sealed class RecordingHandler : HttpMessageHandler {
        readonly Func<HttpRequestMessage, HttpResponseMessage?>? _get;
        readonly Func<HttpRequestMessage, string, HttpResponseMessage>? _post;
        public List<string> Sent { get; } = new();

        public RecordingHandler() { }
        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage?>? getResponse,
            Func<HttpRequestMessage, string, HttpResponseMessage>? postCapture) {
            _get  = getResponse;
            _post = postCapture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Get) {
                return _get?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Sent.Add(body);
            return _post?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-cursor-backfill-test-{Guid.NewGuid().ToString("N")[..8]}");
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
