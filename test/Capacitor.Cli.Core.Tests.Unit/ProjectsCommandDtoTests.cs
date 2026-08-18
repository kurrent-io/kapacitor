using System.Text.Json;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Deserialization tests for the <c>kcap projects</c> / <c>kcap project &lt;slug&gt;</c> DTOs
/// against sample payloads mirroring the server's <c>ProjectSummaryDto</c> / <c>ProjectDetailDto</c>
/// (src/Capacitor.Server.Core/Projects/ProjectContracts.cs) — snake_case wire, no client-side
/// [JsonPropertyName] typos possible without a test catching it.
/// </summary>
public class ProjectsCommandDtoTests {
    [Test]
    public async Task CliProjectSummary_DeserializesFromServerShape() {
        const string json = """
            [
              {
                "project_id": "proj-1",
                "slug": "payments",
                "name": "Payments",
                "description": "Payments squad repos",
                "owner_user_id": "github:42",
                "repo_count": 3,
                "member_count": 5,
                "viewer_membership": "owner",
                "viewer_pending": null,
                "pending_request_count": 2,
                "repo_hashes": ["abc123", "def456"]
              },
              {
                "project_id": "proj-2",
                "slug": "growth",
                "name": "Growth",
                "description": null,
                "owner_user_id": "github:7",
                "repo_count": 1,
                "member_count": 1,
                "viewer_membership": "none",
                "viewer_pending": "invite",
                "pending_request_count": 0,
                "repo_hashes": []
              }
            ]
            """;

        var projects = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ListCliProjectSummary);

        await Assert.That(projects).IsNotNull();
        await Assert.That(projects!.Count).IsEqualTo(2);

        var payments = projects[0];
        await Assert.That(payments.ProjectId).IsEqualTo("proj-1");
        await Assert.That(payments.Slug).IsEqualTo("payments");
        await Assert.That(payments.Name).IsEqualTo("Payments");
        await Assert.That(payments.Description).IsEqualTo("Payments squad repos");
        await Assert.That(payments.OwnerUserId).IsEqualTo("github:42");
        await Assert.That(payments.RepoCount).IsEqualTo(3);
        await Assert.That(payments.MemberCount).IsEqualTo(5);
        await Assert.That(payments.ViewerMembership).IsEqualTo("owner");
        await Assert.That(payments.ViewerPending).IsNull();
        await Assert.That(payments.PendingRequestCount).IsEqualTo(2);
        await Assert.That(payments.RepoHashes).IsEquivalentTo(["abc123", "def456"]);

        var growth = projects[1];
        await Assert.That(growth.Description).IsNull();
        await Assert.That(growth.ViewerMembership).IsEqualTo("none");
        await Assert.That(growth.ViewerPending).IsEqualTo("invite");
        await Assert.That(growth.RepoHashes).IsEmpty();
    }

    [Test]
    public async Task CliProjectDetail_DeserializesFromServerShape() {
        const string json = """
            {
              "project_id": "proj-1",
              "slug": "payments",
              "name": "Payments",
              "description": "Payments squad repos",
              "owner_user_id": "github:42",
              "viewer_membership": "owner",
              "viewer_pending": null,
              "repos": [
                { "repo_hash": "abc123", "repo_slug": "kurrent-io/payments-api" }
              ],
              "members": [
                { "member_kind": "user", "member_id": "github:7", "display_name": "Alice" }
              ],
              "join_requests": [
                { "user_id": "github:99", "direction": "request", "requested_at": "2026-07-01T12:00:00Z" }
              ]
            }
            """;

        var project = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CliProjectDetail);

        await Assert.That(project).IsNotNull();
        await Assert.That(project!.ProjectId).IsEqualTo("proj-1");
        await Assert.That(project.ViewerMembership).IsEqualTo("owner");

        await Assert.That(project.Repos).Count().IsEqualTo(1);
        await Assert.That(project.Repos[0].RepoHash).IsEqualTo("abc123");
        await Assert.That(project.Repos[0].RepoSlug).IsEqualTo("kurrent-io/payments-api");

        await Assert.That(project.Members).Count().IsEqualTo(1);
        await Assert.That(project.Members[0].MemberKind).IsEqualTo("user");
        await Assert.That(project.Members[0].DisplayName).IsEqualTo("Alice");

        await Assert.That(project.JoinRequests).Count().IsEqualTo(1);
        await Assert.That(project.JoinRequests[0].UserId).IsEqualTo("github:99");
        await Assert.That(project.JoinRequests[0].Direction).IsEqualTo("request");
    }

    [Test]
    public async Task CliProjectDetail_DeserializesWithEmptyCollections() {
        const string json = """
            {
              "project_id": "proj-3",
              "slug": "solo",
              "name": "Solo",
              "description": null,
              "owner_user_id": "github:1",
              "viewer_membership": "member",
              "viewer_pending": null,
              "repos": [],
              "members": [],
              "join_requests": []
            }
            """;

        var project = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CliProjectDetail);

        await Assert.That(project).IsNotNull();
        await Assert.That(project!.Repos).IsEmpty();
        await Assert.That(project.Members).IsEmpty();
        await Assert.That(project.JoinRequests).IsEmpty();
    }

    [Test]
    public async Task CliProjectError_DeserializesProjectsNotInPlanBody() {
        const string json = """
            { "error": "projects_not_in_plan", "message": "Projects require the Team or Enterprise plan." }
            """;

        var error = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CliProjectError);

        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Error).IsEqualTo("projects_not_in_plan");
        await Assert.That(error.Message).IsEqualTo("Projects require the Team or Enterprise plan.");
    }
}
