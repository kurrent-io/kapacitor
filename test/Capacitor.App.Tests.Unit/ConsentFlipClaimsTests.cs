using System.Runtime.Versioning;
using Capacitor.App.Services.Onboarding;

namespace Capacitor.App.Tests.Unit;

public class ConsentFlipClaimsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ClaimsPath => Config.PathTo("consent-flip-claims.json");

    // Already canonical (explicit :443) — M1's defensive Arm canonicalization is idempotent for
    // an already-canonical caller, so round-tripping this value must not change it. The
    // deliberately-uncanonical case is covered by Arm_canonicalizes_a_raw_uncanonical_server_url_
    // so_consuming_with_the_canonical_identity_works below.
    static readonly ConsentFlipClaim Claim = new("default", "https://example.test:443");

    [Test]
    public async Task Arm_writes_a_durable_file_with_the_key() {
        var store = new ConsentFlipClaims(Config.Root);

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(File.Exists(ClaimsPath)).IsTrue();

        var reloaded = new ConsentFlipClaims(Config.Root);
        await Assert.That(reloaded.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Arm_twice_same_key_yields_one_entry() {
        var store = new ConsentFlipClaims(Config.Root);

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
        var store = new ConsentFlipClaims(Config.Root);
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
        var store = new ConsentFlipClaims(Config.Root);
        var a = new ConsentFlipClaim("default", "https://a.example.test:443");
        var b = new ConsentFlipClaim("work", "https://b.example.test:443");

        var results = await Task.WhenAll(Task.Run(() => store.Arm(a)), Task.Run(() => store.Arm(b)));

        await Assert.That(results.All(r => r)).IsTrue();
        await Assert.That(store.Pending()).IsEquivalentTo([a, b]);
    }

    [Test]
    public async Task Consume_with_matching_re_resolve_removes_the_key() {
        var store = new ConsentFlipClaims(Config.Root);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.Pending()).IsEmpty();
    }

    [Test]
    public async Task Consume_with_different_resolved_daemon_name_retains_the_claim() {
        var store = new ConsentFlipClaims(Config.Root);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "other-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Consume_with_different_resolved_server_retains_the_claim() {
        var store = new ConsentFlipClaims(Config.Root);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, "https://different.test", "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Consume_with_different_resolved_profile_retains_the_claim() {
        var store = new ConsentFlipClaims(Config.Root);
        store.Arm(Claim);

        var consumed = store.TryConsume(Claim, () => ("other-profile", Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsFalse();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    // Simulates a `kcap config set daemon.name` landing between claim capture and TryConsume: the re-resolve answers with the renamed daemon.
    [Test]
    public async Task Rename_injected_between_capture_and_consume_retains_the_claim() {
        var store = new ConsentFlipClaims(Config.Root);
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
        var store = new ConsentFlipClaims(Config.Root);

        var consumed = store.TryConsume(Claim, () => (Claim.Profile, Claim.CanonicalServer, "kcap-daemon"), "kcap-daemon");

        await Assert.That(consumed).IsTrue();
        await Assert.That(store.Pending()).IsEmpty();
    }

    [Test]
    public async Task Missing_file_yields_no_pending_claims() {
        var store = new ConsentFlipClaims(Config.Root);

        await Assert.That(store.Pending()).IsEmpty();
        await Assert.That(store.Quarantine()).IsNull();
    }

    [Test]
    public async Task Corrupt_file_is_quarantined_aside_with_content_intact_and_fresh_store_arms_fine() {
        File.WriteAllText(ClaimsPath, "{not json");

        var store = new ConsentFlipClaims(Config.Root);
        var pending = store.Pending();

        await Assert.That(pending).IsEmpty();
        var quarantine = store.Quarantine();
        await Assert.That(quarantine).IsNotNull();
        await Assert.That(File.Exists(quarantine!.PreservedPath)).IsTrue();
        await Assert.That(File.ReadAllText(quarantine.PreservedPath)).IsEqualTo("{not json");
        await Assert.That(File.Exists(ClaimsPath)).IsFalse();

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(store.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Second_corruption_after_quarantine_uses_the_next_free_index() {
        var dir = Config.Directory;
        File.WriteAllText(Path.Combine(dir, "consent-flip-claims.quarantined-0.json"), "pre-existing");
        File.WriteAllText(ClaimsPath, "{not json");

        var store = new ConsentFlipClaims(Config.Root);
        store.Pending();

        var quarantine = store.Quarantine();
        await Assert.That(quarantine).IsNotNull();
        await Assert.That(quarantine!.PreservedPath).IsEqualTo(Path.Combine(dir, "consent-flip-claims.quarantined-1.json"));
        await Assert.That(File.ReadAllText(Path.Combine(dir, "consent-flip-claims.quarantined-0.json"))).IsEqualTo("pre-existing");
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_failure_when_directory_is_read_only_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only directory is POSIX-only.");

        var store = new ConsentFlipClaims(Config.Root);

        File.SetUnixFileMode(Config.Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var ok = store.Arm(Claim);
            await Assert.That(ok).IsFalse();
        } finally {
            // Restore before the fixture disposes, or the directory cannot be deleted.
            File.SetUnixFileMode(Config.Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Codex P2: a future-version file (even with a valid-looking claims array) must be quarantined,
    // never applied under v1 semantics or rewritten as v1.
    [Test]
    public async Task Future_version_file_is_quarantined_even_with_valid_looking_claims() {
        var futureVersion = """{"version":2,"claims":[{"profile":"default","server":"https://example.test:443"}]}""";
        File.WriteAllText(ClaimsPath, futureVersion);

        var store = new ConsentFlipClaims(Config.Root);
        var pending = store.Pending();

        await Assert.That(pending).IsEmpty();
        var quarantine = store.Quarantine();
        await Assert.That(quarantine).IsNotNull();
        await Assert.That(File.Exists(quarantine!.PreservedPath)).IsTrue();
        await Assert.That(File.ReadAllText(quarantine.PreservedPath)).IsEqualTo(futureVersion);
        await Assert.That(File.Exists(ClaimsPath)).IsFalse();
    }

    // The ctor is the only place the claims filename is written down; a round trip proves the file
    // lands under the root it was handed, not just that construction succeeds.
    [Test]
    public async Task Claims_file_lands_under_the_root_it_is_given() {
        var store = new ConsentFlipClaims(Config.Root);

        await Assert.That(store.Arm(Claim)).IsTrue();
        await Assert.That(File.Exists(ClaimsPath)).IsTrue();
    }
}
