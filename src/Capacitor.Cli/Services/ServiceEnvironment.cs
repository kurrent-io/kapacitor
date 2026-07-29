using System.Collections;

namespace Capacitor.Cli.Services;

/// <summary>
/// Builds the environment baked into a service unit. Supervised jobs don't
/// inherit the interactive shell PATH (so bare claude/codex lookup fails) and a
/// baked --server-url would null out profile resolution — so we capture PATH +
/// the KCAP_* keys and pin the profile via KCAP_PROFILE.
/// </summary>
static class ServiceEnvironment {
    /// <summary>Variables carried from the installing shell into the service unit.
    ///
    /// <para>Credentials are deliberately absent: the unit is a file on disk, so a token in this list
    /// would be a token at rest. <c>KCAP_COPILOT_TOKEN_CMD</c> is the exception that proves the rule —
    /// it is a COMMAND that prints a token, not the token, so it is safe to persist and is what lets a
    /// supervised daemon authenticate a contained borrowed reviewer at all.</para></summary>
    static readonly string[] Keys = [
        "PATH", "KCAP_CONFIG_DIR", "KCAP_PROFILE", "KCAP_URL", "KCAP_CLAUDE_PATH", "KCAP_CODEX_PATH",
        "KCAP_COPILOT_TOKEN_CMD"
    ];

    /// <summary>Production entry point: capture from the current process env.</summary>
    public static IReadOnlyDictionary<string, string> Capture(string? profileName) =>
        Build(profileName, Snapshot());

    static Dictionary<string, string> Snapshot() {
        var d = new Dictionary<string, string>();
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    /// <summary>Pure: select the relevant keys from <paramref name="source"/>, pin the profile.</summary>
    public static IReadOnlyDictionary<string, string> Build(string? profileName, IReadOnlyDictionary<string, string> source) {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys)
            if (source.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        if (!string.IsNullOrEmpty(profileName)) env["KCAP_PROFILE"] = profileName; // explicit pin wins
        return env;
    }
}
