using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `kcap machine` — the help text is a DELIVERABLE here, not decoration, so it is tested like one.
///
/// <para>Someone provisioning a CI runner has exactly one chance to keep the secret. If the help does
/// not say that plainly, and say what to do when it is lost, the feature produces a support ticket
/// instead of a working runner. These tests pin the facts a reader cannot afford to miss — not the
/// prose around them, which is free to change.</para>
/// </summary>
public class MachineCommandTests {
    static string Help() => EmbeddedResources.Load("help-machine.txt");

    [Test]
    public async Task The_help_resource_is_embedded_and_reachable() {
        // Guards the wiring, not the words: an unembedded resource throws at RUNTIME in the AOT CLI,
        // and `kcap machine --help` is the first thing anyone runs.
        await Assert.That(Help()).IsNotEmpty();
        await Assert.That(Help()).StartsWith("kcap machine");
    }

    [Test]
    public async Task The_help_says_the_secret_is_shown_once_and_what_to_do_when_it_is_lost() {
        var help = Help();

        await Assert.That(help).Contains("SHOWN ONCE");
        await Assert.That(help).Contains("revoke the machine and")
            .Because("a reader who has lost the secret needs the recovery path, not just the bad news");
    }

    /// <summary>
    /// The four things a reader must be able to act on. Asserted individually so a failure names the
    /// missing one rather than saying "the help changed".
    /// </summary>
    [Test]
    [Arguments("KCAP_CLIENT_ID", "the public half the runner needs")]
    [Arguments("KCAP_CLIENT_SECRET", "the secret half the runner needs")]
    [Arguments("default_visibility", "how the machine's sessions become visible")]
    [Arguments("kcap machine revoke", "how to take a machine out of service")]
    public async Task The_help_covers_what_a_reader_has_to_do(string needle, string why) =>
        await Assert.That(Help()).Contains(needle).Because(why);

    /// <summary>
    /// Every subcommand the dispatcher accepts must appear in the help. Without this, adding one and
    /// forgetting to document it is invisible — the two are in different files with nothing joining
    /// them.
    /// </summary>
    [Test]
    [Arguments("create")]
    [Arguments("list")]
    [Arguments("revoke")]
    public async Task Every_subcommand_is_documented(string subcommand) =>
        await Assert.That(Help()).Contains($"  {subcommand}");

    /// <summary>
    /// Visibility is set ON THE MACHINE, not by this flag — the flag only tells you the value to use.
    /// That distinction is the one most likely to be misread, and misreading it means a runner records
    /// with the wrong audience and nobody notices until the sessions are already visible.
    /// </summary>
    [Test]
    public async Task The_help_explains_that_visibility_is_configured_on_the_machine() {
        var help = Help();

        await Assert.That(help).Contains("Why step 4 runs on the machine");
        await Assert.That(help).Contains("it does not configure the runner for you");
    }

    /// <summary>
    /// The stdout/stderr split is what makes piping the secret into a secret store safe, and it is not
    /// guessable — it has to be stated.
    /// </summary>
    [Test]
    public async Task The_help_documents_the_stdout_stderr_split() {
        var help = Help();

        await Assert.That(help).Contains("stdout");
        await Assert.That(help).Contains("stderr");
    }

    /// <summary>
    /// The listed visibility values must match what the command actually accepts. Two lists that must
    /// agree with nothing making them agree is the shape that rots — here the test IS the thing making
    /// them agree, because the help is a text file the compiler never reads.
    /// </summary>
    [Test]
    [Arguments("private")]
    [Arguments("org_public")]
    [Arguments("public")]
    public async Task Every_accepted_visibility_value_is_documented(string value) =>
        await Assert.That(Help()).Contains(value);

    /// <summary>
    /// Revocation's limits, stated. An operator responding to a leaked credential must know the old
    /// token keeps working until it expires, so they can decide whether to also delete the application
    /// in WorkOS. Leaving that out would let someone believe a revoke was instantaneous.
    /// </summary>
    [Test]
    public async Task The_help_states_what_revocation_does_not_do() {
        var help = Help();

        await Assert.That(help).Contains("until it expires");
        await Assert.That(help).Contains("WorkOS");
    }
}
