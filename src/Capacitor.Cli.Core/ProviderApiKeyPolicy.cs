using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// Decides whether to keep provider API keys (<c>ANTHROPIC_API_KEY</c>,
/// <c>OPENAI_API_KEY</c>) in the spawn environment for headless agent CLIs.
///
/// <para>
/// Default behaviour scrubs them so subscription auth (claude.ai login,
/// ChatGPT account login) is used by <c>claude -p</c> / <c>codex exec</c>;
/// a globally-set key would otherwise override subscription auth and break
/// title generation / summaries.
/// </para>
///
/// <para>
/// Users on PAYG / API-key auth opt in via <c>use_provider_api_key: true</c>
/// on their active profile, or via <c>KCAP_USE_PROVIDER_API_KEY=1</c> as a
/// runtime escape hatch. The env var wins when set to a recognised value.
/// </para>
/// </summary>
public static class ProviderApiKeyPolicy {
    public const string EnvVarName = "KCAP_USE_PROVIDER_API_KEY";

    /// <summary>
    /// Convenience entry point: the env var, plus the profile the caller resolved.
    /// </summary>
    public static bool ShouldKeepProviderKey(Profile? profile) => ShouldKeepProviderKey(
        Environment.GetEnvironmentVariable(EnvVarName), profile);

    /// <summary>
    /// Pure resolver — exposed for testing.
    /// </summary>
    public static bool ShouldKeepProviderKey(string? envValue, Profile? profile) {
        if (TryParseBool(envValue) is { } envOverride) {
            return envOverride;
        }

        return profile?.UseProviderApiKey ?? false;
    }

    /// <summary>
    /// Parses a user-supplied truthy/falsy string. Returns <c>null</c> for
    /// unrecognised or empty input so callers can error out (e.g. CLI flag
    /// validation) instead of silently coercing to <c>false</c>.
    /// </summary>
    public static bool? TryParseBool(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch {
            "1" or "true"  or "yes" or "on"  => true,
            "0" or "false" or "no"  or "off" => false,
            _                                => null
        };
    }
}
