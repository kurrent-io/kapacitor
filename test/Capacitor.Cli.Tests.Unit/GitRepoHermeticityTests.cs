using Capacitor.Tests.Helpers.Guards;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins the one guarantee <see cref="GitRepo"/> adds over a hand-rolled fixture: the machine's git
/// configuration cannot reach a fixture repository. Each test plants a hostile config where a real
/// developer's would sit and leaves the guard's <c>GIT_CONFIG_GLOBAL</c> pin alone — overriding the
/// pin instead would only prove git reads the file it is pointed at.
/// </summary>
[NotInParallel]
public class GitRepoHermeticityTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>Plants <paramref name="content"/> as the user-level git config, by every route git
    /// looks for one. Exclusive because a git child of any concurrently running test inherits these.
    /// </summary>
    Planted HostileUserConfig(string content) {
        var home = Tmp.CreateDir("hostile-home");

        home.CreateFile(".gitconfig", content);
        home.CreateFile(Path.Combine("xdg", "git", "config"), content);

        return new Planted(
            EnvScope.Exclusive("HOME", home),
            EnvScope.Exclusive("USERPROFILE", home),
            EnvScope.Exclusive("XDG_CONFIG_HOME", home.PathTo("xdg")));
    }

    /// <summary>The planted config's lifetime. Unwinds in reverse, so a key set twice restores to
    /// what it held before the first set.</summary>
    sealed class Planted(params EnvScope[] scopes) : IDisposable {
        public void Dispose() {
            for (var i = scopes.Length - 1; i >= 0; i--) scopes[i].Dispose();
        }
    }

    [Test]
    public async Task A_user_default_branch_does_not_reach_a_fixture_repo() {
        using var _ = HostileUserConfig("[init]\n\tdefaultBranch = trunk\n");
        using var repo = GitRepo.Create();

        await Assert.That(repo.CurrentBranch).IsEqualTo("main");
    }

    [Test]
    public async Task A_user_hooks_path_does_not_reach_a_fixture_repo() {
        using var _ = HostileUserConfig($"[core]\n\thooksPath = {Tmp.PathTo("hooks")}\n");
        using var repo = GitRepo.Create();

        await Assert.That(repo.Try("config", "--get", "core.hooksPath").ExitCode).IsNotEqualTo(0)
            .Because("the containment suites are meaningless if a personal hooksPath is in force");
    }

    [Test]
    public async Task A_user_clean_filter_does_not_reach_a_fixture_repo() {
        using var _ = HostileUserConfig("[filter \"evil\"]\n\tclean = ./tools/f\n");
        using var repo = GitRepo.Create();

        await Assert.That(repo.Try("config", "--name-only", "--get-regexp", "^filter\\.").Text)
            .IsEmpty();
    }

    /// <summary>The pin is exported, not merely honoured by the fixture's own invocations — this is
    /// what makes the production code's git children hermetic too.</summary>
    [Test]
    public async Task The_config_pin_is_exported_to_every_child() {
        await Assert.That(Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"))
            .IsEqualTo(GitConfigGlobalSetup.EmptyGlobalConfig);
        await Assert.That(Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM")).IsEqualTo("1");
        await Assert.That(Environment.GetEnvironmentVariable("GIT_TERMINAL_PROMPT")).IsEqualTo("0");
        // Command scope outranks the global file, so an inherited count would bypass the empty one.
        await Assert.That(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT")).IsNull();
    }

    /// <summary>The scrub reads the environment it is clearing, never the inherited count naming how
    /// far to look — a count near <c>int.MaxValue</c> would otherwise spin over billions of absent
    /// indices before a single test was discovered. Pinned through counts that disagree with what is
    /// actually present, so a count-driven scrub is caught without waiting on the hostile value.
    /// </summary>
    [Test]
    [Arguments("0")]
    [Arguments("bogus")]
    public async Task The_inherited_config_count_never_drives_the_scrub(string count) {
        using var _   = EnvScope.Exclusive("GIT_CONFIG_COUNT", count);
        using var key = EnvScope.Exclusive("GIT_CONFIG_KEY_0", "core.hooksPath");

        var indexed = GitConfigGlobalSetup.IndexedConfigVariables();

        await Assert.That(indexed).Contains("GIT_CONFIG_KEY_0");
        await Assert.That(indexed).DoesNotContain("GIT_CONFIG_VALUE_0");
    }

    /// <summary>An identity-free global is only safe because the fixture supplies one; a commit that
    /// depended on the machine's would fail here rather than on someone else's laptop.</summary>
    [Test]
    public async Task A_fixture_commit_needs_no_machine_identity() {
        using var _ = HostileUserConfig("");
        using var repo = GitRepo.Create();

        repo.CreateFile("README.md", "test");
        repo.CommitAll("initial");

        await Assert.That(repo.Head).IsNotEmpty();
        await Assert.That(repo.Status).IsEmpty();
    }
}
