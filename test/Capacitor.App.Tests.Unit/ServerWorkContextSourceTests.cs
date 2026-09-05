using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

/// Client ownership under overlapping reads: a client is disposed exactly once, never under a
/// borrower, and disposal of the source drains its reads first. No network: the factory is
/// injected and the handler answers in-process.
public class ServerWorkContextSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";

    /// Answers every route with the scripted status; a request may be parked on a gate first.
    sealed class ScriptedHandler : HttpMessageHandler {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly Queue<TaskCompletionSource> Gates = new();
        public int Sent;
        public bool Disposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref Sent);
            TaskCompletionSource? gate;
            lock (Gates) gate = Gates.Count > 0 ? Gates.Dequeue() : null;
            if (gate is not null) await gate.Task.WaitAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/summary", StringComparison.Ordinal) ? """{"session_id":"s","repositories":[],"pull_requests":[]}""" : "[]";
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static ProfileContext Profiles(ConfigRoot config) => Resolutions.At("http://server.test", config);

    static (ServerWorkContextSource Source, List<ScriptedHandler> Handlers) Build(
            ConfigRoot config, ProfileContext? profiles, Func<AuthStatus>? status = null) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerWorkContextSource(config, profiles, (_, _, _, _) => {
            var handler = new ScriptedHandler();
            handlers.Add(handler);
            return Task.FromResult((new HttpClient(handler), status?.Invoke() ?? AuthStatus.Ok));
        });
        return (source, handlers);
    }

    [Test]
    public async Task A_null_profile_reads_signed_out_without_building_a_client() {
        var (source, handlers) = Build(Config.Root, profiles: null);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers).IsEmpty();
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_rejected_auth_status_disposes_the_client_it_was_handed() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root), () => AuthStatus.Expired);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers.Single().Disposed).IsTrue();
        await Assert.That(handlers.Single().Sent).IsEqualTo(0);
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_retires_the_client_and_the_next_read_builds_a_new_one() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        var first = await source.ReadAsync(Session, CancellationToken.None);
        await Assert.That(first.Kind).IsEqualTo(WorkContextReadKind.Ready);
        handlers[0].Status = HttpStatusCode.Unauthorized;

        var signedOut = await source.ReadAsync(Session, CancellationToken.None);
        var next = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(signedOut.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers[0].Disposed).IsTrue();
        await Assert.That(handlers.Count).IsEqualTo(2);
        await Assert.That(next.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_does_not_dispose_a_client_another_read_still_borrows() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        await source.ReadAsync(Session, CancellationToken.None);
        var handler = handlers.Single();
        var gateB1 = new TaskCompletionSource();
        var gateB2 = new TaskCompletionSource();
        handler.Gates.Enqueue(gateB1);
        handler.Gates.Enqueue(gateB2);
        var b = source.ReadAsync(Session, CancellationToken.None);
        await WorkspaceFixtures.WaitUntilAsync(
            () => { lock (handler.Gates) return handler.Gates.Count == 0; },
            what: "B's two requests parked on their gates");

        handler.Status = HttpStatusCode.Unauthorized;
        var a = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(a.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handler.Disposed).IsFalse();
        gateB1.SetResult();
        gateB2.SetResult();
        var bRead = await b;
        await Assert.That(bRead.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handler.Disposed).IsTrue();
        await source.DisposeAsync();
    }

    [Test]
    public async Task Disposing_during_an_active_read_cancels_it_awaits_it_and_disposes_the_client_once() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        await source.ReadAsync(Session, CancellationToken.None);
        var handler = handlers.Single();
        var gate = new TaskCompletionSource();
        handler.Gates.Enqueue(gate);
        var pending = source.ReadAsync(Session, CancellationToken.None);
        await WorkspaceFixtures.WaitUntilAsync(() => handler.Sent >= 4, what: "the read parked on its gate");

        await source.DisposeAsync();

        var read = await pending;
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(handler.Disposed).IsTrue();
        var after = await source.ReadAsync(Session, CancellationToken.None);
        await Assert.That(after.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await source.DisposeAsync(); // idempotent
    }

    [Test]
    public async Task NoAuthRequired_is_accepted_like_Ok() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root), () => AuthStatus.NoAuthRequired);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await Assert.That(handlers.Count).IsEqualTo(1);
        await source.DisposeAsync();
    }

    [Test]
    public async Task The_callers_own_cancellation_propagates_and_the_source_stays_usable_after() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        await source.ReadAsync(Session, CancellationToken.None);
        var handler = handlers.Single();
        var gate = new TaskCompletionSource();
        handler.Gates.Enqueue(gate);
        using var cts = new CancellationTokenSource();
        var pending = source.ReadAsync(Session, cts.Token);
        await WorkspaceFixtures.WaitUntilAsync(
            () => { lock (handler.Gates) return handler.Gates.Count == 0; },
            what: "the read parked on its gate");

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);

        var after = await source.ReadAsync(Session, CancellationToken.None);
        await Assert.That(after.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await source.DisposeAsync();
    }
}
