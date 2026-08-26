using Avalonia.Media;
using Avalonia.Media.Immutable;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// One card of the Home tab's "Active sessions" grid. Constructed once per
/// AgentStatusDto revision — DynamicData's Transform recreates the whole object on every change,
/// same as AgentRowViewModel — so every field is computed once from the dto passed to the
/// constructor. HomeViewModel has no ticker (unlike MainWindowViewModel), so Age is a
/// point-in-time snapshot rather than a live-updating property.
public sealed class SessionCardViewModel {
    public string Id { get; }
    public string Title { get; }
    public string Vendor { get; }
    public string RepoFull { get; }
    public string StatusText { get; }
    public IBrush StatusDot { get; }
    public string Age { get; }

    // Sort key only, mirroring AgentRowViewModel.CreatedAt — not part of the card's presentation
    // surface.
    internal DateTime CreatedAt { get; }

    public SessionCardViewModel(AgentStatusDto dto) {
        Id = dto.Id;
        Vendor = dto.Vendor;
        RepoFull = dto.RepoPath ?? "";
        Title = $"{RepoLabel.Leaf(dto.RepoPath)} · {dto.Vendor}";
        StatusText = dto.Status;
        StatusDot = StatusDotFor(dto.Status);
        CreatedAt = dto.CreatedAt;

        var createdAtUtc = DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc);
        Age = UptimeFormat.Format(DateTime.UtcNow - createdAtUtc);
    }

    // ImmutableSolidColorBrush, not SolidColorBrush: these cards are built by DynamicData's
    // Transform on the daemon client's own pump thread (HomeViewModel), and a SolidColorBrush is
    // an AvaloniaObject whose thread affinity is taken from whoever constructed it —
    // MainWindowViewModel.DotBrush's own comment covers why that is a trap. An immutable brush has
    // no affinity at all, which is also what makes these four safe to share across every card
    // instead of reallocating per card per revision.
    static readonly ImmutableSolidColorBrush RunningDot  = new(Color.Parse(StatusColors.Connected));
    static readonly ImmutableSolidColorBrush StartingDot = new(Color.Parse(StatusColors.InProgress));
    static readonly ImmutableSolidColorBrush FailedDot   = new(Color.Parse(StatusColors.Disrupted));
    static readonly ImmutableSolidColorBrush NeutralDot  = new(Color.Parse(StatusColors.Unavailable));

    // Running/Starting/Failed are the daemon's own open vocabulary (AgentOrchestrator); anything
    // else (Completed, or a value this build has never heard of) reads as neutral rather than
    // guessing at a verdict.
    static IBrush StatusDotFor(string status) => status switch {
        "Running"  => RunningDot,
        "Starting" => StartingDot,
        "Failed"   => FailedDot,
        _          => NeutralDot,
    };
}
