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
            IObservable<string?>? selected = null, IObservable<IReadOnlySet<string>>? pending = null) =>
        new(path, root, showHeader, cache.AsObservableCache(),
            collapse ?? new RailCollapseState(), selected ?? new BehaviorSubject<string?>(null),
            pending ?? new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>()), _ => { });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Label_is_the_checkout_leaf_and_main_checkout_for_the_root() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            await Assert.That(CheckoutLabel.Format("/repo/.claude/worktrees/wt-a", "/repo")).IsEqualTo("wt-a");
            await Assert.That(CheckoutLabel.Format("/elsewhere/", "/repo")).IsEqualTo("elsewhere");
            await Assert.That(CheckoutLabel.Format("/repo", "/repo")).IsEqualTo("main checkout");
            await Assert.That(CheckoutLabel.Format("/repo/", "/repo")).IsEqualTo("main checkout");
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
            await Assert.That(wt.Sessions.Count).IsEqualTo(2);
            await Assert.That(wt.CountText).IsEqualTo("2");
            await Assert.That(wt.NeedsYou).IsTrue();

            cache.AddOrUpdate(Dto("a2", status: "Running")); // recovery clears the pip
            await Assert.That(wt.NeedsYou).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pip_follows_a_sessions_awaiting_input_verdict() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using var wt = Build(cache);
            cache.AddOrUpdate(Dto("a1") with { AwaitingInput = true });
            await Assert.That(wt.NeedsYou).IsTrue();

            cache.AddOrUpdate(Dto("a1") with { AwaitingInput = false }); // the user answered
            await Assert.That(wt.NeedsYou).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Every_worktree_defaults_expanded_main_checkout_included() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            using var main = Build(cache, path: "/repo", root: "/repo");
            using var wt = Build(cache);
            // The rail only carries current sessions, so nothing hides by default (owner
            // revision of the canvas's collapsed-main rule) — collapsing is an explicit choice.
            await Assert.That(main.IsExpanded).IsTrue();
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
                first.ToggleCommand.Execute().Subscribe(); // explicit collapse
                await Assert.That(first.IsExpanded).IsFalse();
            }
            using var recreated = Build(cache, collapse, path: "/repo", root: "/repo");
            await Assert.That(recreated.IsExpanded).IsFalse(); // survived the group's death
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
            await Assert.That(wt.CountText).IsEqualTo("1");
            await Assert.That(wt.Sessions.Count).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsed_worktree_shows_a_permission_only_alert() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            var collapse = new RailCollapseState();
            collapse.Set("/repo/.claude/worktrees/wt-a", collapsed: true);
            using var wt = Build(cache, collapse, pending: pending);
            cache.AddOrUpdate(Dto("a1"));
            await Assert.That(wt.NeedsYou).IsFalse();
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(wt.NeedsYou).IsTrue();
            pending.OnNext(new HashSet<string> { "somebody-else" });
            await Assert.That(wt.NeedsYou).IsFalse();
        });
    }
}
