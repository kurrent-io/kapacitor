using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptProjectionTests {
    [Test]
    public async Task The_registry_resolves_each_vendor_ignoring_case_to_its_singleton() {
        await Assert.That(TranscriptProjection.For("claude")).IsSameReferenceAs(ClaudeTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("Claude")).IsSameReferenceAs(ClaudeTranscriptEvents.Instance);
        await Assert.That(TranscriptProjection.For("CODEX")).IsSameReferenceAs(CodexRolloutEvents.Instance);
    }

    [Test]
    public async Task An_unknown_vendor_has_no_projection() {
        await Assert.That(TranscriptProjection.For("cursor")).IsNull();
        await Assert.That(TranscriptProjection.For("")).IsNull();
    }
}
