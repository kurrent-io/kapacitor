using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceEnvironmentTests {
    [Test]
    public async Task Build_pins_profile_and_includes_path() {
        var src = new Dictionary<string, string> {
            ["PATH"]              = "/usr/local/bin:/usr/bin",
            ["KCAP_CONFIG_DIR"]   = "/home/u/.config/kcap",
            ["IRRELEVANT"]        = "x",
        };
        var env = ServiceEnvironment.Build(profileName: "work", source: src);
        await Assert.That(env["PATH"]).IsEqualTo("/usr/local/bin:/usr/bin");
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(env["KCAP_CONFIG_DIR"]).IsEqualTo("/home/u/.config/kcap");
        await Assert.That(env.ContainsKey("IRRELEVANT")).IsFalse();
    }

    [Test]
    public async Task Build_omits_profile_when_null_and_keeps_kcap_url() {
        var src = new Dictionary<string, string> { ["KCAP_URL"] = "https://x" };
        var env = ServiceEnvironment.Build(profileName: null, source: src);
        await Assert.That(env.ContainsKey("KCAP_PROFILE")).IsFalse();
        await Assert.That(env["KCAP_URL"]).IsEqualTo("https://x");
    }

    [Test]
    public async Task Build_explicit_profile_overrides_source_env() {
        var src = new Dictionary<string, string> { ["KCAP_PROFILE"] = "old" };
        var env = ServiceEnvironment.Build(profileName: "new", source: src);
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("new");
    }

    /// <summary>The token COMMAND is carried into the unit; a token is not.
    ///
    /// <para>This is the whole mechanism that lets a supervised daemon authenticate a contained borrowed
    /// reviewer: the unit is a file on disk, so it may hold a command that prints a credential but never
    /// the credential. Both halves are asserted together — capturing the command without excluding the
    /// tokens would put a secret at rest, and excluding the tokens without capturing the command would
    /// leave the feature unreachable.</para></summary>
    [Test]
    public async Task Build_captures_the_token_command_but_never_a_token() {
        var src = new Dictionary<string, string> {
            ["KCAP_COPILOT_TOKEN_CMD"] = "gh auth token",
            ["COPILOT_GITHUB_TOKEN"]   = "secret-a",
            ["GH_TOKEN"]               = "secret-b",
            ["GITHUB_TOKEN"]           = "secret-c",
        };

        var env = ServiceEnvironment.Build(profileName: null, source: src, isWindows: false);

        await Assert.That(env["KCAP_COPILOT_TOKEN_CMD"]).IsEqualTo("gh auth token");
        foreach (var secret in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            await Assert.That(env.ContainsKey(secret)).IsFalse()
                .Because($"{secret} is a credential and the unit is a file on disk");
        // By value too, in case a future key name carries one through under a different spelling.
        await Assert.That(env.Values).DoesNotContain("secret-a");
        await Assert.That(env.Values).DoesNotContain("secret-b");
        await Assert.That(env.Values).DoesNotContain("secret-c");
    }

    /// <summary>Excluded on Windows: borrowed review is macOS-only, so it would be inert there, and the
    /// Windows unit is a <c>.cmd</c> wrapper emitting <c>set "K=V"</c> whose escaping does not cover a
    /// quote — a quoted command would corrupt the wrapper and could run unintended content.</summary>
    [Test]
    public async Task Build_omits_the_token_command_on_windows() {
        var src = new Dictionary<string, string> {
            ["KCAP_COPILOT_TOKEN_CMD"] = "powershell -c \"Get-Secret tok\"",
            ["PATH"]                   = "C:\\bin",
        };

        var env = ServiceEnvironment.Build(profileName: null, source: src, isWindows: true);

        await Assert.That(env.ContainsKey("KCAP_COPILOT_TOKEN_CMD")).IsFalse();
        await Assert.That(env["PATH"]).IsEqualTo("C:\\bin");
    }
}
