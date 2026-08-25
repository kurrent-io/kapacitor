using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Capacitor.App.ViewModels;

/// Status → dot brush for session surfaces (Home cards, the rail). ImmutableSolidColorBrush,
/// not SolidColorBrush: these are built on the daemon client's pump thread and an immutable
/// brush has no thread affinity, which is also what makes the four instances shareable.
public static class SessionStatusDots {
    static readonly ImmutableSolidColorBrush RunningDot  = new(Color.Parse(StatusColors.Connected));
    static readonly ImmutableSolidColorBrush StartingDot = new(Color.Parse(StatusColors.InProgress));
    static readonly ImmutableSolidColorBrush FailedDot   = new(Color.Parse(StatusColors.Disrupted));
    static readonly ImmutableSolidColorBrush NeutralDot  = new(Color.Parse(StatusColors.Unavailable));

    // Running/Starting/Failed are the daemon's own open vocabulary (AgentOrchestrator); anything
    // else (Completed, or a value this build has never heard of) reads as neutral.
    public static IBrush For(string status) => status switch {
        "Running"  => RunningDot,
        "Starting" => StartingDot,
        "Failed"   => FailedDot,
        _          => NeutralDot,
    };
}
