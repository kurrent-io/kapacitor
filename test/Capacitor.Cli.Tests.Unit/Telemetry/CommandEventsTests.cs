using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

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
