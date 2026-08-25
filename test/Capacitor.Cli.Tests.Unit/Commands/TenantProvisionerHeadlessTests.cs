using System.Net;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A non-interactive session reaches the create-a-workspace fork, where every way out is a Spectre
/// prompt and Spectre throws rather than returning. Either the two answers arrive as flags, or there
/// is nothing to ask and the run has to say so.
/// </summary>
/// <remarks>
/// Bare rather than keyed: these write SetupFunnel events into the process-global telemetry sink,
/// which the facade-parity suites read back as an exact ordered set, and they capture Console.
/// </remarks>
[NotInParallel]
public class TenantProvisionerHeadlessTests {
    const string BaseUrl = "https://signup.example";

    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

    /// <summary>Interactivity is injected, not read: the ambient value belongs to whatever host the
    /// suite is running under, so reading it would pass in CI and fail in a developer's terminal.</summary>
    [Test]
    public async Task Declines_instead_of_throwing_when_there_is_no_terminal_to_prompt_on() {
        var provisioner = new SpectreTenantProvisioner(
            new TenantProvisioningClient(new HttpClient()), BaseUrl,
            isInteractive: () => false);

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Declined);
    }

    /// <summary>Names every route that actually works, and no other: an org admin cannot fix this, so
    /// "ask your admin" would be a dead end dressed as advice.</summary>
    [Test]
    public async Task The_message_offers_the_flags_signup_and_an_existing_workspace() {
        var message = OAuthLoginFlow.WorkspaceCreationNeedsATerminalMessage();

        await Assert.That(message).Contains("--org");
        await Assert.That(message).Contains("--slug");
        await Assert.That(message).Contains("/signup");
        await Assert.That(message).Contains("--server-url");
        await Assert.That(message).DoesNotContain("admin");
    }

    // --- The answers supplied up front (--org / --slug) ---

    [Test]
    public async Task Provisions_with_no_terminal_when_the_answers_are_supplied() {
        using var handler = new StubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(offer.Tenant!.Slug).IsEqualTo("acme");
        await Assert.That(offer.Tenant.OrganizationId).IsEqualTo("org_123");
    }

    // Reaching Created at all is the assertion: every prompt sits after this branch, and a Spectre
    // prompt in a test host throws rather than returning a default.
    [Test]
    public async Task The_supplied_answers_replace_the_prompts_on_a_terminal_too() {
        using var handler = new StubHandler();
        var provisioner   = Provisioner(handler, () => true, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
    }

    [Test]
    public async Task A_taken_slug_ends_the_run_naming_it() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler { AvailabilityReason = "taken" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("acme");
        await Assert.That(handler.Paths).DoesNotContain("/api/signup/provision");
    }

    [Test]
    public async Task A_slug_that_cannot_be_checked_ends_the_run_rather_than_guessing() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler { AvailabilityStatus = HttpStatusCode.InternalServerError };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("acme");
        await Assert.That(handler.Paths).DoesNotContain("/api/signup/provision");
    }

    [Test]
    public async Task An_unusable_slug_ends_the_run_before_any_network_call() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "Acme Inc!"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("not a valid slug");
        await Assert.That(handler.Paths).IsEmpty();
    }

    // A reserved word fails the same check by a different arm, and the two say different things.
    [Test]
    public async Task A_reserved_slug_says_so_rather_than_calling_it_malformed() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "api"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains("is reserved");
        await Assert.That(handler.Paths).IsEmpty();
    }

    [Test]
    [Arguments("reserved", "being provisioned by someone else")]
    [Arguments("blocked",  "is reserved")]
    [Arguments(null,       "is unavailable")]
    public async Task Every_unavailable_reason_reaches_the_log_with_its_own_wording(string? reason, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler { AvailabilityAvailable = false, AvailabilityReason = reason };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
    }

    // The refusal has to land on the same stream as the earlier ones, or a script reads two logs.
    [Test]
    [Arguments(HttpStatusCode.Conflict, "taken")]
    [Arguments(HttpStatusCode.BadRequest, "Invalid organization name or slug")]
    public async Task A_refusal_from_the_provision_call_ends_the_run_on_stderr(HttpStatusCode status, string expected) {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler { ProvisionStatus = status };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(capture.GetCapturedError()).Contains(expected);
    }

    [Test]
    public async Task The_org_and_hostname_are_stated_before_the_workspace_exists() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler();
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        await provisioner.OfferCreateAsync(Tokens());

        var written = capture.GetCapturedError();

        await Assert.That(written).Contains("Acme");
        await Assert.That(written).Contains("https://acme.kcap.ai");
    }

    // The wait renders through a Spectre live display, the one part of the poll that wants a
    // terminal, so the path is walked rather than assumed.
    [Test]
    public async Task Waits_out_a_pending_workspace_with_no_terminal_to_render_on() {
        using var handler = new StubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "active" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(handler.Paths).Contains("/api/signup/status");
    }

    // A workspace that failed to build does not exist, and `kcap setup <slug>` reads a positional as
    // an existing server — so the way back has to be the command that creates, not the one that points.
    [Test]
    public async Task A_failed_build_points_back_at_the_command_that_creates() {
        using var capture = ConsoleOutput.StartErrorCapture();
        using var handler = new StubHandler { ProvisionStatus = HttpStatusCode.Accepted, StatusState = "failed" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        var written = capture.GetCapturedError();

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(written).Contains("--org \"Acme\" --slug acme");
        await Assert.That(written).DoesNotContain("kcap setup acme");
    }

    [Test]
    public async Task A_slug_already_reserved_to_this_account_still_provisions() {
        using var handler = new StubHandler { AvailabilityAvailable = false, AvailabilityReason = "yours" };
        var provisioner   = Provisioner(handler, () => false, new RequestedWorkspace("Acme", "acme"));

        var offer = await provisioner.OfferCreateAsync(Tokens());

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
    }

    static SpectreTenantProvisioner Provisioner(StubHandler handler, Func<bool> isInteractive, RequestedWorkspace requested) =>
        new(new TenantProvisioningClient(new HttpClient(handler, disposeHandler: false)), BaseUrl, isInteractive, requested);

    sealed class StubHandler : HttpMessageHandler {
        public List<string>    Paths                 { get; } = [];
        public bool            AvailabilityAvailable { get; init; } = true;
        public string?         AvailabilityReason    { get; init; }
        public HttpStatusCode  AvailabilityStatus    { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode  ProvisionStatus       { get; init; } = HttpStatusCode.OK;
        public string?         StatusState           { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            if (path == "/api/signup/availability") {
                var available = AvailabilityAvailable && AvailabilityReason is null;

                return Task.FromResult(Json(AvailabilityStatus,
                    $$"""{"available":{{(available ? "true" : "false")}},"reason":{{Quote(AvailabilityReason)}}}"""));
            }

            if (path == "/api/signup/status")
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"state":{{Quote(StatusState)}},"workosOrgId":"org_123","url":"https://acme.kcap.ai"}"""));

            // 202 means "accepted, come back" and carries no org id, which is what sends the run into the poll.
            return Task.FromResult(ProvisionStatus == HttpStatusCode.Accepted
                ? Json(ProvisionStatus, """{"slug":"acme","state":"provisioning"}""")
                : Json(ProvisionStatus, """{"workosOrgId":"org_123","url":"https://acme.kcap.ai"}"""));
        }

        static string Quote(string? value) => value is null ? "null" : $"\"{value}\"";

        static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
