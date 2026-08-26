namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// The machine platforms the flow names. A closed set, because the browser uses it to decide which
/// affordance to draw and a platform it cannot map is one it must not guess at.
/// </summary>
public static class FirstRunPlatforms {
    public const string MacOs   = "macos";
    public const string Linux   = "linux";
    public const string Windows = "windows";

    /// <summary>This machine, or null when it is none of the three. <b>Null is unknown, not "other"</b> —
    /// the server draws no fix affordance for either, but only one of them is a claim.</summary>
    public static string? Current() =>
        OperatingSystem.IsMacOS()   ? MacOs
      : OperatingSystem.IsLinux()   ? Linux
      : OperatingSystem.IsWindows() ? Windows
      :                               null;
}

/// <summary>
/// What the browser may ask this machine to do. <b>A named capability and nothing else</b> — the CLI
/// resolves its own binary and composes its own command, so a path or a shell line never crosses the
/// server→CLI lane.
/// </summary>
public static class FirstRunMachineCapabilities {
    /// <summary>Put <c>kcap</c> on the login shell's PATH — <c>kcap daemon shim ensure</c>.</summary>
    public const string PathShim = "path_shim";

    public static readonly IReadOnlyList<string> All = [PathShim];

    public static bool IsKnown(string? capability) =>
        capability is not null && All.Contains(capability, StringComparer.Ordinal);
}

/// <summary>
/// How performing a capability ended, and the wire's canonical vocabulary for it — the server validates
/// against this set and renders copy per token, so a second spelling anywhere is a silent wire break.
///
/// <para><b>The copy keys off these, never off an exit code</b>, which collapses "we could not tell"
/// into the same non-zero as "it failed".</para>
/// </summary>
public static class FirstRunMachineActionOutcomes {
    /// <summary>Nothing to do — the terminal already resolves it.</summary>
    public const string AlreadyOnPath = "already_on_path";

    public const string Installed = "installed";

    /// <summary>Linked, but the login shell still does not see it. <b>Not a success</b>: the whole hazard
    /// is what the login shell can find.</summary>
    public const string InstalledNotOnPath = "installed_not_on_path";

    /// <summary>The user dismissed the admin prompt. A choice, not a fault.</summary>
    public const string Cancelled = "cancelled";

    public const string Failed = "failed";

    /// <summary>Nothing was attempted; <see cref="FirstRunMachineActionReasons"/> names the row.</summary>
    public const string Refused = "refused";

    public static readonly IReadOnlyList<string> All =
        [AlreadyOnPath, Installed, InstalledNotOnPath, Cancelled, Failed, Refused];

    public static bool IsKnown(string? outcome) =>
        outcome is not null && All.Contains(outcome, StringComparer.Ordinal);
}

/// <summary>Why a refusal refused. Non-null only alongside
/// <see cref="FirstRunMachineActionOutcomes.Refused"/>, and a coded token rather than prose.</summary>
public static class FirstRunMachineActionReasons {
    /// <summary><b>Not a failure.</b> The probe could not say whether the CLI is on the terminal's PATH,
    /// and rendering that as a failed fix invents the same alarm the null-is-not-false rule prevents.</summary>
    public const string ProbeUnknown = "probe_unknown";

    /// <summary>The shim writer is macOS-only.</summary>
    public const string UnsupportedPlatform = "unsupported_platform";

    public const string NoCliPath = "no_cli_path";

    /// <summary>Something else already sits at the destination and was left untouched.</summary>
    public const string Conflict = "conflict";

    public static readonly IReadOnlyList<string> All =
        [ProbeUnknown, UnsupportedPlatform, NoCliPath, Conflict];

    public static bool IsKnown(string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>One thing the browser asked this machine to do. <paramref name="RequestedAt"/> is the
/// request's identity, and the outcome is reported against it: without it a slow report lands on a
/// request that has already been superseded by a retry.</summary>
public readonly record struct FirstRunMachineActionRequest(string Capability, DateTimeOffset RequestedAt);

/// <summary>What performing one produced, as the two wire tokens.</summary>
public readonly record struct FirstRunMachineActionResult(string Outcome, string? Reason);

/// <summary>
/// The host's ability to act on the machine. A seam because the capabilities live in the command layer
/// while the poll loop that learns of a request lives here; a host supplying none performs nothing.
/// </summary>
public interface IFirstRunMachineActions {
    /// <summary>What this host can actually do. The loop filters on it, so a capability a newer server
    /// invented is never handed to <see cref="PerformAsync"/>.</summary>
    IReadOnlyCollection<string> Capabilities { get; }

    /// <summary>Performs one of <see cref="Capabilities"/>. May take as long as a human takes — the shim
    /// prompts for an admin password — and the loop is blocked while it does, deliberately: the user
    /// asked for this and the answer is what the screen is waiting on.</summary>
    Task<FirstRunMachineActionResult> PerformAsync(string capability, CancellationToken ct);
}
