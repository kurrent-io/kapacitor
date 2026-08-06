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
        ["PATH", "KCAP_CONFIG_DIR", "KCAP_PROFILE", "KCAP_URL", "KCAP_CLAUDE_PATH", "KCAP_CODEX_PATH"];

    /// <summary>
    /// The operator consent flags for the two gated unattended reviewers, carried on every platform.
    ///
    /// <para><b>Why they have to be here.</b> A supervised daemon inherits nothing from the installing
    /// shell, and these flags have no profile or config-file binding — the daemon reads them from its
    /// own environment and nowhere else. Without this, exporting one and reinstalling the service
    /// produced a unit that silently did not carry it, so the reviewer stayed off with no error and no
    /// log line: the same "shipped but unreachable" shape as having no binding at all, just moved from
    /// the daemon to the unit. That is what a supervised install is FOR, so it is not an edge case.</para>
    ///
    /// <para>Nothing here is secret-capable — each is a boolean an operator typed — so
    /// <see cref="GoogleSecretCapableKeys"/>'s platform exclusion does not apply.</para>
    ///
    /// <para><b>Capturing consent is not granting it.</b> These carry an EXISTING opt-in into the unit;
    /// they never create one, so a unit can only end up with a flag the installing environment already
    /// had. It is still a value frozen at install time rather than re-read per boot, which is why
    /// <see cref="CarriedConsentFlags"/> exists: the install path reports what it captured, so freezing
    /// a consent decision is something the operator watches happen rather than discovers later.</para>
    /// </summary>
    internal static readonly string[] ReviewerConsentKeys =
        ["KCAP_GEMINI_UNATTENDED_REVIEWER", "KCAP_KIRO_UNATTENDED_REVIEWER"];

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
        "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_GENAI_USE_GCA"
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

    /// <summary>Production entry point: capture from the current process env.</summary>
    public static IReadOnlyDictionary<string, string> Capture(string? profileName) =>
        Build(profileName, Snapshot(), OperatingSystem.IsWindows());

    static Dictionary<string, string> Snapshot() {
        var d = new Dictionary<string, string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    /// <summary>Pure: select the relevant keys from <paramref name="source"/>, pin the profile.</summary>
    /// <param name="isWindows">Platform, passed rather than probed so the exclusion is testable.</param>
    public static IReadOnlyDictionary<string, string> Build(
            string? profileName, IReadOnlyDictionary<string, string> source, bool isWindows = false) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] keys = isWindows
            ? [.. Keys, .. ReviewerConsentKeys, .. GoogleConfigKeys]
            : [.. Keys, .. ReviewerConsentKeys, TokenCommandKey, .. GoogleConfigKeys, .. GoogleSecretCapableKeys];
        foreach (var key in keys)
            if (source.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        if (!string.IsNullOrEmpty(profileName)) env["KCAP_PROFILE"] = profileName; // explicit pin wins
        return env;
    }

    /// <summary>
    /// The reviewer consent flags a built environment actually carries, for the install path to report.
    ///
    /// <para>Reads the BUILT environment rather than the ambient one, so it can only ever name a value
    /// that really was written into the unit — a message derived from the process environment could
    /// claim a capture that a platform exclusion or an empty value had dropped.</para>
    /// </summary>
    internal static IReadOnlyList<string> CarriedConsentFlags(IReadOnlyDictionary<string, string> env) =>
        [.. ReviewerConsentKeys.Where(env.ContainsKey)];
}
