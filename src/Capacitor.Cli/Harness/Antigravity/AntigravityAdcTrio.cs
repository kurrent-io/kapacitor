namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>
/// The three environment variables a daemon-hosted agy needs, and how a service install completes
/// them. A hosted launch redirects HOME, so ADC's well-known location is invisible to the child and
/// the credential path has to be explicit; the flag selects the auth mode, without which agy demands
/// an interactive login it has no stdin for.
///
/// <para>Deriving belongs to the installer the operator is running, never to the daemon, which goes
/// looking for a credential nowhere. And it lands in the unit only: <c>AGY_ADC_AUTH=1</c> exported
/// in an interactive shell disables agy's own hook capture.</para>
/// </summary>
static class AntigravityAdcTrio {
    internal const string ProjectKey     = "GOOGLE_CLOUD_PROJECT";
    internal const string FlagKey        = "AGY_ADC_AUTH";
    internal const string CredentialsKey = "GOOGLE_APPLICATION_CREDENTIALS";

    /// <summary>agy reads the flag as the literal <c>1</c>; anything else leaves it on interactive
    /// OAuth. Reported as <c>AGY_ADC_AUTH=1</c> so a captured <c>0</c> names its own remedy.</summary>
    const string FlagEnabled = "1";

    /// <summary>The ADC well-known path, only when the file is actually there — deriving the path
    /// without the file would bake a broken half-configuration. Never throws.</summary>
    public static string? ExistingCredentialsPath(Core.UserHome home) {
        try {
            var path = Path.Combine(home.Path, ".config", "gcloud", "application_default_credentials.json");

            return File.Exists(path) ? path : null;
        } catch {
            return null;
        }
    }

    /// <summary>Fills whatever the operator's shell left silent. Exported values always win —
    /// derivation completes a configuration, it never argues with one. The canonical project spelling
    /// is derived even when <c>GOOGLE_CLOUD_PROJECT_ID</c> is exported: that alternate is a Gemini
    /// affordance, and agy reads only the canonical key.</summary>
    public static void Complete(
            IDictionary<string, string> env, string? credentialsPath, string? gcloudProject) {
        if (!env.ContainsKey(CredentialsKey) && !string.IsNullOrEmpty(credentialsPath))
            env[CredentialsKey] = credentialsPath;

        // The flag rides the credential: without a reachable ADC file, AGY_ADC_AUTH=1 is a broken
        // half-configuration (agy fails auth outright).
        if (env.ContainsKey(CredentialsKey) && !env.ContainsKey(FlagKey))
            env[FlagKey] = FlagEnabled;

        if (!env.ContainsKey(ProjectKey) && !string.IsNullOrEmpty(gcloudProject))
            env[ProjectKey] = gcloudProject;
    }

    /// <summary>The trio's state in a built environment, for the install path to report:
    /// <c>AnyPresent</c> false means agy is simply not configured (nothing to say); <c>Missing</c>
    /// non-empty with <c>AnyPresent</c> true is a partial trio — never a working agy configuration,
    /// so always worth a warning.</summary>
    public static (bool AnyPresent, IReadOnlyList<string> Missing) Status(
            IReadOnlyDictionary<string, string> env) {
        var missing = new List<string>();
        if (!env.ContainsKey(ProjectKey))                                        missing.Add(ProjectKey);
        if (!env.TryGetValue(FlagKey, out var flag) || flag.Trim() != FlagEnabled) missing.Add($"{FlagKey}={FlagEnabled}");
        if (!env.ContainsKey(CredentialsKey))                                    missing.Add(CredentialsKey);

        // Keyed on presence, not on the values above: an operator who exported AGY_ADC_AUTH=0 has
        // configured agy, wrongly, and needs the warning rather than silence.
        var anyPresent = env.ContainsKey(ProjectKey) || env.ContainsKey(FlagKey) || env.ContainsKey(CredentialsKey);

        return (anyPresent, missing);
    }
}
