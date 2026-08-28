namespace Capacitor.Cli.Core.Tests.Unit;

// Only the Windows leg discriminates here: measured, GetFolderPath(UserProfile) reads HOME live on
// Unix, so both branches return the same string and a green Mac run proves nothing about the rule.
//
// Bare: HOME is read by every path helper and inherited by every spawned child, so no cohort of
// key-holders can exclude its readers.
[NotInParallel]
public class UserHomeTests {
    [Test]
    public async Task Rooted_HOME_wins_over_the_user_profile() {
        using var tmp  = new TempDir();
        using var home = EnvScope.Exclusive("HOME", tmp.Path);

        await Assert.That(UserHome.FromEnvironment().Path).IsEqualTo(tmp.Path);
    }

    // Asserted as "not the unusable input" rather than against a fresh UserProfile read: on Unix that
    // read consults HOME too, and the fallback is legitimately "" on a Mac and the passwd home on
    // Linux. There are only two branches, so any other value proves the fallback fired.
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("foo/bar")]
    public async Task Unrooted_or_blank_HOME_falls_back(string unusable) {
        using var home = EnvScope.Exclusive("HOME", unusable);

        await Assert.That(UserHome.FromEnvironment().Path).IsNotEqualTo(unusable);
    }
}
