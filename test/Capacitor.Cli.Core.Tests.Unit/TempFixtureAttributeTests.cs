namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The injected-fixture contract the suites depend on.</summary>
public class TempFixtureAttributeTests {
    [TempDir]           public required TempDir         Tmp     { get; init; }
    [TempDir]           public required TempDir         Second  { get; init; }
    [TempDir("pinned")] public required TempDir         Hinted  { get; init; }
    [TempDaemonPaths]   public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Injected_fixtures_are_live_directories_named_after_the_suite() {
        await Assert.That(Directory.Exists(Tmp.Path)).IsTrue();
        await Assert.That(Directory.Exists(Daemons.Directory)).IsTrue();

        await Assert.That(Path.GetFileName(Tmp.Path)).StartsWith("kcap-test-tempfixtureattribute-");
        await Assert.That(Path.GetFileName(Daemons.Directory)).StartsWith("kcap-test-tempfi-");
    }

    [Test]
    public async Task An_explicit_hint_names_the_directory_instead_of_the_suite() {
        await Assert.That(Path.GetFileName(Hinted.Path)).StartsWith("kcap-test-pinned-");
    }

    [Test]
    public async Task Two_properties_of_the_same_type_get_separate_directories() {
        await Assert.That(Second.Path).IsNotEqualTo(Tmp.Path);

        Tmp.CreateFile("only-in-first.txt");

        await Assert.That(File.Exists(Second.PathTo("only-in-first.txt"))).IsFalse();
    }

    [Test]
    public async Task Each_test_gets_its_own_directories() {
        // Two tests in one class: were the default lifetime ever widened, one would see the other's file.
        Tmp.CreateFile("marker-b.txt");
        Daemons.CreateFile("marker-b.txt");

        await Assert.That(Directory.GetFiles(Tmp.Path)).Count().IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Daemons.Directory)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Each_test_gets_its_own_directories_again() {
        Tmp.CreateFile("marker-c.txt");
        Daemons.CreateFile("marker-c.txt");

        await Assert.That(Directory.GetFiles(Tmp.Path)).Count().IsEqualTo(1);
        await Assert.That(Directory.GetFiles(Daemons.Directory)).Count().IsEqualTo(1);
    }
}
