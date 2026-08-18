using System.Net;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class EvalCatalogClientTests {
    sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    // Phase 3 (Task 5) — IEvalObserver.OnFinished now takes V3.
    sealed class CapturingObserver : IEvalObserver {
        public string? FailReason;
        public void OnFailed(string reason) => FailReason = reason;
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
    }

    const string CatalogJson = """
        {"retrospective_prompt":"retro {TRACE_JSON}","retrospective_prompt_version":"120",
         "questions":[{"category":"safety","id":"k1","title":"t","question_text":"raw",
           "prompt":"w raw e","prompt_version":"100","needs_tools":false}]}
        """;

    [Test]
    public async Task Fetches_and_parses_catalog() {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, CatalogJson));
        var observer = new CapturingObserver();

        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);

        await Assert.That(catalog).IsNotNull();
        await Assert.That(catalog!.RetrospectivePrompt).IsEqualTo("retro {TRACE_JSON}");
        await Assert.That(catalog.RetrospectivePromptVersion).IsEqualTo("120");
        await Assert.That(catalog.Questions[0].PromptVersion).IsEqualTo("100");
        await Assert.That(observer.FailReason).IsNull();
    }

    [Test]
    public async Task Returns_null_and_reports_on_401() {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, ""));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("kcap login");
    }

    [Test]
    public async Task Returns_null_and_reports_on_5xx() {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, ""));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("500");
    }

    // SF#4 -- fail-fast validation BEFORE expensive judge work.
    [Test]
    public async Task Returns_null_on_empty_questions() {
        const string body = """{"retrospective_prompt":"r","retrospective_prompt_version":"1","questions":[]}""";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("empty");
    }

    // Fail-closed (not throw) on a JSON `"questions": null` — it overrides the [] initializer,
    // so `.Count` would NRE without the explicit null guard.
    [Test]
    public async Task Returns_null_on_null_questions_array() {
        const string body = """{"retrospective_prompt":"r","retrospective_prompt_version":"1","questions":null}""";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("questions");
    }

    // Fail-closed (not throw) on a `"questions": [null]` element — the field accesses would NRE
    // without the per-item null guard in the validation loop.
    [Test]
    public async Task Returns_null_on_null_question_entry() {
        const string body = """{"retrospective_prompt":"r","retrospective_prompt_version":"1","questions":[null]}""";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("null question");
    }

    [Test]
    public async Task Returns_null_on_missing_retrospective_prompt() {
        const string body = "{\"retrospective_prompt\":\"\",\"retrospective_prompt_version\":\"1\"," +
            "\"questions\":[{\"category\":\"safety\",\"id\":\"k1\",\"title\":\"t\",\"question_text\":\"raw\",\"prompt\":\"p\",\"prompt_version\":\"1\",\"needs_tools\":false}]}";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("retrospective");
    }

    [Test]
    public async Task Returns_null_on_question_missing_prompt_version() {
        const string body = "{\"retrospective_prompt\":\"r\",\"retrospective_prompt_version\":\"1\"," +
            "\"questions\":[{\"category\":\"safety\",\"id\":\"k1\",\"title\":\"t\",\"question_text\":\"raw\",\"prompt\":\"p\",\"prompt_version\":\"\",\"needs_tools\":false}]}";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("prompt_version");
    }

    [Test]
    public async Task Returns_null_on_duplicate_question_ids() {
        const string body = """
            {"retrospective_prompt":"r","retrospective_prompt_version":"1",
             "questions":[
               {"category":"safety","id":"k1","title":"t","question_text":"a","prompt":"p","prompt_version":"1","needs_tools":false},
               {"category":"safety","id":"k1","title":"t","question_text":"b","prompt":"p","prompt_version":"1","needs_tools":false}]}
            """;
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("duplicate");
    }

    // N3 -- fail-fast on blank category
    [Test]
    public async Task Returns_null_on_blank_category() {
        const string body = "{\"retrospective_prompt\":\"r\",\"retrospective_prompt_version\":\"1\"," +
            "\"questions\":[{\"category\":\"\",\"id\":\"k1\",\"title\":\"t\",\"question_text\":\"raw\",\"prompt\":\"p\",\"prompt_version\":\"1\",\"needs_tools\":false}]}";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("category");
    }

    // N3 -- fail-fast on blank title
    [Test]
    public async Task Returns_null_on_blank_title() {
        const string body = "{\"retrospective_prompt\":\"r\",\"retrospective_prompt_version\":\"1\"," +
            "\"questions\":[{\"category\":\"safety\",\"id\":\"k1\",\"title\":\"\",\"question_text\":\"raw\",\"prompt\":\"p\",\"prompt_version\":\"1\",\"needs_tools\":false}]}";
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var observer = new CapturingObserver();
        var catalog = await EvalCatalogClient.FetchAsync("http://server", http, observer, CancellationToken.None);
        await Assert.That(catalog).IsNull();
        await Assert.That(observer.FailReason).Contains("title");
    }
}
