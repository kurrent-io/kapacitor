using System.Text.Json.Serialization;
using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable outcome for <c>kcap daemon shim ensure</c> — the flow's PATH-fix
/// capability. <see cref="Outcome"/> and <see cref="Reason"/> are the wire's own tokens, from
/// <see cref="FirstRunMachineActionOutcomes"/> and <see cref="FirstRunMachineActionReasons"/> — one
/// canonical list, because the browser validates against it and renders copy per token, and a second
/// spelling here would be a wire break nobody could see. <see cref="Reason"/> is non-null only on a
/// refusal. <see cref="Detail"/>/<see cref="SudoFallback"/> carry the installer's actionable guidance on
/// <c>installed_not_on_path</c>/<c>failed</c> and are <b>terminal-only</b>: neither crosses to the browser,
/// which keys its copy off <see cref="Outcome"/> and never off the exit code.</summary>
public sealed record ShimEnsureJson(
    string Capability, string? Target, bool? Probed, bool? OnPath, string Action, string Outcome,
    string? Reason = null, string? Detail = null, string? SudoFallback = null) {
    /// <summary>Exit 0 only when the terminal now resolves <c>kcap</c>. Derived from the outcome rather
    /// than passed in per arm, so the two cannot disagree — and the flow reads the outcome, never this.
    /// <c>JsonIgnore</c> because this record IS the <c>--json</c> document: an exit code is the process's
    /// answer to a shell, not a field of the result.</summary>
    [JsonIgnore]
    public int ExitCode =>
        Outcome is FirstRunMachineActionOutcomes.AlreadyOnPath or FirstRunMachineActionOutcomes.Installed
            ? 0
            : 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShimEnsureJson))]
public partial class ShimJsonContext : JsonSerializerContext;
