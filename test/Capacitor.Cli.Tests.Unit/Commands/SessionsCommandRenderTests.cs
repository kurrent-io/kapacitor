using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionsCommandRenderTests {
    static RepoSessionDto Row(string id, string status, string access, string? branch, bool stale = false) =>
        new(id, null, "Title " + id, new("github:1", "alice", "Alice", null), "claude", status, access, stale,
            new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.Zero), null, new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
            "da9c523c68aee2f1", true, branch, "/w", null, [], 0);

    [Test]
    public async Task Table_shows_stale_in_place_of_active_and_blanks_branch_below_full() {
        var page = new RepoSessionsResponse([Row("s-1", "active", "full", "main", stale: true), Row("s-2", "active", "overview", null)], 2, 20, 0);

        var text = SessionsCommand.Render(page, "acme/widgets", "active");

        await Assert.That(text).Contains("SESSION");
        await Assert.That(text).Contains("s-1");
        await Assert.That(text).Contains("stale");
        await Assert.That(text).Contains("main");
        await Assert.That(text).Contains("overview");
        await Assert.That(text).Contains("kcap recap --full <session-id>");
    }

    [Test]
    public async Task Empty_page_says_so_with_the_state_and_repo() {
        var text = SessionsCommand.Render(new RepoSessionsResponse([], 0, 20, 0), "acme/widgets", "ended");

        await Assert.That(text).Contains("No ended sessions visible to you on acme/widgets.");
    }

    [Test]
    public async Task Url_maps_mine_to_owner_me_and_encodes_touching() {
        var url = SessionsCommand.BuildUrl("http://srv", "da9c523c68aee2f1", new("all", null, true, "src/Foo Bar", 7, false));

        await Assert.That(url).IsEqualTo("http://srv/api/repositories/da9c523c68aee2f1/sessions?state=all&limit=7&owner=me&touching_path=src%2FFoo%20Bar");
    }
}
