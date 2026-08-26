using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable outcome for <c>kcap daemon shim ensure</c> — the flow's PATH-fix
/// capability. <see cref="Outcome"/> is the wire token (<c>already_on_path</c>,
/// <c>installed</c>, <c>installed_not_on_path</c>, <c>cancelled</c>, <c>failed</c>,
/// <c>refused</c>); <see cref="Reason"/> is non-null only on refusals — a coded token
/// (<c>probe_unknown</c>, <c>unsupported_platform</c>, <c>no_cli_path</c>, <c>conflict</c>), never
/// prose. <see cref="Detail"/>/<see cref="SudoFallback"/> carry the installer's actionable
/// guidance on <c>installed_not_on_path</c>/<c>failed</c>. The flow's copy keys off
/// <see cref="Outcome"/>, never off the exit code.</summary>
public sealed record ShimEnsureJson(
    string Capability, string? Target, bool? Probed, bool? OnPath, string Action, string Outcome,
    string? Reason = null, string? Detail = null, string? SudoFallback = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ShimEnsureJson))]
public partial class ShimJsonContext : JsonSerializerContext;
