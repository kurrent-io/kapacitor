using Capacitor.Cli.Commands;
using Spectre.Console;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Regression tests for a crash reported during import:
///   System.InvalidOperationException: Could not find color or style 'Could'.
///   ...
///   at Spectre.Console.Markup..ctor(String, Nullable`1)
///   at Capacitor.Cli.Commands.ImportCommand.&lt;HandleImport&gt;b__63(...)
///
/// Cause: the per-session "Skipping" log built its markup with LITERAL
/// outer brackets — <c>$"... [{Markup.Escape(reason)}]"</c>. When a server
/// error surfaced as "Could not parse JSONL", Spectre saw the rendered
/// <c>[Could not parse JSONL]</c> as a markup tag and tried to resolve
/// "Could" as a color/style. <see cref="Markup.Escape"/> only escapes
/// characters INSIDE the substring; the literal brackets we wrap around it
/// must be doubled separately.
/// </summary>
public class ImportMarkupEscapeTests {
    [Test]
    [Arguments("Could not parse JSONL")]
    [Arguments("server unreachable: HTTP 502")]
    [Arguments("exit 1")]
    [Arguments("session-start failed: HTTP 500")]
    public async Task skipped_reason_markup_parses_without_throwing(string reason) {
        var markup = ImportCommand.FormatSkippedReasonMarkup("abc123", reason);

        // The Markup ctor parses the string. Before the fix, an unescaped
        // outer "[Could not parse JSONL]" was treated as a markup tag and
        // the ctor threw InvalidOperationException.
        _ = new Markup(markup);

        // After escaping, the literal "[reason]" survives intact in the
        // raw markup string (as "[[reason]]"), and the inner reason text
        // appears verbatim.
        await Assert.That(markup).Contains($"[[{reason}]]");
    }

    [Test]
    public async Task skipped_reason_markup_escapes_brackets_in_reason() {
        // A reason that itself contains square brackets must not break
        // either the inner escape (Markup.Escape) or the outer literal
        // brackets.
        var reason = "got [unexpected] token";
        var markup = ImportCommand.FormatSkippedReasonMarkup("abc123", reason);

        _ = new Markup(markup);
    }

    [Test]
    public async Task skipped_reason_markup_escapes_brackets_in_session_id() {
        // SessionId values are normally GUIDs but be defensive.
        var markup = ImportCommand.FormatSkippedReasonMarkup("weird[id]", "boom");

        _ = new Markup(markup);
    }

    [Test]
    [Arguments("new")]
    [Arguments("resuming from line 42")]
    public async Task loaded_summary_markup_parses_without_throwing(string verb) {
        var markup = ImportCommand.FormatLoadedSummaryMarkup("abc123", lines: 1234, verb: verb);

        _ = new Markup(markup);

        await Assert.That(markup).Contains($"[[{verb}]]");
        await Assert.That(markup).Contains("1234 lines");
    }
}
