using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the auto-retry for the two settlement-layer coded 409s (flow_settlement_busy /
/// reviewer_launch_incarnation_superseded): the low-level SendWithSettlementRetryAsync gate, its
/// wiring into the start path (HandleToolCallAsync), and the poll path (PollUntilTerminalAsync,
/// reached indirectly through HandleToolCallAsync).
/// </summary>
public class McpFlowsServerSettlementRetryTests {
    static Func<TimeSpan, Task> NoDelay(List<TimeSpan> recorded) => ts => { recorded.Add(ts); return Task.CompletedTask; };

    static JsonObject StartArguments() => new() {
        ["kind"]         = "code-review",
        ["target_kind"]  = "pr",
        ["target_ref"]   = "123",
        ["target_title"] = "some PR",
        ["context"]      = "some context"
    };

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    // === TryParseCodedError: pure decode, shared by FormatFlowStartError and the retry gate ===

    [Test]
    public async Task TryParseCodedError_decodes_code_and_message() {
        var ok = McpFlowsServer.TryParseCodedError(
            """{"error":"flow_settlement_busy","message":"try again"}""", out var code, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(code).IsEqualTo("flow_settlement_busy");
        await Assert.That(message).IsEqualTo("try again");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"message":"no code here"}""")]
    [Arguments("""{"error":""}""")]
    [Arguments("""{"error":123,"message":"wrong type"}""")]
    public async Task TryParseCodedError_returns_false_for_uncoded_or_malformed_bodies(string body) {
        var ok = McpFlowsServer.TryParseCodedError(body, out var code, out var message);

        await Assert.That(ok).IsFalse();
        await Assert.That(code).IsNull();
        await Assert.That(message).IsNull();
    }

    // === SendWithSettlementRetryAsync: the low-level gate, driven directly (fast, injectable delay) ===

    [Test]
    public async Task Settlement_busy_then_success_retries_transparently() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("settlement-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("settlement-busy")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f-new","status":"running"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), NoDelay(delays));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(2);
        await Assert.That(delays).HasCount().EqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromMilliseconds(200));
    }

    [Test]
    public async Task Incarnation_superseded_then_success_retries_transparently() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("incarnation-superseded")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"reviewer_launch_incarnation_superseded","message":"superseded — retry."}"""));
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("incarnation-superseded")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f-new","status":"running"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), NoDelay(delays));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task Exhaustion_after_max_attempts_returns_final_failing_response() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"still racing"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), NoDelay(delays));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        // 3 total attempts (bounded), 2 waits in between.
        await Assert.That(server.LogEntries.Count()).IsEqualTo(3);
        await Assert.That(delays).HasCount().EqualTo(2);

        var body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("flow_settlement_busy");
    }

    [Test]
    public async Task Different_coded_4xx_is_not_retried() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), NoDelay(delays));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(1); // no retry at all
        await Assert.That(delays).IsEmpty();
    }

    [Test]
    public async Task Uncoded_4xx_is_not_retried() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody("plain text error, not JSON"));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), NoDelay(delays));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(1);
        await Assert.That(delays).IsEmpty();
    }

    // === Wired into the start path via HandleToolCallAsync (full dispatch) ===

    [Test]
    public async Task Start_review_flow_transparently_retries_settlement_busy_and_surfaces_no_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .InScenario("start-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .InScenario("start-busy")
              .WhenStateIs("second")
              // round_id/round_number null: a terminal-in-POST result, same shape the vendor-echo
              // tests use, so this test exercises ONLY the start-path retry — polling is covered
              // separately below.
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"f-fresh","status":"running","round_id":null,"round_number":null,"result_kind":null,"result_text":null}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("f-fresh");
        await Assert.That(text).DoesNotContain("flow_settlement_busy");

        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v2")).IsEqualTo(2);
    }

    [Test]
    public async Task Start_review_flow_exhausts_settlement_retries_and_surfaces_the_coded_message() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("flow_settlement_busy");

        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v2")).IsEqualTo(3);
    }

    [Test]
    public async Task Start_review_flow_does_not_retry_a_different_coded_4xx() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend for this run"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("budget_unverifiable");

        // Exactly one attempt — a different coded 4xx must not be retried.
        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v2")).IsEqualTo(1);
    }

    // === Wired into the poll path (PollUntilTerminalAsync), reached through HandleToolCallAsync ===

    [Test]
    public async Task Poll_path_transparently_retries_settlement_busy_and_returns_the_terminal_result() {
        const string flowRunId = "flow-poll-busy";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));

        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("poll-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("poll-busy")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  $$"""{"flow_run_id":"{{flowRunId}}","round_number":1,"status":"closed","round_status":"clean","round_result_text":"all clean"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("all clean");
        await Assert.That(text).DoesNotContain("flow_settlement_busy");

        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(2);
    }

    [Test]
    public async Task Poll_path_does_not_retry_a_different_coded_4xx() {
        const string flowRunId = "flow-poll-other";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));

        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend for this run"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("budget_unverifiable");

        // Exactly one GET — a different coded 4xx must fail immediately, no retry.
        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(1);
    }
}
