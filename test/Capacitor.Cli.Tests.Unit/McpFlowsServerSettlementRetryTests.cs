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
    // Every wait in both retry lanes runs on the injected clock, so these tests are instant and
    // the requested schedule is directly assertable (VirtualFlowRetryClock.Delays).
    static VirtualFlowRetryClock Clock() => new();

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

    // === SettlementBackoff: the delay schedule shared by the POST and poll lanes ===
    //
    // Pinned formula (settlement-admission design §3.2 G): for retry n (1-based),
    // raw(n) = min(10s, 500ms · 2^(n−1)) with the cap applied BEFORE jitter, then equal jitter
    // delay(n) = raw(n)/2 + U(0, raw(n)/2), then truncation to the caller's remaining budget.

    [Test]
    [Arguments(1, 500)]
    [Arguments(2, 1_000)]
    [Arguments(3, 2_000)]
    [Arguments(4, 4_000)]
    [Arguments(5, 8_000)]
    [Arguments(6, 10_000)]   // capped
    [Arguments(7, 10_000)]
    [Arguments(40, 10_000)]  // a far-out ordinal must not overflow past the cap
    public async Task Backoff_raw_is_exponential_and_capped_at_ten_seconds(int retry, int expectedMs) {
        await Assert.That(SettlementBackoff.Raw(retry)).IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Test]
    public async Task Backoff_applies_the_cap_before_jitter() {
        // Cap-before-jitter: a saturated ordinal jitters around the 10s CAP (5–10s), never around
        // the uncapped exponential (which at retry 8 would be 64s → 32–64s if capped afterwards).
        var low  = new SettlementBackoff(() => 0.0);
        var high = new SettlementBackoff(() => 0.999);

        await Assert.That(low.Delay(8, TimeSpan.FromHours(1))).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(high.Delay(8, TimeSpan.FromHours(1))).IsLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(high.Delay(8, TimeSpan.FromHours(1))).IsGreaterThan(TimeSpan.FromSeconds(9));
    }

    [Test]
    public async Task Backoff_equal_jitter_puts_the_first_retry_in_250_to_500ms_and_steady_state_in_5_to_10s() {
        var backoff = SettlementBackoff.Seeded(4242);
        var budget  = TimeSpan.FromHours(1);   // never the binding constraint here

        for (var i = 0; i < 50; i++) {
            var first = backoff.Delay(1, budget);
            await Assert.That(first).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            await Assert.That(first).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));

            var steady = backoff.Delay(9, budget);
            await Assert.That(steady).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
            await Assert.That(steady).IsLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task Backoff_truncates_to_the_remaining_budget() {
        var backoff = new SettlementBackoff(() => 0.999);   // ~the top of the jitter band

        // Budget shorter than the jittered delay -> exactly the budget, never past it.
        await Assert.That(backoff.Delay(6, TimeSpan.FromSeconds(2))).IsEqualTo(TimeSpan.FromSeconds(2));
        // Budget longer -> untruncated.
        await Assert.That(backoff.Delay(1, TimeSpan.FromHours(1))).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
        // Exhausted / negative budget -> zero, never a negative delay.
        await Assert.That(backoff.Delay(3, TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
        await Assert.That(backoff.Delay(3, TimeSpan.FromSeconds(-5))).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Backoff_is_deterministic_for_a_seeded_rng() {
        // Two independently seeded instances produce the identical sequence — which is what lets the
        // lane tests below assert the exact schedule the code under test will request.
        var a = SettlementBackoff.Seeded(99);
        var b = SettlementBackoff.Seeded(99);
        var budget = TimeSpan.FromHours(1);

        var fromA = Enumerable.Range(1, 8).Select(n => a.Delay(n, budget)).ToArray();
        var fromB = Enumerable.Range(1, 8).Select(n => b.Delay(n, budget)).ToArray();

        await Assert.That(fromA).IsEquivalentTo(fromB);
        // ...and it is a real schedule, not a constant.
        await Assert.That(fromA.Distinct().Count()).IsGreaterThan(1);
    }

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

        var clock = Clock();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), clock);
        var delays = clock.Delays;

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

        var clock = Clock();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), clock);
        var delays = clock.Delays;

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

        var clock = Clock();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), clock);
        var delays = clock.Delays;

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

        var clock = Clock();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), clock);
        var delays = clock.Delays;

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

        var clock = Clock();
        using var response = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, c => c.PostAsync($"{server.Url}/start", null), clock);
        var delays = clock.Delays;

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

    /// <summary>The poll lane shares the POST lane's backoff SCHEDULE but keeps its own budget: it
    /// retries a settlement-busy GET on the exact same jittered ladder, bounded by the 8-minute
    /// PollCap rather than by an attempt count, and never overshoots that cap.</summary>
    [Test]
    public async Task Poll_lane_settlement_retries_follow_the_shared_schedule_and_stop_at_poll_cap() {
        const string flowRunId = "flow-poll-schedule";
        const int    seed      = 7;
        var          pollCap   = TimeSpan.FromMinutes(8);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));
        // Never settles — the lane must keep retrying until its own cap, not until an attempt count.
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"still settling"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(seed));

        // The independently-seeded oracle: the same ladder, each rung truncated to what is left of
        // the cap. This is the schedule the lane must have requested, rung for rung.
        var expected  = new List<TimeSpan>();
        var oracle    = SettlementBackoff.Seeded(seed);
        var remaining = pollCap;
        for (var n = 1; remaining > TimeSpan.Zero; n++) {
            var next = oracle.Delay(n, remaining);
            if (next <= TimeSpan.Zero) break;
            expected.Add(next);
            remaining -= next;
        }

        await Assert.That(clock.Delays).IsEquivalentTo(expected);
        await Assert.That(clock.Delays.Count).IsGreaterThan(3);            // genuinely past the old 3-attempt bound
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(pollCap);     // never overshoots PollCap

        // It stopped by exhausting the cap, not by turning the retryable busy into a hard error.
        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("Flow still running");
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
