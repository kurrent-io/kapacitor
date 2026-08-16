namespace Capacitor.App.ViewModels.Onboarding;

/// Spec §3 order is also display order.
public enum WizardStepId { Shim, Connect, SignIn, Defaults, Agents, Import, Daemon, Done }

public enum WizardNavigation { Back, Next, Skip }

/// One wizard page. Applicable is evaluated once, at OnboardingViewModel construction.
public interface IWizardStep {
    WizardStepId Id { get; }
    string Title { get; }
    bool Applicable { get; }
    bool Satisfied { get; }
    Task OnEnterAsync(CancellationToken ct);

    /// False vetoes the navigation and holds the current step.
    Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct);
}
