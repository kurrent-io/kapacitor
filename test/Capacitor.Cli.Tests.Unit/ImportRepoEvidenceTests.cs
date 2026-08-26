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

    // Regression guard: File.ReadLines validates its argument eagerly — an empty path throws
    // ArgumentException synchronously (verified directly: `System.IO.File.ReadLines("")`) even
    // though the actual file read is lazy. Passed as a plain call argument, that throw would
    // escape BEFORE the line-based overload's own try/catch ever runs, marking an
    // otherwise-importable session Errored instead of importing without evidence. Empty (not
    // null) because that's the reachable real value — session.FilePath is a non-nullable
    // `required string` that IS empty for some vendor classifications elsewhere in this file.
    [Test]
    public async Task Invalid_path_degrades_to_null_instead_of_throwing() {
        var node = await ImportCommand.TryBuildEvidenceRepositoryNodeAsync(
            "claude", "", _ => null, _ => Task.FromResult<RepositoryPayload?>(null));
        await Assert.That(node).IsNull();
    }

    [Test]
    public async Task Valid_path_reads_the_file_and_attributes() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("transcript.jsonl", [
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/h/dev/repo-a/x.cs"}}]}}""",
        ]);

        string? FindRoot(string d) => d.StartsWith("/h/dev/repo-a", StringComparison.Ordinal) ? "/h/dev/repo-a" : null;
        Task<RepositoryPayload?> Detect(string root) =>
            Task.FromResult<RepositoryPayload?>(new() { Owner = "acme", RepoName = "repo-a" });

        var node = await ImportCommand.TryBuildEvidenceRepositoryNodeAsync("claude", path, FindRoot, Detect);
        await Assert.That(node).IsNotNull();
        await Assert.That(node!["owner"]!.GetValue<string>()).IsEqualTo("acme");
    }
}
