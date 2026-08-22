using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class WatchRepoEvidenceTests {
    [Test]
    public async Task Refresh_never_clears_an_evidence_payload() {
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: true)).IsFalse();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: false)).IsTrue();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: new(), repositoryFromEvidence: true)).IsTrue();
    }
}
