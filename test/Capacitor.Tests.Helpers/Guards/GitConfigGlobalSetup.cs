namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin making every git this process starts hermetic — the fixtures' invocations and
/// the production code's children alike. Unpinned, the machine's <c>~/.gitconfig</c> decides
/// <c>init.defaultBranch</c>, can sign or hang a commit through <c>commit.gpgsign</c>, and supplies
/// <c>core.hooksPath</c> and clean/smudge filters to the suites written to prove those are contained.
///
/// <para>The pinned global carries no author identity: <see cref="GitRepo"/> writes one per
/// repository, and production code that commits passes its own.</para>
/// </summary>
public class GitConfigGlobalSetup {
    static readonly TempDir Dir = new("gitconfig");

    static readonly List<(string Key, string? Inherited)> Pinned = [];

    /// <summary>An existing but empty file. Git reads a missing path as empty config too, but only a
    /// real file makes "the global config is empty" a state a test can rely on.</summary>
    public static string EmptyGlobalConfig => Path.Combine(Dir.Path, "gitconfig");

    [BeforeEvery(Assembly)]
    public static void PinGitConfig() {
        Dir.CreateFile("gitconfig");
        Pinned.Clear();

        Pin("GIT_CONFIG_GLOBAL", EmptyGlobalConfig);
        Pin("GIT_CONFIG_NOSYSTEM", "1");
        Pin("GIT_TERMINAL_PROMPT", "0");

        // Command-scope config, which outranks the global file. WorktreeManager passes its own
        // overrides this way and APPENDS to whatever it inherits, so an ambient count reaches every
        // git the suite starts and an empty global would not stop it.
        foreach (var key in IndexedConfigVariables()) Pin(key, null);

        Pin(CountVariable, null);
    }

    [AfterEvery(Assembly)]
    public static void UnpinGitConfig() {
        // Restored, not cleared: these are process-wide, and a host that had them set keeps them.
        foreach (var (key, inherited) in Pinned) Environment.SetEnvironmentVariable(key, inherited);

        Pinned.Clear();
        Dir.Dispose();
    }

    const string CountVariable = "GIT_CONFIG_COUNT";

    /// <summary>The indexed entries the environment actually holds. Read from the environment rather
    /// than counted up to <c>GIT_CONFIG_COUNT</c>: that count is inherited, and a value near
    /// <c>int.MaxValue</c> would spin here over billions of absent indices before any test runs.
    /// Enumerating also covers a sparse or malformed set, which counting would miss.</summary>
    public static List<string> IndexedConfigVariables() =>
        Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(key => key.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                       || key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal))
            .ToList();

    static void Pin(string key, string? value) {
        Pinned.Add((key, Environment.GetEnvironmentVariable(key)));
        Environment.SetEnvironmentVariable(key, value);
    }
}
