// src/Capacitor.Cli.Core/Acp/AcpModelResolver.cs
namespace Capacitor.Cli.Core.Acp;

/// <summary>
/// Pure model-resolution helper shared by both wire model selectors
/// (<c>session/set_config_option</c> for Cursor/Copilot, <c>session/set_model</c> for Kiro). The
/// wire requires an EXACT <c>modelId</c> from <c>session/new</c>'s
/// <c>result.models.availableModels</c>. Cursor's ids are parameterized (e.g.
/// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>), so a bare family name like
/// <c>claude-sonnet-4-5</c> (the shape both <c>RuntimeStartContext.Model</c> and
/// <c>DaemonConfig.CursorModel</c>'s default are typically given in) is not itself a valid wire
/// value there; Kiro's ids are already bare (e.g. <c>claude-haiku-4.5</c>) and pass through the
/// exact-match arm. This resolves the caller's requested string against that list; the caller
/// (<c>AcpHostedAgentRuntime.StartAsync</c>) treats a <see langword="null"/> result as "skip the
/// selector's RPC — use the vendor's own default model" rather than a failure.
/// </summary>
public static class AcpModelResolver {
    /// <summary>
    /// Resolves <paramref name="requested"/> to an exact <see cref="AvailableModelDto.ModelId"/>
    /// using first-hit-wins precedence, all comparisons case-insensitive (no vendor's family-name
    /// casing convention is pinned by probe):
    /// <list type="number">
    /// <item><description>an exact <see cref="AvailableModelDto.ModelId"/> match;</description></item>
    /// <item><description>the first <see cref="AvailableModelDto.ModelId"/> that starts with
    /// <paramref name="requested"/> — handles a bare family prefix (e.g. <c>claude-sonnet-4-5</c>)
    /// resolving to the parameterized id (e.g. <c>claude-sonnet-4-5[thinking=true,context=200k]</c>);</description></item>
    /// <item><description>the first entry whose <see cref="AvailableModelDto.Name"/> equals or
    /// contains <paramref name="requested"/>.</description></item>
    /// </list>
    /// Returns <see langword="null"/> (no match — or nothing was requested, or no models were
    /// offered) so the caller can fall back to the vendor's own default model rather than fail the
    /// launch.
    /// </summary>
    public static string? Resolve(string? requested, IReadOnlyList<AvailableModelDto>? availableModels) {
        if (string.IsNullOrWhiteSpace(requested) || availableModels is not { Count: > 0 })
            return null;

        foreach (var model in availableModels) {
            if (string.Equals(model.ModelId, requested, StringComparison.OrdinalIgnoreCase))
                return model.ModelId;
        }

        foreach (var model in availableModels) {
            if (model.ModelId.StartsWith(requested, StringComparison.OrdinalIgnoreCase))
                return model.ModelId;
        }

        foreach (var model in availableModels) {
            if (model.Name is { Length: > 0 } name && name.Contains(requested, StringComparison.OrdinalIgnoreCase))
                return model.ModelId;
        }

        return null;
    }
}
