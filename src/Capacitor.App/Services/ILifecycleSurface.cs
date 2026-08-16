namespace Capacitor.App.Services;

/// Everything the controller shows a human (spec §4/§6). The Avalonia implementation (Task
/// 22/23) renders dialogs/status lines; tests fake it.
public interface ILifecycleSurface {
    /// Honest one-liners — the message lane (e.g. degraded-but-owned, coded-failure surfaces).
    void Status(string message);

    Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct);

    /// Like ConfirmAsync, but null distinguishes "ct won before the dialog factory ran" from a genuinely shown-and-declined dialog (false).
    Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct);

    /// Repair-affordance surfaces (spec §4.4) — never a silent mutation.
    void Attention(string message);
}

/// <param name="Kind">One of the Kind* consts below.</param>
/// <param name="PathDegraded">Decision-7 disclosure when the terminal PATH is unknown.</param>
/// <param name="Disclosure">Replacement/recapture text (decision 3).</param>
public sealed record LifecyclePrompt(
    string Kind, string? DaemonVersion, string? CliVersion, bool PathDegraded, string Disclosure) {
    public const string KindRestartUpdate = "restart-update";
    public const string KindTakeover      = "takeover";
    public const string KindRepair        = "repair"; // Task 21
    public const string KindShim          = "shim";   // Task 24
    public const string KindQuarantine    = "quarantine"; // Task 10
}
