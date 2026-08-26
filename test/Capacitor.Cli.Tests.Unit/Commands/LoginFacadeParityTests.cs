using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using static Capacitor.Tests.Helpers.AuthFixtures;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// `LoginCommand`'s parity with the pre-re-plumb `HandleDiscoverLoginAsync`/
/// `OAuthLoginFlow.LoginWithDiscoveryAsync`: same exit codes, same final banner line, same
/// per-tenant token/profile publication, and no funnel events of the login path's own. Shares
/// the OnboardingFacadeTests keys — it drives the same telemetry sink.
/// </summary>
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class LoginFacadeParityTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Before(Test)]
    public void Cleanup() => CliTelemetry.Reset();

    ProfileConfig ReadConfig() => ConfigMutator.LoadPure(ConfigPath);

    bool TokenFileExists(string profile) => File.Exists(Path.Combine(TokensDir, $"{profile}.json"));

    // ── discover: GitHub, multiple tenants ──────────────────────────────────

    [Test]
    public async Task Discover_github_two_tenants_publishes_both_and_prints_todays_final_line() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(Config.Root, progress, handler, PickerReturningFirst());

        var exit = await LoginCommand.HandleAsync(["login", "--discover", "--github", "--device"], null, ProfileConfig.DefaultName, facade, progress);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsTrue();

        var cfg = ReadConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.ai");

        // Pinned parity with the pre-re-plumb HandleDiscoverLoginAsync's final line (today's L913).
        await Assert.That(progress.Notices).Contains("Logged in. Active profile: acme.");
    }

    [Test]
    public async Task Discover_github_zero_funnel_events_of_its_own() {
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("login", null, loggedIn: false, Config.Root);
        sink.Clear(); // drop cli_first_run

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(Config.Root, progress, handler, PickerReturningFirst());

        var exit = await LoginCommand.HandleAsync(["login", "--discover", "--github", "--device"], null, ProfileConfig.DefaultName, facade, progress);

        await Assert.That(exit).IsEqualTo(0);
        // The GitHub discover path has no SetupFunnel calls anywhere in its Core dependency chain
        // (unlike WorkOS discovery, which fires its embedded signin_completed/tenant_none events
        // regardless of caller) — login must not have grown any of its own.
        await Assert.That(sink).IsEmpty();
    }

    // ── login: known server ──────────────────────────────────────────────────

    [Test]
    public async Task Login_known_server_none_provider_exits_zero_and_writes_the_stamp() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"None"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(Config.Root, progress, handler);

        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile> { ["solo"] = new() { ServerUrl = "https://none.example" } },
            ActiveProfile = "solo",
        });

        var exit = await LoginCommand.HandleAsync(["login"], "https://none.example", "solo", facade, progress);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(progress.Notices).Contains("Server has no authentication configured — login not required.");
        await Assert.That(ReadConfig().Profiles["solo"].AuthProvider!.Provider).IsEqualTo(AuthProvider.None);
    }

    [Test]
    public async Task Login_known_server_failure_exits_one_without_a_second_message() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"martian"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(Config.Root, progress, handler);

        var exit = await LoginCommand.HandleAsync(["login"], "https://acme.kcap.ai", ProfileConfig.DefaultName, facade, progress);

        await Assert.That(exit).IsEqualTo(1);
        // Rendered once, by the facade itself — the adapter does not re-print AuthResult.Failed.Message.
        await Assert.That(progress.Errors).Count().IsEqualTo(1);
    }

    // A foreign profile writes no config, so a failed token save leaves nothing behind — `kcap login`
    // must not exit 0 having saved nothing.
    [Test]
    public async Task Login_known_server_token_only_commit_that_saves_nothing_exits_one() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles      = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://other.example" } },
            ActiveProfile = "acme",
        });
        Directory.CreateDirectory(Path.GetDirectoryName(TokensDir)!);
        await File.WriteAllTextAsync(TokensDir, "not a directory");

        try {
            using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
            var       progress = new RecordingAuthProgress();
            var       facade   = NewFacade(Config.Root, progress, handler);

            var exit = await LoginCommand.HandleAsync(["login", "--device"], "https://acme.kcap.ai", "acme", facade, progress);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(progress.Notices.Any(n => n.StartsWith("Logged in as", StringComparison.Ordinal))).IsFalse();
        } finally {
            File.Delete(TokensDir);
        }
    }

    // ── discover result mapping (pure) ──────────────────────────────────────

    [Test]
    public async Task MapDiscoverResult_github_committed_prints_the_active_profile_line() {
        var progress = new RecordingAuthProgress();
        var result   = new AuthResult.Committed("acme", "https://acme.kcap.ai", AuthProvider.GitHubApp, "alice", []);

        var exit = LoginCommand.MapDiscoverResult(result, progress);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(progress.Notices).Contains("Logged in. Active profile: acme.");
    }

    [Test]
    public async Task MapDiscoverResult_workos_committed_does_not_duplicate_the_facades_own_notice() {
        var progress = new RecordingAuthProgress();
        var result   = new AuthResult.Committed("eventuous", "https://eventuous.kcap.ai", AuthProvider.WorkOS, "Ada", []);

        var exit = LoginCommand.MapDiscoverResult(result, progress);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(progress.Notices).IsEmpty();
    }

    [Test]
    public async Task MapDiscoverResult_retarget_prints_todays_setup_hint_and_fails() {
        var progress = new RecordingAuthProgress();
        var result   = new AuthResult.Retarget("kurrent");

        var exit = LoginCommand.MapDiscoverResult(result, progress);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(progress.Errors).Contains("Run `kcap setup kurrent` to configure that workspace.");
    }

    [Test]
    public async Task MapDiscoverResult_failed_exits_one_without_touching_progress() {
        var progress = new RecordingAuthProgress();
        var result   = new AuthResult.Failed("boom");

        var exit = LoginCommand.MapDiscoverResult(result, progress);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(progress.Notices).IsEmpty();
        await Assert.That(progress.Errors).IsEmpty();
    }

    [Test]
    public async Task MapDiscoverResult_cancelled_exits_one_without_touching_progress() {
        var progress = new RecordingAuthProgress();

        var exit = LoginCommand.MapDiscoverResult(new AuthResult.Cancelled(), progress);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(progress.Notices).IsEmpty();
        await Assert.That(progress.Errors).IsEmpty();
    }
}
