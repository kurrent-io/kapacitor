using System.Net;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI;

namespace Capacitor.App.Tests.Unit;

/// The spec §3/§10 transition table for the step that runs ONE façade operation. The service is
/// driven by a scripted operation (the façade itself is covered in Core), but the picker, the
/// provisioner and the progress sink are the REAL bridges — they are what this task builds.
public class SignInStepViewModelTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static bool CanExecute(ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> command) {
        Dispatcher.UIThread.RunJobs(); // canExecute is delivered through the dispatcher scheduler
        var value = false;
        using var subscription = command.CanExecute.Subscribe(v => value = v);

        return value;
    }

    static async Task WaitUntil(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + Bounded;

        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for {what}");

            await Task.Delay(5);
        }
    }

    static DiscoveredTenant Tenant(string login) => new() { OrgLogin = login, Origin = $"https://{login}.kcap.ai" };

    static WorkOSTokenSource Tokens() =>
        new("access-token", refreshToken: null, (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

    static AuthResult.Committed Committed(string provider = AuthProvider.GitHubApp, string? username = "sam") =>
        new("acme", "https://acme.kcap.ai:443", provider, username, [new AuthIdentity("acme", "https://acme.kcap.ai:443")]);

    sealed class FakeAppState : IAppStateStore {
        public AppState State = new();
        public int Updates;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            Updates++;
            State = mutate(State);

            return Task.FromResult(true);
        }
    }

    sealed class Harness : IDisposable {
        readonly TempConfigRoot                 _config = new();
        public readonly ConnectStepViewModel    Connect = new();
        public readonly WizardTenantPicker      Picker;
        public readonly ScriptedSignupHandler   Signup  = new();
        public readonly RecordingOpener         Opener  = new();
        public readonly FakeAppState            AppState = new();
        public readonly FakeTimeProvider        Time    = new();
        public readonly ConsentFlipClaims       Claims;
        public readonly UiAuthProgress          Progress;
        public readonly WizardTenantProvisioner Provisioner;
        public readonly WizardAuthService       Service;
        public readonly SignInStepViewModel     Vm;

        public int Runs;

        public Func<ConnectIntent, CancellationToken, Task<AuthResult>> Operation =
            (_, _) => Task.FromResult<AuthResult>(Committed());

        public Harness() {
            Claims = new ConsentFlipClaims(_config.Root);

            var bridges = new WizardBridges(
                action => action(),
                progress => new WizardTenantProvisioner(
                    new TenantProvisioningClient(new HttpClient(Signup)), "https://signup.example", progress, Time));

            Picker      = bridges.Picker;
            Progress    = bridges.Progress;
            Provisioner = bridges.Provisioner;
            Service = new WizardAuthService((intent, ct) => {
                Runs++;

                return Operation(intent, ct);
            });
            Vm = new SignInStepViewModel(Service, Connect, bridges, Claims, AppState, Opener);
        }

        public string ClaimsPath => _config.PathTo("consent-flip-claims.json");

        public Task<System.Reactive.Unit> SignIn() => Vm.SignInCommand.Execute().ToTask();

        public void Dispose() => _config.Dispose();
    }

    // ── committed outcomes ───────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(AuthProvider.GitHubApp)]
    [Arguments(AuthProvider.WorkOS)]
    public async Task A_committed_discovery_satisfies_the_step_and_names_the_signed_in_user(string provider) {
        var (satisfied, status, isError, detail, showPrimary) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Connect.DiscoveryProvider = provider;
            h.Operation = (_, _) => Task.FromResult<AuthResult>(Committed(provider));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail, h.Vm.ShowPrimaryAction);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
        await Assert.That(isError).IsFalse();
        await Assert.That(detail).IsEqualTo("You're signed in. Updating the app…");
        await Assert.That(showPrimary).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_none_provider_join_auto_satisfies_with_the_no_sign_in_copy() {
        var (satisfied, status, intent) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Paste;
            h.Connect.ServerInputText = "http://localhost:5108";
            ConnectIntent? seen = null;
            h.Operation = (i, _) => {
                seen = i;

                return Task.FromResult<AuthResult>(Committed(AuthProvider.None, username: null));
            };

            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, seen);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("No sign-in required for this server.");
        await Assert.That(intent).IsEqualTo(new ConnectIntent.Paste("http://localhost:5108"));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Re_entry_after_a_commit_still_shows_the_step_satisfied() {
        var (satisfied, status, runs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;

            await h.SignIn();
            await h.Vm.OnEnterAsync(CancellationToken.None); // Back then forward again

            return (h.Vm.Satisfied, h.Vm.Status, h.Runs);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
        await Assert.That(runs).IsEqualTo(1); // entering the step starts nothing
    }

    // ── cancellation and failure ─────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelling_is_not_a_failure_and_leaves_the_step_retryable() {
        var (satisfied, status, isError, detail) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Operation = (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());

            await h.SignIn();

            return (h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(isError).IsFalse();
        await Assert.That(detail).IsNull();
    }

    // The defect this ticket is about: sign-in succeeded and the workspace is being created, so the
    // generic failure headline below would tell the user something untrue.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_provisioning_timeout_headlines_the_pending_workspace_and_is_not_an_error() {
        var (status, isError, detail, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Operation = (_, _) => {
                h.Progress.Notice("Still provisioning — finish later by joining 'acme' from the Connect step.");

                return Task.FromResult<AuthResult>(new AuthResult.Failed(
                    "'acme' is still being created.", AuthFailureReason.ProvisioningInProgress));
            };

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail, h.Vm.Satisfied);
        });

        await Assert.That(status).IsEqualTo("'acme' is still being created.");
        await Assert.That(isError)
                    .IsFalse()
                    .Because("nothing failed — the poll outran its window while the workspace was "
                           + "still being created");
        await Assert.That(detail).IsEqualTo("Still provisioning — finish later by joining 'acme' from the Connect step.");
        // Still not signed in to anything, so the step cannot be satisfied.
        await Assert.That(satisfied).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failure_shows_one_generic_headline_and_never_re_logs_the_facade_message() {
        var (status, isError, detail, log, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Operation = (_, _) => {
                h.Progress.Error("Error: the auth service is unreachable.");

                return Task.FromResult<AuthResult>(new AuthResult.Failed("the auth service is unreachable"));
            };

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError, h.Vm.StatusDetail, h.Vm.Log.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(status).IsEqualTo("Sign-in failed.");
        await Assert.That(isError).IsTrue();
        await Assert.That(satisfied).IsFalse();
        await Assert.That(detail).IsEqualTo("The auth service is unreachable.");
        await Assert.That(log).IsEmpty();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unexpected_operation_throw_lands_as_a_failure_not_a_crash() {
        var (status, isError) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Operation = (_, _) => throw new InvalidOperationException("claim_arm_failed");

            await h.SignIn();

            return (h.Vm.Status, h.Vm.StatusIsError);
        });

        await Assert.That(status).IsEqualTo("Sign-in failed.");
        await Assert.That(isError).IsTrue();
    }

    // ── progress rendering ───────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_device_code_renders_without_the_clipboard_note_and_the_raw_line_stays_in_the_log() {
        var (code, uri, log) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => {
                h.Progress.DeviceCode("ABCD-1234  (copied to clipboard)", "https://github.com/login/device", "GitHub", prefilled: false);

                return gate.Task;
            };

            var exec = h.SignIn();
            // Read while the flow is live: a settled attempt clears the code it no longer polls.
            var rendered = (h.Vm.DeviceCode, h.Vm.VerificationUri, h.Vm.Log.ToList());

            gate.SetResult(Committed());
            await exec;

            return rendered;
        });

        await Assert.That(code).IsEqualTo("ABCD-1234");
        await Assert.That(uri).IsEqualTo("https://github.com/login/device"); // the clickable link is the device URI
        await Assert.That(string.Join("\n", log)).Contains("(copied to clipboard)");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_browser_fallback_url_renders_and_opens_through_the_url_opener() {
        var (fallback, opened) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => {
                h.Progress.BrowserOpening("https://auth.example/authorize?x=1");

                return gate.Task;
            };

            var exec = h.SignIn();
            var url  = h.Vm.BrowserUrl; // the fallback link only exists while the browser wait is live
            await h.Vm.OpenSignInUrlCommand.Execute().ToTask();

            gate.SetResult(Committed());
            await exec;

            return (url, h.Opener.Opened.ToList());
        });

        await Assert.That(fallback).IsEqualTo("https://auth.example/authorize?x=1");
        await Assert.That(opened).IsEquivalentTo(["https://auth.example/authorize?x=1"]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_progress_log_stays_bounded() {
        var (count, last) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();

            for (var i = 0; i < SignInStepViewModel.LogLimit + 50; i++) h.Progress.Notice($"line {i}");

            await Task.Yield();

            return (h.Vm.Log.Count, h.Vm.Log[^1]);
        });

        await Assert.That(count).IsEqualTo(SignInStepViewModel.LogLimit);
        await Assert.That(last).IsEqualTo($"line {SignInStepViewModel.LogLimit + 49}");
    }

    // ── tenant picker ────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Confirming_a_tenant_resolves_the_awaited_pick() {
        var (offered, satisfied, status, stillVisible) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = async (_, ct) => {
                var picked = await h.Picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, ct);

                return picked is null
                    ? new AuthResult.Failed("No tenant selected.")
                    : Committed(username: picked.OrgLogin);
            };

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.TenantPickerVisible, "the tenant list");
            var listed = h.Vm.Tenants.Select(t => t.OrgLogin).ToList();

            h.Vm.SelectedTenant = h.Vm.Tenants[1];
            await h.Vm.ConfirmTenantCommand.Execute().ToTask();
            await exec;

            return (listed, h.Vm.Satisfied, h.Vm.Status, h.Vm.TenantPickerVisible);
        });

        await Assert.That(offered).IsEquivalentTo(["acme", "globex"]);
        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as globex");
        await Assert.That(stillVisible).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Backing_out_of_the_tenant_list_resolves_the_pick_with_nothing() {
        var (satisfied, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Operation = async (_, ct) => {
                var picked = await h.Picker.PickAsync([Tenant("acme"), Tenant("globex")], TenantPickContext.None, ct);

                return picked is null ? new AuthResult.Failed("No tenant selected.") : Committed();
            };

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.TenantPickerVisible, "the tenant list");

            await h.Vm.CancelTenantCommand.Execute().ToTask();
            await exec;

            return (h.Vm.Satisfied, h.Vm.Status);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in failed."); // the façade's own "No tenant selected."
    }

    // ── create sub-flow ──────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_create_intent_skips_the_mode_menu_and_walks_the_provisioner_prompts() {
        var (modeShows, slugSuggestion, confirmText, satisfied, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Signup.Respond = (request, _) => request.RequestUri!.AbsolutePath == "/api/signup/availability"
                ? (HttpStatusCode.OK, """{"available":true}""")
                : (HttpStatusCode.OK, """{"slug":"acme","state":"active","workosOrgId":"org_1","url":"https://acme.kcap.ai"}""");

            h.Operation = async (_, ct) => {
                var offer = await h.Provisioner.OfferCreateAsync(Tokens(), ct);

                return offer.Status == ProvisionOfferStatus.Created
                    ? Committed(AuthProvider.WorkOS)
                    : new AuthResult.Failed($"Workspace creation did not complete ({offer.Status}).");
            };

            var shows = 0;
            h.Vm.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(SignInStepViewModel.ModeChoiceVisible) && h.Vm.ModeChoiceVisible) shows++;
            };

            var exec = h.SignIn();

            await WaitUntil(() => h.Vm.OrgNamePromptVisible, "the organization-name prompt");
            h.Vm.OrgName = "Acme";
            await h.Vm.SubmitOrgNameCommand.Execute().ToTask();

            await WaitUntil(() => h.Vm.SlugPromptVisible, "the slug prompt");
            var suggestion = h.Vm.Slug;
            await h.Vm.SubmitSlugCommand.Execute().ToTask();

            await WaitUntil(() => h.Vm.ConfirmVisible, "the create confirmation");
            var confirm = h.Vm.ConfirmText;
            await h.Vm.ConfirmCreateCommand.Execute().ToTask();

            await exec;

            return (shows, suggestion, confirm, h.Vm.Satisfied, h.Vm.Status);
        });

        await Assert.That(modeShows).IsEqualTo(0); // the Connect intent IS the mode
        await Assert.That(slugSuggestion).IsEqualTo("acme");
        await Assert.That(confirmText).Contains("https://acme.kcap.ai");
        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Declining_the_create_confirmation_explains_itself_instead_of_a_bare_failure() {
        var (status, detail, log) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Signup.Respond = (_, _) => (HttpStatusCode.OK, """{"available":true}""");
            h.Operation = async (_, ct) => {
                var offer = await h.Provisioner.OfferCreateAsync(Tokens(), ct);

                return offer.Status == ProvisionOfferStatus.Created
                    ? Committed(AuthProvider.WorkOS)
                    : new AuthResult.Failed($"Workspace creation did not complete ({offer.Status}).");
            };

            var exec = h.SignIn();

            await WaitUntil(() => h.Vm.OrgNamePromptVisible, "the organization-name prompt");
            h.Vm.OrgName = "Acme";
            await h.Vm.SubmitOrgNameCommand.Execute().ToTask();

            await WaitUntil(() => h.Vm.SlugPromptVisible, "the slug prompt");
            await h.Vm.SubmitSlugCommand.Execute().ToTask();

            await WaitUntil(() => h.Vm.ConfirmVisible, "the create confirmation");
            await h.Vm.DeclineCreateCommand.Execute().ToTask();
            await exec;

            return (h.Vm.Status, h.Vm.StatusDetail, h.Vm.Log.ToList());
        });

        await Assert.That(status).IsEqualTo("Sign-in failed.");
        await Assert.That(detail).IsEqualTo("No workspace created.");
        await Assert.That(log).Contains("No workspace created.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_empty_create_prompt_cannot_be_submitted_at_all() {
        var (orgEmpty, orgBlank, orgNamed, existingEmpty, existingBlank, existingNamed) =
            await AvaloniaSession.DispatchAsync(() => {
                using var h = new Harness();

                var orgEmpty = CanExecute(h.Vm.SubmitOrgNameCommand);
                h.Vm.OrgName = "   ";
                var orgBlank = CanExecute(h.Vm.SubmitOrgNameCommand);
                h.Vm.OrgName = "Acme";
                var orgNamed = CanExecute(h.Vm.SubmitOrgNameCommand);

                var existingEmpty = CanExecute(h.Vm.UseExistingWorkspaceCommand);
                h.Vm.ExistingWorkspaceInput = "  ";
                var existingBlank = CanExecute(h.Vm.UseExistingWorkspaceCommand);
                h.Vm.ExistingWorkspaceInput = "acme";
                var existingNamed = CanExecute(h.Vm.UseExistingWorkspaceCommand);

                return (orgEmpty, orgBlank, orgNamed, existingEmpty, existingBlank, existingNamed);
            });

        await Assert.That(orgEmpty).IsFalse();
        await Assert.That(orgBlank).IsFalse();
        await Assert.That(orgNamed).IsTrue();
        await Assert.That(existingEmpty).IsFalse();
        await Assert.That(existingBlank).IsFalse();
        await Assert.That(existingNamed).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_zero_tenant_discovery_offers_the_three_way_choice_and_retargets_to_connect() {
        var (retargets, choice, prefilled, status, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            h.Connect.DiscoveryProvider = AuthProvider.WorkOS;
            h.Operation = async (_, ct) => {
                var offer = await h.Provisioner.OfferCreateAsync(Tokens(), ct);

                return offer.Status == ProvisionOfferStatus.ExistingWorkspace
                    ? new AuthResult.Retarget(offer.ExistingWorkspaceInput!)
                    : new AuthResult.Failed($"Workspace creation did not complete ({offer.Status}).");
            };

            var raised = new List<string>();
            h.Vm.RetargetRequested += target => raised.Add(target);

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.ModeChoiceVisible, "the workspace mode choice");

            h.Vm.ExistingWorkspaceInput = "acme";
            await h.Vm.UseExistingWorkspaceCommand.Execute().ToTask();
            await exec;

            return (raised, h.Connect.Choice, h.Connect.ServerInputText, h.Vm.Status, h.Vm.Satisfied);
        });

        await Assert.That(retargets).IsEquivalentTo(["acme"]);
        await Assert.That(choice).IsEqualTo(ConnectChoice.Paste);
        await Assert.That(prefilled).IsEqualTo("acme");
        await Assert.That(status).Contains("Connect step");
        await Assert.That(satisfied).IsFalse();
    }

    // ── step transitions across the boundary ─────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(WizardNavigation.Back)]
    [Arguments(WizardNavigation.Skip)]
    [Arguments(WizardNavigation.Next)]
    public async Task Leaving_before_the_boundary_cancels_the_attempt_and_is_allowed(WizardNavigation direction) {
        var (canLeave, satisfied, status, isError, runs) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var started = new TaskCompletionSource();
            h.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // the pre-boundary lane, cancellable

                return Committed();
            };

            var exec = h.SignIn();
            await started.Task.WaitAsync(Bounded);

            await h.Vm.SignInAsync(); // a re-entrant run while one is live must be a silent no-op

            var allowed = await h.Vm.CanLeaveAsync(direction, CancellationToken.None);
            await exec;

            return (allowed, h.Vm.Satisfied, h.Vm.Status, h.Vm.StatusIsError, h.Runs);
        });

        await Assert.That(canLeave).IsTrue();
        await Assert.That(satisfied).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(isError).IsFalse();
        await Assert.That(runs).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_after_the_boundary_waits_for_the_commit_and_is_allowed() {
        var (heldWhileCommitting, canLeave, satisfied, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var gate = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Operation = (_, _) => gate.Task; // past the boundary: cancellation is ignored

            var exec = h.SignIn();
            var leaving = h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var held = !leaving.IsCompleted; // the wizard is genuinely waiting on the commit

            gate.SetResult(Committed());
            var allowed = await leaving.WaitAsync(Bounded);
            // Read before awaiting the command: the leave itself must have settled the state.
            var (satisfied, status) = (h.Vm.Satisfied, h.Vm.Status);
            await exec;

            return (held, allowed, satisfied, status);
        });

        await Assert.That(heldWhileCommitting).IsTrue();
        await Assert.That(canLeave).IsTrue();
        await Assert.That(satisfied).IsTrue();
        await Assert.That(status).IsEqualTo("Signed in as sam");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Leaving_while_a_create_prompt_is_parked_releases_it_and_cancels() {
        var (canLeave, promptVisible, status, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.Operation = async (_, ct) => {
                var offer = await h.Provisioner.OfferCreateAsync(Tokens(), ct);

                return offer.Status == ProvisionOfferStatus.Created
                    ? Committed(AuthProvider.WorkOS)
                    : new AuthResult.Failed($"Workspace creation did not complete ({offer.Status}).");
            };

            var exec = h.SignIn();
            await WaitUntil(() => h.Vm.OrgNamePromptVisible, "the organization-name prompt");

            var allowed = await h.Vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None);
            await exec;

            return (allowed, h.Vm.OrgNamePromptVisible, h.Vm.Status, h.Vm.Satisfied);
        });

        await Assert.That(canLeave).IsTrue();
        await Assert.That(promptVisible).IsFalse();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
        await Assert.That(satisfied).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_settled_attempt_clears_the_device_code_and_browser_surfaces() {
        var (code, uri, browser, waiting, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Discover;
            var started = new TaskCompletionSource();
            h.Operation = async (_, ct) => {
                h.Progress.BrowserOpening("https://auth.example/authorize");
                h.Progress.DeviceCode("ABCD-1234", "https://github.com/login/device", "GitHub", prefilled: false);
                h.Progress.PollTick();
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);

                return Committed();
            };

            var exec = h.SignIn();
            await started.Task.WaitAsync(Bounded);
            await h.Vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None);
            await exec;

            return (h.Vm.DeviceCode, h.Vm.VerificationUri, h.Vm.BrowserUrl, h.Vm.WaitingText, h.Vm.Status);
        });

        await Assert.That(code).IsNull(); // nothing polls a cancelled device flow
        await Assert.That(uri).IsNull();
        await Assert.That(browser).IsNull();
        await Assert.That(waiting).IsNull();
        await Assert.That(status).IsEqualTo("Sign-in cancelled.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Signing_in_without_a_staged_intent_starts_nothing_and_says_where_to_choose() {
        var (runs, status) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Paste; // nothing typed — no valid intent

            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.SignIn();

            return (h.Runs, h.Vm.Status);
        });

        await Assert.That(runs).IsEqualTo(0);
        await Assert.That(status).Contains("Connect");
    }

    // ── quarantine notice ────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_quarantined_claims_file_surfaces_one_dismissible_notice_and_the_ack_persists() {
        var (notice, afterAck, acked, updates) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            await File.WriteAllTextAsync(h.ClaimsPath, "{ this is not json");

            await h.SignIn();
            var shown = h.Vm.QuarantineNotice;

            await h.Vm.AcknowledgeQuarantineCommand.Execute().ToTask();

            return (shown, h.Vm.QuarantineNotice, h.AppState.State.ConsentQuarantineAcked, h.AppState.Updates);
        });

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!).Contains("consent-flip claims file");
        await Assert.That(notice).Contains(".quarantined-0.json");
        await Assert.That(afterAck).IsNull();
        await Assert.That(acked).IsTrue();
        await Assert.That(updates).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_already_acknowledged_quarantine_is_never_surfaced_again() {
        var notice = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;
            h.AppState.State = h.AppState.State with { ConsentQuarantineAcked = true };
            await File.WriteAllTextAsync(h.ClaimsPath, "{ this is not json");

            await h.SignIn();

            return h.Vm.QuarantineNotice;
        });

        await Assert.That(notice).IsNull();
    }

    // ── templates ────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_selects_a_template_per_step_view_model() {
        var (connectBox, signInButton, signInStatus) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            var vm = new OnboardingViewModel([h.Connect, h.Vm]);
            await vm.PendingEnterForTesting;

            var window = new OnboardingWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "ServerInputBox");

            await vm.NextCommand.Execute().ToTask(); // Connect -> Sign in
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SignInButton");
            var status = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "SignInStatusText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (box, button, status);
        });

        await Assert.That(connectBox).IsNotNull();
        await Assert.That(signInButton).IsNotNull();
        await Assert.That(signInStatus).IsEqualTo("Find your workspaces with GitHub");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_healthy_claims_store_surfaces_no_notice() {
        var notice = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new Harness();
            h.Connect.Choice = ConnectChoice.Create;

            await h.SignIn();

            return h.Vm.QuarantineNotice;
        });

        await Assert.That(notice).IsNull();
    }
}
