using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Pure decision-core tests for <see cref="ServiceVerify.EvaluateStartGate"/>.
/// No filesystem, no lock, no manager — every case is driven purely through the function's own
/// parameters, per the task brief verbatim.</summary>
public class ServiceVerifyStartGateTests {
    [Test]
    public async Task Gate_inactive_without_invoking_directive() {
        var r = ServiceVerify.EvaluateStartGate(new Dictionary<string, string>(), "/b", "/b", _ => null);
        await Assert.That(r).IsNull();
    }

    // Exact-value contract (spec): an empty invoking directive is a deliberate refusal, not
    // absence — only a truly-null env value passes the gate through untouched.
    [Test]
    [Arguments("")]
    [Arguments("allow")]
    [Arguments("deny")]
    [Arguments("Prompt")]
    public async Task Non_prompt_invoking_directive_including_empty_is_directive_invalid_before_touching_the_unit(string invoking) {
        // The unit env is never even consulted — if it were, the empty dict below would report
        // DirectiveMissing instead, so DirectiveInvalid here proves the invoking-side check fires first.
        var r = ServiceVerify.EvaluateStartGate(new Dictionary<string, string>(), "/b", "/b",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? invoking : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveInvalid);
    }

    [Test]
    public async Task Missing_unit_directive_is_directive_missing() {
        var r = ServiceVerify.EvaluateStartGate(
            new Dictionary<string, string>(),  // unit bakes nothing
            "/b", "/b", k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveMissing);
    }

    [Test]
    public async Task Unit_directive_with_wrong_value_is_directive_invalid() {
        var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "allow" };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveInvalid);
    }

    // A present-but-empty unit directive is a deliberate (if broken) value, not absence — it must
    // classify as DirectiveInvalid, the same bucket a wrong value gets, never DirectiveMissing
    // (which is reserved for the key being entirely absent from the unit).
    [Test]
    public async Task Unit_directive_present_but_empty_is_directive_invalid_not_missing() {
        var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "" };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveInvalid);
    }

    [Test]
    public async Task Digest_mismatch_at_canonical_sibling_is_package_inconsistent_elsewhere_foreign() {
        // placeholder digest in test builds → Matches() false for any file:
        var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt" };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt", _ => null };

        var same = ServiceVerify.EvaluateStartGate(unit, "/opt/kcap/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
        await Assert.That(same).IsEqualTo(StartGateReason.PackageInconsistent);

        var other = ServiceVerify.EvaluateStartGate(unit, "/somewhere/else/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
        await Assert.That(other).IsEqualTo(StartGateReason.ForeignBinary);
    }

    // ── SameBinaryPath: OS-aware comparison split out of the digest-mismatch branch above ──

    [Test]
    public async Task Ordinal_comparison_treats_case_only_difference_as_a_different_path() {
        var r = ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/opt/kcap/KCAP-DAEMON", StringComparison.Ordinal);
        await Assert.That(r).IsFalse();
    }

    [Test]
    public async Task OrdinalIgnoreCase_comparison_treats_case_only_difference_as_the_same_path() {
        var r = ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/opt/kcap/KCAP-DAEMON", StringComparison.OrdinalIgnoreCase);
        await Assert.That(r).IsTrue();
    }

    [Test]
    public async Task Both_comparisons_agree_a_genuinely_different_path_is_different() {
        await Assert.That(ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/somewhere/else/kcap-daemon", StringComparison.Ordinal)).IsFalse();
        await Assert.That(ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/somewhere/else/kcap-daemon", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Stale_unit_expectation_is_identity_mismatch() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",              // unit resolves S
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://t.example",  // fresh invocation expects T
            _ => null };
        // digest can't pass in test builds — inject a digest-pass seam for identity-only tests:
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    // ── fail-closed on absent required identity evidence (spec §3.4(b)) ──

    [Test]
    public async Task Fully_matching_identity_passes() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsNull();
    }

    // A present-but-empty baked unit expectation is a deliberate value, not absence — it must
    // MISMATCH rather than be silently skipped the way a null/missing value never would be either.
    [Test]
    public async Task Unit_baked_empty_expectation_is_identity_mismatch() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",
            ["KCAP_EXPECT_SERVER_URL"] = "",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    [Test]
    public async Task Unit_missing_baked_expectation_is_identity_mismatch() {
        // The unit resolves a server (KCAP_URL) and the invoking env carries a full expectation,
        // but the unit itself never baked KCAP_EXPECT_SERVER_URL — absent required evidence must
        // fail closed, not be silently skipped as "no assertion to make".
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    [Test]
    public async Task Invoking_env_missing_profile_is_identity_mismatch() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            // no KCAP_PROFILE at all
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    [Test]
    public async Task Invoking_env_missing_expectation_is_identity_mismatch() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            // no KCAP_EXPECT_SERVER_URL at all
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    [Test]
    public async Task Unresolvable_unit_server_is_identity_mismatch() {
        // The unit bakes an expectation and NO KCAP_URL and NO KCAP_PROFILE — there is genuinely
        // nothing to resolve its own identity from, so it must fail closed rather than let a
        // three-way comparison with only two live candidates silently pass.
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }

    [Test]
    public async Task Unreadable_config_directory_in_place_of_file_is_evidence_unreadable() {
        var configDir = Directory.CreateTempSubdirectory("kcap-gate-cfg-").FullName;
        // A directory sitting exactly where config.json belongs: File.Exists alone reads as
        // absent, but this must surface as unreadable EVIDENCE (28/evidence_unreadable), never
        // silently treated the same as an unconfigured profile (which would be identity_mismatch).
        Directory.CreateDirectory(Path.Combine(configDir, "config.json"));

        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "work",
            ["KCAP_CONFIG_DIR"] = configDir,
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
            // no KCAP_URL — forces the BakedProfileServerUrl fallback that reads config.json
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "work",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };

        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.EvidenceUnreadable);
    }

    /// <summary>A malformed (unparseable) config.json is unreadable EVIDENCE for the gate — same
    /// bucket as the directory-in-place-of-file case above — never silently treated as an
    /// unconfigured profile (identity_mismatch). Pins <c>ConfigMutator.TryLoadPure</c>'s hardened
    /// contract: malformed content is now a genuine failure, not degrade-to-defaults.</summary>
    [Test]
    public async Task Malformed_config_file_is_evidence_unreadable() {
        var configDir = Directory.CreateTempSubdirectory("kcap-gate-cfg-").FullName;
        File.WriteAllText(Path.Combine(configDir, "config.json"), "{not json");

        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "work",
            ["KCAP_CONFIG_DIR"] = configDir,
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
            // no KCAP_URL — forces the BakedProfileServerUrl fallback that reads config.json
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "work",
            "KCAP_EXPECT_SERVER_URL" => "https://s.example",
            _ => null };

        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.EvidenceUnreadable);
    }
}
