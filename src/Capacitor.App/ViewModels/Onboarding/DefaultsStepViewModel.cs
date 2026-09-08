using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using ReactiveUI.Reactive;

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

    readonly ConfigRoot     _config;
    readonly Func<string?>? _resolveProfileName;

    string  _visibility = "org_public";
    string  _daemonName;
    bool    _satisfied;
    string? _message;

    /// <param name="resolveProfileName">Re-invoked per persist rather than captured; null or unresolved falls back to <c>c.ActiveProfile</c>.</param>
    public DefaultsStepViewModel(
            ConfigRoot     config,
            string?        defaultDaemonName  = null,
            Func<string?>? resolveProfileName = null
        ) {
        _config = config;

        _daemonName = string.IsNullOrWhiteSpace(defaultDaemonName)
            ? Environment.UserName.ToLowerInvariant()
            : defaultDaemonName;
        _resolveProfileName = resolveProfileName;
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

    /// Set when a persist attempt fails, so the veto below is visible, not just logged.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;

    /// Persists on Next only — Back and Skip leave the active profile untouched, so re-entering
    /// this step (or abandoning the wizard) never writes a value the user didn't confirm. A
    /// persist failure vetoes (stays on the step) with a visible Message rather than the shell's
    /// generic stderr-only catch.
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        if (direction != WizardNavigation.Next) return true;

        try {
            await ConfigMutator.MutateAsync(_config, c => {
                var resolvedName = _resolveProfileName?.Invoke();
                var activeName   = resolvedName is not null && c.Profiles.ContainsKey(resolvedName)
                    ? resolvedName
                    : string.IsNullOrWhiteSpace(c.ActiveProfile) ? "default" : c.ActiveProfile;
                var profile    = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

                profile = profile with {
                    DefaultVisibility = Visibility,
                    Daemon            = (profile.Daemon ?? new DaemonSettings()) with { Name = DaemonName }
                };

                return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = profile } };
            }, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Message = $"Could not save defaults: {ex.Message}";

            return false;
        }

        Message   = null;
        Satisfied = true;

        return true;
    }
}
