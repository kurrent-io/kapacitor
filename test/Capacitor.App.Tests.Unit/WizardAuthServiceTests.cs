using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// The wizard's single-flight sign-in driver and the decision-7 claim-arming hook it hands the
/// façade. Every claims assertion drives the REAL <see cref="ConsentFlipClaims"/> on a temp path —
/// a recording double would prove nothing about durability or about the false-return path that
/// must block a commit.
/// </summary>
public class WizardAuthServiceTests {
    static readonly AuthIdentity Acme = new("acme", "https://acme.example:443");
    static readonly AuthIdentity Work = new("work", "https://work.example:443");

    static AuthResult.Committed Committed(params AuthIdentity[] identities) =>
        new("acme", Acme.CanonicalServer, AuthProvider.None, "someone", identities);

    static (ConsentFlipClaims Claims, string ClaimsPath) TempClaims(TempConfigRoot config) =>
        (new ConsentFlipClaims(config.Root), config.PathTo("consent-flip-claims.json"));

    static (ConsentFlipClaims Claims, string Dir) ReadOnlyClaims(TempDir tmp) {
        var dir = tmp.CreateDir("wizardauth-ro");
        return (new ConsentFlipClaims(new ConfigRoot(dir)), dir);
    }

    [UnsupportedOSPlatform("windows")]
    static void SetWritable(string dir, bool writable) =>
        File.SetUnixFileMode(dir, writable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserExecute);

    // Mirrors CommitBoundary's catch shape so the scripted operation classifies a hook failure
    // exactly as the façade would: OperationCanceledException is a cancel, anything else is Failed.
    static async Task<AuthResult> ScriptedCommitAsync(
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task> hook,
            IReadOnlyList<AuthIdentity> identities, Action published, CancellationToken ct) {
        try {
            await hook(identities, ct);
        } catch (OperationCanceledException) {
            return new AuthResult.Cancelled();
        } catch (Exception ex) {
            return new AuthResult.Failed(ex.Message);
        }

        published();
        return Committed([.. identities]);
    }

    // ── single-flight ────────────────────────────────────────────────────────

    [Test]
    public async Task No_attempt_has_run_yet_so_current_is_null_and_the_service_is_quiesced() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var service = new WizardAuthService((_, _) => Task.FromResult<AuthResult>(Committed(Acme)));

        await Assert.That(service.Current).IsNull();
        await service.QuiescedAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Begin_runs_the_operation_with_the_intent_and_publishes_it_as_current() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        ConnectIntent? seen = null;
        var service = new WizardAuthService((intent, _) => {
            seen = intent;
            return Task.FromResult<AuthResult>(Committed(Acme));
        });

        var attempt = service.Begin(new ConnectIntent.Paste("acme.example"));
        var result  = await attempt.Result.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(seen).IsEqualTo(new ConnectIntent.Paste("acme.example"));
        await Assert.That(service.Current).IsSameReferenceAs(attempt);
        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
    }

    [Test]
    public async Task Begin_while_an_attempt_is_live_throws() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var gate = new TaskCompletionSource<AuthResult>();
        var service = new WizardAuthService((_, _) => gate.Task);

        var attempt = service.Begin(new ConnectIntent.Create());

        Assert.Throws<InvalidOperationException>(() => service.Begin(new ConnectIntent.Create()));

        gate.SetResult(Committed(Acme));
        await attempt.Result.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Begin_is_admitted_again_once_the_previous_result_completed() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var gate = new TaskCompletionSource<AuthResult>();
        var starts = 0;
        var service = new WizardAuthService((_, _) => {
            starts++;
            return starts == 1 ? gate.Task : Task.FromResult<AuthResult>(Committed(Work));
        });

        var first = service.Begin(new ConnectIntent.Create());
        gate.SetResult(new AuthResult.Failed("nope"));
        await first.Result.WaitAsync(TimeSpan.FromSeconds(5));

        var second = service.Begin(new ConnectIntent.Discover(AuthProvider.WorkOS));

        await Assert.That(await second.Result.WaitAsync(TimeSpan.FromSeconds(5))).IsTypeOf<AuthResult.Committed>();
        await Assert.That(service.Current).IsSameReferenceAs(second);
        await Assert.That(starts).IsEqualTo(2);
    }

    // ── claim-arming hook ────────────────────────────────────────────────────

    [Test]
    public async Task The_hook_arms_one_claim_per_identity() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);

        await WizardAuthService.ArmingHook(claims)([Acme, Work], CancellationToken.None);

        await Assert.That(claims.Pending()).IsEquivalentTo([
            new ConsentFlipClaim(Acme.Profile, Acme.CanonicalServer),
            new ConsentFlipClaim(Work.Profile, Work.CanonicalServer)
        ]);
    }

    [Test]
    public async Task The_arming_hook_binds_to_the_claims_store_it_was_built_over() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);

        await WizardAuthService.ArmingHook(claims)([Acme], CancellationToken.None);

        await Assert.That(claims.Pending()).IsEquivalentTo([new ConsentFlipClaim(Acme.Profile, Acme.CanonicalServer)]);
    }

    // A cancelled token must not turn arming into a half-written claim: the hook never hands the
    // token to the store, so an already-cancelled attempt still arms rather than throwing an OCE
    // the façade's boundary would read as a user cancel.
    [Test]
    public async Task The_hook_arms_even_under_an_already_cancelled_token() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await WizardAuthService.ArmingHook(claims)([Acme], cts.Token);

        await Assert.That(claims.Pending()).IsEquivalentTo([new ConsentFlipClaim(Acme.Profile, Acme.CanonicalServer)]);
    }

    // InvalidOperationException, never OperationCanceledException: the boundary maps ANY OCE from
    // this hook to Cancelled, which would silently render a store failure as "the user backed out".
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_false_arm_throws_claim_arm_failed_rather_than_a_cancellation() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only directory is POSIX-only.");

        using var tmp = new TempDir();
        var (claims, dir) = ReadOnlyClaims(tmp);
        SetWritable(dir, false);
        try {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WizardAuthService.ArmingHook(claims)([Acme], CancellationToken.None));

            await Assert.That(ex!.Message).IsEqualTo("claim_arm_failed");
        } finally {
            SetWritable(dir, true);
        }
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_false_arm_stops_the_operation_before_anything_is_published() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only directory is POSIX-only.");

        using var tmp = new TempDir();
        var (claims, dir) = ReadOnlyClaims(tmp);
        var published = false;
        var service = new WizardAuthService(
            (_, ct) => ScriptedCommitAsync(WizardAuthService.ArmingHook(claims), [Acme], () => published = true, ct));

        SetWritable(dir, false);
        try {
            var result = await service.Begin(new ConnectIntent.Create()).Result.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(result).IsTypeOf<AuthResult.Failed>();
            await Assert.That(((AuthResult.Failed)result).Message).IsEqualTo("claim_arm_failed");
            await Assert.That(published).IsFalse();
        } finally {
            SetWritable(dir, true);
        }
    }

    // Corruption discovered mid-attempt is quarantined aside and the claim lands in the fresh
    // store — a wizard sign-in is never rejected because a previous claims file was unreadable.
    [Test]
    public async Task Arming_into_a_corrupt_store_lands_in_the_fresh_store_and_quarantines_the_old_one() {
        using var config = new TempConfigRoot();
        var (_, claimsPath) = TempClaims(config);
        File.WriteAllText(claimsPath, "{not json");
        var claims  = new ConsentFlipClaims(config.Root);
        var service = new WizardAuthService(
            (_, ct) => ScriptedCommitAsync(WizardAuthService.ArmingHook(claims), [Acme], () => { }, ct));

        var result = await service.Begin(new ConnectIntent.Create()).Result.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(claims.Pending()).IsEquivalentTo([new ConsentFlipClaim(Acme.Profile, Acme.CanonicalServer)]);
        await Assert.That(claims.Quarantine()).IsNotNull();
    }

    // ── cancellation and the close handoff ───────────────────────────────────

    [Test]
    public async Task Cancel_before_the_boundary_yields_cancelled_and_quiesces() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var started = new TaskCompletionSource();
        var service = new WizardAuthService(async (_, ct) => {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Committed(Acme);
        });

        var attempt = service.Begin(new ConnectIntent.Create());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attempt.Cancel();

        await Assert.That(await attempt.Result.WaitAsync(TimeSpan.FromSeconds(5))).IsTypeOf<AuthResult.Cancelled>();
        await service.QuiescedAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Past the boundary the façade runs its publications under CancellationToken.None and answers
    // Committed; the service just delivers that answer to the close path.
    [Test]
    public async Task Cancel_after_the_boundary_still_yields_committed() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var started      = new TaskCompletionSource();
        var cancelSeen   = new TaskCompletionSource();
        var service = new WizardAuthService(async (_, ct) => {
            using var registration = ct.Register(() => cancelSeen.TrySetResult());
            started.SetResult();
            await cancelSeen.Task;
            return Committed(Acme);
        });

        var attempt = service.Begin(new ConnectIntent.Create());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        attempt.Cancel();

        await Assert.That(await attempt.Result.WaitAsync(TimeSpan.FromSeconds(5))).IsTypeOf<AuthResult.Committed>();
    }

    [Test]
    public async Task QuiescedAsync_waits_for_a_live_attempt_to_settle() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var gate = new TaskCompletionSource<AuthResult>();
        var service = new WizardAuthService((_, _) => gate.Task);

        service.Begin(new ConnectIntent.Create());
        var quiesced = service.QuiescedAsync();

        await Assert.That(quiesced.IsCompleted).IsFalse();

        gate.SetResult(Committed(Acme));
        await quiesced.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // The close path awaits Result during shutdown, so an operation bug must arrive as an outcome
    // rather than as a faulted task nobody is positioned to catch.
    [Test]
    public async Task An_operation_that_throws_is_reported_as_failed() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var service = new WizardAuthService((_, _) => throw new InvalidOperationException("boom"));

        var result = await service.Begin(new ConnectIntent.Create()).Result.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(((AuthResult.Failed)result).Message).IsEqualTo("boom");
    }

    [Test]
    public async Task Cancelling_a_settled_attempt_is_a_no_op() {
        using var config = new TempConfigRoot();
        var (claims, _) = TempClaims(config);
        var service = new WizardAuthService((_, _) => Task.FromResult<AuthResult>(Committed(Acme)));

        var attempt = service.Begin(new ConnectIntent.Create());
        await attempt.Result.WaitAsync(TimeSpan.FromSeconds(5));
        attempt.Cancel();

        await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Committed>();
    }
}

/// <summary>
/// <c>App.ResolveConsentFlipIdentity</c> runs INSIDE <see cref="ConsentFlipClaims.TryConsume"/>'s
/// two-lock section, so an unreadable config must fail closed to an identity that matches nothing
/// rather than resolve to plausible defaults or let an exception escape the locks.
/// </summary>
public class ConsentFlipIdentityFailClosedTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Test]
    public async Task Unreadable_config_resolves_an_identity_that_matches_nothing() {
        File.WriteAllText(ConfigPath, "{not json");

        await Assert.That(AppUnderTest.ResolveConsentFlipIdentity(Config.Root)).IsEqualTo(("", "", ""));
    }

    [Test]
    public async Task Unreadable_config_retains_the_claim_instead_of_throwing_inside_TryConsume() {
        File.WriteAllText(ConfigPath, "{not json");
        var claims = new ConsentFlipClaims(Config.Root);
        var claim  = new ConsentFlipClaim("acme", "https://acme.example:443");
        claims.Arm(claim);

        var consumed = claims.TryConsume(claim, () => AppUnderTest.ResolveConsentFlipIdentity(Config.Root), "acme-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(claims.Pending()).IsEquivalentTo([claim]);
    }

    // Control: a readable config still resolves the live identity, so the fail-closed arm above
    // cannot be mistaken for "this delegate never resolves anything".
    [Test]
    public async Task Readable_config_still_resolves_and_consumes_the_matching_claim() {
        var profile = new Profile { ServerUrl = "https://acme.example", Daemon = new DaemonSettings { Name = "acme-daemon" } };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(
            new ProfileConfig { ActiveProfile = "acme", Profiles = new() { ["acme"] = profile } },
            ProfileConfigJsonContext.Default.ProfileConfig));
        var claims = new ConsentFlipClaims(Config.Root);
        var claim  = new ConsentFlipClaim("acme", ServerIdentity.Canonicalize("https://acme.example")!);
        claims.Arm(claim);

        var consumed = claims.TryConsume(claim, () => AppUnderTest.ResolveConsentFlipIdentity(Config.Root), "acme-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(claims.Pending()).IsEmpty();
    }
}
