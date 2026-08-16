using Capacitor.Cli.Core.Config;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One entry in the visibility picker (spec §3 step 4). A dedicated record rather than a
/// ValueTuple — Avalonia's reflection-based bindings need real CLR properties, not a tuple's
/// compiler-only element-name aliases.
public sealed record VisibilityOption(string Value, string Label);

/// spec §3 step 4 / decision 5: visibility picker + daemon-name field, written to the active
/// profile on Next only (decision 10's ConfigMutator). No claim maintenance — claims key on
/// {profile, server} and resolve the daemon name at application time (decision 7), so a rename
/// here needs no second-store write.
public sealed class DefaultsStepViewModel : ReactiveObject, IWizardStep {
    /// The SAME four labels SetupCommand's interactive visibility prompt uses (Step 3/6), over
    /// the SAME value set as <see cref="AppConfig.ValidVisibilities"/>.
    public static readonly IReadOnlyList<VisibilityOption> VisibilityOptions = [
        new("private",    "All private — only you can see your sessions"),
        new("project",    "Project repos public to fellow project members, others private"),
        new("org_public", "Org repos public, others private (default)"),
        new("public",     "All public — others can see all your sessions"),
    ];

    string _visibility = "org_public";
    string _daemonName;
    bool   _satisfied;

    public DefaultsStepViewModel(string? defaultDaemonName = null) {
        _daemonName = string.IsNullOrWhiteSpace(defaultDaemonName)
            ? Environment.UserName.ToLowerInvariant()
            : defaultDaemonName;
    }

    public WizardStepId Id         => WizardStepId.Defaults;
    public string       Title      => "Defaults";
    public bool         Applicable => true;

    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public string Visibility {
        get => _visibility;
        set => this.RaiseAndSetIfChanged(ref _visibility, value);
    }

    public string DaemonName {
        get => _daemonName;
        set => this.RaiseAndSetIfChanged(ref _daemonName, value);
    }

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    /// Persists on Next only — Back and Skip leave the active profile untouched, so re-entering
    /// this step (or abandoning the wizard) never writes a value the user didn't confirm.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction != WizardNavigation.Next) return true;

        await ConfigMutator.MutateAsync(c => {
            var activeName = string.IsNullOrWhiteSpace(c.ActiveProfile) ? "default" : c.ActiveProfile;
            var profile    = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

            profile = profile with {
                DefaultVisibility = Visibility,
                Daemon            = (profile.Daemon ?? new DaemonSettings()) with { Name = DaemonName }
            };

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = profile } };
        }, ct).ConfigureAwait(false);

        Satisfied = true;

        return true;
    }
}
