using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ReviewerVendorLookupTests {
    static DaemonVendorRecord Daemon(
        string[] repoPaths, string? machineId, string[]? unattended,
        string[]? supported = null, IReadOnlyList<UnattendedVendorCapabilityLite>? caps = null)
        => new(repoPaths, machineId, supported, unattended, caps);

    [Test]
    public async Task Prefers_repo_hosting_intersection_and_sorts_alphabetically() {
        var daemons = new[] {
            Daemon(["/repo/a"], "m1", ["codex", "claude"]),
            Daemon(["/other"],  "m1", ["cursor"]),
        };

        var r = ReviewerVendorLookup.Aggregate(daemons, "/repo/a", "m1", driverVendor: "claude");

        await Assert.That(r.Reviewers.Count).IsEqualTo(2);
        await Assert.That(r.Reviewers[0].Vendor).IsEqualTo("claude"); // alphabetical, cursor excluded (other repo)
        await Assert.That(r.Reviewers[1].Vendor).IsEqualTo("codex");
        await Assert.That(r.Diagnostics.Reason).IsNull();
        await Assert.That(r.DriverVendor).IsEqualTo("claude");
        await Assert.That(r.Repo.Resolved).IsTrue();
    }

    [Test]
    public async Task Reason_null_iff_reviewers_nonempty() {
        var r = ReviewerVendorLookup.Aggregate([Daemon(["/r"], "m1", ["codex"])], "/r", "m1", null);
        await Assert.That(r.Reviewers.Count).IsEqualTo(1);
        await Assert.That(r.Diagnostics.Reason).IsNull();
    }

    [Test]
    public async Task Empty_reason_precedence_repo_unresolved_wins() {
        var r = ReviewerVendorLookup.Aggregate([], repoRoot: null, "m1", null);
        await Assert.That(r.Reviewers.Count).IsEqualTo(0);
        await Assert.That(r.Repo.Resolved).IsFalse();
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("repo_unresolved");
    }

    [Test]
    public async Task Schema_skew_beats_lookup_and_count_reasons() {
        var r = ReviewerVendorLookup.Aggregate([], "/r", "m1", null, schemaSkew: true);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("schema_skew");
    }

    [Test]
    public async Task Null_daemons_is_lookup_failed() {
        var r = ReviewerVendorLookup.Aggregate(null, "/r", "m1", null);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("lookup_failed");
    }

    [Test]
    public async Task No_daemons_connected_when_empty_list() {
        var r = ReviewerVendorLookup.Aggregate([], "/r", "m1", null);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("no_daemons_connected");
    }

    [Test]
    public async Task No_repo_hosting_daemon_when_paths_disjoint() {
        var r = ReviewerVendorLookup.Aggregate([Daemon(["/x"], "m1", ["codex"])], "/r", "m1", null);
        await Assert.That(r.Diagnostics.RepoHostingDaemons).IsEqualTo(0);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("no_repo_hosting_daemon");
    }

    [Test]
    public async Task No_unattended_reviewer_when_hosting_daemon_advertises_none() {
        var r = ReviewerVendorLookup.Aggregate([Daemon(["/r"], "m1", [])], "/r", "m1", null);
        await Assert.That(r.Diagnostics.RepoHostingDaemons).IsEqualTo(1);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("no_unattended_reviewer");
    }

    [Test]
    public async Task Machine_mismatch_excludes_a_daemon_from_hosting() {
        var r = ReviewerVendorLookup.Aggregate([Daemon(["/r"], "other-machine", ["codex"])], "/r", "m1", null);
        await Assert.That(r.Diagnostics.Reason).IsEqualTo("no_repo_hosting_daemon");
    }

    [Test]
    public async Task Model_override_is_conservative_AND_across_hosting_daemons() {
        var caps1 = new[] { new UnattendedVendorCapabilityLite("codex", SupportsReviewerModelResolution: true) };
        var daemons = new[] {
            Daemon(["/r"], "m1", ["codex"], caps: caps1),
            Daemon(["/r"], "m1", ["codex"], caps: null), // second hosting daemon lacks the resolver → AND is false
        };
        var r = ReviewerVendorLookup.Aggregate(daemons, "/r", "m1", null);
        var codex = r.Reviewers.Single(e => e.Vendor == "codex");
        await Assert.That(codex.Daemons).IsEqualTo(2);
        await Assert.That(codex.ModelOverride).IsFalse();
    }

    [Test]
    public async Task Model_override_true_when_every_hosting_daemon_supports_it() {
        var caps = new[] { new UnattendedVendorCapabilityLite("codex", true) };
        var daemons = new[] {
            Daemon(["/r"], "m1", ["codex"], caps: caps),
            Daemon(["/r"], "m1", ["codex"], caps: caps),
        };
        var r = ReviewerVendorLookup.Aggregate(daemons, "/r", "m1", null);
        await Assert.That(r.Reviewers.Single(e => e.Vendor == "codex").ModelOverride).IsTrue();
    }

    [Test]
    public async Task Supported_but_not_unattended_is_reported() {
        var r = ReviewerVendorLookup.Aggregate(
            [Daemon(["/r"], "m1", ["codex"], supported: ["codex", "kiro"])], "/r", "m1", null);
        await Assert.That(r.Diagnostics.SupportedButNotUnattended).Contains("kiro");
        await Assert.That(r.Reviewers.Single().Vendor).IsEqualTo("codex");
    }

    [Test]
    public async Task Dedup_same_vendor_across_two_hosting_daemons() {
        var daemons = new[] { Daemon(["/r"], "m1", ["codex"]), Daemon(["/r"], "m1", ["codex"]) };
        var r = ReviewerVendorLookup.Aggregate(daemons, "/r", "m1", null);
        await Assert.That(r.Reviewers.Count).IsEqualTo(1);
        await Assert.That(r.Reviewers[0].Daemons).IsEqualTo(2);
    }

    [Test]
    public async Task Null_requester_machine_does_not_filter_on_machine() {
        var r = ReviewerVendorLookup.Aggregate(
            [Daemon(["/r"], "anything", ["codex"])], "/r", requesterMachineId: null, null);
        await Assert.That(r.Reviewers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Identity_is_the_non_sensitive_repo_id_not_the_local_path() {
        var r = ReviewerVendorLookup.Aggregate(
            [Daemon(["/repo/a"], "m1", ["codex"])], "/repo/a", "m1", null, repoIdentity: "acme/widgets");
        await Assert.That(r.Repo.Identity).IsEqualTo("acme/widgets");
        await Assert.That(r.Repo.Resolved).IsTrue();
        await Assert.That(r.Repo.Identity).IsNotEqualTo("/repo/a"); // the local path must not leak
    }

    [Test]
    public async Task Repo_path_matches_across_separators_and_trailing_slash() {
        // Backslashes + a trailing separator must not produce a false no_repo_hosting_daemon.
        var r = ReviewerVendorLookup.Aggregate(
            [Daemon(["\\repo\\a\\"], "m1", ["codex"])], "/repo/a", "m1", null);
        await Assert.That(r.Reviewers.Count).IsEqualTo(1);
        await Assert.That(r.Diagnostics.Reason).IsNull();
    }

    /// A daemon advertises the MAIN repository root (RepoPathStore collapses a linked worktree before
    /// storing it), so a session running inside a linked worktree must collapse the same way or it
    /// reads as no_repo_hosting_daemon for a repository the daemon does host.
    [Test]
    public async Task Linked_worktree_root_matches_a_daemon_advertising_the_main_repository() {
        using var tmp = new TempDir();
        var main = tmp.CreateDir("main");
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var worktree = tmp.PathTo("main", ".capacitor", "worktrees", "agent-1");
        tmp.CreateFile(["main", ".capacitor", "worktrees", "agent-1", ".git"], "gitdir: ../../../.git/worktrees/wt1\n");

        var r = ReviewerVendorLookup.Aggregate([Daemon([main], "m1", ["codex"])], worktree, "m1", null);

        await Assert.That(r.Diagnostics.RepoHostingDaemons).IsEqualTo(1);
        await Assert.That(r.Diagnostics.Reason).IsNull();
        await Assert.That(r.Reviewers.Single().Vendor).IsEqualTo("codex");
    }

    /// The other origin of a daemon's RepoPaths: an AllowedRepoPaths entry is advertised as the
    /// operator wrote it, so an allowlisted worktree root reaches the wire uncollapsed and must still
    /// match the session standing in it.
    [Test]
    public async Task Linked_worktree_root_matches_a_daemon_advertising_that_worktree_path() {
        using var tmp = new TempDir();
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var worktree = tmp.PathTo("main", ".capacitor", "worktrees", "agent-1");
        tmp.CreateFile(["main", ".capacitor", "worktrees", "agent-1", ".git"], "gitdir: ../../../.git/worktrees/wt1\n");

        var r = ReviewerVendorLookup.Aggregate([Daemon([worktree], "m1", ["codex"])], worktree, "m1", null);

        await Assert.That(r.Diagnostics.RepoHostingDaemons).IsEqualTo(1);
        await Assert.That(r.Diagnostics.Reason).IsNull();
    }

    /// The collapse must not merge two genuinely distinct repositories: a submodule's .git file points
    /// into .git/modules, and a submodule is a repository of its own.
    [Test]
    public async Task Submodule_checkout_does_not_match_the_superproject_daemon_path() {
        using var tmp = new TempDir();
        var super = tmp.CreateDir("super");
        tmp.CreateDir("super", ".git", "modules", "sub");
        var sub = tmp.PathTo("super", "sub");
        tmp.CreateFile(["super", "sub", ".git"], "gitdir: ../.git/modules/sub\n");

        var r = ReviewerVendorLookup.Aggregate([Daemon([super], "m1", ["codex"])], sub, "m1", null);

        await Assert.That(r.Diagnostics.Reason).IsEqualTo("no_repo_hosting_daemon");
    }

    // --- ParseDaemons ---

    [Test]
    public async Task ParseDaemons_reads_snake_case_wire_fields() {
        // Matches the server's SnakeCaseLower policy for /api/daemons (DaemonInfo).
        const string body = """
        [{"name":"d1","repo_paths":["/r"],"machine_id":"m1","supported_vendors":["codex","kiro"],
          "unattended_vendors":["codex"],
          "unattended_vendor_capabilities":[{"vendor":"codex","supports_reviewer_model_resolution":true}]}]
        """;
        var (records, skipped, skew) = ReviewerVendorLookup.ParseDaemons(body);
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(skipped).IsEqualTo(0);
        await Assert.That(skew).IsFalse();
        await Assert.That(records[0].RepoPaths[0]).IsEqualTo("/r");
        await Assert.That(records[0].MachineId).IsEqualTo("m1");
        await Assert.That(records[0].UnattendedVendors!).Contains("codex");
        await Assert.That(records[0].Capabilities!.Single().SupportsReviewerModelResolution).IsTrue();
    }

    [Test]
    public async Task ParseDaemons_non_array_is_schema_skew() {
        var (records, _, skew) = ReviewerVendorLookup.ParseDaemons("""{"oops":true}""");
        await Assert.That(records.Count).IsEqualTo(0);
        await Assert.That(skew).IsTrue();
    }

    [Test]
    public async Task ParseDaemons_unparseable_body_is_schema_skew() {
        var (_, _, skew) = ReviewerVendorLookup.ParseDaemons("not json");
        await Assert.That(skew).IsTrue();
    }

    [Test]
    public async Task ParseDaemons_skips_malformed_element_but_keeps_good_one() {
        const string body = """[{"no_repo_paths":true},{"repo_paths":["/r"],"unattended_vendors":["codex"]}]""";
        var (records, skipped, skew) = ReviewerVendorLookup.ParseDaemons(body);
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(skipped).IsEqualTo(1);
        await Assert.That(skew).IsFalse();
    }

    [Test]
    public async Task ParseDaemons_all_malformed_is_schema_skew() {
        var (records, skipped, skew) = ReviewerVendorLookup.ParseDaemons("""[{"x":1},{"y":2}]""");
        await Assert.That(records.Count).IsEqualTo(0);
        await Assert.That(skipped).IsEqualTo(2);
        await Assert.That(skew).IsTrue();
    }

    [Test]
    public async Task ParseDaemons_empty_array_is_not_skew() {
        var (records, skipped, skew) = ReviewerVendorLookup.ParseDaemons("[]");
        await Assert.That(records.Count).IsEqualTo(0);
        await Assert.That(skipped).IsEqualTo(0);
        await Assert.That(skew).IsFalse();
    }
}
