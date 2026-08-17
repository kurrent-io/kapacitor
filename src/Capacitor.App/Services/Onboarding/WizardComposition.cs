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

/// What wizard-first mode runs on: the shell, the sign-in driver the close path awaits, and every
/// step (including ones the shell filtered out as inapplicable — the summary still names them).
internal sealed record WizardGraph(
    OnboardingViewModel ViewModel, WizardAuthService Auth, IReadOnlyList<IWizardStep> Steps);

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
    Func<ILocalControlOps>                                                       ResolveOps,
    Func<(string Profile, string Server, string DaemonName)>                     ResolveIdentity,
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
        var defaults = new DefaultsStepViewModel(options.DefaultDaemonName);
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
            options.Observation, new LateBoundLocalControlOps(options.ResolveOps), claims,
            options.ResolveIdentity, options.Surface, options.Probe.TerminalPathAsync, options.Time);

        IWizardStep[] configured = [shim, connect, signIn, defaults, agents, import, daemon];
        // Read on every entry, so a Back-then-forward re-render sees each step's current state.
        var done = new DoneStepViewModel(() => Summarize(configured, cli.CliPath is not null));
        IWizardStep[] steps = [.. configured, done];

        var wizard = new OnboardingViewModel(steps, options.ShutdownToken, options.Surface);
        // A WorkOS "I already have a workspace" prefills the Connect step; without the navigation
        // the prefill would sit on a page the user is not looking at.
        signIn.RetargetRequested += _ => wizard.TryGoTo(WizardStepId.Connect);

        return new WizardGraph(wizard, auth, steps);
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
