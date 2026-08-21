using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// The plist writer's value guard must match the XML 1.0 character set, not approximate it.
///
/// <para>The first implementation used <c>char.IsControl</c>, which differs from XML 1.0 in BOTH directions —
/// it accepts non-characters and lone surrogates that XML cannot carry, and rejects C1 characters that XML
/// 1.0 permits. Each direction has its own consequence, so each gets its own test: an under-rejection writes
/// a plist <c>launchctl</c> will not load (or lets the encoder substitute a different character), while an
/// over-rejection refuses to install a service over a value that would have been fine.</para>
///
/// <para>Every character here is written as an escape. A literal control character in source is invisible to
/// review and survives a careless editor pass as something else.</para>
/// </summary>
public class XmlRepresentableValueTests {
    static void Check(string value) => ServiceText.RequireXmlRepresentableValue("KCAP_TEST", value);

    static async Task Rejects(string value, string because) {
        var ex = Assert.Throws<InvalidOperationException>(() => Check(value));

        await Assert.That(ex!.Message).Contains("KCAP_TEST").Because(because);
    }

    static async Task Accepts(string value) =>
        await Assert.That(() => Check(value)).ThrowsNothing();

    // ── accepted: ordinary text and the three legal whitespace controls ──

    [Test]
    public async Task Accepts_plain_text() => await Accepts("/usr/local/bin:/usr/bin");

    [Test]
    [Arguments('\t')]
    [Arguments('\n')]
    [Arguments('\r')]
    public async Task Accepts_the_three_legal_whitespace_controls(char c) => await Accepts($"a{c}b");

    // ── accepted: what char.IsControl wrongly rejected ──

    /// <summary>
    /// U+007F (DEL) and the C1 block are <c>char.IsControl</c> but sit inside XML 1.0's #x20–#xD7FF range, so
    /// rejecting them refused an install over a value XML can represent verbatim.
    /// </summary>
    [Test]
    [Arguments('\u007F')]
    [Arguments('\u0085')]
    [Arguments('\u009F')]
    public async Task Accepts_c1_characters_that_xml_1_0_permits(char c) => await Accepts($"a{c}b");

    /// <summary>A supplementary character arrives as a surrogate PAIR; judging each half alone rejects every emoji.</summary>
    [Test]
    public async Task Accepts_a_valid_supplementary_pair() => await Accepts("profile \U0001F600 ok");

    [Test]
    public async Task Accepts_a_supplementary_pair_at_the_end_of_the_value() => await Accepts("\U0001F600");

    /// <summary>U+FFFD is the last legal XML 1.0 character in the BMP.</summary>
    [Test]
    public async Task Accepts_the_top_of_the_bmp_range() => await Accepts("a\uFFFDb");

    // ── rejected: C0 controls, which have no XML 1.0 representation at all ──

    [Test]
    public async Task Rejects_a_c0_control() =>
        await Rejects("a\u0001b", "U+0001 cannot be represented in XML 1.0, escaped or not");

    [Test]
    public async Task Rejects_a_nul() =>
        await Rejects("a\0b", "a POSIX environment value can carry any byte except NUL — but not into a plist");

    [Test]
    public async Task Names_the_offending_code_point_in_the_message() {
        var ex = Assert.Throws<InvalidOperationException>(() => Check("a\u0001b"));

        await Assert.That(ex!.Message).Contains("U+0001");
    }

    // ── rejected: what char.IsControl wrongly accepted ──

    /// <summary>U+FFFE/U+FFFF are permanently reserved non-characters: legal in a .NET string, illegal in XML.</summary>
    [Test]
    [Arguments('\uFFFE')]
    [Arguments('\uFFFF')]
    public async Task Rejects_the_bmp_noncharacters(char c) =>
        await Rejects($"a{c}b", "a non-character is not a legal XML character despite not being a control");

    /// <summary>
    /// A lone surrogate is not a Unicode scalar value, so it cannot be encoded at all: depending on the
    /// encoder it throws or is silently replaced, which would change the value the service runs with.
    /// </summary>
    [Test]
    public async Task Rejects_a_lone_high_surrogate() =>
        await Rejects("a\uD83Db", "an unpaired high surrogate has no encoding");

    [Test]
    public async Task Rejects_a_lone_low_surrogate() =>
        await Rejects("a\uDE00b", "an unpaired low surrogate has no encoding");

    [Test]
    public async Task Rejects_a_high_surrogate_at_the_very_end() =>
        await Rejects("ab\uD83D", "there is no following char to pair with — the scan must not read past the end");

    /// <summary>Reversed halves are two lone surrogates, not a pair.</summary>
    [Test]
    public async Task Rejects_a_reversed_surrogate_pair() =>
        await Rejects("a\uDE00\uD83Db", "low-then-high is not a valid pair");

    /// <summary>
    /// The pair must be consumed as a UNIT. If the scan advanced by one after matching a pair, the low half
    /// would then be judged alone and rejected — so a value that is nothing but pairs proves the skip.
    /// </summary>
    [Test]
    public async Task Accepts_consecutive_supplementary_pairs() =>
        await Accepts("\U0001F600\U0001F601\U0001F602");

    /// <summary>And a rejectable character AFTER a pair must still be caught — the skip must not overshoot.</summary>
    [Test]
    public async Task Still_rejects_a_control_following_a_supplementary_pair() =>
        await Rejects("\U0001F600\u0001", "consuming the pair must not skip the character after it");

    // ── the guard must cover EVERY value the plist writer interpolates, not only the environment ──

    static ServiceSpec PlistSpec() => new(
        "laptop", "/opt/kcap/kcap-daemon", "/Users/u/.config/kcap/daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, ["--max-agents", "8"]);

    static async Task PlistRejects(ServiceSpec spec, string because) {
        var ex = Assert.Throws<InvalidOperationException>(() => LaunchdUnit.Plist(spec));

        await Assert.That(ex!.Message).Contains("U+0001").Because(because);
    }

    [Test]
    public async Task Plist_rejects_a_control_character_in_the_binary_path() =>
        await PlistRejects(PlistSpec() with { DaemonBinaryPath = "/opt/kcap/da\u0001emon" },
            "ProgramArguments carries the binary path and SecurityElement.Escape passes controls through");

    [Test]
    public async Task Plist_rejects_a_control_character_in_the_log_path() =>
        await PlistRejects(PlistSpec() with { LogPath = "/Users/u/lo\u0001gs/d.log" },
            "the log path reaches both ProgramArguments and StandardOutPath");

    [Test]
    public async Task Plist_rejects_a_control_character_in_an_extra_arg() =>
        await PlistRejects(PlistSpec() with { ExtraArgs = ["--tag", "a\u0001b"] },
            "extra args are interpolated the same way");

    [Test]
    public async Task Plist_still_rejects_a_control_character_in_an_environment_value() =>
        await PlistRejects(
            PlistSpec() with { Environment = new Dictionary<string, string> { ["PATH"] = "/u\u0001b" } },
            "the arm that was already guarded must stay guarded");

    /// <summary>A plist whose values are all legal must still render — the guard is not a blanket refusal.</summary>
    [Test]
    public async Task Plist_renders_when_every_value_is_representable() {
        var plist = LaunchdUnit.Plist(PlistSpec() with { LogPath = "/Users/u/logs/\U0001F600.log" });

        await Assert.That(plist).Contains("<key>ProgramArguments</key>");
        await Assert.That(plist).Contains("\U0001F600.log");
    }
}
