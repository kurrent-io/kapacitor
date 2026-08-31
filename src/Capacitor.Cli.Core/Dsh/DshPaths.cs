namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Filesystem layout for DeepSeek Harness (dsh). dsh is a Cordis-based agent
/// whose session module declares "persistence is a plugin concern". The shipped kcap
/// Cordis plugin (<see cref="DshExtensionInstaller"/>) forwards every appended
/// <c>SessionEvent</c> to a per-session JSONL file under the kcap cache, which
/// <c>kcap watch --vendor dsh</c> tails and <c>kcap import --dsh</c> replays — one
/// server-side normalizer serves both feeds. This mirrors OpenCode's
/// <c>~/.cache/kcap/opencode/&lt;id&gt;.jsonl</c> layout exactly.
/// </summary>
public static class DshPaths {
    /// <summary>dsh's home dir (<c>$DSH_HOME</c>, else <c>~/.dsh</c>) — the Cordis profile +
    /// installed plugin live here.</summary>
    public static string DshHome(string? home = null) {
        var dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrEmpty(dshHome)) return dshHome;

        home ??= UserHome.FromEnvironment().Path;
        return Path.Combine(home, ".dsh");
    }

    /// <summary>Per-session transcript cache the kcap plugin writes and the watcher tails:
    /// <c>~/.cache/kcap/dsh</c> (flat <c>{id}.jsonl</c>). Matches the plugin's path verbatim
    /// (<c>homedir()/.cache/kcap/dsh</c>, independent of <c>$DSH_HOME</c>).</summary>
    public static string SessionsDir(string? home = null) {
        home ??= UserHome.FromEnvironment().Path;
        return Path.Combine(home, ".cache", "kcap", "dsh");
    }

    /// <summary>The per-session transcript file (<c>~/.cache/kcap/dsh/{id}.jsonl</c>).</summary>
    public static string SessionJsonl(string sessionId, string? home = null) =>
        Path.Combine(SessionsDir(home), $"{sessionId}.jsonl");

    /// <summary>kcap's Cordis plugin, installed into the dsh home
    /// (<c>$DSH_HOME/kcap-dsh.plugin.mjs</c>). Loaded by adding an entry to dsh's
    /// <c>cordis.yml</c> / profile config.</summary>
    public static string KcapPlugin(string? home = null) =>
        Path.Combine(DshHome(home), "kcap-dsh.plugin.mjs");

    /// <summary>Version marker beside the installed plugin (mirrors the OpenCode installer).</summary>
    public static string KcapPluginMarker(string? home = null) =>
        Path.Combine(DshHome(home), ".kcap-extension-version");

    /// <summary>dsh profiles root (<c>$DSH_HOME/profiles</c>). Each profile subdir has a
    /// <c>package.json</c> + a live-watched <c>cordis.patch.yml</c> where the plugin registers.</summary>
    public static string ProfilesDir(string? home = null) =>
        Path.Combine(DshHome(home), "profiles");

    /// <summary>A profile's user patch file (<c>&lt;profile&gt;/cordis.patch.yml</c>).</summary>
    public static string CordisPatch(string profileDir) =>
        Path.Combine(profileDir, "cordis.patch.yml");

    /// <summary>Detection: the dsh home exists (callers also OR
    /// <c>AgentDetector.IsInstalled("dsh")</c> for binary-name coverage).</summary>
    public static bool IsInstalled(string? home = null) => Directory.Exists(DshHome(home));

    // ── Pure (no ambient env) variants for the HarnessCatalog/AgentDetection snapshot ──
    // A null dshHome means genuinely unset (→ ~/.dsh under the injected home), never a re-read
    // of the real $DSH_HOME. Mirror the other vendors' *Pure helpers.

    public static string DshHomePure(string? home, string? dshHome) =>
        !string.IsNullOrEmpty(dshHome) ? dshHome : Path.Combine(home ?? UserHome.FromEnvironment().Path, ".dsh");

    public static string KcapPluginPure(string? home, string? dshHome) =>
        Path.Combine(DshHomePure(home, dshHome), "kcap-dsh.plugin.mjs");

    public static bool IsInstalledPure(string? home, string? dshHome) =>
        Directory.Exists(DshHomePure(home, dshHome));
}
