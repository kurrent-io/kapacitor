using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Onboarding;

/// <summary>
/// <see cref="ILocalControlOps"/> that resolves its daemon socket per CALL. The wizard's Defaults
/// step can rename the daemon after the daemon step was composed, and a pinned name would send the
/// consent put to a socket nobody answers — which fails closed, and therefore silently.
/// </summary>
public sealed class LateBoundLocalControlOps(Func<ILocalControlOps> bind) : ILocalControlOps {
    public Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) =>
        bind().StopAgentAsync(agentId, force, ct);

    public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) =>
        bind().GetConsentPolicyAsync(ct);

    public Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) =>
        bind().PutConsentPolicyAsync(policy, ct);

    public Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct) =>
        bind().PutConsentPolicyV2Async(put, ct);

    public Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) =>
        bind().ResolveConsentAsync(resolve, ct);
}

/// <see cref="IKcapCli"/> rebound per call, for the same reason: profile, canonical server and
/// daemon name are all still being written while the wizard is open.
public sealed class LateBoundKcapCli(Func<IKcapCli> bind) : IKcapCli {
    public string? CliPath => bind().CliPath;

    public Task<string?> VersionAsync(CancellationToken ct) => bind().VersionAsync(ct);

    public Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) => bind().ServiceStatusAsync(ct);

    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) => bind().ServiceStartVerifiedAsync(ct);

    public Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct) =>
        bind().ServiceInstallVerifiedAsync(replace, ct);

    public Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct) =>
        bind().DetachedStartAsync(bootAttemptId, ct);

    public Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct) =>
        bind().PluginInstallAsync(vendorFlag, ct);

    public Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct) =>
        bind().ImportAsync(request, onLine, ct);
}
