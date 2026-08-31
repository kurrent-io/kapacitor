using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class PermissionServiceTests {
    static PermissionPendingDto Dto(string id = "r1", string agent = "a1") =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, "2026-08-28T10:00:00.0000000+00:00");

    sealed class FakePermissionStream {
        readonly Channel<PermissionStreamEvent?> _channel = Channel.CreateUnbounded<PermissionStreamEvent?>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<PermissionStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        }

        public void EmitSubscribed() => _channel.Writer.TryWrite(new PermissionStreamEvent.Subscribed());
        public void EmitPending(PermissionPendingDto dto) => _channel.Writer.TryWrite(new PermissionStreamEvent.Pending(dto));
        public void EmitResolved(string id, string source) => _channel.Writer.TryWrite(new PermissionStreamEvent.Resolved(new PermissionResolvedDto(id, "allow", source)));
        public void EndAttempt() => _channel.Writer.TryWrite(null);
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakePermissionStream Stream = new();
        public readonly PermissionService Service;
        public readonly IObservableCache<PendingPermissionRequest, string> View;
        public IReadOnlySet<string> Agents = new HashSet<string>();
        public int Count;

        public Harness() {
            Service = new PermissionService(Daemon, Ops, Stream.RunAsync, new FakeTimeProvider(), CancellationToken.None);
            View = Service.Pending.AsObservableCache();
            Service.AgentsWithPending.Subscribe(s => Agents = s);
            Service.PendingCount.Subscribe(c => Count = c);
        }

        public void Connect(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));

        public async Task StartAsync() {
            Connect("consent/1", "permission/1");
            await WaitUntilAsync(() => Stream.Attempts == 1, what: "the subscribe attempt");
            Stream.EmitSubscribed();
        }

        public async Task<PendingPermissionRequest> EmitAsync(PermissionPendingDto dto) {
            Stream.EmitPending(dto);
            await WaitUntilAsync(() => View.Lookup(dto.RequestId).HasValue, what: $"pending {dto.RequestId} cached");
            return View.Lookup(dto.RequestId).Value;
        }

        public void Dispose() { Service.Dispose(); View.Dispose(); }
    }

    [Test]
    public async Task Subscribes_only_with_the_permission_capability_and_clears_on_a_down_level_daemon() {
        using var h = new Harness();
        h.Connect("consent/1");
        await Task.Delay(50);
        await Assert.That(h.Stream.Attempts).IsEqualTo(0);

        await h.StartAsync();
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("consent/1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared on a down-level daemon");
    }

    [Test]
    public async Task Resolved_push_from_the_server_clears_entry_agent_set_and_count_together() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1", "a1"));
        await WaitUntilAsync(() => h.Agents.Contains("a1") && h.Count == 1, what: "derivations lit");

        h.Stream.EmitResolved("r1", "server");
        await WaitUntilAsync(() => h.View.Count == 0 && !h.Agents.Contains("a1") && h.Count == 0, what: "every derivation cleared");
    }

    [Test]
    public async Task A_replayed_ghost_of_a_resolved_request_is_dropped() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Stream.EmitResolved("r1", "app");
        await WaitUntilAsync(() => h.View.Count == 0, what: "removed");
        h.Stream.EmitPending(Dto("r1"));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_outcomes_and_the_always_allow_payload() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.ResolveAsync(entry, PermissionAnswer.AllowAlways, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        await Assert.That(h.Ops.PermissionResolvePayloads[0].Decision).IsEqualTo("allow");
        await Assert.That(h.Ops.PermissionResolvePayloads[0].ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.ResolveAsync(second, PermissionAnswer.Deny, CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.ResolveAsync(third, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(failed.Error).IsEqualTo("daemon_unreachable");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscribed_clears_at_the_boundary_and_disconnect_retains() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("permission/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "resubscribe");
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared at Subscribed");
    }
}
