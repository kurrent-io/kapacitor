using System.Collections;

namespace Capacitor.Cli.Services;

/// <summary>
/// Builds the environment baked into a service unit. Supervised jobs don't
/// inherit the interactive shell PATH (so bare claude/codex lookup fails) and a
/// baked --server-url would null out profile resolution — so we capture PATH +
/// the KCAP_* keys and pin the profile via KCAP_PROFILE.
/// </summary>
static class ServiceEnvironment {
    /// <summary>Variables carried from the installing shell into the service unit. No credentials —
    /// the unit is a file on disk.</summary>
    static readonly string[] Keys =
        ["PATH", "KCAP_PROFILE", "KCAP_URL", "KCAP_CLAUDE_PATH", "KCAP_CODEX_PATH",
         "KCAP_CONSENT_SEED_DEFAULT", "KCAP_EXPECT_SERVER_URL"];

    /// <summary>
    /// The unattended reviewers' opt-OUT switches. Carried on every platform: these are booleans, not
    /// secret-capable, so <see cref="GoogleSecretCapableKeys"/>'s exclusion does not apply.
    ///
    /// <para>Required because the daemon reads them from its own environment and nowhere else — no
    /// profile or config-file binding — so without this a supervised install silently drops them.</para>
    ///
    /// <para><b>These became opt-OUTs, which inverted what a miss here costs.</b> While they were opt-ins,
    /// dropping one meant a reviewer that could not be turned ON: annoying, and safe. Now it means a
    /// reviewer that cannot be turned OFF — the operator's only lever, unreachable, on the supported
    /// install path. That is the single compensating control the ungating rests on (see
    /// <c>DaemonRunner.ParseConsentFlag</c>), so this array is load-bearing for that argument rather
    /// than a convenience.</para>
    ///
    /// <para><b>DERIVED from <see cref="Core.GatedReviewers"/>, not listed again here</b> — the SAME rows
    /// the daemon's own apply loop iterates to build its config. This was three hand-maintained lists of
    /// one set (here, the daemon, and the affirm verb) with nothing making them agree; now there is one
    /// registry and the drift class is gone rather than tested for. Post-inversion that is what
    /// guarantees a newly-added reviewer arrives disableable: a row that reaches the daemon reaches the
    /// service unit by construction, and one the daemon has no accessor for fails the boot loudly
    /// instead of shipping an opt-out that does nothing.</para>
    ///
    /// <para><b>Capture FREEZES the value at install time</b> (what <see cref="CarriedConsentFlags"/>
    /// reports). A supervised daemon inherits nothing later, so an opt-out an operator exports after
    /// installing the service has no effect until the service is reinstalled.</para>
    /// </summary>
    internal static readonly string[] ReviewerConsentKeys =
        [.. Core.GatedReviewers.All.Select(r => r.EnableEnvVar)];

    /// <summary>
    /// Gemini's project/backend selection, carried on every platform because none of it is
    /// secret-capable: project ids, a region, and two backend-selection booleans.
    ///
    /// <para><b>Why this is needed at all.</b> A supervised daemon inherits nothing from an interactive
    /// shell — launchd passes no shell environment, and a non-interactive shell does not read a profile —
    /// so a project exported in <c>.zshrc</c> is invisible to a hosted Gemini agent. Gemini then reports
    /// the absence as <c>IneligibleTierError: … no longer supported for Gemini Code Assist for
    /// individuals</c>, thrown by a function named <c>throwIneligibleOrProjectIdError</c>: the same text
    /// for a missing project as for a real tier problem. That message sends people to the wrong place —
    /// it did exactly that while this was being specced.</para>
    ///
    /// <para>Both project spellings are carried because Gemini honours both; its own error says
    /// <i>"The GOOGLE_CLOUD_PROJECT (or GOOGLE_CLOUD_PROJECT_ID) environment variable must be set"</i>.</para>
    /// </summary>
    static readonly string[] GoogleConfigKeys = [
        "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_PROJECT_ID", "GOOGLE_CLOUD_LOCATION",
        "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_GENAI_USE_GCA",
        // Antigravity CLI's ADC switch. A boolean, not a credential — it selects the auth
        // MODE, and without it agy demands an interactive OAuth login even with ADC and a
        // project present. Belongs here rather than in the secret-capable list.
        "AGY_ADC_AUTH"
    ];

    /// <summary>
    /// Gemini's ADC path and endpoint overrides — carried off-Windows ONLY.
    ///
    /// <para>These are secret-CAPABLE rather than secret: a credential path discloses where the
    /// credential lives, and a base URL can carry userinfo or a query token. On Unix that is bounded by a
    /// guarantee this code enforces — <see cref="ServiceFiles"/> writes units 0600, re-checks the mode on
    /// the open handle because <c>UnixCreateMode</c> is filtered through the umask, and refuses a
    /// group/other-writable directory — so the reader is the same local user who owns the credential
    /// anyway.</para>
    ///
    /// <para><b>On Windows there is no such guarantee.</b> Every permission path in
    /// <see cref="ServiceFiles"/> returns early there: the wrapper carries whatever the user profile's
    /// inherited ACL grants, which this code neither sets nor checks. So these are excluded, following the
    /// <see cref="TokenCommandKey"/> precedent — a platform whose guarantee is unverified does not get the
    /// secret-capable values. Hosted Gemini still works there for a project-scoped login; an operator who
    /// needs Vertex-with-ADC on Windows sets it another way, and the README says so.</para>
    ///
    /// <para>The principle, so future additions are not classified by variable name: persist a value into
    /// a unit only when there is no non-persistent alternative AND the platform gives an owner-only
    /// guarantee this code actually enforces.</para>
    /// </summary>
    static readonly string[] GoogleSecretCapableKeys = [
        "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_GEMINI_BASE_URL", "GOOGLE_VERTEX_BASE_URL"
    ];

    /// <summary>Never carried, on any platform: these ARE the credential, and they have a non-persistent
    /// alternative (log the CLI in, or supply them per-launch). Named rather than merely absent so a
    /// future author adding a <c>GOOGLE_*</c> key has to decide which list it belongs in.</summary>
    internal static readonly string[] NeverCapturedKeys = ["GOOGLE_API_KEY", "GOOGLE_CREDENTIALS"];

    /// <summary>A COMMAND that prints a borrowed-review token — safe to persist where the token is not,
    /// and what lets a supervised daemon authenticate a contained reviewer.
    ///
    /// <para>Excluded on Windows. Borrowed review is macOS-only, so it would do nothing there, and the
    /// Windows unit is a <c>.cmd</c> wrapper emitting <c>set "K=V"</c> whose escaping does not cover a
    /// quote — a quoted command would corrupt the wrapper and could run unintended content. Carrying an
    /// inert variable into the one serializer that cannot hold it safely buys nothing.</para></summary>
    const string TokenCommandKey = "KCAP_COPILOT_TOKEN_CMD";

    /// <summary>Production entry point: capture from the current process env, completing the
    /// Antigravity ADC trio from gcloud's own on-disk state where the operator exported nothing —
    /// the same silent capture every other key gets. Install time is the one moment this is
    /// legitimate: the operator is running the command, and the daemon itself still never reads a
    /// credential location of its own accord.</summary>
    public static IReadOnlyDictionary<string, string> Capture(string? profileName, Core.ConfigRoot config, Core.UserHome home) =>
        Build(profileName, Snapshot(), config, OperatingSystem.IsWindows(),
            adcCredentialsPath: ExistingAdcPath(home),
            gcloudProject: GcloudConfig.DefaultProject(home));

    /// <summary>The ADC well-known path, only when the file is actually there — deriving the path
    /// without the file would bake a broken half-configuration.</summary>
    static string? ExistingAdcPath(Core.UserHome home) {
        try {
            var path = Path.Combine(home.Path, ".config", "gcloud", "application_default_credentials.json");

            return File.Exists(path) ? path : null;
        } catch {
            return null;
        }
    }

    static Dictionary<string, string> Snapshot() {
        var d = new Dictionary<string, string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    /// <summary>These two carry the exact-value contract (spec): an empty value is a deliberate
    /// refusal at the gate/daemon, distinct from the key being absent altogether — so unlike every
    /// other key here (which skips a present-but-empty value as if unset), these are baked
    /// VERBATIM whenever the key is present, empty or not. Silently dropping an empty directive
    /// on the way into the unit would let it vanish instead of failing closed.</summary>
    static readonly string[] BakeEvenEmptyKeys = ["KCAP_CONSENT_SEED_DEFAULT", "KCAP_EXPECT_SERVER_URL"];

    /// <summary>Pure: select the relevant keys from <paramref name="source"/>, pin the profile and the
    /// config root.</summary>
    /// <param name="isWindows">Platform, passed rather than probed so the exclusion is testable.</param>
    public static IReadOnlyDictionary<string, string> Build(
            string? profileName, IReadOnlyDictionary<string, string> source, Core.ConfigRoot config,
            bool isWindows = false, string? adcCredentialsPath = null, string? gcloudProject = null) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] keys = isWindows
            ? [.. Keys, .. ReviewerConsentKeys, .. GoogleConfigKeys]
            : [.. Keys, .. ReviewerConsentKeys, TokenCommandKey, .. GoogleConfigKeys, .. GoogleSecretCapableKeys];
        foreach (var key in keys) {
            if (!source.TryGetValue(key, out var v)) continue;
            if (!string.IsNullOrEmpty(v) || BakeEvenEmptyKeys.Contains(key)) env[key] = v;
        }
        if (!string.IsNullOrEmpty(profileName)) env["KCAP_PROFILE"] = profileName; // explicit pin wins

        // The Antigravity ADC trio, completed from gcloud's own state where the operator exported
        // nothing. Exported values always win — derivation fills silence, it never argues. POSIX
        // only: Windows carries no GOOGLE_APPLICATION_CREDENTIALS at all (no owner-only unit
        // guarantee), so the trio can never complete there and a derived half would only mislead.
        // Deriving into the UNIT (not the operator's shell) is load-bearing: AGY_ADC_AUTH=1 in an
        // interactive shell disables agy's hook capture for the operator's own sessions.
        if (!isWindows) {
            if (!env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS") && !string.IsNullOrEmpty(adcCredentialsPath))
                env["GOOGLE_APPLICATION_CREDENTIALS"] = adcCredentialsPath;

            // The flag rides the credential: without a reachable ADC file, AGY_ADC_AUTH=1 is a
            // broken half-configuration (agy fails auth outright).
            if (env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS") && !env.ContainsKey("AGY_ADC_AUTH"))
                env["AGY_ADC_AUTH"] = "1";

            if (!env.ContainsKey("GOOGLE_CLOUD_PROJECT") && !env.ContainsKey("GOOGLE_CLOUD_PROJECT_ID")
                && !string.IsNullOrEmpty(gcloudProject))
                env["GOOGLE_CLOUD_PROJECT"] = gcloudProject;
        }

        // From the installer's context rather than captured from its environment: a unit inherits
        // nothing, so a captured root would be baked only when one happened to be exported — and the
        // supervisor's own HOME would decide the rest.
        env[Core.ConfigRoot.ConfigDirEnvVar] = config.Directory;

        return env;
    }

    /// <summary>The hosted-agy auth trio's state in a built environment, for the install path to
    /// report: <c>AnyPresent</c> false means agy is simply not configured (nothing to say);
    /// <c>Missing</c> non-empty with <c>AnyPresent</c> true is a partial trio — never a working agy
    /// configuration, so always worth a warning. Either project spelling satisfies the project leg.</summary>
    internal static (bool AnyPresent, IReadOnlyList<string> Missing) AgyTrio(IReadOnlyDictionary<string, string> env) {
        var hasProject     = env.ContainsKey("GOOGLE_CLOUD_PROJECT") || env.ContainsKey("GOOGLE_CLOUD_PROJECT_ID");
        var hasFlag        = env.ContainsKey("AGY_ADC_AUTH");
        var hasCredentials = env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS");

        var missing = new List<string>();
        if (!hasProject)     missing.Add("GOOGLE_CLOUD_PROJECT");
        if (!hasFlag)        missing.Add("AGY_ADC_AUTH");
        if (!hasCredentials) missing.Add("GOOGLE_APPLICATION_CREDENTIALS");

        return (missing.Count < 3, missing);
    }

    /// <summary>
    /// Which consent flags a built environment carries, for the install path to report. Reads the BUILT
    /// environment, not the ambient one, so it cannot claim a capture that an empty value or a platform
    /// exclusion dropped on the way in.
    /// </summary>
    internal static IReadOnlyList<string> CarriedConsentFlags(IReadOnlyDictionary<string, string> env) =>
        [.. ReviewerConsentKeys.Where(env.ContainsKey)];
}
