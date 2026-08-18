using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

public class GitLabPrDetectorTests {
    const string TwoOpenMrs = """
    [
      {"iid":5,"title":"old","web_url":"https://gitlab.com/g/p/-/merge_requests/5","source_branch":"feat/x","updated_at":"2026-06-01T00:00:00Z"},
      {"iid":9,"title":"new","web_url":"https://gitlab.com/g/p/-/merge_requests/9","source_branch":"feat/x","updated_at":"2026-06-30T00:00:00Z"},
      {"iid":7,"title":"other","web_url":"https://gitlab.com/g/p/-/merge_requests/7","source_branch":"feat/other","updated_at":"2026-07-01T00:00:00Z"}
    ]
    """;

    [Test]
    public async Task Picks_matching_branch_most_recently_updated() {
        string? capturedCmd = null, capturedArgs = null;

        CommandRunner fake = (cmd, args, _, _) => {
            capturedCmd  = cmd;
            capturedArgs = args;
            return Task.FromResult<string?>(TwoOpenMrs);
        };
        var pr = await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake);

        await Assert.That(capturedCmd).IsEqualTo("glab");
        await Assert.That(capturedArgs).Contains("--hostname gitlab.com");
        await Assert.That(capturedArgs).Contains("projects/g%2Fp/merge_requests");
        await Assert.That(capturedArgs).Contains("source_branch=feat%2Fx");
        await Assert.That(pr!.Number).IsEqualTo(9);              // iid, newest updated_at, branch match
        await Assert.That(pr.Url).IsEqualTo("https://gitlab.com/g/p/-/merge_requests/9");
        await Assert.That(pr.HeadRef).IsEqualTo("feat/x");
    }

    [Test]
    public async Task Nested_group_encodes_full_project_path() {
        // a nested namespace owner ("group/sub") must be URL-encoded whole
        // into the glab project path (group%2Fsub%2Fproj), not left with a raw slash.
        string? seen = null;
        CommandRunner fake = (_, args, _, _) => { seen = args; return Task.FromResult<string?>("[]"); };

        await GitLabPrDetector.DetectAsync("gitlab.com", "group/sub", "proj", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake);

        await Assert.That(seen).Contains("projects/group%2Fsub%2Fproj/merge_requests");
    }

    [Test]
    public async Task Empty_branch_never_calls_glab_and_returns_null() {
        var called = false;
        CommandRunner fake = (_, _, _, _) => { called = true; return Task.FromResult<string?>("[]"); };
        var pr = await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(pr).IsNull();
        await Assert.That(called).IsFalse();                    // guard: no branch → no query (would return all MRs)
    }

    [Test]
    public async Task Encodes_branch_with_ampersand() {
        string seen = "";
        CommandRunner fake = (_, args, _, _) => { seen = args; return Task.FromResult<string?>("[]"); };
        await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/a&b", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(seen).Contains("source_branch=feat%2Fa%26b");
    }

    [Test]
    public async Task No_matching_branch_yields_null() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(
            """[{"iid":5,"title":"x","web_url":"u","source_branch":"different","updated_at":"2026-06-01T00:00:00Z"}]""");
        await Assert.That(await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }

    [Test]
    public async Task Bad_iid_record_is_skipped_not_the_whole_result() {
        // A non-numeric iid on one matching record must not drop the OTHER valid match — the bad
        // record is skipped, not the whole detection (the per-record parse used to throw and the
        // outer catch returned null for everything).
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("""
        [
          {"iid":9,"title":"good","web_url":"https://gitlab.com/g/p/-/merge_requests/9","source_branch":"feat/x","updated_at":"2026-06-30T00:00:00Z"},
          {"iid":"oops","title":"bad","web_url":"u","source_branch":"feat/x","updated_at":"2026-07-01T00:00:00Z"}
        ]
        """);
        var pr = await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat/x", "/cwd", TimeSpan.FromSeconds(2), fake);

        await Assert.That(pr!.Number).IsEqualTo(9);
    }

    [Test]
    public async Task Encodes_branch_with_plus() {
        // '+' is a query-string metacharacter (space in x-www-form-urlencoded); it must be
        // percent-encoded to %2B so the source_branch filter matches the real branch, not " ".
        string seen = "";
        CommandRunner fake = (_, args, _, _) => { seen = args; return Task.FromResult<string?>("[]"); };
        await GitLabPrDetector.DetectAsync("gitlab.com", "g", "p", "feat+x", "/cwd", TimeSpan.FromSeconds(2), fake);
        await Assert.That(seen).Contains("source_branch=feat%2Bx");
    }
}
