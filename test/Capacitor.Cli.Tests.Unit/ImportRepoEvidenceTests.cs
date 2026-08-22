using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class ImportRepoEvidenceTests {
    [Test]
    public async Task Outside_repo_transcript_gains_a_repository_node() {
        string? FindRoot(string d) => d.StartsWith("/h/dev/repo-a", StringComparison.Ordinal) ? "/h/dev/repo-a" : null;
        Task<RepositoryPayload?> Detect(string root) =>
            Task.FromResult<RepositoryPayload?>(new() { Owner = "acme", RepoName = "repo-a", RemoteUrl = "git@github.com:acme/repo-a.git" });

        var lines = new[] {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/h/dev/repo-a/x.cs"}}]}}""",
        };

        var node = await ImportCommand.TryBuildEvidenceRepositoryNodeAsync("claude", lines, FindRoot, Detect);
        await Assert.That(node).IsNotNull();
        await Assert.That(node!["owner"]!.GetValue<string>()).IsEqualTo("acme");
    }

    [Test]
    public async Task Read_only_transcript_promotes_the_read_fallback() {
        string? FindRoot(string d) => d.StartsWith("/h/dev/repo-b", StringComparison.Ordinal) ? "/h/dev/repo-b" : null;
        Task<RepositoryPayload?> Detect(string root) =>
            Task.FromResult<RepositoryPayload?>(new() { Owner = "acme", RepoName = "repo-b" });

        var lines = new[] {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/h/dev/repo-b/y.cs"}}]}}""",
        };

        var node = await ImportCommand.TryBuildEvidenceRepositoryNodeAsync("claude", lines, FindRoot, Detect);
        await Assert.That(node!["repo_name"]!.GetValue<string>()).IsEqualTo("repo-b");
    }

    [Test]
    public async Task No_evidence_yields_null() {
        var node = await ImportCommand.TryBuildEvidenceRepositoryNodeAsync(
            "claude", ["""{"type":"user"}"""], _ => null, _ => Task.FromResult<RepositoryPayload?>(null));
        await Assert.That(node).IsNull();
    }
}
