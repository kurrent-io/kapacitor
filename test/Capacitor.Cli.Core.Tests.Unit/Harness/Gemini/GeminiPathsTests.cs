using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Gemini;

public class GeminiPathsTests {
    // Parallel-safe: the override param is non-null, so no env var is read.
    [Test]
    public async Task Root_gemini_cli_home_param_is_parent_of_dot_gemini() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: "/foo"))
            .IsEqualTo(Path.Combine("/foo", ".gemini"));
    }

    [Test]
    public async Task Root_defaults_to_dot_gemini_under_home() {
        await Assert.That(GeminiPaths.Root(home: "/fake/home", geminiCliHome: null))
            .IsEqualTo(Path.Combine("/fake/home", ".gemini"));
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Root_reads_GEMINI_CLI_HOME_and_ignores_GEMINI_HOME() {
        var originalCli = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        var originalOld = Environment.GetEnvironmentVariable("GEMINI_HOME");
        try {
            // The defunct GEMINI_HOME must NOT be honored.
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", "/should/be/ignored");
            await Assert.That(GeminiPaths.Root(home: "/fake/home"))
                .IsEqualTo(Path.Combine("/fake/home", ".gemini"));

            // GEMINI_CLI_HOME is the parent of .gemini, and SettingsJson follows.
            var parent = Path.Combine(Path.GetTempPath(), "kcap-gemini-cfg");
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", parent);
            await Assert.That(GeminiPaths.Root()).IsEqualTo(Path.Combine(parent, ".gemini"));
            await Assert.That(GeminiPaths.SettingsJson())
                .IsEqualTo(Path.Combine(parent, ".gemini", "settings.json"));
        } finally {
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", originalCli);
            Environment.SetEnvironmentVariable("GEMINI_HOME", originalOld);
        }
    }

    [Test]
    public async Task GeminiMd_defaults_to_dot_gemini_under_home() {
        await Assert.That(GeminiPaths.GeminiMd(home: "/fake/home", geminiCliHome: null))
            .IsEqualTo(Path.Combine("/fake/home", ".gemini", "GEMINI.md"));
    }

    [Test]
    public async Task GeminiMd_follows_GEMINI_CLI_HOME_relocation() {
        await Assert.That(GeminiPaths.GeminiMd(home: "/fake/home", geminiCliHome: "/foo"))
            .IsEqualTo(Path.Combine("/foo", ".gemini", "GEMINI.md"));
    }

    // ~/.gemini is shared with Google Antigravity — an Antigravity-only
    // home must NOT read as a Gemini install, but a real Gemini marker still must.
    [Test]
    public async Task IsInstalled_false_when_only_antigravity_present() {
        using var tmp = new TempDir();
        // Antigravity-only: ~/.gemini exists but holds only antigravity subdirs.
        tmp.CreateDir(".gemini", "antigravity", "brain");
        tmp.CreateDir(".gemini", "antigravity-cli");
        await Assert.That(GeminiPaths.IsInstalled(home: tmp.Path, geminiCliHome: "")).IsFalse();
    }

    [Test]
    [Arguments("settings.json")]
    [Arguments("projects.json")]
    public async Task IsInstalled_true_on_gemini_marker_file(string marker) {
        using var tmp = new TempDir();
        tmp.CreateFile([".gemini", marker], "{}");
        await Assert.That(GeminiPaths.IsInstalled(home: tmp.Path, geminiCliHome: "")).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_on_tmp_recordings_dir() {
        using var tmp = new TempDir();
        tmp.CreateDir(".gemini", "tmp");
        await Assert.That(GeminiPaths.IsInstalled(home: tmp.Path, geminiCliHome: "")).IsTrue();
    }
}
