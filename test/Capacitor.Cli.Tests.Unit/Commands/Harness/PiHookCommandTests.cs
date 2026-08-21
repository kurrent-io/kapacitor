using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>
/// Unit tests for <see cref="PiHookCommand.ExtractSessionId"/> (PR #162 review).
/// Pi can hand the extension the session file before its header line is flushed,
/// and names files <c>&lt;timestamp&gt;_&lt;uuid&gt;.jsonl</c>, so the session id
/// must be recoverable from the filename suffix when the header isn't readable.
/// </summary>
public class PiHookCommandTests {
    const string Uuid     = "11111111-2222-3333-4444-555555555555";
    const string Dashless = "11111111222233334444555555555555";

    [Test]
    public async Task ExtractSessionId_PrefersHeaderUuid_OverFilename() {
        var id = PiHookCommand.ExtractSessionId(
            "/x/2026-06-12T10-00-00_99999999-9999-9999-9999-999999999999.jsonl", Uuid);
        await Assert.That(id).IsEqualTo(Dashless);
    }

    [Test]
    public async Task ExtractSessionId_FallsBackToFilenameSuffix_WhenHeaderMissing() {
        // Header not yet flushed at session_start → parse the uuid from the name.
        var id = PiHookCommand.ExtractSessionId($"/x/2026-06-12T10-00-00_{Uuid}.jsonl", null);
        await Assert.That(id).IsEqualTo(Dashless);
    }

    [Test]
    public async Task ExtractSessionId_HandlesPlainUuidFilename() {
        var id = PiHookCommand.ExtractSessionId($"/x/{Uuid}.jsonl", null);
        await Assert.That(id).IsEqualTo(Dashless);
    }

    [Test]
    public async Task ExtractSessionId_ReturnsNull_ForNonPiFilename() {
        await Assert.That(PiHookCommand.ExtractSessionId("/x/notes.jsonl", null)).IsNull();
        // Whitespace/empty header is ignored; falls through to the (non-uuid) stem.
        await Assert.That(PiHookCommand.ExtractSessionId("/x/notes.jsonl", "")).IsNull();
    }
}
