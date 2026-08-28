using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class EvalCatalogFetchTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    const string CatalogJson = """
        {"retrospective_prompt":"RETRO {TRACE_JSON}","retrospective_prompt_version":"1240",
         "questions":[{"category":"safety","id":"k1","title":"t","question_text":"raw1",
           "prompt":"RENDERED1 {TRACE_JSON} {CACHE_BOUNDARY}","prompt_version":"1234","needs_tools":false}]}
        """;

    // Concrete minimal eval-context: build an EvalContextResult (verified shape — session_id,
    // session_chain, trace[], compaction{}) and serialize via the source-gen context. PrepareAsync
    // returns null if Trace is empty (EvalService.cs:366), so the trace has one entry.
    static string EvalContextJson(string sessionId) {
        var result = new EvalContextResult {
            SessionId    = sessionId,
            SessionChain = [sessionId],
            Trace        = [ new EvalContextEntry {
                Kind = "user_message", Timestamp = DateTimeOffset.UtcNow, Text = "do a thing"
            } ],
            Compaction = new EvalContextCompactionSummary {
                ThresholdBytes = 0, Entries = 1, ToolResultsTotal = 0,
                ToolResultsTruncated = 0, BytesSaved = 0
            }
        };
        return JsonSerializer.Serialize(result, CapacitorJsonContext.Default.EvalContextResult);
    }

    [Test]
    public async Task PrepareAsync_fetches_catalog_and_reconciles_onto_context() {
        _server.Given(Request.Create().WithPath("/api/eval/catalog").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(CatalogJson));
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/eval-context").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(EvalContextJson("sess-1")));

        var observer = new SilentObserver();
        using var http = new HttpClient();

        var catalog = await EvalCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNotNull();

        // The selected ids come from the (alias or dispatch) question list; here pass the catalog ids.
        var selected = catalog!.Questions
            .Select(q => new EvalQuestionDto { Category = q.Category, Id = q.Id, Text = q.Title, Prompt = q.QuestionText, NeedsTools = q.NeedsTools })
            .ToList();

        var ctx = await EvalService.PrepareAsync(
            _server.Url!, http, profile: null, Home, "sess-1", selected, catalog, chain: false, thresholdBytes: null,
            observer, CancellationToken.None, model: "sonnet", evalRunId: "run-1");

        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.RetrospectivePrompt).IsEqualTo("RETRO {TRACE_JSON}");
        await Assert.That(ctx.RetrospectivePromptVersion).IsEqualTo("1240");
        await Assert.That(ctx.Questions.Count).IsEqualTo(1);
        await Assert.That(ctx.Questions[0].Prompt).Contains("RENDERED1");   // rendered, from catalog
        await Assert.That(ctx.Questions[0].RawText).IsEqualTo("raw1");
        await Assert.That(ctx.Questions[0].PromptVersion).IsEqualTo("1234");

        // The catalog endpoint was actually hit.
        var hits = _server.FindLogEntries(Request.Create().WithPath("/api/eval/catalog").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1);

        // Building the text prompt fills the trace and strips cache-boundary.
        var prompt = EvalService.BuildTextQuestionPrompt(ctx.Questions[0], "sess-1", "run-1", "TRACE_CONTENT");
        await Assert.That(prompt).Contains("TRACE_CONTENT");
        await Assert.That(prompt).DoesNotContain("{CACHE_BOUNDARY}");
    }

    sealed class SilentObserver : IEvalObserver {
        public void OnInfo(string m) { }
        public void OnStarted(string r, string j, int t) { }
        public void OnContextFetched(int a, int b, int c, int d, long e) { }
        public void OnQuestionStarted(int i, int t, string c, string q) { }
        public void OnQuestionCompleted(int i, int t, EvalQuestionVerdict v, long it, long ot) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV3 a) { }
        public void OnFailed(string r) { }
    }
}
