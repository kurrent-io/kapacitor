using kapacitor;
using kapacitor.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class EvalQuestionCatalogClientTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

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
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(2);
        await Assert.That(result[0].Id).IsEqualTo("sensitive_files");
        await Assert.That(result[1].Prompt).IsEqualTo("question?");
    }

    [Test]
    public async Task FetchAsync_returns_null_on_non_success() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var http = new HttpClient();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, CancellationToken.None);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FetchAsync_returns_null_on_invalid_json() {
        _server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("not json at all"));

        using var http = new HttpClient();
        var result = await EvalQuestionCatalogClient.FetchAsync(_server.Url!, http, CancellationToken.None);
        await Assert.That(result).IsNull();
    }
}
