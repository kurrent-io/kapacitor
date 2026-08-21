using System.Net;
using System.Text;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Scripts kcap-web's /api/signup/* surface so the REAL TenantProvisioningClient (a concrete class
/// with no interface) is exercised end to end — a stubbed client would prove nothing about the
/// status/body handling the provisioner branches on.
sealed class ScriptedSignupHandler : HttpMessageHandler {
    public readonly List<string> Requests = [];
    public Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> Respond =
        (_, _) => (HttpStatusCode.OK, "{}");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var index = Requests.Count;
        Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
        var (status, body) = Respond(request, index);

        return Task.FromResult(new HttpResponseMessage(status) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
    }
}

sealed class RecordingAuthProgress : IAuthProgress {
    public readonly List<string> Notices = [];
    public readonly List<string> Errors = [];

    public void Notice(string message) => Notices.Add(message);
    public void Error(string message) => Errors.Add(message);
    public void BrowserOpening(string url) { }
    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) { }
    public void PollTick() { }
}

public class WizardAuthBridgesTests {
    const string BaseUrl = "https://signup.example";

    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

    static (WizardTenantProvisioner Provisioner, ScriptedSignupHandler Handler, RecordingAuthProgress Progress, FakeTimeProvider Time)
            NewProvisioner() {
        var handler  = new ScriptedSignupHandler();
        var progress = new RecordingAuthProgress();
        var time     = new FakeTimeProvider();
        var client   = new TenantProvisioningClient(new HttpClient(handler));

        return (new WizardTenantProvisioner(client, BaseUrl, progress, time), handler, progress, time);
    }

    /// The poll's only suspension is Task.Delay(interval, time, ct), whose continuation resumes
    /// inside Advance() — so the whole flow settles without a real wait (DaemonMutationLaneTests idiom).
    static async Task<ProvisionOffer> Drive(Task<ProvisionOffer> task, FakeTimeProvider time) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 400) time.Advance(TimeSpan.FromSeconds(4));

        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    static DiscoveredTenant Tenant(string login) => new() { OrgLogin = login, Origin = $"https://{login}.kcap.ai" };

    // ── tenant picker ────────────────────────────────────────────────────────

    [Test]
    public async Task PickAsync_publishes_the_tenants_and_completes_on_the_selection() {
        var picker = new WizardTenantPicker();
        DiscoveredTenant[]? offered = null;
        picker.SelectionRequested += tenants => offered = tenants;

        var pick = picker.PickAsync([Tenant("acme"), Tenant("globex")], CancellationToken.None);
        picker.Select(Tenant("globex"));

        await Assert.That((await pick.WaitAsync(TimeSpan.FromSeconds(5)))!.OrgLogin).IsEqualTo("globex");
        await Assert.That(offered!.Select(t => t.OrgLogin)).IsEquivalentTo(["acme", "globex"]);
    }

    [Test]
    public async Task Selecting_nothing_completes_the_await_with_no_tenant() {
        var picker = new WizardTenantPicker();

        var pick = picker.PickAsync([Tenant("acme")], CancellationToken.None);
        picker.Select(null);

        await Assert.That(await pick.WaitAsync(TimeSpan.FromSeconds(5))).IsNull();
    }

    [Test]
    public async Task Cancelling_releases_a_pending_pick() {
        var picker = new WizardTenantPicker();
        using var cts = new CancellationTokenSource();

        var pick = picker.PickAsync([Tenant("acme")], cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pick.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void The_synchronous_pick_is_not_supported() {
        var picker = new WizardTenantPicker();

        Assert.Throws<NotSupportedException>(() => picker.Pick([Tenant("acme")]));
    }

    // ── progress bridge ──────────────────────────────────────────────────────

    [Test]
    public async Task Every_progress_event_is_marshalled_through_the_post_delegate() {
        var posts = 0;
        var progress = new UiAuthProgress(action => {
            posts++;
            action();
        });

        var seen = new List<string>();
        progress.NoticeReceived     += m => seen.Add($"notice:{m}");
        progress.ErrorReceived      += m => seen.Add($"error:{m}");
        progress.BrowserOpened      += u => seen.Add($"browser:{u}");
        progress.DeviceCodeReceived += (code, uri) => seen.Add($"device:{code}@{uri}");
        progress.PollTicked         += () => seen.Add("tick");

        progress.Notice("hello");
        progress.Error("bad");
        progress.BrowserOpening("https://login.example");
        progress.DeviceCode("ABCD-1234", "https://github.com/login/device", "GitHub", prefilled: false);
        progress.PollTick();

        await Assert.That(posts).IsEqualTo(5);
        await Assert.That(seen).IsEquivalentTo([
            "notice:hello", "error:bad", "browser:https://login.example",
            "device:ABCD-1234@https://github.com/login/device", "tick"
        ]);
    }

    [Test]
    public async Task A_second_pick_cancels_the_one_it_displaces() {
        var picker = new WizardTenantPicker();

        var first  = picker.PickAsync([Tenant("acme")], CancellationToken.None);
        var second = picker.PickAsync([Tenant("globex")], CancellationToken.None);
        picker.Select(Tenant("globex"));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.That((await second.WaitAsync(TimeSpan.FromSeconds(5)))!.OrgLogin).IsEqualTo("globex");
    }

    // ── intent → façade call mapping ─────────────────────────────────────────

    [Test]
    [Arguments("acme", "https://acme.kcap.ai")]
    [Arguments("https://acme.kcap.ai/sessions/9", "https://acme.kcap.ai")]
    [Arguments(" http://localhost:5108/ ", "http://localhost:5108")]
    // Core's own scheme rule for scheme-less input: loopback is http, everything else https.
    [Arguments("localhost:5108", "http://localhost:5108")]
    [Arguments("127.0.0.1:5108", "http://127.0.0.1:5108")]
    [Arguments("[::1]:5108", "http://[::1]:5108")]
    [Arguments("acme.kcap.ai", "https://acme.kcap.ai")]
    [Arguments("capacitor.example.com", "https://capacitor.example.com")]
    public async Task A_pasted_server_resolves_through_origin_then_slug_expansion(string typed, string expected) =>
        await Assert.That(WizardSignInOperation.ResolveServer(typed)).IsEqualTo(expected);

    // ── provisioner: the create sub-flow ─────────────────────────────────────

    [Test]
    public async Task The_create_flow_re_prompts_on_an_unavailable_slug_then_provisions_and_polls_to_created() {
        var (provisioner, handler, _, time) = NewProvisioner();

        handler.Respond = (request, _) => request.RequestUri!.AbsolutePath switch {
            "/api/signup/availability" => request.RequestUri.Query.Contains("slug=acme&") || request.RequestUri.Query.EndsWith("slug=acme", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"available":false,"reason":"taken"}""")
                : (HttpStatusCode.OK, """{"available":true}"""),
            "/api/signup/provision" => (HttpStatusCode.Accepted, """{"slug":"acme-two","state":"provisioning"}"""),
            "/api/signup/status" => handler.Requests.Count(r => r.Contains("/api/signup/status")) > 1
                ? (HttpStatusCode.OK, """{"state":"active","workosOrgId":"org_1","url":"https://acme-two.kcap.ai"}""")
                : (HttpStatusCode.OK, """{"state":"provisioning"}"""),
            _ => (HttpStatusCode.NotFound, "{}")
        };

        var slugPrompts = new List<(string Suggestion, string? Error)>();
        var confirms    = new List<(string Slug, string Origin)>();
        var polls       = new List<(int Attempt, int Max)>();

        provisioner.OfferMode     = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult<string?>("Acme");
        provisioner.PromptSlug = (suggestion, error, _) => {
            slugPrompts.Add((suggestion, error));

            return Task.FromResult<string?>(slugPrompts.Count == 1 ? "Acme" : "acme-two");
        };
        provisioner.ConfirmCreate = (slug, origin, _) => {
            confirms.Add((slug, origin));

            return Task.FromResult(true);
        };
        provisioner.PollProgress = (attempt, max) => polls.Add((attempt, max));

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(offer.Tenant).IsEqualTo(new ProvisionedTenant("org_1", "acme-two", "Acme", "https://acme-two.kcap.ai"));
        await Assert.That(slugPrompts[0]).IsEqualTo(("acme", (string?)null)); // SlugValidator.Derive suggestion
        await Assert.That(slugPrompts[1].Error).Contains("taken");
        await Assert.That(confirms).IsEquivalentTo([("acme-two", "https://acme-two.kcap.ai")]);
        await Assert.That(polls).IsEquivalentTo([(1, WizardTenantProvisioner.MaxPolls)]);
    }

    [Test]
    public async Task An_invalid_slug_is_rejected_locally_without_an_availability_call() {
        var (provisioner, handler, _, time) = NewProvisioner();

        handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/api/signup/availability"
            ? (HttpStatusCode.OK, """{"available":true}""")
            : (HttpStatusCode.OK, """{"slug":"acme","state":"active","workosOrgId":"org_1"}""");

        var errors = new List<string?>();
        provisioner.OfferMode     = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult<string?>("Acme");
        provisioner.PromptSlug = (_, error, _) => {
            errors.Add(error);

            return Task.FromResult<string?>(errors.Count == 1 ? "kcap" : "acme"); // "kcap" is reserved
        };
        provisioner.ConfirmCreate = (_, _, _) => Task.FromResult(true);

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Created);
        await Assert.That(errors[1]).Contains("reserved");
        await Assert.That(handler.Requests.Count(r => r.Contains("slug=kcap"))).IsEqualTo(0);
    }

    [Test]
    public async Task A_poll_that_never_goes_live_ends_in_progress_with_the_join_from_connect_copy() {
        var (provisioner, handler, progress, time) = NewProvisioner();

        handler.Respond = (request, _) => request.RequestUri!.AbsolutePath switch {
            "/api/signup/availability" => (HttpStatusCode.OK, """{"available":true}"""),
            "/api/signup/provision"    => (HttpStatusCode.Accepted, """{"slug":"acme","state":"provisioning"}"""),
            _                          => (HttpStatusCode.OK, """{"state":"provisioning"}""")
        };

        var polls = new List<int>();
        provisioner.OfferMode     = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult<string?>("Acme");
        provisioner.PromptSlug    = (suggestion, _, _) => Task.FromResult<string?>(suggestion);
        provisioner.ConfirmCreate = (_, _, _) => Task.FromResult(true);
        provisioner.PollProgress  = (attempt, _) => polls.Add(attempt);

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.InProgress);
        await Assert.That(offer.PendingSlug)
            .IsEqualTo("acme")
            .Because("the caller names the workspace when telling the user to come back to it");
        await Assert.That(polls.Count).IsEqualTo(WizardTenantProvisioner.MaxPolls);
        // A Notice, not an Error: the workspace is being created, nothing has gone wrong.
        await Assert.That(progress.Notices)
            .Contains("Still provisioning — finish later by joining 'acme' from the Connect step.");
    }

    [Test]
    public async Task A_conflicting_slug_fails_the_offer_with_the_servers_reason() {
        var (provisioner, handler, progress, time) = NewProvisioner();

        handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/api/signup/availability"
            ? (HttpStatusCode.OK, """{"available":true}""")
            : (HttpStatusCode.Conflict, """{"reason":"owned_by_other"}""");

        provisioner.OfferMode     = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult<string?>("Acme");
        provisioner.PromptSlug    = (suggestion, _, _) => Task.FromResult<string?>(suggestion);
        provisioner.ConfirmCreate = (_, _, _) => Task.FromResult(true);

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Failed);
        await Assert.That(string.Join("\n", progress.Errors)).Contains("owned by someone else");
    }

    [Test]
    public async Task Choosing_an_existing_workspace_hands_the_input_back_unresolved() {
        var (provisioner, handler, _, time) = NewProvisioner();
        provisioner.OfferMode = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Existing("  acme  "));

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.ExistingWorkspace);
        await Assert.That(offer.ExistingWorkspaceInput).IsEqualTo("acme");
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("no-org-name")]
    [Arguments("no-slug")]
    [Arguments("declined")]
    public async Task Backing_out_of_any_create_prompt_declines_without_provisioning(string exit) {
        var (provisioner, handler, progress, time) = NewProvisioner();

        handler.Respond = (_, _) => (HttpStatusCode.OK, """{"available":true}""");

        provisioner.OfferMode = _ => Task.FromResult<ProvisionMode>(
            exit == "cancel" ? new ProvisionMode.Cancel() : new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult(exit == "no-org-name" ? null : "Acme");
        provisioner.PromptSlug    = (suggestion, _, _) => Task.FromResult(exit == "no-slug" ? null : suggestion);
        provisioner.ConfirmCreate = (_, _, _) => Task.FromResult(exit != "declined");

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Status).IsEqualTo(ProvisionOfferStatus.Declined);
        await Assert.That(handler.Requests.Any(r => r.Contains("/api/signup/provision"))).IsFalse();
        // The provisioner owns every non-Created message — a silent decline renders as a bare failure.
        await Assert.That(progress.Notices).Contains("No workspace created.");
    }

    [Test]
    public async Task A_provision_that_returns_the_org_immediately_needs_no_poll() {
        var (provisioner, handler, _, time) = NewProvisioner();

        handler.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/api/signup/availability"
            ? (HttpStatusCode.OK, """{"available":true}""")
            : (HttpStatusCode.OK, """{"slug":"acme","state":"active","workosOrgId":"org_9","url":"https://acme.kcap.ai"}""");

        provisioner.OfferMode     = _ => Task.FromResult<ProvisionMode>(new ProvisionMode.Create());
        provisioner.PromptOrgName = _ => Task.FromResult<string?>("Acme");
        provisioner.PromptSlug    = (suggestion, _, _) => Task.FromResult<string?>(suggestion);
        provisioner.ConfirmCreate = (_, _, _) => Task.FromResult(true);

        var offer = await Drive(provisioner.OfferCreateAsync(Tokens()), time);

        await Assert.That(offer.Tenant).IsEqualTo(new ProvisionedTenant("org_9", "acme", "Acme", "https://acme.kcap.ai"));
        await Assert.That(handler.Requests.Any(r => r.Contains("/api/signup/status"))).IsFalse();
    }
}
