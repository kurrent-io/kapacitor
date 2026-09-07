using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public enum AgentOrigin { Local, Remote }

/// One merged row. Key is SOURCE-scoped ("local:{id}" / "remote:{id}") so the lanes can never
/// clobber each other; Id is the logical agent id workspaces bind to.
public sealed record AgentRow(
        string Key, AgentOrigin Origin, string Id, string Kind, string Vendor, string Status,
        DateTime CreatedAt, string? RepoPath, string? Title, string? Model, string? RequesterDisplay,
        string? WorktreePath, string? WorkLocation, string? BorrowedFrom,
        string? MachineBadge, // remote rows: the daemon name; local rows: null
        string RepoGroupKey, string RepoGroupLabel, string CheckoutKey, string CheckoutLabel,
        // Null means unknown (the server registry carries no turn verdict), never "working".
        bool? AwaitingInput = null) {

    public static AgentRow FromLocal(AgentStatusDto dto, RepoIdentity repo) => new(
        Key: $"local:{dto.Id}", Origin: AgentOrigin.Local, Id: dto.Id, Kind: dto.Kind,
        Vendor: dto.Vendor, Status: dto.Status, CreatedAt: dto.CreatedAt, RepoPath: dto.RepoPath,
        Title: dto.Title, Model: dto.Model, RequesterDisplay: dto.RequesterDisplay,
        WorktreePath: dto.WorktreePath, WorkLocation: dto.WorkLocation, BorrowedFrom: dto.BorrowedFrom,
        MachineBadge: null, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
        CheckoutKey: ViewModels.SessionRailViewModel.WorktreeKeyFor(dto), CheckoutLabel: "",
        AwaitingInput: dto.AwaitingInput);

    public static AgentRow FromRemote(AgentInstanceDto dto) {
        var daemonKey = $"{dto.OwnerUserId}/{dto.DaemonName}";
        var repo = RepoIdentityResolver.ForRemote(dto.RepoOwner, dto.RepoName, dto.RepoPath, daemonKey);
        return new(
            Key: $"remote:{dto.AgentId}", Origin: AgentOrigin.Remote, Id: dto.AgentId, Kind: "agent",
            Vendor: dto.Vendor ?? "", Status: dto.Status, CreatedAt: dto.RegisteredAt, RepoPath: dto.RepoPath,
            Title: TitleFromPrompt(dto.Prompt), Model: dto.Model, RequesterDisplay: null,
            WorktreePath: null, WorkLocation: null, BorrowedFrom: null,
            MachineBadge: dto.DaemonName, RepoGroupKey: repo.Key, RepoGroupLabel: repo.Label,
            CheckoutKey: $"@{daemonKey}", CheckoutLabel: $"on {dto.DaemonName}");
    }

    internal static string? TitleFromPrompt(string? prompt) {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var line = prompt.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length <= 80 ? line : line[..80];
    }
}
