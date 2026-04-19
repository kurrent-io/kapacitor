using kapacitor;
using kapacitor.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class EvalQuestionCatalogClientTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    /// <summary>Test-only recording observer that captures all OnFailed calls.</summary>
    sealed class RecordingObserver : IEvalObserver {
        public List<string> FailureMessages { get; } = [];

        public void OnInfo(string message) { }
        public void OnStarted(string evalRunId, string sessionId, string judgeModel, int totalQuestions) { }
        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) { }
        public void OnQuestionStarted(int index, int total, string category, string questionId) { }
        public void OnQuestionCompleted(int index, int total, EvalQuestionVerdict verdict, long inputTokens, long outputTokens) { }
        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) { }
        public void OnFactRetained(string category, string fact) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospective retrospective) { }
        public void OnRetrospectiveFailed(string reason) { }
        public void OnFinished(SessionEvalCompletedPayload aggregate) { }
        public void OnFailed(string reason) => FailureMessages.Add(reason);
    }

    [Test]
    public async Task FetchAsync_deserializes_server_response() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                [
                  {"category":"safety","id":"sensitive_files","text":"label","prompt":"question?"},
                  {"category":"quality","id":"tests_written","text":"label","prompt":"question?"}
                ]
                """));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(2);
        await Assert.That(result[0].Id).IsEqualTo("sensitive_files");
        await Assert.That(result[1].Prompt).IsEqualTo("question?");
        await Assert.That(observer.FailureMessages).IsEmpty();
    }

    [Test]
    public async Task FetchAsync_emits_http_error_on_non_success() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.FailureMessages).Count().IsEqualTo(1);
        await Assert.That(observer.FailureMessages[0]).IsEqualTo("failed to load eval question catalog: HTTP 500");
    }

    [Test]
    public async Task FetchAsync_emits_auth_error_on_401() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.FailureMessages).Count().IsEqualTo(1);
        await Assert.That(observer.FailureMessages[0]).IsEqualTo("authentication failed — run 'kapacitor login' to re-authenticate");
    }

    [Test]
    public async Task FetchAsync_emits_json_error_on_invalid_json() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("not json at all"));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.FailureMessages).Count().IsEqualTo(1);
        await Assert.That(observer.FailureMessages[0]).Contains("eval question catalog response was not valid JSON");
    }

    [Test]
    public async Task FetchAsync_emits_malformed_entry_error_on_missing_category() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                [
                  {"category":"","id":"test_id","text":"label","prompt":"question?"}
                ]
                """));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.FailureMessages).Count().IsEqualTo(1);
        await Assert.That(observer.FailureMessages[0]).IsEqualTo("eval question catalog contains a malformed entry (missing category, id, or prompt)");
    }

    [Test]
    public async Task FetchAsync_emits_empty_error_on_empty_array() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("[]"));

        using var http = new HttpClient();
        var observer = new RecordingObserver();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, observer, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.FailureMessages).Count().IsEqualTo(1);
        await Assert.That(observer.FailureMessages[0]).IsEqualTo("eval question catalog is empty");
    }
}
