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
        var keys = isWindows ? Keys : [.. Keys, TokenCommandKey];
        foreach (var key in keys)
            if (source.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) env[key] = v;
        if (!string.IsNullOrEmpty(profileName)) env["KCAP_PROFILE"] = profileName; // explicit pin wins
        return env;
    }
}
