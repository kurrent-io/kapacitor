using Capacitor.App.Services.Onboarding;

namespace Capacitor.App.Tests.Unit;

public class ConsentFlipClaimsTests {
    static (string ClaimsPath, string ConfigPath) TempPaths() {
        var dir = Directory.CreateTempSubdirectory("kcap-flipclaims-").FullName;
        return (Path.Combine(dir, "consent-flip-claims.json"), Path.Combine(dir, "config.json"));
    }

    // Already canonical (explicit :443) — M1's defensive Arm canonicalization is idempotent for
    // an already-canonical caller, so round-tripping this value must not change it. The
    // deliberately-uncanonical case is covered by Arm_canonicalizes_a_raw_uncanonical_server_url_
    // so_consuming_with_the_canonical_identity_works below.
    static readonly ConsentFlipClaim Claim = new("default", "https://example.test:443");

    [Test]
    public async Task Arm_writes_a_durable_file_with_the_key() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(File.Exists(claimsPath)).IsTrue();

        var reloaded = new ConsentFlipClaims(claimsPath, configPath);
        await Assert.That(reloaded.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Arm_twice_same_key_yields_one_entry() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(store.Arm(Claim)).IsTrue();

        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    // M1 (final review): Arm defensively canonicalizes CanonicalServer at entry — a raw/uncanonical
    // URL armed here must still be found by a later TryConsume that re-resolves to the canonical
    // identity, or the claim would be stuck pending forever (the stuck-pending bug class this
    // guards against).
    [Test]
    public async Task Arm_canonicalizes_a_raw_uncanonical_server_url_so_consuming_with_the_canonical_identity_works() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        var raw = new ConsentFlipClaim("default", "HTTPS://Example.TEST:443/");
        var canonical = new ConsentFlipClaim("default", "https://example.test:443");

        await Assert.That(store.Arm(raw)).IsTrue();
        await Assert.That(store.Pending()).IsEquivalentTo([canonical]);

        var consumed = store.TryConsume(canonical, () => (canonical.Profile, canonical.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.Pending()).IsEmpty();
    }

    [Test]
    public async Task Two_distinct_identities_arm_concurrently_without_clobbering() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        var a = new ConsentFlipClaim("default", "https://a.example.test:443");
        var b = new ConsentFlipClaim("work", "https://b.example.test:443");

        var results = await Task.WhenAll(Task.Run(() => store.Arm(a)), Task.Run(() => store.Arm(b)));

        await Assert.That(results.All(r => r)).IsTrue();
        await Assert.That(store.Pending()).IsEquivalentTo([a, b]);
    }

    [Test]
    public async Task Consume_with_matching_re_resolve_removes_the_key() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.Pending()).IsEmpty();
    }

    [Test]
    public async Task Consume_with_different_resolved_daemon_name_retains_the_claim() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "other-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Consume_with_different_resolved_server_retains_the_claim() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, "https://different.test", "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Consume_with_different_resolved_profile_retains_the_claim() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => ("other-profile", Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    // Simulates a `kcap config set daemon.name` landing between claim capture and TryConsume: the re-resolve answers with the renamed daemon.
    [Test]
    public async Task Rename_injected_between_capture_and_consume_retains_the_claim() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Arm(Claim);

        var capturedDaemonName = "original-daemon";
        var liveDaemonNameAfterRename = "renamed-daemon";

        var consumed = store.TryConsume(
            Claim, () => (Claim.Profile, Claim.CanonicalServer, liveDaemonNameAfterRename), capturedDaemonName);

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Consuming_an_already_absent_claim_is_idempotently_true() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.Pending()).IsEmpty();
    }

    [Test]
    public async Task Missing_file_yields_no_pending_claims() {
        var (claimsPath, configPath) = TempPaths();
        var store = new ConsentFlipClaims(claimsPath, configPath);

        await Assert.That(store.Pending()).IsEmpty();
        await Assert.That(store.Quarantine()).IsNull();
    }

    [Test]
    public async Task Corrupt_file_is_quarantined_aside_with_content_intact_and_fresh_store_arms_fine() {
        var (claimsPath, configPath) = TempPaths();
        File.WriteAllText(claimsPath, "{not json");

        var store = new ConsentFlipClaims(claimsPath, configPath);
        var pending = store.Pending();

        await Assert.That(pending).IsEmpty();
        var quarantine = store.Quarantine();
        await Assert.That(quarantine).IsNotNull();
        await Assert.That(File.Exists(quarantine!.PreservedPath)).IsTrue();
        await Assert.That(File.ReadAllText(quarantine.PreservedPath)).IsEqualTo("{not json");
        await Assert.That(File.Exists(claimsPath)).IsFalse();

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Second_corruption_after_quarantine_uses_the_next_free_index() {
        var (claimsPath, configPath) = TempPaths();
        var dir = Path.GetDirectoryName(claimsPath)!;
        File.WriteAllText(Path.Combine(dir, "consent-flip-claims.quarantined-0.json"), "pre-existing");
        File.WriteAllText(claimsPath, "{not json");

        var store = new ConsentFlipClaims(claimsPath, configPath);
        store.Pending();

        var quarantine = store.Quarantine();
        await Assert.That(quarantine).IsNotNull();
        await Assert.That(quarantine!.PreservedPath).IsEqualTo(Path.Combine(dir, "consent-flip-claims.quarantined-1.json"));
        await Assert.That(File.ReadAllText(Path.Combine(dir, "consent-flip-claims.quarantined-0.json"))).IsEqualTo("pre-existing");
    }

    [Test]
    public async Task Write_failure_when_directory_is_read_only_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only directory is POSIX-only.");

        var dir = Directory.CreateTempSubdirectory("kcap-flipclaims-ro-").FullName;
        var claimsPath = Path.Combine(dir, "consent-flip-claims.json");
        var configPath = Path.Combine(dir, "config.json");
        var store = new ConsentFlipClaims(claimsPath, configPath);

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var ok = store.Arm(Claim);
            await Assert.That(ok).IsFalse();
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task Default_constructs_without_touching_the_filesystem() {
        // Construction only — Default() targets the real user config dir, so arming it would be non-hermetic.
        var store = ConsentFlipClaims.Default();
        await Assert.That(store).IsNotNull();
    }
}
