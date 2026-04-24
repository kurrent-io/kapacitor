using kapacitor.Auth;
using TUnit;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for per-profile TokenStore methods.
///
/// PathHelpers.ConfigDir is static readonly — captured once at class-load time from
/// KAPACITOR_CONFIG_DIR. RepoPathStoreGlobalSetup.[Before(Assembly)] sets that env var
/// to a shared temp dir before PathHelpers is first touched, so all path-based tests
/// in this process share that same base dir.
///
/// Each test cleans up token files it might leave behind via [Before(Test)].
/// [NotInParallel] on the class ensures tests don't race on the shared token dir.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class TokenStoreProfileTests {
    // Paths resolved through PathHelpers — same base dir as RepoPathStoreTests
    static string TokensDir  => PathHelpers.ConfigPath("tokens");
    static string LegacyPath => PathHelpers.ConfigPath("tokens.json");

    [Before(Test)]
    public void Cleanup() {
        if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);
    }

    [Test]
    public async Task SaveAsync_with_profile_writes_to_per_profile_path() {
        await TokenStore.SaveAsync("acme", MakeTokens("alice"));

        var expected = Path.Combine(TokensDir, "acme.json");
        await Assert.That(File.Exists(expected)).IsTrue();
    }

    [Test]
    public async Task LoadAsync_with_profile_reads_per_profile_file() {
        await TokenStore.SaveAsync("acme", MakeTokens("alice"));
        var loaded = await TokenStore.LoadAsync("acme");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GitHubUsername).IsEqualTo("alice");
    }

    [Test]
    public async Task LoadAsync_with_unknown_profile_returns_null() {
        var loaded = await TokenStore.LoadAsync("nonexistent");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task Legacy_tokens_json_is_migrated_on_first_profile_save() {
        // Write a legacy tokens.json in the config base dir
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy"), KapacitorJsonContext.Default.StoredTokens));

        await TokenStore.SaveAsync("acme", MakeTokens("alice"));

        await Assert.That(File.Exists(LegacyPath)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }

    [Test]
    public async Task Per_profile_files_are_independent() {
        await TokenStore.SaveAsync("acme", MakeTokens("alice"));
        await TokenStore.SaveAsync("contoso", MakeTokens("bob"));

        await Assert.That((await TokenStore.LoadAsync("acme"))!.GitHubUsername).IsEqualTo("alice");
        await Assert.That((await TokenStore.LoadAsync("contoso"))!.GitHubUsername).IsEqualTo("bob");
    }

    static StoredTokens MakeTokens(string username) => new() {
        AccessToken    = "t",
        ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
        GitHubUsername = username,
        Provider       = AuthProvider.GitHubApp
    };

    // Inline copy matching SetupCommandTests.cs / ProfileCommandTests.cs
    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "kapacitor-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
