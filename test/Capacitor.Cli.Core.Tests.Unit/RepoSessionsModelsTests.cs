using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Pins the wire names the server emits for the repo listing; a renamed property here
/// would deserialize to null silently.</summary>
public class RepoSessionsModelsTests {
    const string Body = """
        {"items":[{"session_id":"abc","slug":"abc-slug","title":"Fix it","owner":{"user_id":"github:1","username":"alice","display_name":"Alice","avatar_url":null},
        "vendor":"claude","status":"active","access_level":"full","stale":true,"started_at":"2026-09-02T09:00:00+00:00","ended_at":null,
        "last_activity_at":"2026-09-02T10:00:00+00:00","primary_repo_hash":"da9c523c68aee2f1","is_primary":false,"branch":"main","cwd":"/work",
        "last_prompt":"do the thing","write_attempt_paths":["/work/a.cs"],"write_attempt_count":1},
        {"session_id":"def","slug":null,"title":null,"owner":null,"vendor":null,"status":"ended","access_level":"overview","stale":false,
        "started_at":"2026-09-01T09:00:00+00:00","ended_at":"2026-09-01T10:00:00+00:00","last_activity_at":"2026-09-01T10:00:00+00:00",
        "primary_repo_hash":null,"is_primary":true,"branch":null,"cwd":null,"last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0}],
        "total":2,"limit":20,"offset":0}
        """;

    [Test]
    public async Task Deserializes_every_field_and_tolerates_nulls() {
        var page = JsonSerializer.Deserialize(Body, CapacitorJsonContext.Default.RepoSessionsResponse)!;

        await Assert.That(page.Total).IsEqualTo(2);
        await Assert.That(page.Limit).IsEqualTo(20);
        await Assert.That(page.Items.Count).IsEqualTo(2);

        var first = page.Items[0];
        await Assert.That(first.SessionId).IsEqualTo("abc");
        await Assert.That(first.Owner!.Username).IsEqualTo("alice");
        await Assert.That(first.Status).IsEqualTo("active");
        await Assert.That(first.AccessLevel).IsEqualTo("full");
        await Assert.That(first.Stale).IsTrue();
        await Assert.That(first.IsPrimary).IsFalse();
        await Assert.That(first.WriteAttemptPaths).IsEquivalentTo(new[] { "/work/a.cs" });
        await Assert.That(first.WriteAttemptCount).IsEqualTo(1);
        await Assert.That(first.LastActivityAt.Hour).IsEqualTo(10);

        var second = page.Items[1];
        await Assert.That(second.Owner).IsNull();
        await Assert.That(second.Branch).IsNull();
        await Assert.That(second.WriteAttemptPaths).IsEmpty();
        await Assert.That(second.EndedAt).IsNotNull();
    }
}
