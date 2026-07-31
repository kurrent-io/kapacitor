using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Both unit writers interpolate values into a format that performs its OWN expansion pass before the value
/// is read as data — cmd.exe expands <c>%VAR%</c> in a batch file, systemd expands <c>%n</c>-style specifiers
/// in a unit. A value carrying a literal percent therefore does not survive unless it is doubled, and a
/// value whose expansion produces punctuation can change the line's structure.
///
/// <para>These are round-trip and preservation tests rather than "contains" tests: the failure being guarded
/// is a value that reads back as something OTHER than what was captured.</para>
/// </summary>
public class UnitWriterExpansionTests {
    static ServiceSpec Spec() => new(
        "laptop", "/opt/kcap/kcap-daemon", "/home/u/.config/kcap/daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
        ["--max-agents", "8"]);

    // ── systemd: Environment= and Description= specifier expansion ──

    [Test]
    public async Task SystemdValue_doubles_a_literal_percent() {
        await Assert.That(ServiceText.SystemdValue("100%")).IsEqualTo("100%%");
    }

    /// <summary>`%n` is systemd's unit-name specifier: undoubled, the value silently becomes the unit name.</summary>
    [Test]
    public async Task SystemdValue_doubles_a_recognised_specifier() {
        await Assert.That(ServiceText.SystemdValue("/opt/%n/bin")).IsEqualTo("/opt/%%n/bin");
    }

    /// <summary>An UNrecognised specifier is worse than a rewrite: systemd refuses to load the unit at all.</summary>
    [Test]
    public async Task SystemdValue_doubles_an_unrecognised_specifier() {
        await Assert.That(ServiceText.SystemdValue("a%zb")).IsEqualTo("a%%zb");
    }

    [Test]
    public async Task Systemd_environment_value_carries_a_percent_doubled() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["PATH"] = "/opt/50%off/bin" },
        };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("Environment=PATH=/opt/50%%off/bin");
    }

    [Test]
    public async Task Systemd_description_carries_a_percent_doubled() {
        // ServiceId is sanitized, so it cannot itself hold a percent — drive the directive directly.
        await Assert.That(ServiceText.SystemdValue("kcap daemon (a%b)")).IsEqualTo("kcap daemon (a%%b)");
    }

    // ── systemd: ExecStart= specifier expansion, and its reversal ──

    [Test]
    public async Task Systemd_execstart_doubles_percent_in_the_binary_path() {
        var spec = Spec() with { DaemonBinaryPath = "/home/50%off/kcap-daemon" };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("ExecStart=/home/50%%off/kcap-daemon ");
    }

    [Test]
    public async Task Systemd_execstart_doubles_percent_in_the_log_path() {
        var spec = Spec() with { LogPath = "/var/log/100%/daemon.log" };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("--log-file /var/log/100%%/daemon.log");
    }

    /// <summary>
    /// `daemon doctor` reads the binary back out of the rendered unit, so the doubling MUST be reversed —
    /// otherwise the doctor reports a path that does not exist and the service looks broken when it is fine.
    /// </summary>
    [Test]
    public async Task BinaryFromUnit_round_trips_a_percent_path() {
        var spec = Spec() with { DaemonBinaryPath = "/home/50%off/kcap-daemon" };

        var recovered = SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(spec));

        await Assert.That(recovered).IsEqualTo("/home/50%off/kcap-daemon");
    }

    /// <summary>The quoted arm is a separate code path from the bare one — a space forces quoting.</summary>
    [Test]
    public async Task BinaryFromUnit_round_trips_a_percent_path_that_also_needs_quoting() {
        var spec = Spec() with { DaemonBinaryPath = "/home/50% off/kcap-daemon" };

        var unit      = SystemdUnit.Unit(spec);
        var recovered = SystemdUnit.BinaryFromUnit(unit);

        await Assert.That(unit).Contains("ExecStart=\"/home/50%% off/kcap-daemon\"");
        await Assert.That(recovered).IsEqualTo("/home/50% off/kcap-daemon");
    }

    // ── Windows: cmd.exe expansion on the exec line ──

    /// <summary>
    /// The env-var arm was already escaped; these three values share the same line and were not. `%` is a
    /// legal Windows filename character, so this is reachable through an ordinary directory name.
    /// </summary>
    [Test]
    public async Task Windows_wrapper_doubles_percent_in_the_log_path() {
        var spec = Spec() with { LogPath = @"C:\Users\u\50%off\daemon.log" };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains(@"--log-file ""C:\Users\u\50%%off\daemon.log""");
    }

    [Test]
    public async Task Windows_wrapper_doubles_percent_in_extra_args() {
        var spec = Spec() with { ExtraArgs = ["--tag", "100%"] };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("\"--tag\" \"100%%\"");
    }

    [Test]
    public async Task Windows_wrapper_doubles_percent_in_an_environment_value() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["PATH"] = @"C:\50%off" },
        };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains(@"set ""PATH=C:\50%%off""");
    }

    /// <summary>
    /// The structural case behind the escaping: a value shaped like a variable reference whose EXPANSION
    /// would close the quoted assignment and append a command. Doubled, cmd never expands it, so the
    /// assignment stays one token and the payload stays data.
    /// </summary>
    [Test]
    public async Task Windows_wrapper_neutralises_a_value_shaped_like_a_variable_reference() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["KCAP_URL"] = "%PAYLOAD%" },
        };

        var wrapper = WindowsTaskUnit.Wrapper(spec);

        await Assert.That(wrapper).Contains("set \"KCAP_URL=%%PAYLOAD%%\"");
        await Assert.That(wrapper).DoesNotContain("set \"KCAP_URL=%PAYLOAD%\"");
    }

    [Test]
    public async Task BinaryFromWrapper_round_trips_a_percent_path() {
        var spec = Spec() with { DaemonBinaryPath = @"C:\Program Files\50%off\kcap-daemon.exe" };

        var recovered = WindowsTaskUnit.BinaryFromWrapper(WindowsTaskUnit.Wrapper(spec));

        await Assert.That(recovered).IsEqualTo(@"C:\Program Files\50%off\kcap-daemon.exe");
    }

    // ── systemd: ExecStart carries a SECOND expansion (variables) and its own separator grammar ──

    /// <summary>
    /// systemd expands `$NAME`/`${NAME}` in an ExecStart command line — a different mechanism from the `%`
    /// specifiers, and one that also reaches the executable path.
    /// </summary>
    [Test]
    public async Task Systemd_execstart_doubles_a_dollar_in_the_binary_path() {
        var spec = Spec() with { DaemonBinaryPath = "/opt/${HOME}/kcap-daemon" };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("ExecStart=/opt/$${HOME}/kcap-daemon ");
    }

    [Test]
    public async Task Systemd_execstart_doubles_a_bare_dollar_variable() {
        var spec = Spec() with { ExtraArgs = ["--tag", "$HOME"] };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("--tag $$HOME");
    }

    [Test]
    public async Task BinaryFromUnit_round_trips_a_dollar_path() {
        var spec = Spec() with { DaemonBinaryPath = "/opt/${HOME}/kcap-daemon" };

        await Assert.That(SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(spec)))
            .IsEqualTo("/opt/${HOME}/kcap-daemon");
    }

    [Test]
    public async Task BinaryFromUnit_round_trips_a_path_with_both_percent_and_dollar() {
        var spec = Spec() with { DaemonBinaryPath = "/opt/50%$HOME/kcap-daemon" };

        await Assert.That(SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(spec)))
            .IsEqualTo("/opt/50%$HOME/kcap-daemon");
    }

    /// <summary>
    /// The asymmetry is deliberate: systemd expands specifiers in `Environment=` but NOT variables, so
    /// doubling `$` there would corrupt the value. Different sink, different escape.
    /// </summary>
    [Test]
    public async Task Systemd_environment_value_does_NOT_double_a_dollar() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["KCAP_URL"] = "http://h/$path" },
        };

        var unit = SystemdUnit.Unit(spec);

        await Assert.That(unit).Contains("Environment=KCAP_URL=http://h/$path");
        await Assert.That(unit).DoesNotContain("$$path");
    }

    /// <summary>A bare `;` is systemd's command separator: everything after it would run as a second command.</summary>
    [Test]
    public async Task Systemd_rejects_a_bare_semicolon_argument() {
        var spec = Spec() with { ExtraArgs = [";", "/bin/touch", "/tmp/pwned"] };

        var ex = Assert.Throws<InvalidOperationException>(() => SystemdUnit.Unit(spec));

        await Assert.That(ex!.Message).Contains("command separator");
    }

    /// <summary>A semicolon INSIDE a value is not a separator — systemd tokenizes on whitespace first.</summary>
    [Test]
    public async Task Systemd_allows_a_semicolon_inside_a_value() {
        var spec = Spec() with { ExtraArgs = ["--tag", "a;b"] };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("--tag a;b");
    }

    /// <summary>Quoting cannot contain a newline: the directive ends at the line break.</summary>
    [Test]
    [Arguments("a\nExecStartPre=/bin/touch /tmp/pwned")]
    [Arguments("a\rb")]
    public async Task Systemd_rejects_a_line_break_in_an_execstart_value(string bad) {
        var spec = Spec() with { ExtraArgs = ["--tag", bad] };

        var ex = Assert.Throws<InvalidOperationException>(() => SystemdUnit.Unit(spec));

        await Assert.That(ex!.Message).Contains("line break");
    }

    // ── Windows: cmd metacharacters, which %-doubling alone does not neutralise ──

    /// <summary>
    /// `foo&calc.exe` contains no space and no percent, so the old quote-when-it-has-a-space rule emitted it
    /// bare and cmd read `&` as a command separator — in a file the OS runs at every logon.
    /// </summary>
    [Test]
    [Arguments("8&calc.exe")]
    [Arguments("8|calc.exe")]
    [Arguments("8>out.txt")]
    [Arguments("8<in.txt")]
    [Arguments("8^&calc.exe")]
    [Arguments("(8)")]
    public async Task Windows_wrapper_quotes_an_argument_bearing_a_cmd_metacharacter(string arg) {
        var spec = Spec() with { ExtraArgs = ["--max-agents", arg] };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains($"\"{arg}\"");
    }

    /// <summary>A quote cannot be represented on the exec line at all — quoting it does not help.</summary>
    [Test]
    [Arguments("8\" & calc.exe & rem \"")]
    [Arguments("8\ncalc.exe")]
    [Arguments("8\rcalc.exe")]
    public async Task Windows_wrapper_rejects_an_unrepresentable_argument(string arg) {
        var spec = Spec() with { ExtraArgs = ["--max-agents", arg] };

        var ex = Assert.Throws<InvalidOperationException>(() => WindowsTaskUnit.Wrapper(spec));

        await Assert.That(ex!.Message).Contains("quote or newline");
    }

    [Test]
    public async Task Windows_wrapper_rejects_an_unrepresentable_log_path() {
        var spec = Spec() with { LogPath = "C:\\logs\\a\"b.log" };

        var ex = Assert.Throws<InvalidOperationException>(() => WindowsTaskUnit.Wrapper(spec));

        await Assert.That(ex!.Message).Contains("the log path");
    }

    /// <summary>
    /// A trailing backslash inside a quoted argument escapes the closing quote under the Windows argv rules,
    /// merging this argument with the next. Doubling the run is the documented encoding.
    /// </summary>
    [Test]
    public async Task Windows_wrapper_doubles_a_trailing_backslash_run() {
        var spec = Spec() with { ExtraArgs = ["--dir", "C:\\logs\\"] };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("\"C:\\logs\\\\\"");
    }

    [Test]
    public async Task Windows_wrapper_leaves_an_interior_backslash_alone() {
        var spec = Spec() with { ExtraArgs = ["--dir", "C:\\a\\b"] };

        await Assert.That(WindowsTaskUnit.Wrapper(spec)).Contains("\"C:\\a\\b\"");
    }

    // ── the matching narrowing at the source ──

    /// <summary>
    /// --max-agents is the only caller-supplied ExtraArgs entry and it is numeric, so the value is validated
    /// where it enters rather than only escaped where it lands. Independent of the sink hardening above.
    /// </summary>
    [Test]
    [Arguments("8&calc.exe")]
    [Arguments("8;calc")]
    [Arguments("abc")]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("8.5")]
    public async Task ServiceExtraArgs_rejects_a_non_positive_integer(string bad) {
        var ex = Assert.Throws<ArgumentException>(() => DaemonCommands.ServiceExtraArgs(bad));

        await Assert.That(ex!.Message).Contains("positive integer");
    }

    [Test]
    public async Task ServiceExtraArgs_accepts_an_integer() {
        await Assert.That(DaemonCommands.ServiceExtraArgs("8")).IsEquivalentTo(["--max-agents", "8"]);
    }

    [Test]
    public async Task ServiceExtraArgs_is_empty_when_the_flag_is_absent() {
        await Assert.That(DaemonCommands.ServiceExtraArgs(null)).IsEmpty();
    }

    // ── the execution MODE must be part of the artifact, not inherited from the machine ──

    /// <summary>
    /// `!NAME!` delayed expansion happens INSIDE double quotes, and CmdValue doubles `%`, not `!` — so
    /// quoting and percent-escaping both leave it live where a machine enables the mode by default.
    /// </summary>
    [Test]
    public async Task Windows_wrapper_disables_delayed_expansion() {
        await Assert.That(WindowsTaskUnit.Wrapper(Spec())).Contains("setlocal DisableDelayedExpansion");
    }

    /// <summary>It must come before any value is set, or the values it protects are already on the line.</summary>
    [Test]
    public async Task Windows_wrapper_disables_delayed_expansion_before_setting_anything() {
        var wrapper = WindowsTaskUnit.Wrapper(Spec());

        await Assert.That(wrapper.IndexOf("setlocal DisableDelayedExpansion", StringComparison.Ordinal))
            .IsLessThan(wrapper.IndexOf("set \"", StringComparison.Ordinal));
    }

    /// <summary>A bang-shaped value is carried literally rather than escaped — the mode is what neutralises it.</summary>
    [Test]
    public async Task Windows_wrapper_carries_a_bang_shaped_value_under_a_disabled_mode() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["KCAP_URL"] = "!PAYLOAD!" },
        };

        var wrapper = WindowsTaskUnit.Wrapper(spec);

        await Assert.That(wrapper).Contains("setlocal DisableDelayedExpansion");
        await Assert.That(wrapper).Contains("set \"KCAP_URL=!PAYLOAD!\"");
    }

    [Test]
    public async Task Task_xml_fixes_the_execution_mode_and_skips_autorun() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\u\.config\kcap\daemon-service-laptop.cmd");

        await Assert.That(xml).Contains("/d /s /v:off /c");
    }

    /// <summary>
    /// The nested-quote form: `/s` plus a command starting and ending in a quote makes cmd strip exactly the
    /// outer pair, so the inner pair survives to quote a path containing `&amp;`.
    /// </summary>
    [Test]
    public async Task Task_xml_double_quotes_the_wrapper_path() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\a&b\daemon-service-laptop.cmd");

        // The outer quote pair is literal text in the element (only < and & need escaping in XML content),
        // so cmd receives a command that both starts and ends with a quote — the /s precondition.
        await Assert.That(xml).Contains(@"/c """"C:\Users\a&amp;b\daemon-service-laptop.cmd""""");
    }

    [Test]
    public async Task Task_xml_accepts_a_wrapper_path_with_a_space_and_an_ampersand() {
        var xml = WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\a & b\daemon-service-laptop.cmd");

        await Assert.That(xml).Contains(@"a &amp; b\daemon-service-laptop.cmd");
    }

    /// <summary>
    /// `%` has no escape on a cmd COMMAND LINE — `%%` is a batch-file construct — so this sink refuses where
    /// the wrapper body escapes. Same character, two sinks, two correct treatments.
    /// </summary>
    [Test]
    public async Task Task_xml_rejects_a_percent_shaped_wrapper_path_component() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WindowsTaskUnit.TaskXml(Spec(), @"C:\Users\%USERNAME%\daemon-service-laptop.cmd"));

        await Assert.That(ex!.Message).Contains("percent sign");
    }

    [Test]
    [Arguments("C:\\Users\\a\"b\\w.cmd")]
    [Arguments("C:\\Users\\a\nb\\w.cmd")]
    public async Task Task_xml_rejects_a_structurally_unrepresentable_wrapper_path(string path) {
        var ex = Assert.Throws<InvalidOperationException>(() => WindowsTaskUnit.TaskXml(Spec(), path));

        await Assert.That(ex!.Message).Contains("quote or newline");
    }

    // ── systemd: the apostrophe is structural to its word lexer too ──

    /// <summary>
    /// Bare, an unpaired apostrophe opens a single-quoted string that never closes (unit unloadable); a
    /// paired one is stripped (value silently changed). `O'Reilly` in a home directory is the ordinary case.
    /// </summary>
    [Test]
    public async Task Systemd_quotes_an_execstart_value_containing_an_apostrophe() {
        var spec = Spec() with { DaemonBinaryPath = "/home/o'reilly/kcap-daemon" };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("ExecStart=\"/home/o'reilly/kcap-daemon\"");
    }

    [Test]
    public async Task BinaryFromUnit_round_trips_an_apostrophe_path() {
        var spec = Spec() with { DaemonBinaryPath = "/home/o'reilly/kcap-daemon" };

        await Assert.That(SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(spec)))
            .IsEqualTo("/home/o'reilly/kcap-daemon");
    }

    [Test]
    public async Task Systemd_quotes_an_environment_value_containing_an_apostrophe() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> { ["PATH"] = "/home/o'reilly/bin" },
        };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("Environment=\"PATH=/home/o'reilly/bin\"");
    }

    /// <summary>A PAIRED apostrophe is the silent-corruption case, and needs quoting just as much.</summary>
    [Test]
    public async Task Systemd_quotes_a_value_with_paired_apostrophes() {
        var spec = Spec() with { ExtraArgs = ["--tag", "'quoted'"] };

        await Assert.That(SystemdUnit.Unit(spec)).Contains("\"'quoted'\"");
    }
}
