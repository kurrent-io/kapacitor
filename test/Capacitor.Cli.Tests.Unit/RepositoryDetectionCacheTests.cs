using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class RepositoryDetectionCacheTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task GitCacheEntry_without_schema_version_deserializes_as_stale() {
        // A pre-upgrade entry (no schema_version, no host) must be treated as stale.
        const string legacy = """{"owner":"o","repo_name":"r","cached_at":"2020-01-01T00:00:00+00:00"}""";
        var entry = JsonSerializer.Deserialize(legacy, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(entry!.SchemaVersion).IsEqualTo(0);        // absent → default 0
        await Assert.That(entry.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }

    [Test]
    public async Task GitCacheEntry_v2_is_stale_after_nested_group_bump() {
        // v2 pre-dates multi-segment owner parsing; a nested repo cached under
        // v2 has owner=null and must be re-derived, not served stale, after the bump.
        const string v2 = """{"schema_version":2,"host":"gitlab.com","owner":null,"repo_name":null,"cached_at":"2020-01-01T00:00:00+00:00"}""";
        var entry = JsonSerializer.Deserialize(v2, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(entry!.SchemaVersion).IsEqualTo(2);
        await Assert.That(entry.SchemaVersion == RepositoryDetection.CacheSchemaVersion).IsFalse();
    }

    // Exercises the real detection path end-to-end for a nested namespace, proving the
    // greedy owner parse flows through DetectRepositoryAsync (glab-independent).
    [Test]
    public async Task Detects_nested_gitlab_repo_base_info() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "git@gitlab.com:group/sub/project.git");
        var payload = await RepositoryDetection.DetectRepositoryAsync(Config.Root, repo);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.Owner).IsEqualTo("group/sub");
        await Assert.That(payload.RepoName).IsEqualTo("project");
        await Assert.That(payload.Host).IsEqualTo("gitlab.com");
    }

    [Test]
    public async Task GitCacheEntry_roundtrips_host_and_version() {
        var entry = new GitCacheEntry {
            RemoteUrl = "git@gitlab.com:group/project.git",
            Owner = "group", RepoName = "project", Host = "gitlab.com",
            SchemaVersion = RepositoryDetection.CacheSchemaVersion, CachedAt = DateTimeOffset.UnixEpoch
        };
        var json = JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);
        await Assert.That(back!.Host).IsEqualTo("gitlab.com");
        await Assert.That(back.SchemaVersion).IsEqualTo(RepositoryDetection.CacheSchemaVersion);
    }

    // Exercises the real best-effort detection path (calls DetectRepositoryAsync, which
    // shells out to the real `glab` if present) and asserts only glab-independent base
    // info. PR fields are intentionally not asserted because glab may be absent/unauthenticated.
    [Test]
    public async Task Detects_gitlab_repo_base_info() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "git@gitlab.com:group/project.git");
        var payload = await RepositoryDetection.DetectRepositoryAsync(Config.Root, repo);

        await Assert.That(payload).IsNotNull();
        await Assert.That(payload!.Owner).IsEqualTo("group");
        await Assert.That(payload.RepoName).IsEqualTo("project");
        await Assert.That(payload.Host).IsEqualTo("gitlab.com");
        // No glab installed/authenticated in CI → PR fields stay null (best-effort).
    }

    // Mirrors RepoMatcherTests.MakeTempRepo: creates a throwaway git repo with a
    // controlled origin remote so router/detector dispatch can be exercised end-to-end.
    static string MakeTempRepo(TempDir tmp, string originUrl) {
        var root = tmp.CreateDir("repo");

        RunGit(root, "init", "-q");
        RunGit(root, "remote", "add", "origin", originUrl);

        return root;
    }

    static void RunGit(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        // Drain BOTH pipes before WaitForExit. A child that fills its stdout buffer blocks on the
        // write while we block on WaitForExit → deadlock. Harmless for init/remote add (near-empty
        // output), but a footgun the moment this helper is reused for a chattier git subcommand.
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.GetAwaiter().GetResult()}");
        }
        _ = stdout.GetAwaiter().GetResult();
    }
}
