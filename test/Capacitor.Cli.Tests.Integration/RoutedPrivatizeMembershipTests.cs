using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins the boundary between the Done-grid counting override and <c>--private</c> membership:
/// <see cref="ImportCommand.ResolveRoutedOutcomeForCounting"/> may reclassify a <c>Skipped</c>
/// replay that attached child content as <c>Loaded</c> FOR COUNTING, but that must never feed
/// <c>importedSessionIds</c>, which keys off the call's RAW outcome.
///
/// <para>
/// <see cref="AntigravitySkippedChildOverrideRoutedLoopTests"/> cannot pin this any more. The
/// privatize set is <c>HashSet(importedSessionIds) ∪ privateScopeSessionIds</c>, and every REAL
/// vendor that can report <c>SentChildContent: true</c> (Cursor, Antigravity, Gemini) is now in the
/// outcome-independent private scope — so the union absorbs the difference and both the correct and
/// the rewired behaviour emit exactly one PUT. For routed vendors the union is also
/// <c>importedSessionIds</c>' only remaining consumer: <c>ComputePerSourceFinalCounts</c> takes the
/// early <c>routedOutcomes</c> return and ignores its <c>imported</c> argument entirely.
/// </para>
///
/// <para>
/// Observing the rule therefore requires a source that attaches child content while sitting OUTSIDE
/// the private scope — a combination no shipped source has, hence the probe below. It is
/// deliberately contradictory (<see cref="IImportSource.AttachesChildContentOnReplay"/> is
/// <c>false</c> while the call reports <c>SentChildContent: true</c>) precisely so the two
/// mechanisms can be told apart: key membership off <c>resolved</c> and this test's zero-PUT
/// assertion fails, while the Loaded-count assertion keeps passing.
/// </para>
///
/// <para>
/// This exercises the non-TTY renderer only, which is sound because there is exactly ONE membership
/// site to break: <c>HandleImport</c>'s <c>RecordRoutedResultAsync</c> performs the privatize
/// capture, the counting resolution and the membership decision for both renderers, which differ
/// only in how they draw. If that bookkeeping is ever inlined back into the two
/// <c>Parallel.ForEachAsync</c> bodies, this test stops covering the TTY branch and a TTY variant
/// (or an injectable display mode) becomes necessary.
/// </para>
/// </summary>
public class RoutedPrivatizeMembershipTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string ProbeVendor    = "membership-probe";
    const string ProbeSessionId = "9f9f0000000040008000000000000f0f";

    /// <summary>
    /// Reports the exact shape the counting override targets — an <c>AlreadyLoaded</c> replay whose
    /// own outcome is <c>Skipped</c> but which attached brand-new nested-child content — while
    /// declaring it cannot attach child content on a replay, so the orchestrator's
    /// outcome-independent privatize capture does NOT pick it up.
    /// </summary>
    sealed class ChildContentOutsidePrivateScopeSource : IImportSource {
        public string Vendor                      => ProbeVendor;
        public bool   IsAvailable                 => true;
        public bool   SupportsTitleGeneration     => false;
        public bool   AttachesChildContentOnReplay => false;

        public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredSession>>([
                new DiscoveredSession(ProbeSessionId, Vendor, Cwd: null, FirstTimestamp: null,
                    SourceMeta: new Dictionary<string, object?>())
            ]);

        public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
                IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportCommand.SessionClassification>>([
                new ImportCommand.SessionClassification {
                    SessionId  = ProbeSessionId,
                    FilePath   = "", // empty FilePath => routed phase, not the chain phase
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
                    TotalLines = 2,
                    SourceMeta = new Dictionary<string, object?>(),
                    Vendor     = Vendor,
                }
            ]);

        public Task<ImportSessionResult> ImportSessionAsync(
                ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) =>
            Task.FromResult(new ImportSessionResult(ImportOutcome.Skipped, SentChildContent: true));
    }

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        using var capture = ConsoleOutput.StartCapture();
        await action();
        return capture.GetCapturedOutput();
    }

    static bool LineMatches(string text, string label, int value) =>
        Regex.IsMatch(text, $@"(?m)^\s*{Regex.Escape(label)}\s+{value}\s*$");

    [Test, NotInParallel]
    public async Task counting_override_does_not_pull_a_skipped_replay_into_the_private_set() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var exitCode = 0;
        var stdout = await CaptureStdoutAsync(async () => {
            exitCode = await ImportCommand.HandleImport(
                baseUrl: _server.Url!,
                filterCwd: null,
                minLines: 0,
                sources: [new ChildContentOutsidePrivateScopeSource()],
                scope: new ImportScope.All(),
                skipConfirmation: true,
                forcePrivate: true
            );
        });

        await Assert.That(exitCode).IsEqualTo(0);

        // The override DID fire: the raw outcome is Skipped, yet the run counts it as Loaded and
        // prints the "Loading" line. Without this the zero-PUT assertion below would pass
        // vacuously — a plain suppressed no-op replay also emits no PUT.
        await Assert.That(stdout).Contains($"Loading {ProbeSessionId} ({ProbeVendor})");
        var doneIdx = stdout.IndexOf("== Done ==", StringComparison.Ordinal);
        await Assert.That(doneIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(LineMatches(stdout[doneIdx..], "Loaded", 1)).IsTrue();

        // ...and membership did NOT follow it. This source is not in the private scope, so the
        // union cannot mask the difference: keying membership off `resolved` instead of the raw
        // outcome would add this id to importedSessionIds and produce one PUT here.
        var visibilityPuts = _server.LogEntries.Count(e =>
            e.RequestMessage.Method == "PUT"
         && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal));
        await Assert.That(visibilityPuts).IsEqualTo(0);
    }
}
