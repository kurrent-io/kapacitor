using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The saved-preference reviewer-vendor fallback: the pure retry trigger and its wiring into the
/// start arm of the flows tool dispatch. The trigger is safety-critical — a flow start is
/// NON-IDEMPOTENT (each accepted POST mints a run and launches a paid reviewer), so the only
/// acceptable retry is one the server has provably refused before doing anything: the structured
/// reviewer_vendor_required code on a vendor-less, model-less catalog start.
/// </summary>
public class ReviewerVendorFallbackTests {
    // The wire shape TryParseCodedError accepts: a JSON object with a non-empty string "error"
    // plus a string "message" — the CLI-side reading of the server's FlowReviewerResultError.
    const string VendorRequired =
        """{"error":"reviewer_vendor_required","message":"no reviewer vendor was requested and the definition names none"}""";

    // === The pure trigger ===

    [Test]
    public async Task Fires_for_a_vendorless_modelless_catalog_start_with_the_structured_code() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", wasDynamicStart: false, wasModelStart: false,
            requestedVendor: null, isSuccess: false, VendorRequired)).IsTrue();

    [Test]
    public async Task Fires_for_the_generic_catalog_start_alias() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_flow", wasDynamicStart: false, wasModelStart: false,
            requestedVendor: null, isSuccess: false, VendorRequired)).IsTrue();

    [Test]
    public async Task Not_on_success() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, false, null, isSuccess: true, VendorRequired)).IsFalse();

    [Test]
    public async Task Not_when_a_vendor_was_requested() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, false, requestedVendor: "codex", isSuccess: false, VendorRequired)).IsFalse();

    /// <summary>The v3 (model-bearing) route rejects a blank vendor with the SAME code, so a model
    /// start must be excluded by shape rather than by code: retrying there would let a saved
    /// preference shadow a model the caller pinned against a different vendor.</summary>
    [Test]
    public async Task Not_on_a_model_bearing_v3_start() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, wasModelStart: true, null, false, VendorRequired)).IsFalse();

    [Test]
    public async Task Not_on_a_dynamic_start() =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_flow", wasDynamicStart: true, false, null, false, VendorRequired)).IsFalse();

    [Test]
    [Arguments("submit_review_round")]
    [Arguments("send_to_participant")]
    [Arguments("get_review_flow_status")]
    [Arguments("close_flow")]
    public async Task Not_for_non_start_tools(string toolName) =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            toolName, false, false, null, false, VendorRequired)).IsFalse();

    /// <summary>Every other coded rejection passes straight through — including the two the
    /// reviewer-vendor feature itself introduces on OTHER surfaces (an unavailable vendor is a
    /// verdict, not a request for one; reviewer_vendor_unresolvable is a SEND-surface code for a
    /// legacy in-flight run and has no business on the start path).</summary>
    [Test]
    [Arguments("server_catching_up")]
    [Arguments("flow_settlement_busy")]
    [Arguments("reviewer_vendor_unavailable")]
    [Arguments("reviewer_vendor_unresolvable")]
    [Arguments("unknown_vendor")]
    [Arguments("vendor_containment_unreadable")]
    [Arguments("budget_unverifiable")]
    public async Task Not_on_other_codes(string code) =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, false, null, false,
            $$"""{"error":"{{code}}","message":"something else"}""")).IsFalse();

    /// <summary>The uncoded text-prefixed 400s an older server still returns. They can carry the
    /// words but not the structure, and structure is the whole gate.</summary>
    [Test]
    [Arguments("""{"detail":"no_daemon_available: no connected daemon has this repo"}""")]
    [Arguments("""{"detail":"daemon_outdated: upgrade the kcap daemon"}""")]
    [Arguments("reviewer_vendor_required")]
    [Arguments("Error: reviewer_vendor_required — pick a vendor")]
    [Arguments("<html>502 Bad Gateway</html>")]
    [Arguments("")]
    public async Task Not_on_uncoded_bodies(string body) =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, false, null, false, body)).IsFalse();

    /// <summary>A coded envelope missing "message" is not coded by this CLI's definition — the same
    /// parse the settlement gate uses, so the two can never disagree about what a code is.</summary>
    [Test]
    [Arguments("""{"error":"reviewer_vendor_required"}""")]
    [Arguments("""{"error":"","message":"blank code"}""")]
    [Arguments("""{"error":123,"message":"wrong type"}""")]
    [Arguments("""["reviewer_vendor_required"]""")]
    public async Task Not_on_malformed_coded_envelopes(string body) =>
        await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
            "start_review_flow", false, false, null, false, body)).IsFalse();

    /// <summary>Documents the ambiguity boundary by construction: a timeout, a cancelled POST or a
    /// dropped connection never produces a status and a body at all — the settlement lane returns
    /// DeadlineExhausted (handled before this point) or the exception escapes to the tool-error
    /// catch, so this function is never even called. The nearest thing it CAN see — a transport-ish
    /// non-JSON body — is refused above. There is no input for which "the server may or may not
    /// have started a run" evaluates true.</summary>
    [Test]
    public async Task Code_must_match_exactly_not_by_prefix_or_case() {
        foreach (var body in new[] {
                     """{"error":"reviewer_vendor_required_v2","message":"m"}""",
                     """{"error":"Reviewer_Vendor_Required","message":"m"}""",
                     """{"error":"reviewer_vendor_require","message":"m"}""",
                     """{"error":" reviewer_vendor_required","message":"m"}""",
                 })
            await Assert.That(McpFlowsServer.ShouldPreferenceRetry(
                "start_review_flow", false, false, null, false, body)).IsFalse();
    }

    // === Wired into the start arm via HandleToolCallAsync (full dispatch, WireMock-backed) ===

    const string StartV2 = "/api/flows/review/start/v2";

    const string VendorRequiredBody =
        """{"error":"reviewer_vendor_required","message":"no reviewer vendor was requested and the definition names none"}""";

    // A round-less start: rendered straight from the POST body, so these tests exercise only the
    // start arm and never enter the poll lane.
    static string StartedWithVendor(string appliedVendor) =>
        $$"""{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"{{appliedVendor}}"}""";

    /// <summary>A catalog start's arguments. <paramref name="generic"/> names the kind in the
    /// generic alias's own argument (definition_id) — which is also what proves the retry re-selects
    /// the right argument name rather than hard-coding start_review_flow's.</summary>
    static JsonObject StartArguments(string? vendor = null, string? definitionYaml = null, bool generic = false) {
        var args = new JsonObject {
            ["target_kind"]  = "pr",
            ["target_ref"]   = "123",
            ["target_title"] = "some PR",
            ["context"]      = "some context"
        };
        if (definitionYaml is not null) args["definition_yaml"] = definitionYaml;
        else args[generic ? "definition_id" : "kind"] = "code-review";
        if (vendor is not null) args["vendor"] = vendor;

        return args;
    }

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments.DeepClone() }
    };

    static Func<Task<string?>> Preference(string? value) => () => Task.FromResult(value);

    static (bool IsError, string Text) Unwrap(string response) {
        var result = JsonNode.Parse(response)!.AsObject()["result"]!;

        return (result["isError"]?.GetValue<bool>() ?? false,
                result["content"]![0]!["text"]!.GetValue<string>());
    }

    static int PostCount(WireMockServer server, string path) =>
        server.LogEntries.Count(e => e.RequestMessage.Path == path);

    static string? PostedVendor(WireMockServer server, int index) =>
        JsonNode.Parse(server.LogEntries.ElementAt(index).RequestMessage.Body!)!.AsObject()["vendor"]?.GetValue<string>();

    [Test]
    public async Task No_saved_preference_surfaces_the_coded_error_plus_ask_and_save_guidance() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference(null));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        // The server's own verdict is never swallowed by the local guidance.
        await Assert.That(text).Contains("reviewer_vendor_required");
        await Assert.That(text).Contains("no reviewer vendor was requested and the definition names none");
        await Assert.That(text).Contains("Ask the user which reviewer vendor to use");
        await Assert.That(text).Contains("kcap config set flows.reviewer_vendor");
        await Assert.That(text).Contains("copilot");

        // Nothing was retried and nothing was invented.
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(1);
    }

    [Test]
    public async Task Saved_preference_is_sent_as_an_explicit_vendor_on_exactly_one_retry() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("preference").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("preference").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(StartedWithVendor("codex")));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        // The driver is told the reviewer came from a saved preference, not from this conversation.
        await Assert.That(text).StartsWith("reviewer vendor 'codex' applied from your saved preference (flows.reviewer_vendor)");
        await Assert.That(text).Contains("flow_run_id: f1");

        await Assert.That(PostCount(server, StartV2)).IsEqualTo(2);
        // The first POST stayed vendor-less (the definition's authored vendor still gets its turn);
        // only the retry names one, which is also what makes the echo check assert against it.
        await Assert.That(PostedVendor(server, 0)).IsNull();
        await Assert.That(PostedVendor(server, 1)).IsEqualTo("codex");
    }

    [Test]
    public async Task Preference_is_normalized_before_it_reaches_the_wire() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("normalize").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("normalize").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(StartedWithVendor("codex")));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments(generic: true)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("  Codex  "));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        // Canonical on the wire AND in the echo comparison — an un-normalized preference would be
        // reported by the server as a vendor mismatch and close the run it just started.
        await Assert.That(PostedVendor(server, 1)).IsEqualTo("codex");
        await Assert.That(text).StartsWith("reviewer vendor 'codex' applied");
    }

    [Test]
    public async Task A_stale_preference_is_terminal_and_says_to_re_ask_and_re_save() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("stale").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("stale").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(
                  """{"error":"reviewer_vendor_unavailable","message":"codex is not certified unattended on any eligible daemon"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("reviewer_vendor_unavailable");
        await Assert.That(text).Contains("Your saved preference 'codex' (flows.reviewer_vendor) no longer works");
        await Assert.That(text).Contains("kcap config set flows.reviewer_vendor");

        await Assert.That(PostCount(server, StartV2)).IsEqualTo(2);
    }

    [Test]
    public async Task An_unknown_vendor_on_the_retry_is_also_read_as_a_stale_preference() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("unknown").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("unknown").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(
                  """{"error":"unknown_vendor","message":"'kodex' is not a known vendor"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("kodex"));

        var (_, text) = Unwrap(response);
        await Assert.That(text).Contains("unknown_vendor");
        await Assert.That(text).Contains("Your saved preference 'kodex' (flows.reviewer_vendor) no longer works");
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(2);
    }

    /// <summary>A retry failure the preference did not cause must surface as itself — blaming the
    /// saved vendor for a busy daemon would send the user to change a setting that was never wrong.</summary>
    [Test]
    public async Task An_unrelated_retry_failure_carries_no_stale_preference_advice() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("unrelated").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("unrelated").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"server_catching_up","message":"the flows read model is replaying"}"""));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("server_catching_up");
        await Assert.That(text).DoesNotContain("no longer works");
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(2);
    }

    /// <summary>The single-retry guarantee, stated as its worst case: a server that keeps answering
    /// reviewer_vendor_required must not produce a third POST. Each start is a paid launch attempt;
    /// a self-feeding loop here is the failure mode this whole gate exists to prevent.</summary>
    [Test]
    public async Task The_retry_never_retries_itself() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("reviewer_vendor_required");
        // Not a stale-preference code, so no re-save advice — and, above all, exactly two POSTs.
        await Assert.That(text).DoesNotContain("no longer works");
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(2);
    }

    /// <summary>The retry is an EXPLICIT vendor request, so it inherits the explicit-vendor echo
    /// check: a server that applies something else has its run closed defensively rather than left
    /// running an unattended reviewer nobody chose.</summary>
    [Test]
    public async Task A_mismatched_echo_on_the_retry_closes_the_run() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("echo").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("echo").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(StartedWithVendor("claude")));
        server.Given(Request.Create().WithPath("/api/flows/f1/close").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("requested reviewer vendor 'codex'");
        await Assert.That(text).Contains("applied 'claude'");
        await Assert.That(PostCount(server, "/api/flows/f1/close")).IsEqualTo(1);
    }

    [Test]
    public async Task An_explicit_vendor_never_triggers_the_preference_retry() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments(vendor: "claude")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("reviewer_vendor_required");
        await Assert.That(text).DoesNotContain("saved preference");
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(1);
    }

    [Test]
    public async Task A_dynamic_start_never_triggers_the_preference_retry() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("start_flow", StartArguments(definitionYaml: "participants: {}")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).DoesNotContain("saved preference");
        await Assert.That(PostCount(server, "/api/flows/review/start")).IsEqualTo(1);
    }

    // === The shared settlement budget ===

    /// <summary>The retry runs on the FIRST send's elapsed budget, never a fresh one. Two
    /// independent 3-minute windows plus the poll cap would outlast the harness MCP tool timeout the
    /// deadline was sized against — and the way that ends is the worst one available: the retry POST
    /// succeeds, a paid reviewer launches, the harness has already given up, and the driver starts
    /// the flow a second time. Pinned as an EXACT total on the virtual clock, so a second window
    /// shows up as any elapsed time past the single-call deadline.</summary>
    [Test]
    public async Task The_retry_shares_the_first_sends_settlement_budget() {
        // Scripted transport rather than WireMock: the first POST is HELD server-side for 90s (the
        // admission wait this budget exists to absorb) before refusing, and every later POST is
        // busy. A WireMock scenario cannot express "busy from here on" — its state machine wraps and
        // re-serves the refusal, which silently ends the retry instead of exhausting it.
        var clock   = new VirtualFlowRetryClock();
        var held    = TimeSpan.FromSeconds(90);
        var handler = new ScriptedStartHandler(clock, held);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://flows.test") };

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, "http://flows.test", cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(3),
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow_settlement_busy");
        await Assert.That(text).Contains("retryable");

        // The whole tool call — first send AND retry — fits inside ONE deadline. On a fresh budget
        // this would be the 90s the first send spent PLUS a full second window.
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);

        // It really did fall back, and the retry really was the vendor-bearing one.
        await Assert.That(handler.Requests).IsGreaterThan(2);
        await Assert.That(handler.Bodies[0]).DoesNotContain("\"vendor\"");
        await Assert.That(handler.Bodies[1]).Contains("\"vendor\":\"codex\"");
    }

    /// <summary>First POST: held for <paramref name="held"/> of virtual time, then the vendor
    /// refusal that arms the fallback. Every later POST: a settlement busy, so the retry runs to
    /// exhaustion rather than being let off the hook.</summary>
    sealed class ScriptedStartHandler(VirtualFlowRetryClock clock, TimeSpan held) : HttpMessageHandler {
        public int Requests { get; private set; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests++;
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            if (Requests > 1)
                return new(HttpStatusCode.Conflict) {
                    Content = new StringContent(
                        """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}""")
                };

            clock.Advance(held);

            return new(HttpStatusCode.BadRequest) { Content = new StringContent(VendorRequiredBody) };
        }
    }

    /// <summary>The budget's other end, pinned at the wrapper: with the window already spent, the
    /// send delegate is never invoked. A start is non-idempotent, so launching a paid reviewer when
    /// no time remains to deliver its result is the one outcome worse than reporting the failure.</summary>
    [Test]
    public async Task A_spent_shared_budget_sends_nothing_at_all() {
        var clock = new VirtualFlowRetryClock();
        var sends = 0;
        using var client = new HttpClient();

        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test",
            (_, _) => {
                sends++;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            },
            clock, SettlementBackoff.Seeded(3),
            budgetStartedAt: clock.UtcNow - McpFlowsServer.SettlementElapsedDeadline);

        await Assert.That(result as McpFlowsServer.SettlementSendResult.DeadlineExhausted).IsNotNull();
        await Assert.That(sends).IsEqualTo(0);
        await Assert.That(clock.Elapsed).IsEqualTo(TimeSpan.Zero);
    }

    /// <summary>An expired token on the retry is an auth failure, not a vendor verdict — the caller
    /// gets the same actionable login line the first POST would have produced, not a raw 401 that
    /// reads like the flow was rejected.</summary>
    [Test]
    public async Task An_expired_token_on_the_retry_says_to_log_in() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("auth").WillSetStateTo("retried")
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(VendorRequiredBody));
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .InScenario("auth").WhenStateIs("retried")
              .RespondWith(Response.Create().WithStatusCode(401));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: Preference("codex"));

        var (isError, text) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Not logged in");
        await Assert.That(text).DoesNotContain("no longer works");
    }

    /// <summary>The preference is read only when the trigger fires — a successful start must not
    /// touch the config at all (and, on the production path, must not read a file it never needs).</summary>
    [Test]
    public async Task A_successful_start_never_reads_the_preference() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(StartV2).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        using var client = new HttpClient();

        var reads = 0;

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            reviewerVendorPreference: () => { reads++; return Task.FromResult<string?>("codex"); });

        var (isError, _) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(reads).IsEqualTo(0);
        await Assert.That(PostCount(server, StartV2)).IsEqualTo(1);
    }
}
