using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A requested model the vendor does not publish is dropped for the vendor's default — correct, and
/// until now visible only in the daemon log, so the launcher kept showing the model the user picked
/// while another one answered. These pin the note that tells them, and pin that a model which WAS
/// applied stays silent (a note on every launch would train the user to ignore it).
/// </summary>
public class AcpModelFallbackNoticeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static RuntimeStartContext MakeContext(string agentId, string? model) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "",
        Model: model, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        ActivityClock: null);

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent                 Fake    { get; }
        public AcpHostedAgentRuntimeFactory Factory { get; }
        public CancellationTokenSource      Cts     { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() {
            Fake = new FakeAcpAgent();

            var connection = new ServerConnection(
                new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
                NullLoggerFactory.Instance,
                NullLogger<ServerConnection>.Instance);

            Factory = new AcpHostedAgentRuntimeFactory(
                descriptor: AcpVendorDescriptors.Cursor,
                config: new DaemonConfig { CursorPath = "cursor-agent" },
                loggerFactory: NullLoggerFactory.Instance,
                connection: connection,
                connectionSource: _ => (Fake.ClientWriteStream, Fake.ClientReadStream, new FakeAcpProcess()),
                timeProvider: new FakeTimeProvider());
        }

        public void PublishModels(params string[] modelIds) =>
            Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
                FakeAcpAgent.FixedSessionId, modelIds[0], modelIds.Select(m => (m, m))));

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try { await _fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    static List<AcpEventEnvelope> Drain(IAcpTranscriptSource transcript) {
        var envelopes = new List<AcpEventEnvelope>();
        while (transcript.Envelopes.TryRead(out var envelope)) envelopes.Add(envelope);
        return envelopes;
    }

    static IEnumerable<string> SystemNotes(IAcpTranscriptSource transcript) =>
        Drain(transcript).Where(e => e.Kind == AcpEventKind.SystemNote).Select(e => e.Text ?? "");

    [Test]
    public async Task A_requested_model_the_vendor_does_not_publish_is_reported_as_a_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(MakeContext("agent-mismatch", model: "gemini-3.7-flash"), h.Cts.Token)
            .WaitAsync(HangGuard);

        var notes = SystemNotes(start.Transcript!).ToList();
        await Assert.That(notes.Count).IsEqualTo(1);
        await Assert.That(notes[0]).Contains("gemini-3.7-flash");
        await Assert.That(notes[0]).Contains("cursor");
    }

    [Test]
    public async Task An_applied_model_emits_no_system_note() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(MakeContext("agent-applied", model: "cursor-smart"), h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(start.Transcript!.ResolvedModel).IsEqualTo("cursor-smart");
        await Assert.That(SystemNotes(start.Transcript!)).IsEmpty();
    }

    /// A launch that picked nothing still carries the daemon-wide default down to the selector, and
    /// the models published here deliberately exclude it — so the drop really does happen and the
    /// silence is the gate doing its job, not the absence of anything to report.
    [Test]
    public async Task A_daemon_default_the_vendor_does_not_publish_is_dropped_silently() {
        await using var h = new Harness();
        h.PublishModels("cursor-fast", "cursor-smart");
        h.StartFakeAgentLoop();

        var start = await h.Factory
            .StartAsync(MakeContext("agent-default", model: null), h.Cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(start.Transcript!.ResolvedModel).IsNull();
        await Assert.That(SystemNotes(start.Transcript!)).IsEmpty();
    }
}
