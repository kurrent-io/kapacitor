using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using System.Reactive.Subjects;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// ReactiveCommand execution and OAPH reads both ride RxSchedulers.MainThreadScheduler, which is
/// not immediate in a bare test process — see MainWindowViewModelTests' header comment. Every
/// test here runs inside AvaloniaSession.WithImmediateRxScheduler and carries
/// [NotInParallel("AvaloniaSession")].
public class RailWorktreeViewModelTests {
    static AgentStatusDto Dto(string id, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", "/repo/.claude/worktrees/wt-a", status,
            null, null, null, created ?? DateTime.UtcNow, null, null);

    static RailWorktreeViewModel Build(
            SourceCache<AgentStatusDto, string> cache, RailCollapseState? collapse = null,
            string path = "/repo/.claude/worktrees/wt-a", string root = "/repo", bool showHeader = true,
            IObservable<string?>? selected = null) =>
        new(path, root, showHeader, cache.AsObservableCache(),
            collapse ?? new RailCollapseState(), selected ?? new BehaviorSubject<string?>(null), _ => { });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Label_is_the_checkout_leaf_and_main_checkout_for_the_root() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            await Assert.That(RailWorktreeViewModel.LabelFor("/repo/.claude/worktrees/wt-a", false)).IsEqualTo("wt-a");
            await Assert.That(RailWorktreeViewModel.LabelFor("/repo/", false)).IsEqualTo("repo");
            await Assert.That(RailWorktreeViewModel.LabelFor("/repo", true)).IsEqualTo("main checkout");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Count_and_pip_follow_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using var wt = Build(cache);
            cache.AddOrUpdate(Dto("a1"));
            cache.AddOrUpdate(Dto("a2", status: "Failed"));
            await Assert.That(wt.SessionCount).IsEqualTo(2);
            await Assert.That(wt.CountText).IsEqualTo("2");
            await Assert.That(wt.NeedsYou).IsTrue();

            cache.AddOrUpdate(Dto("a2", status: "Running")); // recovery clears the pip
            await Assert.That(wt.NeedsYou).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Main_checkout_defaults_collapsed_and_others_expanded() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using var main = Build(cache, path: "/repo", root: "/repo");
            using var wt = Build(cache);
            await Assert.That(main.IsExpanded).IsFalse();
            await Assert.That(wt.IsExpanded).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toggle_persists_in_the_shared_state_across_VM_recreation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var collapse = new RailCollapseState();
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using (var first = Build(cache, collapse, path: "/repo", root: "/repo")) {
                first.ToggleCommand.Execute().Subscribe();
                await Assert.That(first.IsExpanded).IsTrue();
            }
            using var recreated = Build(cache, collapse, path: "/repo", root: "/repo");
            await Assert.That(recreated.IsExpanded).IsTrue(); // survived the group's death
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sessions_sort_by_created_then_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            var t = DateTime.UtcNow;
            using var wt = Build(cache);
            cache.AddOrUpdate(Dto("b", created: t));
            cache.AddOrUpdate(Dto("a", created: t));
            cache.AddOrUpdate(Dto("c", created: t.AddMinutes(-1)));
            await Assert.That(wt.Sessions.Select(s => s.Id)).IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Headerless_group_always_shows_sessions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using var wt = Build(cache, showHeader: false, path: "", root: "");
            await Assert.That(wt.SessionsVisible).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_tracking_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            var wt = Build(cache);
            cache.AddOrUpdate(Dto("a1"));
            wt.Dispose();
            cache.AddOrUpdate(Dto("a2"));
            await Assert.That(wt.SessionCount).IsEqualTo(1);
            await Assert.That(wt.Sessions.Count).IsEqualTo(1);
        });
    }
}
