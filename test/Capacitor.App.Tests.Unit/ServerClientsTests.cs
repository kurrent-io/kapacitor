using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The one place the app's server clients are torn down: the static sequence is ordered and
/// fault-isolated; the holder makes it happen once however many times, or how concurrently,
/// the two cleanup paths reach it.
public class ServerClientsTests {
    sealed class Spy(List<string> log, string name, Exception? throwOn = null) : IAsyncDisposable {
        public int Disposals;
        public TaskCompletionSource Gate = new();
        public bool Gated;

        public async ValueTask DisposeAsync() {
            Disposals++;
            log.Add(name);
            if (Gated) await Gate.Task;
            if (throwOn is not null) throw throwOn;
        }
    }

    [Test]
    public async Task The_sequence_disposes_launch_then_source_then_completes_and_disposes_the_subject() {
        var log = new List<string>();
        var subject = new Subject<ReactiveUnit>();
        var completed = false;
        subject.Subscribe(_ => { }, () => completed = true);

        await ServerClients.CleanupAsync(new Spy(log, "launch"), new Spy(log, "source"), subject);

        await Assert.That(log).IsEquivalentTo(new[] { "launch", "source" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(completed).IsTrue();
        await Assert.That(subject.IsDisposed).IsTrue();
    }

    [Test]
    public async Task A_throwing_launch_disposal_still_disposes_the_source() {
        var log = new List<string>();
        var source = new Spy(log, "source");

        await ServerClients.CleanupAsync(new Spy(log, "launch", new InvalidOperationException("gate gone")), source, new Subject<ReactiveUnit>());

        await Assert.That(source.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task The_holder_disposes_each_once_across_sequential_and_concurrent_calls() {
        var log = new List<string>();
        var launch = new Spy(log, "launch") { Gated = true };
        var source = new Spy(log, "source");
        var holder = new ServerClients(launch, source);

        var first = holder.DisposeAsync().AsTask();
        var second = holder.DisposeAsync().AsTask();
        await Assert.That(holder.CleanupStarted).IsTrue();
        launch.Gate.SetResult();
        await Task.WhenAll(first, second);
        await holder.DisposeAsync();

        await Assert.That(launch.Disposals).IsEqualTo(1);
        await Assert.That(source.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Sign_in_completion_reaches_subscribers_before_cleanup_and_is_inert_after() {
        var holder = new ServerClients(null, null);
        var seen = 0;
        using var sub = holder.SignInCompleted.Subscribe(_ => seen++);

        holder.NotifySignInCompleted();
        await holder.DisposeAsync();
        holder.NotifySignInCompleted();

        await Assert.That(seen).IsEqualTo(1);
    }
}
