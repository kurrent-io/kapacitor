using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Gemini;

public class GeminiPathsTests {
    static GeminiPaths Gem(string home, string? geminiCliHome) => new(new(home), geminiCliHome);

    [Test]
    public async Task Root_gemini_cli_home_param_is_parent_of_dot_gemini() {
        await Assert.That(Gem("/fake/home", "/foo").Root)
            .IsEqualTo(Path.Combine("/foo", ".gemini"));
    }

    [Test]
    public async Task Root_defaults_to_dot_gemini_under_home() {
        await Assert.That(Gem("/fake/home", null).Root)
            .IsEqualTo(Path.Combine("/fake/home", ".gemini"));
    }

    // Bare: GEMINI_CLI_HOME is inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_GEMINI_CLI_HOME_as_the_parent_of_dot_gemini() {
        var parent = Path.Combine(Path.GetTempPath(), "kcap-gemini-cfg");

        using var env = EnvScope.Exclusive("GEMINI_CLI_HOME", parent);

        await Assert.That(GeminiPaths.FromEnvironment(new("/fake/home")).SettingsJson)
            .IsEqualTo(Path.Combine(parent, ".gemini", "settings.json"));
    }

    // The defunct GEMINI_HOME must NOT be honored.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_ignores_GEMINI_HOME() {
        using var cli = EnvScope.Exclusive("GEMINI_CLI_HOME", null);
        using var old = EnvScope.Exclusive("GEMINI_HOME", "/should/be/ignored");

        await Assert.That(GeminiPaths.FromEnvironment(new("/fake/home")).Root)
            .IsEqualTo(Path.Combine("/fake/home", ".gemini"));
    }

    [Test]
    public async Task GeminiMd_defaults_to_dot_gemini_under_home() {
        await Assert.That(Gem("/fake/home", null).GeminiMd)
            .IsEqualTo(Path.Combine("/fake/home", ".gemini", "GEMINI.md"));
    }

    [Test]
    public async Task GeminiMd_follows_GEMINI_CLI_HOME_relocation() {
        await Assert.That(Gem("/fake/home", "/foo").GeminiMd)
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
        await Assert.That(Gem(tmp.Path, "").IsInstalled).IsFalse();
    }

    [Test]
    [Arguments("settings.json")]
    [Arguments("projects.json")]
    public async Task IsInstalled_true_on_gemini_marker_file(string marker) {
        using var tmp = new TempDir();
        tmp.CreateFile([".gemini", marker], "{}");
        await Assert.That(Gem(tmp.Path, "").IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_on_tmp_recordings_dir() {
        using var tmp = new TempDir();
        tmp.CreateDir(".gemini", "tmp");
        await Assert.That(Gem(tmp.Path, "").IsInstalled).IsTrue();
    }
}
