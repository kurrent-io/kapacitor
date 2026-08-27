using System.Text.Json.Nodes;
using Capacitor.Cli.Core.FirstRun;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The wire shape the tenant is written against. Nothing else checks it — the flow's own tests run
// over a fake channel, which is the client this replaces — and every way of getting it wrong is
// silent: a mistyped path reads as "this tenant does not offer browser setup", and a mistyped field
// as a malformed request the CLI reports as the server's fault.
public class FirstRunFlowClientTests {
    const string FlowId = "b7f3a1c2d4e5f607a1b2c3";

    const string CreatePath = "/api/first-run/flows";

    static string PollPath => $"{CreatePath}/{FlowId}";

    static FirstRunMachineReport Report(
            string? machine = null, Dictionary<string, FirstRunHarnessReport>? harnesses = null,
            string[]? declined = null, bool? loginShellFindsCli = null, string? platform = null) =>
        new(machine, machine is null ? null : "machine-1", harnesses ?? [], declined ?? [], loginShellFindsCli,
            platform);

    static string StateBody(string doneStatus) =>
        $$$"""
          {"flow_id":"{{{FlowId}}}","machine":"nostromo","step":"Done","can_finish":true,
           "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Skipped","Done":"{{{doneStatus}}}"}}
          """;

    [Test]
    public async Task CreateAsync_sends_snake_case_fields_and_parses_the_response() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending"))
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report("nostromo"), CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body!.FlowId).IsEqualTo(FlowId);
        await Assert.That(outcome.Body.Machine).IsEqualTo("nostromo");
        await Assert.That(outcome.Body.CanFinish).IsTrue();
        await Assert.That(outcome.Body.Steps!["Import"]).IsEqualTo("Skipped");

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath(CreatePath).UsingPost())[0].RequestMessage.Body!)!;

        // Case-insensitive matching does NOT bridge an underscore, so a camelCase field here would
        // arrive as a null flow_id and be refused as malformed on every single create.
        await Assert.That(body["flow_id"]!.GetValue<string>()).IsEqualTo(FlowId);
        await Assert.That(body["machine"]!.GetValue<string>()).IsEqualTo("nostromo");
    }

    [Test]
    public async Task CreateAsync_sends_the_machine_report_the_agents_screen_renders_from() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        await new FirstRunFlowClient(http).CreateAsync(
            server.Urls[0], FlowId,
            Report(
                "nostromo",
                new Dictionary<string, FirstRunHarnessReport> {
                    ["claude"] = new() { BinaryOnPath = true,  ConfigFound = false, AlreadyWired = true },
                    ["cursor"] = new() { BinaryOnPath = false, ConfigFound = true,  AlreadyWired = false }
                },
                ["kiro"],
                loginShellFindsCli: false),
            CancellationToken.None);

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath(CreatePath).UsingPost())[0].RequestMessage.Body!)!;

        await Assert.That(body["machine_id"]!.GetValue<string>()).IsEqualTo("machine-1");

        // The two signals travel APART. ORing them into one field is what the daemon's inventory does,
        // and it costs the screen the ability to say which one it saw — which differs per vendor.
        await Assert.That(body["harnesses"]!["claude"]!["binary_on_path"]!.GetValue<bool>()).IsTrue();
        await Assert.That(body["harnesses"]!["claude"]!["config_found"]!.GetValue<bool>()).IsFalse();
        await Assert.That(body["harnesses"]!["claude"]!["already_wired"]!.GetValue<bool>()).IsTrue();
        await Assert.That(body["harnesses"]!["cursor"]!["binary_on_path"]!.GetValue<bool>()).IsFalse();
        await Assert.That(body["harnesses"]!["cursor"]!["config_found"]!.GetValue<bool>()).IsTrue();

        await Assert.That(body["declined"]!.AsArray()[0]!.GetValue<string>()).IsEqualTo("kiro");
        await Assert.That(body["login_shell_finds_cli"]!.GetValue<bool>()).IsFalse();
    }

    // An explicit macos is what the browser draws its fix button from; every other value, and no value,
    // draw none.
    [Test]
    public async Task CreateAsync_reports_the_platform_so_the_screen_knows_what_it_can_offer() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        await new FirstRunFlowClient(http).CreateAsync(
            server.Urls[0], FlowId, Report("nostromo", platform: FirstRunPlatforms.MacOs), CancellationToken.None);

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath(CreatePath).UsingPost())[0].RequestMessage.Body!)!;

        await Assert.That(body["platform"]!.GetValue<string>()).IsEqualTo("macos");
    }

    [Test]
    public async Task ReportMachineActionAsync_posts_the_outcome_to_the_flow_s_actions_route() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"{PollPath}/actions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).ReportMachineActionAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunMachineActionRequest {
                Capability  = FirstRunMachineCapabilities.PathShim,
                RequestedAt = new DateTimeOffset(2026, 8, 21, 12, 5, 0, TimeSpan.Zero),
                Outcome     = FirstRunMachineActionOutcomes.InstalledNotOnPath
            },
            CancellationToken.None);

        await Assert.That(outcome.Recorded).IsTrue();

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath($"{PollPath}/actions").UsingPost())[0]
                  .RequestMessage.Body!)!;

        await Assert.That(body["capability"]!.GetValue<string>()).IsEqualTo("path_shim");
        await Assert.That(body["outcome"]!.GetValue<string>()).IsEqualTo("installed_not_on_path");
        await Assert.That(body["requested_at"]!.GetValue<string>()).StartsWith("2026-08-21T12:05:00");

        // No free text on this lane: the terminal has the shell error and the sudo line, and the
        // browser keys its copy off the outcome token.
        await Assert.That(body.AsObject().ContainsKey("detail")).IsFalse();
    }

    // A report the server did not accept leaves the request outstanding, which is what makes the
    // loop's retry self-terminating.
    [Test]
    public async Task ReportMachineActionAsync_reports_a_refusal_as_not_recorded() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"{PollPath}/actions").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).ReportMachineActionAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunMachineActionRequest {
                Capability  = FirstRunMachineCapabilities.PathShim,
                RequestedAt = DateTimeOffset.UnixEpoch,
                Outcome     = FirstRunMachineActionOutcomes.Failed
            },
            CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(500);
        await Assert.That(outcome.Recorded).IsFalse();
    }

    // The server draws its one error state from an explicit false, so a probe that never ran must not
    // arrive as one. Sent as JSON null rather than omitted; the server reads both as "not probed".
    [Test]
    public async Task CreateAsync_does_not_report_an_unprobed_login_shell_as_a_failed_one() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report("nostromo"), CancellationToken.None);

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath(CreatePath).UsingPost())[0].RequestMessage.Body!)!;

        // Present-and-null, not omitted. The server reads both as "not probed", so the assertion is
        // about the two things that must NOT happen: a true, or a false the probe never determined.
        await Assert.That(body.AsObject().ContainsKey("login_shell_finds_cli")).IsTrue();
        await Assert.That(body["login_shell_finds_cli"]).IsNull();
    }

    [Test]
    public async Task PollAsync_reads_the_agents_decision_and_its_cursor() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$$"""
                    {"flow_id":"{{{FlowId}}}","step":"Done","can_finish":true,
                     "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Skipped","Done":"Completed"},
                     "agents":[{"vendor":"claude","record":true,"tools":true},
                               {"vendor":"cursor","record":true,"tools":false}],
                     "agents_decided_at":"2026-08-25T09:30:00+00:00"}
                    """));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);
        var answer  = FirstRunFlowOutcomes.Agents(outcome.Body);

        await Assert.That(answer).IsNotNull();
        await Assert.That(answer!.Choices.Count).IsEqualTo(2);
        await Assert.That(answer.Records("cursor")).IsTrue();
        await Assert.That(answer.Tools("cursor")).IsFalse();
        await Assert.That(answer.DecidedAt).IsEqualTo(new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task CreateAsync_reads_the_retry_after_a_429_carries() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "600"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report(), CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(429);
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    // The availability oracle for the whole leg, so it has to survive the client rather than be
    // flattened into a transport failure.
    [Test]
    public async Task CreateAsync_surfaces_a_404_rather_than_degrading() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report(), CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task CreateAsync_tolerates_a_server_url_with_a_trailing_slash() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync($"{server.Urls[0]}/", FlowId, Report(), CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task PollAsync_reads_the_flow_state() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Completed")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(FirstRunFlowOutcomes.IsFinished(outcome.Body!)).IsTrue();
    }

    [Test]
    [Arguments(404)]
    [Arguments(410)]
    public async Task PollAsync_surfaces_a_refusal_with_no_body(int status) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"error":"flow_expired"}"""));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(status);
        await Assert.That(outcome.Body).IsNull();
    }

    // A server that answered must not be reported as one that could not be reached: status 0 sends
    // the flow down the "could not reach the server" branch, about a server that just replied.
    [Test]
    public async Task PollAsync_reports_an_unreadable_200_as_200() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("not json").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body).IsNull();
    }

    [Test]
    public async Task CreateAsync_reads_a_retry_after_sent_as_an_http_date() {
        // Not what this tenant sends, but a proxy in front of it may rewrite the header, and reading
        // only the delta form would report that as no Retry-After at all. Kestrel stamps the response
        // Date header itself, so the date is pinned to now rather than scripted.
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(10);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Retry-After", retryAt.ToString("r")));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report(), CancellationToken.None);

        await Assert.That(outcome.RetryAfter).IsNotNull();
        await Assert.That(outcome.RetryAfter!.Value).IsGreaterThanOrEqualTo(TimeSpan.FromMinutes(9.5));
        await Assert.That(outcome.RetryAfter!.Value).IsLessThanOrEqualTo(TimeSpan.FromMinutes(10.5));
    }

    [Test]
    public async Task CreateAsync_floors_a_retry_after_date_already_in_the_past_at_zero() {
        var retryAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Retry-After", retryAt.ToString("r")));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, Report(), CancellationToken.None);

        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.Zero);
    }

    // A caller's cancel reported as status 0 reads as a transport failure, and the poll loop would
    // keep going to its 30-minute budget rather than stopping on Ctrl-C or a host shutdown.
    [Test]
    public async Task PollAsync_does_not_swallow_the_callers_cancellation() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(30)));

        using var http = new HttpClient();
        using var cts  = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, cts.Token))
                    .Throws<OperationCanceledException>();
    }

    // The same exception type, from HttpClient's own timeout with the token unsignalled. That one IS
    // a blip, and the loop's next tick is the right answer to it.
    [Test]
    public async Task PollAsync_still_degrades_its_own_timeout_to_status_0() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task PollAsync_carries_a_429s_retry_after() {
        // The loop backs off on the server's own number, not a fixed step — same header, both routes.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Retry-After", "60"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(429);
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task Degrades_to_status_0_when_the_server_is_unreachable() {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };

        // Reserved as unroutable by RFC 5737, so this fails to connect rather than reaching anything.
        var outcome = await new FirstRunFlowClient(http)
            .PollAsync("http://192.0.2.1:9", FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task ReportImportAsync_posts_the_report_to_the_flow_s_import_route() {
        // Every key here is one the server reads by name; a rename on either side shows up as a
        // picker with no figures against it rather than as a failure.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"{PollPath}/import").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).ReportImportAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunImportRequest {
                Repos = [
                    new FirstRunImportRepoReport {
                        Owner         = "kurrent-io",
                        Name          = "kcap-server",
                        Sessions      = new Dictionary<string, int> {
                            [FirstRunImportWindows.Last30]     = 12,
                            [FirstRunImportWindows.Everything] = 41
                        },
                        LastSessionAt = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero)
                    }
                ],
                Unmatched = new Dictionary<string, int> { [FirstRunImportWindows.Everything] = 35 },
                RepoTotal = 312,
                Vendors   = ["claude", "codex"]
            },
            CancellationToken.None);

        await Assert.That(outcome.Recorded).IsTrue();

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath($"{PollPath}/import").UsingPost())[0]
                  .RequestMessage.Body!)!;

        var repo = body["repos"]![0]!;

        await Assert.That(repo["owner"]!.GetValue<string>()).IsEqualTo("kurrent-io");
        await Assert.That(repo["name"]!.GetValue<string>()).IsEqualTo("kcap-server");
        await Assert.That(repo["sessions"]!["30"]!.GetValue<int>()).IsEqualTo(12);
        await Assert.That(repo["sessions"]!["all"]!.GetValue<int>()).IsEqualTo(41);
        await Assert.That(repo["last_session_at"]!.GetValue<string>()).StartsWith("2026-08-24T09:00:00");

        await Assert.That(body["unmatched"]!["all"]!.GetValue<int>()).IsEqualTo(35);
        await Assert.That(body["repo_total"]!.GetValue<int>()).IsEqualTo(312);
        await Assert.That(body["vendors"]!.AsArray().Select(v => v!.GetValue<string>()))
                    .IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task ReportImportAsync_sends_an_empty_repo_list_rather_than_omitting_it() {
        // A machine with no history is a real report, and the screen waits for it either way.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"{PollPath}/import").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        await new FirstRunFlowClient(http).ReportImportAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunImportRequest {
                Repos     = [],
                Unmatched = new Dictionary<string, int>(),
                RepoTotal = 0,
                Vendors   = []
            },
            CancellationToken.None);

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath($"{PollPath}/import").UsingPost())[0]
                  .RequestMessage.Body!)!;

        await Assert.That(body["repos"]!.AsArray()).IsEmpty();
        await Assert.That(body["vendors"]!.AsArray()).IsEmpty().Because("empty is not null on this field");
    }

    [Test]
    public async Task PollAsync_reads_the_import_decision_the_server_returns() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($$$"""
                  {"flow_id":"{{{FlowId}}}","step":"Done","can_finish":true,
                   "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Completed","Done":"Pending"},
                   "import_decided_at":"2026-08-24T10:00:00Z",
                   "import":{"window":"90","titles":"Server","vendors":["claude"],
                             "repos":[{"owner":"kurrent-io","name":"kcap-server","level":"Shared"}]}}
                  """)
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        var answer = FirstRunFlowOutcomes.Import(outcome.Body)!;

        await Assert.That(answer.Window).IsEqualTo(FirstRunImportWindows.Last90);
        await Assert.That(answer.Titles).IsEqualTo(FirstRunImportTitles.Server);
        await Assert.That(answer.Vendors).IsEquivalentTo(["claude"]);
        await Assert.That(answer.Choices.Single().Level).IsEqualTo(FirstRunImportLevel.Shared);
    }

    [Test]
    public async Task PollAsync_reads_the_default_visibility_the_decision_carries() {
        // Through the source-generated context, not a hand-built response: the field lands in profile
        // config, so a naming or AOT-binding slip would silently leave the profile untouched forever
        // while every unit test above still passed.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($$$"""
                  {"flow_id":"{{{FlowId}}}","step":"Import","can_finish":true,
                   "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Active","Done":"Pending"},
                   "agents":[{"vendor":"claude","record":true,"tools":true}],
                   "agents_decided_at":"2026-08-26T10:00:00Z",
                   "default_visibility":"private"}
                  """)
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.Body!.DefaultVisibility).IsEqualTo("private");
        await Assert.That(FirstRunFlowOutcomes.Agents(outcome.Body)!.DefaultVisibility).IsEqualTo("private");
    }

    [Test]
    public async Task PollAsync_leaves_the_visibility_null_when_the_decision_carries_none() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody($$$"""
                  {"flow_id":"{{{FlowId}}}","step":"Import","can_finish":true,
                   "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Active","Done":"Pending"},
                   "agents":[{"vendor":"claude","record":true,"tools":true}],
                   "agents_decided_at":"2026-08-26T10:00:00Z"}
                  """)
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.Body!.DefaultVisibility).IsNull();
        await Assert.That(FirstRunFlowOutcomes.Agents(outcome.Body)!.DefaultVisibility).IsNull();
    }

    // ---- The import-outcome route. Every other test in this file builds the request object directly,
    // so nothing else proves the source-generated names actually reach the wire — and a slip there
    // leaves the screen waiting for ever with the whole suite green.

    static string OutcomePath => $"{PollPath}/import-outcome";

    [Test]
    public async Task ReportImportOutcomeAsync_sends_the_counts_under_the_names_the_route_reads() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(OutcomePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Completed")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).ReportImportOutcomeAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunImportOutcomeRequest {
                DecidedAt = new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero),
                Imported  = 7,
                Skipped   = 2,
                Failed    = 1
            },
            CancellationToken.None);

        await Assert.That(outcome.Recorded).IsTrue();

        var sent = System.Text.Json.JsonDocument.Parse(server.LogEntries.Single()
            .RequestMessage.Body!).RootElement;

        await Assert.That(sent.GetProperty("imported").GetInt32()).IsEqualTo(7);
        await Assert.That(sent.GetProperty("skipped").GetInt32()).IsEqualTo(2);
        await Assert.That(sent.GetProperty("failed").GetInt32()).IsEqualTo(1);
        await Assert.That(sent.GetProperty("decided_at").GetDateTimeOffset())
                    .IsEqualTo(new DateTimeOffset(2026, 8, 27, 9, 30, 0, TimeSpan.Zero));
        await Assert.That(sent.TryGetProperty("reason", out var reason)).IsTrue();
        await Assert.That(reason.IsNull).IsTrue();
    }

    [Test]
    public async Task ReportImportOutcomeAsync_sends_a_refusal_token_verbatim() {
        // The server validates against its own closed set and rejects the whole report on a token it
        // does not know, so a second spelling here is a silent wire break rather than a dropped field.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(OutcomePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Completed")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        await new FirstRunFlowClient(http).ReportImportOutcomeAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunImportOutcomeRequest {
                DecidedAt = DateTimeOffset.UnixEpoch,
                Imported  = 0,
                Skipped   = 0,
                Failed    = 0,
                Reason    = FirstRunImportOutcomeReasons.NoReadableAgents
            },
            CancellationToken.None);

        var sent = System.Text.Json.JsonDocument.Parse(server.LogEntries.Single()
            .RequestMessage.Body!).RootElement;

        await Assert.That(sent.GetProperty("reason").GetString()).IsEqualTo("no_readable_agents");
    }

    [Test]
    [Arguments(400)]
    [Arguments(410)]
    [Arguments(500)]
    public async Task ReportImportOutcomeAsync_reports_a_refusal_as_not_recorded(int status) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(OutcomePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).ReportImportOutcomeAsync(
            server.Urls[0], FlowId,
            new ReportFirstRunImportOutcomeRequest {
                DecidedAt = DateTimeOffset.UnixEpoch, Imported = 0, Skipped = 0, Failed = 0
            },
            CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(status);
        await Assert.That(outcome.Recorded).IsFalse();
    }
}
