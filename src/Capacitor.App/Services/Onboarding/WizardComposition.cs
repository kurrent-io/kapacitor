using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.Onboarding;

/// The façade's constructor arguments as data, so the composition root's choices — which sink,
/// which picker, whether a provisioner is armed, which before-commit hook — are inspectable.
internal sealed record WizardFacadeSpec(
    IAuthProgress                                              Progress,
    ITenantPicker                                              Picker,
    ITenantProvisioner?                                        Provisioner,
    Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> BeforeCommit);

/// What wizard-first mode runs on: the shell, the sign-in driver the close path awaits, every step
/// (including ones the shell filtered out as inapplicable — the summary still names them), and the
/// Import step by name — the close path must cancel its in-flight run directly (spec §7), which
/// CanLeaveAsync alone does not cover since closing the window never navigates away from a step.
internal sealed record WizardGraph(
    OnboardingViewModel ViewModel, WizardAuthService Auth, IReadOnlyList<IWizardStep> Steps,
    ImportStepViewModel Import);

/// <summary>
/// Everything wizard-first mode is composed from. The daemon-facing entries are FACTORIES, not
/// instances: the wizard writes config while it runs (a sign-in adopts a server, the Defaults step
/// renames the daemon), so a socket or CLI binding pinned at composition time would land later
/// calls on the wrong daemon — silently, since a wrong-identity put fails closed.
/// </summary>
internal sealed record WizardGraphOptions(
    ConsentFlipClaims                                                            Claims,
    WizardBridges                                                                Bridges,
    Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> Operation,
    WizardLifecycleSurface                                                       Surface,
    Func<IKcapCli>                                                               ResolveCli,
    // Takes the already-resolved identity's daemon name — never re-resolves identity on its own
    // (finding 3/4: a factory that re-derives its own answer is exactly how the old ("","","")
    // sentinel leaked in). Invoked only from an identity-non-null path below.
    Func<string, ILocalControlOps>                                               ResolveOps,
    // The daemon step's OWN identity — nullable (finding 3): a KCAP_PROFILE override set after
    // startup, or an unreadable/invalid config, must read as "not ready" rather than a stale or
    // empty-string sentinel.
    Func<(string Profile, string Server, string DaemonName)?>                    ResolveIdentity,
    // The claims path's identity — deliberately the SEPARATE, literal ResolveConsentFlipIdentity
    // (its own doc comment justifies the literal read); never swapped for ResolveIdentity above.
    Func<(string Profile, string Server, string DaemonName)>                     ResolveConsentFlipIdentity,
    Func<MutationRequest, CancellationToken, Task<MutationOutcome>>              RunMutation,
    IDaemonObservation                                                           Observation,
    IAppStateStore                                                               AppState,
    PathShimInstaller                                                            ShimInstaller,
    IUrlOpener                                                                   UrlOpener,
    ILoginShellProbe                                                             Probe,
    Func<ILoginShellProbe, Func<CancellationToken, Task<AgentDetectionResult>>>  DetectionFeed,
    string?                                                                      CliPath,
    bool                                                                         ShimApplicable,
    string?                                                                      ShimTarget,
    string?                                                                      DefaultDaemonName,
    TimeProvider                                                                 Time,
    CancellationToken                                                            ShutdownToken);

/// The wizard half of the composition root (spec decision 2), split out of App so it can be driven
/// with fakes: nothing here touches a daemon, a socket or the network until a step is used.
internal static class WizardComposition {
    internal const string CliMissingNote     = "kcap CLI not found";
    internal const string RequiresSignInNote = "requires sign-in";

    /// Production bridges: one marshalling boundary (Avalonia's dispatcher in the app) and a
    /// provisioner built from the bridges' OWN sink, per WizardBridges' contract.
    internal static WizardBridges BuildBridges(Action<Action> post) =>
        new(post, progress => new WizardTenantProvisioner(
            new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url, progress));

    /// Production operation: the spec IS the façade's arguments, and WizardSignInOperation owns
    /// the intent→call map (paste adopts the server; create/discover run WorkOS discovery).
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> NewOperation(WizardFacadeSpec spec) =>
        WizardSignInOperation.For(new OnboardingFacade(spec.Progress, spec.Picker, spec.Provisioner, spec.BeforeCommit));

    /// The ONE façade a wizard run signs in through — provisioner armed (a provisioner-less façade
    /// dead-ends "Create a workspace" at "ask your admin") and the decision-7 arming hook wired as
    /// before-commit, so a claim exists before anything durable is published.
    internal static Func<ConnectIntent, CancellationToken, Task<AuthResult>> BuildOperation(
            WizardBridges bridges, ConsentFlipClaims claims,
            Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> operation) =>
        operation(new WizardFacadeSpec(
            bridges.Progress, bridges.Picker, bridges.Provisioner, WizardAuthService.ArmingHook(claims)));

    internal static WizardGraph BuildGraph(WizardGraphOptions options) {
        var claims = options.Claims;
        var cli    = new LateBoundKcapCli(options.ResolveCli, options.CliPath);
        var auth   = new WizardAuthService(BuildOperation(options.Bridges, claims, options.Operation));

        var connect  = new ConnectStepViewModel();
        var signIn   = new SignInStepViewModel(auth, connect, options.Bridges, claims, options.AppState, options.UrlOpener);
        var shim     = new ShimStepViewModel(options.ShimApplicable, options.ShimInstaller, options.AppState, options.ShimTarget);
        // The Defaults step's persist targets the SAME fresh identity the daemon step gates on
        // (finding 3) — falling back to c.ActiveProfile itself when unresolved (today's behavior).
        var defaults = new DefaultsStepViewModel(options.DefaultDaemonName, () => options.ResolveIdentity()?.Profile);
        // ONE detection feed for both vendor steps: two would probe the login shell twice for the
        // same answer, and the two steps' vendor lists could then disagree.
        var detect = options.DetectionFeed(options.Probe);
        var agents = new AgentsStepViewModel(cli, detect);
        var import = new ImportStepViewModel(cli, detect, options.Bridges.Post);
        var daemon = new DaemonStepViewModel(
            cli, options.RunMutation,
            // Gated on a COMMITTED sign-in (the RequiresSignIn row is what a skipped sign-in must
            // read as) and resolved FRESH per call — never the startup-cached profile.
            () => signIn.Satisfied ? options.ResolveIdentity() : null,
            options.Observation,
            // The ops factory only ever runs from this identity-non-null branch (finding 4): a
            // null resolution never reaches ResolveOps, so there is no name to fail closed on in
            // the first place — the step's own gate above is what keeps a live socket un-dialed.
            new LateBoundLocalControlOps(() => options.ResolveOps(options.ResolveIdentity() is { } id
                ? id.DaemonName
                : options.DefaultDaemonName ?? "daemon")),
            claims,
            options.ResolveConsentFlipIdentity, options.Surface, options.Probe.TerminalPathAsync, options.Time);

        IWizardStep[] configured = [shim, connect, signIn, defaults, agents, import, daemon];
        // Read on every entry, so a Back-then-forward re-render sees each step's current state.
        var done = new DoneStepViewModel(() => Summarize(configured, cli.CliPath is not null));
        IWizardStep[] steps = [.. configured, done];

        var wizard = new OnboardingViewModel(steps, options.ShutdownToken, options.Surface);
        // A WorkOS "I already have a workspace" prefills the Connect step; without the navigation
        // the prefill would sit on a page the user is not looking at.
        signIn.RetargetRequested += _ => wizard.TryGoTo(WizardStepId.Connect);

        return new WizardGraph(wizard, auth, steps, import);
    }

    /// The Done step's rows: what each earlier step reached and — when it didn't — why it was skipped.
    internal static IReadOnlyList<(string Title, bool Satisfied, string? Note)> Summarize(
            IReadOnlyList<IWizardStep> steps, bool cliAvailable) =>
        steps.Where(step => step.Id != WizardStepId.Done)
            .Select(step => (step.Title, step.Satisfied, step.Satisfied ? null : SkipNote(step, cliAvailable)))
            .ToList();

    // A missing CLI dominates: every step that shells out is unreachable for that one reason.
    static string? SkipNote(IWizardStep step, bool cliAvailable) {
        if (!cliAvailable && NeedsCli(step.Id)) return CliMissingNote;

        return step switch {
            DaemonStepViewModel { Row: DaemonRow.RequiresSignIn } => RequiresSignInNote,
            DaemonStepViewModel daemonStep                        => daemonStep.Message,
            ShimStepViewModel shimStep                            => shimStep.Message,
            _                                                     => null,
        };
    }

    static bool NeedsCli(WizardStepId id) =>
        id is WizardStepId.Shim or WizardStepId.Agents or WizardStepId.Import or WizardStepId.Daemon;
}
