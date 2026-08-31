using System.Globalization;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

public enum PermissionAnswer { Allow, AllowAlways, Deny }

/// Only TransportFailure leaves the request pending; the other two are conclusive.
public enum PermissionResolveKind { Applied, AlreadyDecided, TransportFailure }

public sealed record PermissionResolveOutcome(PermissionResolveKind Kind, string? Error);

public sealed class PendingPermissionRequest {
    internal PendingPermissionRequest(PermissionPendingDto dto) {
        Dto = dto;
        RequestedAt = DateTimeOffset.TryParse(dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t : DateTimeOffset.MinValue;
    }

    public PermissionPendingDto Dto { get; }
    public string RequestId => Dto.RequestId;
    public string AgentId => Dto.AgentId;
    public string Vendor => Dto.Vendor;
    public string ToolName => Dto.ToolName;
    public string? ToolInputJson => Dto.ToolInput?.GetRawText();
    public bool ToolInputOmitted => Dto.ToolInputOmitted;
    public DateTimeOffset RequestedAt { get; }
}

public interface IPermissionService : IDisposable {
    /// Mutated on background continuations — consumers ObserveOn(RxSchedulers.MainThreadScheduler).
    IObservable<IChangeSet<PendingPermissionRequest, string>> Pending { get; }
    /// Replays the current count on subscribe (DynamicData's CountChanged).
    IObservable<int> PendingCount { get; }
    /// The distinct agent ids in the cache; replays the current set on subscribe.
    IObservable<IReadOnlySet<string>> AgentsWithPending { get; }
    Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct);
}
