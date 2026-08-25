using Avalonia.Media;
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
        StatusDot = SessionStatusDots.For(dto.Status);
        CreatedAt = dto.CreatedAt;

        var createdAtUtc = DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc);
        Age = UptimeFormat.Format(DateTime.UtcNow - createdAtUtc);
    }
}
