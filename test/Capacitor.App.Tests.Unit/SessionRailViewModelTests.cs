using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class SessionRailViewModelTests {
    // "/x/wt/<leaf>" resolves to "/x" — a stand-in for GitRepository.ResolveMainRepoRoot so no
    // test touches real .git files.
    static string Resolve(string path) {
        var marker = path.IndexOf("/wt/", StringComparison.Ordinal);
        return marker < 0 ? path : path[..marker];
    }

    static AgentStatusDto Dto(string id, string? repoPath, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", repoPath, status, null, null, null, created ?? DateTime.UtcNow, null, null);

    static (FakeDaemonClientService Service, SessionRailViewModel Rail) Build(Action<string>? open = null) {
        var service = new FakeDaemonClientService();
        return (service, new SessionRailViewModel(service, open ?? (_ => { }), Resolve));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Groups_repo_worktree_session_with_no_repository_last() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/zeta"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a4", null));

                await Assert.That(rail.Repos.Select(r => r.Label))
                    .IsEquivalentTo(["alpha", "zeta", "No repository"], CollectionOrdering.Matching);

                var alpha = rail.Repos[0];
                await Assert.That(alpha.Worktrees.Select(w => w.Label))
                    .IsEquivalentTo(["main checkout", "feature-x"], CollectionOrdering.Matching);

                var noRepo = rail.Repos[2];
                await Assert.That(noRepo.IsNoRepository).IsTrue();
                await Assert.That(noRepo.Worktrees).Count().IsEqualTo(1);
                await Assert.That(noRepo.Worktrees[0].ShowHeader).IsFalse();
            }
        });
    }

    /// A reviewer that borrowed a checkout belongs on that checkout's node, beside the session it
    /// reviews — a snapshot reviewer too, which runs elsewhere but names what it borrowed. The
    /// collapse key follows the same rule, so opening the reviewer expands that node.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_borrowed_reviewer_files_under_the_worktree_it_reviews() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                const string worktree = "/dev/alpha/wt/agent-1";
                service.Agents.AddOrUpdate(Dto("p1", "/dev/alpha") with { WorktreePath = worktree, WorkLocation = "owned" });
                service.Agents.AddOrUpdate(Dto("r1", "/dev/alpha") with {
                    WorktreePath = worktree, WorkLocation = "borrowed", BorrowedFrom = worktree });
                service.Agents.AddOrUpdate(Dto("s1", "/dev/alpha") with {
                    WorktreePath = "/snapshots/borrowed-1", WorkLocation = "borrowed", BorrowedFrom = worktree });

                var node = rail.Repos.Single().Worktrees.Single();
                await Assert.That(node.Label).IsEqualTo("agent-1");
                await Assert.That(node.Sessions.Select(s => s.Id)).IsEquivalentTo(["p1", "r1", "s1"]);

                node.ToggleCommand.Execute().Subscribe();
                await Assert.That(node.IsExpanded).IsFalse();
                rail.NotifySessionOpened("s1");
                await Assert.That(node.IsExpanded).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Counts_and_hosted_text_track_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                await Assert.That(rail.IsEmpty).IsTrue();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.IsEmpty).IsFalse();
                await Assert.That(rail.HostedText).IsEqualTo("2 hosted");
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("2 sessions");

                service.Agents.RemoveKey("a2");
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("1 session");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_repo_group_disappears_and_collapse_survives_recreation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                rail.Repos[0].Worktrees[0].ToggleCommand.Execute().Subscribe(); // explicit collapse

                service.Agents.RemoveKey("a1"); // the whole repo group dies
                await Assert.That(rail.Repos).Count().IsEqualTo(0);

                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha")); // and re-forms
                await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsFalse();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NotifySessionOpened_expands_the_collapsed_worktree_and_selects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                rail.Repos[0].Worktrees[0].ToggleCommand.Execute().Subscribe(); // explicit collapse
                await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsFalse();

                rail.NotifySessionOpened("a1"); // opening beats the collapse — never a hidden highlight
                rail.SelectedAgentId = "a1";

                var wt = rail.Repos[0].Worktrees[0];
                await Assert.That(wt.IsExpanded).IsTrue();
                await Assert.That(wt.HoldsSelected).IsTrue();
                await Assert.That(wt.Sessions[0].IsSelected).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_tracking() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
            rail.Dispose();
            service.Agents.AddOrUpdate(Dto("a2", "/dev/zeta"));
            await Assert.That(rail.Repos).Count().IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_pip_reaches_the_worktree_row_when_a_session_fails() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.Repos[0].Worktrees[0].NeedsYou).IsFalse();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha/wt/feature-x", status: "Failed"));
                await Assert.That(rail.Repos[0].Worktrees[0].NeedsYou).IsTrue();
            }
        });
    }
}
