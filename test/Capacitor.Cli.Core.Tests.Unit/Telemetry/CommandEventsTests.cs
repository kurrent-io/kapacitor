using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class CommandEventsTests {
    [Test]
    [Arguments("hook")]
    [Arguments("watch")]
    [Arguments("mcp")]
    [Arguments("permission-request")]
    [Arguments("generate-whats-done")]
    [Arguments("set-title")]
    [Arguments("copilot-finalize")]
    [Arguments("cursor-verify-appendonly")]
    public async Task Machine_driven_verbs_are_not_reportable(string command) {
        await Assert.That(CommandEvents.IsReportable(command)).IsFalse();
    }

    [Test]
    [Arguments("setup")]
    [Arguments("recap")]
    [Arguments("daemon")]
    [Arguments("status")]
    [Arguments("import")]
    public async Task Human_verbs_are_reportable(string command) {
        await Assert.That(CommandEvents.IsReportable(command)).IsTrue();
    }

    // `uninstall` is human-invoked, unlike the rest of the denylist — it's excluded for a
    // different reason: Directory.Delete on the config dir happens BEFORE the ProcessExit
    // telemetry flush, and a failed POST spills to telemetry-spool.jsonl via
    // TelemetrySpool.Append, which Directory.CreateDirectory's the config dir right back into
    // existence. Program.cs already skips the update check for the identical reason.
    [Test]
    public async Task Uninstall_is_not_reportable() {
        await Assert.That(CommandEvents.IsReportable("uninstall")).IsFalse();
    }

    [Test]
    public async Task Known_subcommands_are_reported() {
        await Assert.That(CommandEvents.Subcommand("daemon", ["daemon", "start"])).IsEqualTo("start");
        await Assert.That(CommandEvents.Subcommand("plugin", ["plugin", "install"])).IsEqualTo("install");
        await Assert.That(CommandEvents.Subcommand("config", ["config", "set", "server_url", "https://acme.kcap.ai"])).IsEqualTo("set");
        await Assert.That(CommandEvents.Subcommand("curate", ["curate", "apply"])).IsEqualTo("apply");
    }

    [Test]
    public async Task Unknown_subcommand_token_is_dropped() {
        await Assert.That(CommandEvents.Subcommand("daemon", ["daemon", "frobnicate"])).IsNull();
    }

    // The whole point of the allowlist: verbs whose positional is user data.
    [Test]
    public async Task Session_ids_and_paths_in_positionals_never_survive() {
        await Assert.That(CommandEvents.Subcommand("recap", ["recap", "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("hide",  ["hide",  "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("ignore", ["ignore", Path.Combine("Users", "alexey", "secret")])).IsNull();
        await Assert.That(CommandEvents.Subcommand("remap", ["remap", "git@github.com:acme/private.git"])).IsNull();
    }

    [Test]
    public async Task Verbs_with_no_subcommands_report_none() {
        await Assert.That(CommandEvents.Subcommand("status", ["status"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("setup",  ["setup"])).IsNull();
    }

    [Test]
    public async Task Flags_are_collected_sorted_and_deduplicated() {
        var flags = CommandEvents.Flags(["setup", "--no-prompt", "--skip-codex-hooks", "--no-prompt"]);

        await Assert.That(flags.Length).IsEqualTo(2);
        await Assert.That(flags[0]).IsEqualTo("--no-prompt");
        await Assert.That(flags[1]).IsEqualTo("--skip-codex-hooks");
    }

    [Test]
    public async Task Flag_values_are_stripped_and_never_reported() {
        var flags = CommandEvents.Flags(["setup", "--server-url=https://internal.corp.example", "--server-url", "https://internal.corp.example"]);

        await Assert.That(flags.Length).IsEqualTo(1);
        await Assert.That(flags[0]).IsEqualTo("--server-url");
    }

    [Test]
    public async Task Non_flag_tokens_are_dropped_entirely() {
        var flags = CommandEvents.Flags(["recap", "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b", "some/path"]);

        await Assert.That(flags.Length).IsEqualTo(0);
    }

    // Regression: a --message/-m value is free-form user prose, and a message that happens to
    // start with "--" (e.g. someone pasting a real flag name as part of their bug report) must
    // never be captured as if it were a flag name in its own right — that would leak the message
    // text itself into telemetry, defeating the whole point of never capturing message content.
    [Test]
    public async Task Message_value_that_looks_like_a_flag_is_never_captured_short_form() {
        var flags = CommandEvents.Flags(["feedback", "--bug", "-m", "--looks-like-a-flag"]);

        await Assert.That(flags).Contains("--bug");
        await Assert.That(flags).DoesNotContain("--looks-like-a-flag");
        // "-m" itself was never captured before this fix either — short flags don't match
        // FlagShape's "--" prefix requirement — so this isn't a NEW gap introduced by the fix.
        await Assert.That(flags).DoesNotContain("-m");
    }

    [Test]
    public async Task Message_value_that_looks_like_a_flag_is_never_captured_long_form() {
        var flags = CommandEvents.Flags(["feedback", "--bug", "--message", "--looks-like-a-flag"]);

        // The flag NAME --message is still legitimate metadata and must still be reported —
        // only the value bound to it is data.
        await Assert.That(flags).Contains("--bug");
        await Assert.That(flags).Contains("--message");
        await Assert.That(flags).DoesNotContain("--looks-like-a-flag");
        await Assert.That(flags.Length).IsEqualTo(2);
    }

    // A --message value that does NOT look like a flag must obviously still be excluded (it never
    // starts with "--", so it was already excluded before this fix) — pinned here so a future
    // change to the skip mechanism can't accidentally start treating it as a flag some other way.
    [Test]
    public async Task Ordinary_message_value_is_still_excluded() {
        var flags = CommandEvents.Flags(["feedback", "--feedback", "--message", "the app crashed on startup"]);

        await Assert.That(flags).Contains("--feedback");
        await Assert.That(flags).Contains("--message");
        await Assert.That(flags.Length).IsEqualTo(2);
    }

    // Other verbs' flag extraction must be byte-for-byte unchanged by the value-flag skip —
    // none of their flags are in CommandEvents' ValueFlags set, so this is the same assertion
    // Flags_are_collected_sorted_and_deduplicated and Flag_values_are_stripped_and_never_reported
    // already make; repeated here explicitly as the regression check for this fix.
    [Test]
    public async Task Unrelated_verbs_flag_extraction_is_unchanged() {
        var setupFlags = CommandEvents.Flags(["setup", "--no-prompt", "--skip-codex-hooks", "--no-prompt"]);
        await Assert.That(setupFlags).IsEquivalentTo(["--no-prompt", "--skip-codex-hooks"]);

        var serverUrlFlags = CommandEvents.Flags(
            ["setup", "--server-url=https://internal.corp.example", "--server-url", "https://internal.corp.example"]);
        await Assert.That(serverUrlFlags).IsEquivalentTo(["--server-url"]);
    }

    // Shape rule: the pattern cannot express a path, URL, GUID, or email.
    [Test]
    [Arguments("--Bad-Upper")]
    [Arguments("--1leading-digit")]
    [Arguments("--has/slash")]
    [Arguments("--has.dot")]
    [Arguments("--has@at")]
    [Arguments("--")]
    [Arguments("-short")]
    [Arguments("--this-flag-name-is-far-too-long-to-be-a-real-flag-name")]
    public async Task Malformed_flag_shapes_are_rejected(string token) {
        await Assert.That(CommandEvents.Flags(["setup", token]).Length).IsEqualTo(0);
    }

    // GUIDs share this pattern's alphabet (lowercase hex + hyphen), so length bound alone rejects them.
    // A UUID is 36 hex+hyphen chars; with `--` prefix it's 38 total and exceeds the 37-char limit.
    [Test]
    public async Task Guid_shaped_tokens_are_rejected() {
        await Assert.That(CommandEvents.Flags(["setup", "--ab9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b"]).Length).IsEqualTo(0);
    }

    // The longest real kcap flag, and the floor of the shape rule's window. If this ever
    // stops matching, a real flag has silently vanished from telemetry with no error anywhere.
    [Test]
    public async Task Longest_real_flag_is_accepted() {
        var flags = CommandEvents.Flags(["setup", "--skip-antigravity-instructions"]);

        await Assert.That(flags.Length).IsEqualTo(1);
        await Assert.That(flags[0]).IsEqualTo("--skip-antigravity-instructions");
    }

    [Test]
    [Arguments("setup")]
    [Arguments("daemon")]
    [Arguments("status")]
    [Arguments("hook")] // known-but-denylisted verbs are still KNOWN verbs — the two lists are independent
    [Arguments("uninstall")]
    public async Task Known_verbs_report_themselves(string command) {
        await Assert.That(CommandEvents.ReportableCommand(command)).IsEqualTo(command);
    }

    // The allowlist half of the redaction guarantee: `Program.cs` falls through to
    // `Unknown command: {command}` for anything not in its dispatch switch, and args[0] is
    // arbitrary — a fat-fingered session GUID, an absolute path pasted a token early, a repo URL.
    // None of these are real verbs, so none may reach the `command` property verbatim.
    [Test]
    [Arguments("0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b")]
    [Arguments("/Users/me/work/acme-private")]
    [Arguments("git@github.com:acme/private.git")]
    public async Task Unrecognised_tokens_report_unknown(string command) {
        await Assert.That(CommandEvents.ReportableCommand(command)).IsEqualTo("unknown");
    }

    [Test]
    public async Task Flag_list_is_capped() {
        var many = new[] { "setup" }
            .Concat(Enumerable.Range(0, 40).Select(i => $"--flag-{i}"))
            .ToArray();

        await Assert.That(CommandEvents.Flags(many).Length).IsEqualTo(12);
    }
}
