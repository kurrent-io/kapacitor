# Multi-Server Profiles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support multiple Kapacitor server instances via named profiles with automatic git remote matching and explicit context switching.

**Architecture:** Extend the existing flat `config.json` with a profile-based structure (v2). A `ProfileResolver` replaces `AppConfig.ResolveServerUrl()` and walks a 7-step resolution chain: CLI flag, env var, env profile, `.kapacitor.json`, git remote match, repo binding, default profile. New commands (`profile add/list/remove/show`, `use`) manage profiles.

**Tech Stack:** .NET 10, System.Text.Json with source generators (AOT), TUnit

**Spec:** `docs/superpowers/specs/2026-04-10-multi-server-profiles-design.md`

---

## File Structure

### New files

| File | Responsibility |
|------|---------------|
| `src/kapacitor/Config/ProfileConfig.cs` | V2 config model: `ProfileConfig`, `Profile`, `RepoConfig` records + JSON source gen contexts |
| `src/kapacitor/Config/ProfileResolver.cs` | Resolution chain logic: resolve current repo to a `Profile` |
| `src/kapacitor/Config/ConfigMigration.cs` | V1 → V2 migration logic |
| `src/kapacitor/Config/RemoteMatcher.cs` | Git remote URL normalization and glob matching |
| `src/kapacitor/Commands/ProfileCommand.cs` | `kapacitor profile add/list/remove/show` |
| `src/kapacitor/Commands/UseCommand.cs` | `kapacitor use <name> [--global] [--save]` |
| `test/kapacitor.Tests.Unit/ConfigMigrationTests.cs` | Migration tests |
| `test/kapacitor.Tests.Unit/ProfileResolverTests.cs` | Resolution chain tests |
| `test/kapacitor.Tests.Unit/RemoteMatcherTests.cs` | URL normalization + glob tests |
| `test/kapacitor.Tests.Unit/ProfileCommandTests.cs` | Profile command tests |
| `test/kapacitor.Tests.Unit/UseCommandTests.cs` | Use command tests |

### Modified files

| File | Changes |
|------|---------|
| `src/kapacitor/Config/AppConfig.cs` | `Load`/`Save` become v2-aware; `ResolveServerUrl` delegates to `ProfileResolver`; add JSON contexts for new types |
| `src/kapacitor/Program.cs` | Add `"profile"` and `"use"` to offline commands and switch; replace `baseUrl` resolution |
| `src/kapacitor/Commands/SetupCommand.cs` | Save to default profile instead of flat config |
| `src/kapacitor/Commands/ConfigCommand.cs` | Work with resolved profile |

---

## Task 1: V2 Config Model

**Files:**
- Create: `src/kapacitor/Config/ProfileConfig.cs`
- Test: `test/kapacitor.Tests.Unit/ConfigMigrationTests.cs` (model serialization tests only in this task)

- [ ] **Step 1: Write failing test — V2 config round-trips through JSON**

```csharp
// test/kapacitor.Tests.Unit/ConfigMigrationTests.cs
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ConfigMigrationTests {
    [Test]
    public async Task ProfileConfig_RoundTrips_ThroughJson() {
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() {
                    ServerUrl = "https://example.com",
                    Daemon = new DaemonSettings { Name = "dev", MaxAgents = 5 },
                    DefaultVisibility = "org_public",
                    UpdateCheck = true,
                    ExcludedRepos = []
                },
                ["contoso"] = new() {
                    ServerUrl = "https://contoso.kapacitor.io",
                    Daemon = new DaemonSettings { Name = "consulting", MaxAgents = 2 },
                    DefaultVisibility = "private",
                    UpdateCheck = true,
                    ExcludedRepos = [],
                    Remotes = ["github.com/contoso/*"]
                }
            },
            ProfileBindings = new Dictionary<string, string> {
                ["/home/user/contoso-project"] = "contoso"
            }
        };

        var json = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        var deserialized = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.Version).IsEqualTo(2);
        await Assert.That(deserialized.ActiveProfile).IsEqualTo("default");
        await Assert.That(deserialized.Profiles).HasCount().EqualTo(2);
        await Assert.That(deserialized.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(deserialized.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
        await Assert.That(deserialized.ProfileBindings["/home/user/contoso-project"]).IsEqualTo("contoso");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ConfigMigrationTests/*"`
Expected: FAIL — `ProfileConfig` type does not exist

- [ ] **Step 3: Implement the V2 config model**

```csharp
// src/kapacitor/Config/ProfileConfig.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kapacitor.Config;

public record ProfileConfig {
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("active_profile")]
    public string ActiveProfile { get; init; } = "default";

    [JsonPropertyName("profiles")]
    public Dictionary<string, Profile> Profiles { get; init; } = new();

    [JsonPropertyName("profile_bindings")]
    public Dictionary<string, string> ProfileBindings { get; init; } = new();
}

public record Profile {
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonSettings? Daemon { get; init; }

    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; init; } = true;

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; init; } = "org_public";

    [JsonPropertyName("excluded_repos")]
    public string[] ExcludedRepos { get; init; } = [];

    [JsonPropertyName("remotes")]
    public string[] Remotes { get; init; } = [];
}

/// <summary>Repo-level .kapacitor.json committed to VCS.</summary>
public record RepoConfig {
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }
}

[JsonSerializable(typeof(ProfileConfig))]
internal partial class ProfileConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(ProfileConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ProfileConfigJsonContextIndented : JsonSerializerContext;

[JsonSerializable(typeof(RepoConfig))]
internal partial class RepoConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(RepoConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class RepoConfigJsonContextIndented : JsonSerializerContext;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ConfigMigrationTests/*"`
Expected: PASS

- [ ] **Step 5: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output (no warnings)

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Config/ProfileConfig.cs test/kapacitor.Tests.Unit/ConfigMigrationTests.cs
git commit -m "feat: add V2 profile config model with JSON source gen"
```

---

## Task 2: Config Migration (V1 → V2)

**Files:**
- Create: `src/kapacitor/Config/ConfigMigration.cs`
- Modify: `src/kapacitor/Config/AppConfig.cs`
- Test: `test/kapacitor.Tests.Unit/ConfigMigrationTests.cs`

- [ ] **Step 1: Write failing test — V1 flat config migrates to V2**

```csharp
// Add to ConfigMigrationTests.cs
[Test]
public async Task Migrate_V1FlatConfig_CreatesDefaultProfile() {
    var v1Json = """
        {
            "server_url": "https://my-server.com",
            "daemon": { "name": "dev", "max_agents": 3 },
            "default_visibility": "private",
            "update_check": false,
            "excluded_repos": ["owner/secret"]
        }
        """;

    var result = ConfigMigration.MigrateIfNeeded(v1Json);

    await Assert.That(result.WasMigrated).IsTrue();

    var config = result.Config;
    await Assert.That(config.Version).IsEqualTo(2);
    await Assert.That(config.ActiveProfile).IsEqualTo("default");
    await Assert.That(config.Profiles).ContainsKey("default");

    var defaultProfile = config.Profiles["default"];
    await Assert.That(defaultProfile.ServerUrl).IsEqualTo("https://my-server.com");
    await Assert.That(defaultProfile.Daemon!.Name).IsEqualTo("dev");
    await Assert.That(defaultProfile.Daemon.MaxAgents).IsEqualTo(3);
    await Assert.That(defaultProfile.DefaultVisibility).IsEqualTo("private");
    await Assert.That(defaultProfile.UpdateCheck).IsEqualTo(false);
    await Assert.That(defaultProfile.ExcludedRepos).Contains("owner/secret");
}

[Test]
public async Task Migrate_V2Config_NoMigration() {
    var v2Json = """
        {
            "version": 2,
            "active_profile": "default",
            "profiles": {
                "default": { "server_url": "https://example.com" }
            },
            "profile_bindings": {}
        }
        """;

    var result = ConfigMigration.MigrateIfNeeded(v2Json);

    await Assert.That(result.WasMigrated).IsFalse();
    await Assert.That(result.Config.ActiveProfile).IsEqualTo("default");
}

[Test]
public async Task Migrate_EmptyJson_CreatesEmptyV2() {
    var result = ConfigMigration.MigrateIfNeeded("{}");

    await Assert.That(result.WasMigrated).IsTrue();
    await Assert.That(result.Config.Version).IsEqualTo(2);
    await Assert.That(result.Config.Profiles).ContainsKey("default");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ConfigMigrationTests/*"`
Expected: FAIL — `ConfigMigration` does not exist

- [ ] **Step 3: Implement migration logic**

```csharp
// src/kapacitor/Config/ConfigMigration.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Config;

public static class ConfigMigration {
    public record MigrationResult(ProfileConfig Config, bool WasMigrated);

    public static MigrationResult MigrateIfNeeded(string json) {
        var node = JsonNode.Parse(json)?.AsObject();

        if (node is null)
            return new(new ProfileConfig { Profiles = { ["default"] = new Profile() } }, true);

        // Check if already V2
        if (node["version"]?.GetValue<int>() is 2) {
            var v2 = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
            return new(v2, false);
        }

        // V1 → V2: read old flat fields, build default profile
        var v1 = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.KapacitorConfig)
            ?? new KapacitorConfig();

        var defaultProfile = new Profile {
            ServerUrl = v1.ServerUrl,
            Daemon = v1.Daemon,
            DefaultVisibility = v1.DefaultVisibility,
            UpdateCheck = v1.UpdateCheck,
            ExcludedRepos = v1.ExcludedRepos
        };

        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> { ["default"] = defaultProfile },
            ProfileBindings = new Dictionary<string, string>()
        };

        return new(config, true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ConfigMigrationTests/*"`
Expected: PASS

- [ ] **Step 5: Update AppConfig.Load/Save to use V2 format**

Modify `src/kapacitor/Config/AppConfig.cs`:

Replace the `Load` method body so it reads raw JSON, calls `ConfigMigration.MigrateIfNeeded`, writes back if migrated, and returns the V2 config. Replace `Save` to serialize `ProfileConfig`. Keep `KapacitorConfig` and its JSON contexts (needed by migration). Add a new `LoadProfileConfig` method and a `SaveProfileConfig` method. The existing `Load`/`Save` for `KapacitorConfig` remain but become internal (migration uses them).

Add these methods to `AppConfig`:

```csharp
public static async Task<ProfileConfig> LoadProfileConfig() {
    if (!File.Exists(ConfigPath))
        return new ProfileConfig { Profiles = { ["default"] = new Profile() } };

    try {
        var json = await File.ReadAllTextAsync(ConfigPath);
        var result = ConfigMigration.MigrateIfNeeded(json);

        if (result.WasMigrated) {
            await SaveProfileConfig(result.Config);
        }

        return result.Config;
    } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
        await Console.Error.WriteLineAsync($"Warning: could not read config at {ConfigPath}: {ex.Message}");
        return new ProfileConfig { Profiles = { ["default"] = new Profile() } };
    }
}

public static async Task SaveProfileConfig(ProfileConfig config) {
    var dir = Path.GetDirectoryName(ConfigPath)!;
    Directory.CreateDirectory(dir);
    var tempPath = $"{ConfigPath}.tmp";

    await File.WriteAllBytesAsync(
        tempPath,
        JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig)
    );
    File.Move(tempPath, ConfigPath, overwrite: true);
}
```

- [ ] **Step 6: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: All PASS

- [ ] **Step 7: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 8: Commit**

```bash
git add src/kapacitor/Config/ConfigMigration.cs src/kapacitor/Config/AppConfig.cs test/kapacitor.Tests.Unit/ConfigMigrationTests.cs
git commit -m "feat: add V1 to V2 config migration"
```

---

## Task 3: Remote Matcher

**Files:**
- Create: `src/kapacitor/Config/RemoteMatcher.cs`
- Test: `test/kapacitor.Tests.Unit/RemoteMatcherTests.cs`

- [ ] **Step 1: Write failing tests — URL normalization**

```csharp
// test/kapacitor.Tests.Unit/RemoteMatcherTests.cs
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class RemoteMatcherTests {
    [Test]
    [Arguments("https://github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://github.com/contoso/repo", "github.com/contoso/repo")]
    [Arguments("git@github.com:contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("git@github.com:contoso/repo", "github.com/contoso/repo")]
    [Arguments("ssh://git@github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://user:token@github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://oauth2:ghp_abc@github.com/contoso/repo", "github.com/contoso/repo")]
    public async Task NormalizeRemoteUrl_VariousFormats_ReturnsCanonical(string input, string expected) {
        var result = RemoteMatcher.NormalizeRemoteUrl(input);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("not-a-url")]
    public async Task NormalizeRemoteUrl_Invalid_ReturnsNull(string input) {
        var result = RemoteMatcher.NormalizeRemoteUrl(input);

        await Assert.That(result).IsNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: FAIL — `RemoteMatcher` does not exist

- [ ] **Step 3: Implement URL normalization**

```csharp
// src/kapacitor/Config/RemoteMatcher.cs
using System.Text.RegularExpressions;

namespace kapacitor.Config;

public static partial class RemoteMatcher {
    /// <summary>
    /// Normalizes a git remote URL to "host/owner/path" form.
    /// Strips protocol, auth, .git suffix, and normalizes SSH colon syntax.
    /// Returns null if the URL is not a recognized git remote format.
    /// </summary>
    public static string? NormalizeRemoteUrl(string url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // SSH: git@host:owner/repo(.git)?
        var sshMatch = SshRemoteRegex().Match(url);
        if (sshMatch.Success) {
            var host = sshMatch.Groups["host"].Value;
            var path = sshMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        // SSH with protocol: ssh://git@host/owner/repo(.git)?
        var sshProtoMatch = SshProtoRemoteRegex().Match(url);
        if (sshProtoMatch.Success) {
            var host = sshProtoMatch.Groups["host"].Value;
            var path = sshProtoMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        // HTTPS/HTTP: https://(user:pass@)?host/owner/repo(.git)?
        var httpsMatch = HttpsRemoteRegex().Match(url);
        if (httpsMatch.Success) {
            var host = httpsMatch.Groups["host"].Value;
            var path = httpsMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        return null;
    }

    static string StripGitSuffix(string path) =>
        path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;

    [GeneratedRegex(@"^git@(?<host>[\w.-]+):(?<path>.+)$")]
    private static partial Regex SshRemoteRegex();

    [GeneratedRegex(@"^ssh://(?:[^@]+@)?(?<host>[\w.-]+)/(?<path>.+)$")]
    private static partial Regex SshProtoRemoteRegex();

    [GeneratedRegex(@"^https?://(?:[^@]+@)?(?<host>[\w.-]+)/(?<path>.+)$")]
    private static partial Regex HttpsRemoteRegex();
}
```

- [ ] **Step 4: Run normalization tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: PASS

- [ ] **Step 5: Write failing tests — glob matching against profiles**

Add to `RemoteMatcherTests.cs`:

```csharp
[Test]
public async Task FindMatchingProfile_SingleMatch_ReturnsProfileName() {
    var profiles = new Dictionary<string, Profile> {
        ["default"] = new() { ServerUrl = "https://default.com" },
        ["contoso"] = new() {
            ServerUrl = "https://contoso.kapacitor.io",
            Remotes = ["github.com/contoso/*", "github.com/contoso-labs/*"]
        }
    };
    var remoteUrls = new[] { "https://github.com/contoso/my-app.git" };

    var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

    await Assert.That(result).IsEqualTo("contoso");
}

[Test]
public async Task FindMatchingProfile_NoMatch_ReturnsNull() {
    var profiles = new Dictionary<string, Profile> {
        ["contoso"] = new() {
            ServerUrl = "https://contoso.kapacitor.io",
            Remotes = ["github.com/contoso/*"]
        }
    };
    var remoteUrls = new[] { "https://github.com/other-org/repo.git" };

    var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

    await Assert.That(result).IsNull();
}

[Test]
public async Task FindMatchingProfile_MultipleMatches_Throws() {
    var profiles = new Dictionary<string, Profile> {
        ["alpha"] = new() {
            ServerUrl = "https://alpha.com",
            Remotes = ["github.com/shared-org/*"]
        },
        ["beta"] = new() {
            ServerUrl = "https://beta.com",
            Remotes = ["github.com/shared-org/*"]
        }
    };
    var remoteUrls = new[] { "https://github.com/shared-org/repo.git" };

    var act = () => RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

    await Assert.That(act).ThrowsException()
        .With.Message.Containing("alpha")
        .And.Message.Containing("beta");
}

[Test]
public async Task FindMatchingProfile_SshUrl_MatchesHttpsPattern() {
    var profiles = new Dictionary<string, Profile> {
        ["contoso"] = new() {
            ServerUrl = "https://contoso.kapacitor.io",
            Remotes = ["github.com/contoso/*"]
        }
    };
    var remoteUrls = new[] { "git@github.com:contoso/my-app.git" };

    var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

    await Assert.That(result).IsEqualTo("contoso");
}
```

- [ ] **Step 6: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: FAIL — `FindMatchingProfile` does not exist

- [ ] **Step 7: Implement glob matching**

Add to `RemoteMatcher.cs`:

```csharp
/// <summary>
/// Finds the profile whose remote patterns match one of the given repo remote URLs.
/// Returns the profile name, or null if no match.
/// Throws if multiple profiles match.
/// </summary>
public static string? FindMatchingProfile(
    Dictionary<string, Profile> profiles,
    string[] remoteUrls
) {
    // Normalize all repo remotes
    var normalized = remoteUrls
        .Select(NormalizeRemoteUrl)
        .Where(u => u is not null)
        .ToList();

    if (normalized.Count == 0) return null;

    var matches = new List<string>();

    foreach (var (name, profile) in profiles) {
        if (profile.Remotes is not { Length: > 0 }) continue;

        foreach (var pattern in profile.Remotes) {
            if (normalized.Any(url => GlobMatch(url!, pattern))) {
                matches.Add(name);
                break;
            }
        }
    }

    return matches.Count switch {
        0 => null,
        1 => matches[0],
        _ => throw new InvalidOperationException(
            $"Multiple profiles match this repo's remotes: {string.Join(", ", matches)}. " +
            "Add a .kapacitor.json to the repo to disambiguate.")
    };
}

/// <summary>
/// Simple glob match: only supports * (any sequence within a path segment)
/// and ** is not supported — patterns like "github.com/org/*" match "github.com/org/repo".
/// </summary>
static bool GlobMatch(string input, string pattern) {
    // Convert glob to regex: escape dots, replace * with [^/]* (single segment)
    var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", "[^/]*") + "$";
    return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
}
```

- [ ] **Step 8: Run all tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/RemoteMatcherTests/*"`
Expected: PASS

- [ ] **Step 9: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 10: Commit**

```bash
git add src/kapacitor/Config/RemoteMatcher.cs test/kapacitor.Tests.Unit/RemoteMatcherTests.cs
git commit -m "feat: add git remote URL normalization and profile matching"
```

---

## Task 4: Profile Resolver

**Files:**
- Create: `src/kapacitor/Config/ProfileResolver.cs`
- Test: `test/kapacitor.Tests.Unit/ProfileResolverTests.cs`

- [ ] **Step 1: Write failing tests — resolution chain**

```csharp
// test/kapacitor.Tests.Unit/ProfileResolverTests.cs
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ProfileResolverTests {
    static ProfileConfig TwoProfileConfig() => new() {
        ActiveProfile = "default",
        Profiles = new Dictionary<string, Profile> {
            ["default"] = new() { ServerUrl = "https://default.com" },
            ["contoso"] = new() {
                ServerUrl = "https://contoso.kapacitor.io",
                Remotes = ["github.com/contoso/*"]
            }
        },
        ProfileBindings = new Dictionary<string, string> {
            ["/repos/bound-project"] = "contoso"
        }
    };

    [Test]
    public async Task Resolve_CliServerUrlFlag_BypassesProfiles() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: "https://override.com",
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://override.com");
        await Assert.That(result.ProfileName).IsNull();
    }

    [Test]
    public async Task Resolve_EnvUrl_BypassesProfiles() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: "https://env-override.com",
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://env-override.com");
        await Assert.That(result.ProfileName).IsNull();
    }

    [Test]
    public async Task Resolve_EnvProfile_ReturnsNamedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: "contoso",
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_RepoConfig_MatchesLocalProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "contoso", ServerUrl = "https://contoso.kapacitor.io" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_GitRemoteMatch_ReturnsMatchedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: ["https://github.com/contoso/my-app.git"],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_ProfileBinding_ReturnsMatchedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: "/repos/bound-project"
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_NoSignals_FallsBackToActiveProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://default.com");
        await Assert.That(result.ProfileName).IsEqualTo("default");
    }

    [Test]
    public async Task Resolve_RepoConfigMismatchUrl_WarnsButUsesProfileUrl() {
        var config = TwoProfileConfig();
        var resolver = new ProfileResolver(
            config,
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "contoso", ServerUrl = "https://stale-url.com" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        // Uses the local profile's URL, not the stale .kapacitor.json URL
        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.Warning).Contains("stale");
    }

    [Test]
    public async Task Resolve_RepoConfigUnknownProfile_ReturnsNullServerUrl() {
        var config = TwoProfileConfig();
        var resolver = new ProfileResolver(
            config,
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "unknown", ServerUrl = "https://unknown.com" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        // Profile not found locally — server URL comes from .kapacitor.json as hint
        await Assert.That(result.ServerUrl).IsNull();
        await Assert.That(result.Warning).Contains("unknown");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileResolverTests/*"`
Expected: FAIL — `ProfileResolver` does not exist

- [ ] **Step 3: Implement ProfileResolver**

```csharp
// src/kapacitor/Config/ProfileResolver.cs
namespace kapacitor.Config;

public record ResolvedProfile(string? ServerUrl, string? ProfileName, Profile? Profile, string? Warning);

public class ProfileResolver(
    ProfileConfig config,
    string? cliServerUrl,
    string? envUrl,
    string? envProfile,
    RepoConfig? repoConfig,
    string[] repoRemoteUrls,
    string? repoPath
) {
    public ResolvedProfile Resolve() {
        // 1. CLI --server-url flag
        if (!string.IsNullOrEmpty(cliServerUrl))
            return new(AppConfig.NormalizeUrl(cliServerUrl), null, null, null);

        // 2. KAPACITOR_URL env var
        if (!string.IsNullOrEmpty(envUrl))
            return new(AppConfig.NormalizeUrl(envUrl), null, null, null);

        // 3. KAPACITOR_PROFILE env var
        if (!string.IsNullOrEmpty(envProfile))
            return ResolveByName(envProfile);

        // 4. .kapacitor.json in repo
        if (repoConfig?.Profile is { } repoProfileName) {
            if (config.Profiles.TryGetValue(repoProfileName, out var repoProfile)) {
                // Profile exists locally — check URL matches
                if (repoConfig.ServerUrl is { } repoUrl
                    && !string.IsNullOrEmpty(repoProfile.ServerUrl)
                    && AppConfig.NormalizeUrl(repoUrl) != AppConfig.NormalizeUrl(repoProfile.ServerUrl!)) {
                    return new(
                        AppConfig.NormalizeUrl(repoProfile.ServerUrl!),
                        repoProfileName,
                        repoProfile,
                        $"Profile '{repoProfileName}' server URL does not match .kapacitor.json (stale config?)"
                    );
                }

                return new(
                    repoProfile.ServerUrl is not null ? AppConfig.NormalizeUrl(repoProfile.ServerUrl) : null,
                    repoProfileName,
                    repoProfile,
                    null
                );
            }

            // Profile not found locally
            return new(
                null,
                null,
                null,
                $"Profile '{repoProfileName}' from .kapacitor.json not found locally. Run: kapacitor profile add {repoProfileName} --server-url {repoConfig.ServerUrl}"
            );
        }

        // 5. Git remote match
        var remoteMatch = RemoteMatcher.FindMatchingProfile(config.Profiles, repoRemoteUrls);
        if (remoteMatch is not null)
            return ResolveByName(remoteMatch);

        // 6. Profile binding (from `kapacitor use`)
        if (repoPath is not null && config.ProfileBindings.TryGetValue(repoPath, out var boundName))
            return ResolveByName(boundName);

        // 7. Active profile fallback
        return ResolveByName(config.ActiveProfile);
    }

    ResolvedProfile ResolveByName(string name) {
        if (config.Profiles.TryGetValue(name, out var profile)) {
            return new(
                profile.ServerUrl is not null ? AppConfig.NormalizeUrl(profile.ServerUrl) : null,
                name,
                profile,
                null
            );
        }

        return new(null, null, null, $"Profile '{name}' not found.");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileResolverTests/*"`
Expected: PASS

- [ ] **Step 5: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Config/ProfileResolver.cs test/kapacitor.Tests.Unit/ProfileResolverTests.cs
git commit -m "feat: add profile resolution chain"
```

---

## Task 5: Wire ProfileResolver into AppConfig and Program.cs

**Files:**
- Modify: `src/kapacitor/Config/AppConfig.cs`
- Modify: `src/kapacitor/Program.cs`

This task replaces the old `ResolveServerUrl` with the new profile-aware resolution. No new tests — existing tests + the new ProfileResolver tests cover this. We run the full test suite to confirm nothing breaks.

- [ ] **Step 1: Update AppConfig.ResolveServerUrl to use ProfileResolver**

Replace the body of `ResolveServerUrl` in `src/kapacitor/Config/AppConfig.cs`:

```csharp
public static ResolvedProfile? ResolvedProfile { get; private set; }

public static async Task<string?> ResolveServerUrl(string[] args) {
    // Extract CLI --server-url
    var idx = Array.IndexOf(args, "--server-url");
    var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;

    // Env vars
    var envUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL");
    var envProfile = Environment.GetEnvironmentVariable("KAPACITOR_PROFILE");

    // Load V2 config
    var config = await LoadProfileConfig();

    // Read .kapacitor.json from current directory
    RepoConfig? repoConfig = null;
    var repoConfigPath = Path.Combine(Environment.CurrentDirectory, ".kapacitor.json");
    if (File.Exists(repoConfigPath)) {
        try {
            var json = await File.ReadAllTextAsync(repoConfigPath);
            repoConfig = JsonSerializer.Deserialize(json, RepoConfigJsonContext.Default.RepoConfig);
        } catch { /* ignore malformed */ }
    }

    // Get git remote URLs
    var remoteUrls = GetGitRemoteUrls();

    var repoPath = Environment.CurrentDirectory;

    var resolver = new ProfileResolver(
        config, cliServerUrl, envUrl, envProfile,
        repoConfig, remoteUrls, repoPath
    );

    var resolved = resolver.Resolve();
    ResolvedProfile = resolved;
    ResolvedServerUrl = resolved.ServerUrl;

    if (resolved.Warning is not null) {
        await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
    }

    return resolved.ServerUrl;
}

static string[] GetGitRemoteUrls() {
    try {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "remote -v") {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return [];

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', ' ').ElementAtOrDefault(1))
            .Where(url => url is not null)
            .Distinct()
            .ToArray()!;
    } catch {
        return [];
    }
}
```

Remove the old `ResolvedServerUrl` property setter (it's now set inside `ResolveServerUrl`). Keep the property as a public getter.

- [ ] **Step 2: Add "profile" and "use" to offline commands in Program.cs**

In `src/kapacitor/Program.cs`, add `"profile"` and `"use"` to the `offlineCommands` array:

```csharp
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update", "plugin", "profile", "use"];
```

Add placeholder cases in the switch (to be implemented in Tasks 6-7):

```csharp
case "profile":
    return await ProfileCommand.HandleAsync(args);
case "use":
    return await UseCommand.HandleAsync(args);
```

Add the corresponding `using` if needed.

- [ ] **Step 3: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: All PASS

- [ ] **Step 4: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 5: Commit**

```bash
git add src/kapacitor/Config/AppConfig.cs src/kapacitor/Program.cs
git commit -m "feat: wire profile resolver into CLI startup"
```

---

## Task 6: Profile Command

**Files:**
- Create: `src/kapacitor/Commands/ProfileCommand.cs`
- Test: `test/kapacitor.Tests.Unit/ProfileCommandTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// test/kapacitor.Tests.Unit/ProfileCommandTests.cs
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ProfileCommandTests {
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

    [Test]
    public async Task AddProfile_CreatesNewProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        // Start with a minimal V2 config
        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.AddProfile(
            configPath, "contoso", "https://contoso.kapacitor.io",
            ["github.com/contoso/*"]
        );

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).ContainsKey("contoso");
        await Assert.That(config.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(config.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
    }

    [Test]
    public async Task RemoveProfile_DeletesProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "contoso");

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).DoesNotContainKey("contoso");
    }

    [Test]
    public async Task RemoveProfile_CannotRemoveDefault() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await ProfileCommand.RemoveProfile(configPath, "default");

        await Assert.That(result).IsEqualTo(1);

        // Profile still exists
        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(config.Profiles).ContainsKey("default");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileCommandTests/*"`
Expected: FAIL — `ProfileCommand` does not exist

- [ ] **Step 3: Implement ProfileCommand**

```csharp
// src/kapacitor/Commands/ProfileCommand.cs
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ProfileCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await PrintUsage();
            return 1;
        }

        var configPath = AppConfig.GetConfigPath();

        return args[1] switch {
            "add" => await HandleAdd(configPath, args),
            "list" => await HandleList(configPath),
            "remove" when args.Length >= 3 => await RemoveProfile(configPath, args[2]),
            "show" => await HandleShow(configPath, args),
            _ => await PrintUsage()
        };
    }

    static async Task<int> HandleAdd(string configPath, string[] args) {
        if (args.Length < 3) {
            await Console.Error.WriteLineAsync("Usage: kapacitor profile add <name> --server-url <url> [--remote <pattern>]...");
            return 1;
        }

        var name = args[2];
        var serverUrl = GetArg(args, "--server-url");

        if (serverUrl is null) {
            await Console.Error.WriteLineAsync("--server-url is required");
            return 1;
        }

        var remotes = new List<string>();
        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--remote" && i + 1 < args.Length)
                remotes.Add(args[++i]);
        }

        return await AddProfile(configPath, name, serverUrl, remotes.ToArray());
    }

    internal static async Task<int> AddProfile(string configPath, string name, string serverUrl, string[] remotes) {
        var config = await LoadConfig(configPath);

        if (config.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' already exists. Remove it first.");
            return 1;
        }

        var profiles = new Dictionary<string, Profile>(config.Profiles) {
            [name] = new() {
                ServerUrl = AppConfig.NormalizeUrl(serverUrl),
                Remotes = remotes
            }
        };

        config = config with { Profiles = profiles };
        await SaveConfig(configPath, config);

        await Console.Out.WriteLineAsync($"Profile '{name}' added.");
        return 0;
    }

    static async Task<int> HandleList(string configPath) {
        var config = await LoadConfig(configPath);

        foreach (var (name, profile) in config.Profiles) {
            var active = name == config.ActiveProfile ? " (active)" : "";
            var url = profile.ServerUrl ?? "(no server URL)";
            await Console.Out.WriteLineAsync($"  {name}{active} — {url}");

            if (profile.Remotes is { Length: > 0 }) {
                foreach (var remote in profile.Remotes)
                    await Console.Out.WriteLineAsync($"    remote: {remote}");
            }
        }

        return 0;
    }

    internal static async Task<int> RemoveProfile(string configPath, string name) {
        if (name == "default") {
            await Console.Error.WriteLineAsync("Cannot remove the default profile.");
            return 1;
        }

        var config = await LoadConfig(configPath);

        if (!config.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");
            return 1;
        }

        var profiles = new Dictionary<string, Profile>(config.Profiles);
        profiles.Remove(name);

        // Also remove any bindings pointing to this profile
        var bindings = new Dictionary<string, string>(config.ProfileBindings);
        var staleKeys = bindings.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList();
        foreach (var key in staleKeys) bindings.Remove(key);

        config = config with {
            Profiles = profiles,
            ProfileBindings = bindings,
            ActiveProfile = config.ActiveProfile == name ? "default" : config.ActiveProfile
        };

        await SaveConfig(configPath, config);
        await Console.Out.WriteLineAsync($"Profile '{name}' removed.");
        return 0;
    }

    static async Task<int> HandleShow(string configPath, string[] args) {
        var config = await LoadConfig(configPath);
        var name = args.Length >= 3 ? args[2] : config.ActiveProfile;

        if (!config.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Profile: {name}");
        await Console.Out.WriteLineAsync($"  server_url: {profile.ServerUrl ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  default_visibility: {profile.DefaultVisibility}");
        await Console.Out.WriteLineAsync($"  update_check: {profile.UpdateCheck}");
        await Console.Out.WriteLineAsync($"  daemon.name: {profile.Daemon?.Name ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  daemon.max_agents: {profile.Daemon?.MaxAgents ?? 5}");

        if (profile.Remotes is { Length: > 0 }) {
            await Console.Out.WriteLineAsync($"  remotes:");
            foreach (var r in profile.Remotes)
                await Console.Out.WriteLineAsync($"    - {r}");
        }

        if (profile.ExcludedRepos is { Length: > 0 }) {
            await Console.Out.WriteLineAsync($"  excluded_repos: {string.Join(", ", profile.ExcludedRepos)}");
        }

        return 0;
    }

    static async Task<ProfileConfig> LoadConfig(string configPath) {
        if (!File.Exists(configPath))
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new() } };

        var json = await File.ReadAllTextAsync(configPath);
        return ConfigMigration.MigrateIfNeeded(json).Config;
    }

    static async Task SaveConfig(string configPath, ProfileConfig config) {
        var dir = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{configPath}.tmp";
        await File.WriteAllBytesAsync(tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tempPath, configPath, overwrite: true);
    }

    static async Task<int> PrintUsage() {
        await Console.Error.WriteLineAsync("Usage: kapacitor profile <add|list|remove|show>");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  add <name> --server-url <url> [--remote <pattern>]...");
        await Console.Error.WriteLineAsync("  list                          Show all profiles");
        await Console.Error.WriteLineAsync("  remove <name>                 Remove a profile");
        await Console.Error.WriteLineAsync("  show [name]                   Show profile details");
        return 1;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/ProfileCommandTests/*"`
Expected: PASS

- [ ] **Step 5: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/ProfileCommand.cs test/kapacitor.Tests.Unit/ProfileCommandTests.cs
git commit -m "feat: add profile add/list/remove/show commands"
```

---

## Task 7: Use Command

**Files:**
- Create: `src/kapacitor/Commands/UseCommand.cs`
- Test: `test/kapacitor.Tests.Unit/UseCommandTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// test/kapacitor.Tests.Unit/UseCommandTests.cs
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class UseCommandTests {
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

    [Test]
    public async Task Use_InRepo_SetsProfileBinding() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: "/repos/my-project", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ProfileBindings["/repos/my-project"]).IsEqualTo("contoso");
        // active_profile should NOT change
        await Assert.That(config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Use_Global_SetsActiveProfile() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: null, global: true, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.ActiveProfile).IsEqualTo("contoso");
    }

    [Test]
    public async Task Use_Save_WritesRepoConfig() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(repoRoot);

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "contoso", repoPath: repoRoot, global: false, save: true, savePath: repoRoot);

        await Assert.That(result).IsEqualTo(0);

        var repoConfigPath = Path.Combine(repoRoot, ".kapacitor.json");
        await Assert.That(File.Exists(repoConfigPath)).IsTrue();

        var repoConfigJson = await File.ReadAllTextAsync(repoConfigPath);
        var repoConfig = JsonSerializer.Deserialize(repoConfigJson, RepoConfigJsonContext.Default.RepoConfig)!;

        await Assert.That(repoConfig.Profile).IsEqualTo("contoso");
        await Assert.That(repoConfig.ServerUrl).IsEqualTo("https://contoso.com");
    }

    [Test]
    public async Task Use_UnknownProfile_ReturnsError() {
        using var tmp = new TempDir();
        var configPath = Path.Combine(tmp.Path, "config.json");

        var initial = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await UseCommand.SetProfile(configPath, "nonexistent", repoPath: "/repos/x", global: false, save: false, savePath: null);

        await Assert.That(result).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/UseCommandTests/*"`
Expected: FAIL — `UseCommand` does not exist

- [ ] **Step 3: Implement UseCommand**

```csharp
// src/kapacitor/Commands/UseCommand.cs
using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class UseCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kapacitor use <profile-name> [--global] [--save]");
            return 1;
        }

        var name = args[1];
        var global = args.Contains("--global");
        var save = args.Contains("--save");
        var repoPath = global ? null : Environment.CurrentDirectory;
        var configPath = AppConfig.GetConfigPath();

        return await SetProfile(configPath, name, repoPath, global, save, save ? Environment.CurrentDirectory : null);
    }

    internal static async Task<int> SetProfile(
        string configPath, string name, string? repoPath,
        bool global, bool save, string? savePath
    ) {
        var config = await LoadConfig(configPath);

        if (!config.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found. Run `kapacitor profile list` to see available profiles.");
            return 1;
        }

        if (global || repoPath is null) {
            // Set active_profile globally
            config = config with { ActiveProfile = name };
            await Console.Out.WriteLineAsync($"Active profile set to '{name}' (global).");
        } else {
            // Bind repo path to profile
            var bindings = new Dictionary<string, string>(config.ProfileBindings) {
                [repoPath] = name
            };
            config = config with { ProfileBindings = bindings };
            await Console.Out.WriteLineAsync($"Profile '{name}' bound to {repoPath}.");
        }

        await SaveConfig(configPath, config);

        // Optionally write .kapacitor.json
        if (save && savePath is not null) {
            var repoConfig = new RepoConfig {
                Profile = name,
                ServerUrl = profile.ServerUrl
            };
            var repoConfigPath = Path.Combine(savePath, ".kapacitor.json");
            await File.WriteAllBytesAsync(repoConfigPath,
                JsonSerializer.SerializeToUtf8Bytes(repoConfig, RepoConfigJsonContextIndented.Default.RepoConfig));
            await Console.Out.WriteLineAsync($"Wrote {repoConfigPath} — commit this to share with your team.");
        }

        return 0;
    }

    static async Task<ProfileConfig> LoadConfig(string configPath) {
        if (!File.Exists(configPath))
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new() } };

        var json = await File.ReadAllTextAsync(configPath);
        return ConfigMigration.MigrateIfNeeded(json).Config;
    }

    static async Task SaveConfig(string configPath, ProfileConfig config) {
        var dir = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{configPath}.tmp";
        await File.WriteAllBytesAsync(tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tempPath, configPath, overwrite: true);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj --treenode-filter "/*/*/UseCommandTests/*"`
Expected: PASS

- [ ] **Step 5: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 6: Commit**

```bash
git add src/kapacitor/Commands/UseCommand.cs test/kapacitor.Tests.Unit/UseCommandTests.cs
git commit -m "feat: add kapacitor use command for profile switching"
```

---

## Task 8: Update SetupCommand for V2 Config

**Files:**
- Modify: `src/kapacitor/Commands/SetupCommand.cs`

The setup command currently saves a flat `KapacitorConfig`. It needs to save to the `default` profile within a `ProfileConfig` instead.

- [ ] **Step 1: Update SetupCommand to use V2 config**

In `src/kapacitor/Commands/SetupCommand.cs`, replace the config save block (lines 218-226):

Old:
```csharp
var config = existing ?? new KapacitorConfig();

config = config with {
    ServerUrl = serverUrl,
    DefaultVisibility = defaultVisibility,
    Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = daemonName }
};
await AppConfig.Save(config);
```

New:
```csharp
var profileConfig = await AppConfig.LoadProfileConfig();
var defaultProfile = profileConfig.Profiles.GetValueOrDefault("default") ?? new Profile();

defaultProfile = defaultProfile with {
    ServerUrl = serverUrl,
    DefaultVisibility = defaultVisibility,
    Daemon = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
};

var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) {
    ["default"] = defaultProfile
};
profileConfig = profileConfig with { Profiles = profiles };
await AppConfig.SaveProfileConfig(profileConfig);
```

Also update the "Already configured" check (line 19) to read from the profile config:

Old:
```csharp
var existing = await AppConfig.Load();
```

New:
```csharp
var existingProfile = await AppConfig.LoadProfileConfig();
var existing = existingProfile.Profiles.GetValueOrDefault("default");
```

And update the check on line 22 to use `existing?.ServerUrl`.

- [ ] **Step 2: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: All PASS

- [ ] **Step 3: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Commands/SetupCommand.cs
git commit -m "refactor: update setup command to save to V2 profile config"
```

---

## Task 9: Update ConfigCommand for V2 Config

**Files:**
- Modify: `src/kapacitor/Commands/ConfigCommand.cs`

The `config show` and `config set` commands need to work with the resolved profile.

- [ ] **Step 1: Update ConfigCommand to work with profiles**

Replace the `Show` method to display the resolved profile:

```csharp
static async Task<int> Show() {
    var profileConfig = await AppConfig.LoadProfileConfig();
    var json = JsonSerializer.Serialize(profileConfig, ProfileConfigJsonContextIndented.Default.ProfileConfig);
    await Console.Out.WriteLineAsync(json);
    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath()}");

    return 0;
}
```

Update the `Set` method to modify the active profile:

```csharp
static async Task<int> Set(string key, string value) {
    var profileConfig = await AppConfig.LoadProfileConfig();
    var profileName = profileConfig.ActiveProfile;
    var profile = profileConfig.Profiles.GetValueOrDefault(profileName) ?? new Profile();

    profile = key switch {
        "server_url" => profile with { ServerUrl = value },
        "daemon.name" => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { Name = value } },
        "daemon.max_agents" when int.TryParse(value, out var n) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { MaxAgents = n } },
        "update_check" when bool.TryParse(value, out var b) => profile with { UpdateCheck = b },
        "default_visibility" when value is "private" or "org_public" or "public" => profile with { DefaultVisibility = value },
        "default_visibility" => throw new ArgumentException("Invalid value. Must be: private, org_public, or public"),
        "excluded_repos" => profile with { ExcludedRepos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
        _ => throw new ArgumentException($"Unknown config key: {key}")
    };

    var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) { [profileName] = profile };
    profileConfig = profileConfig with { Profiles = profiles };
    await AppConfig.SaveProfileConfig(profileConfig);

    await Console.Out.WriteLineAsync($"Set {key} = {value} (profile: {profileName})");
    return 0;
}
```

- [ ] **Step 2: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: All PASS

- [ ] **Step 3: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 4: Commit**

```bash
git add src/kapacitor/Commands/ConfigCommand.cs
git commit -m "refactor: update config command to work with V2 profiles"
```

---

## Task 10: Final Integration Test

Run the full test suite and verify end-to-end behavior.

- [ ] **Step 1: Run all unit tests**

Run: `dotnet run --project test/kapacitor.Tests.Unit/kapacitor.Tests.Unit.csproj`
Expected: All PASS

- [ ] **Step 2: Run integration tests**

Run: `dotnet run --project test/kapacitor.Tests.Integration/kapacitor.Tests.Integration.csproj`
Expected: All PASS

- [ ] **Step 3: Verify no AOT warnings**

Run: `dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: No output

- [ ] **Step 4: Manual smoke test**

```bash
# Build the CLI
dotnet run --project src/kapacitor/kapacitor.csproj -- profile list
dotnet run --project src/kapacitor/kapacitor.csproj -- profile add test-org --server-url https://test.example.com --remote "github.com/test-org/*"
dotnet run --project src/kapacitor/kapacitor.csproj -- profile list
dotnet run --project src/kapacitor/kapacitor.csproj -- profile show test-org
dotnet run --project src/kapacitor/kapacitor.csproj -- profile remove test-org
```

- [ ] **Step 5: Commit any fixups**

If any tests needed fixes, commit them now.
