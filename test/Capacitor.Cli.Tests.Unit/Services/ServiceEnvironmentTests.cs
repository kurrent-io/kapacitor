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

    // ── Gemini's project/backend configuration ───────────────────────────────
    // Why this is captured at all: a supervised daemon inherits nothing from an interactive shell, so a
    // project exported in a shell profile is invisible to a hosted Gemini agent — and Gemini reports the
    // absence with a message naming a TIER problem, which sends people to the wrong place.

    static Dictionary<string, string> GoogleSource() => new() {
        ["PATH"]                           = "/usr/bin",
        ["GOOGLE_CLOUD_PROJECT"]           = "proj",
        ["GOOGLE_CLOUD_PROJECT_ID"]        = "proj-alt",
        ["GOOGLE_CLOUD_LOCATION"]          = "us-central1",
        ["GOOGLE_GENAI_USE_VERTEXAI"]      = "true",
        ["GOOGLE_GENAI_USE_GCA"]           = "false",
        ["GOOGLE_APPLICATION_CREDENTIALS"] = "/home/u/adc.json",
        ["GOOGLE_GEMINI_BASE_URL"]         = "https://gemini.example",
        ["GOOGLE_VERTEX_BASE_URL"]         = "https://vertex.example",
        ["GOOGLE_API_KEY"]                 = "SECRET-KEY",
        ["GOOGLE_CREDENTIALS"]             = "SECRET-JSON",
    };

    [Test]
    public async Task Build_captures_the_google_configuration_off_windows() {
        var env = ServiceEnvironment.Build(profileName: null, source: GoogleSource(), isWindows: false);

        foreach (var k in new[] { "GOOGLE_CLOUD_PROJECT", "GOOGLE_CLOUD_PROJECT_ID", "GOOGLE_CLOUD_LOCATION",
                                  "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_GENAI_USE_GCA",
                                  "GOOGLE_APPLICATION_CREDENTIALS", "GOOGLE_GEMINI_BASE_URL",
                                  "GOOGLE_VERTEX_BASE_URL" })
            await Assert.That(env.ContainsKey(k)).IsTrue();
    }

    /// <summary>The direction that must never regress. A test asserting only the captures would stay green
    /// if someone widened the allowlist to <c>GOOGLE_*</c>, which is the one change that must not pass.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Build_never_captures_the_google_secrets(bool isWindows) {
        var env = ServiceEnvironment.Build(profileName: null, source: GoogleSource(), isWindows: isWindows);

        foreach (var k in ServiceEnvironment.NeverCapturedKeys)
            await Assert.That(env.ContainsKey(k)).IsFalse();

        // And not smuggled in under another name.
        await Assert.That(env.Values.Any(v => v.Contains("SECRET"))).IsFalse();
    }

    /// <summary>
    /// The platform split. A credential PATH and a base URL that may carry userinfo or a query token are
    /// secret-capable, and Unix bounds that with a guarantee this code enforces (ServiceFiles writes 0600
    /// and re-checks the handle). Every permission path in ServiceFiles returns early on Windows —
    /// "ACL-governed, inherited from the user profile" — so there is no equivalent guarantee there and
    /// those three are excluded, exactly as KCAP_COPILOT_TOKEN_CMD is.
    /// </summary>
    [Test]
    public async Task Build_excludes_the_secret_capable_google_values_on_windows() {
        var env = ServiceEnvironment.Build(profileName: null, source: GoogleSource(), isWindows: true);

        await Assert.That(env.ContainsKey("GOOGLE_APPLICATION_CREDENTIALS")).IsFalse();
        await Assert.That(env.ContainsKey("GOOGLE_GEMINI_BASE_URL")).IsFalse();
        await Assert.That(env.ContainsKey("GOOGLE_VERTEX_BASE_URL")).IsFalse();

        // ...while the non-secret configuration still reaches the unit, so hosted Gemini keeps working
        // there for a project-scoped login.
        await Assert.That(env["GOOGLE_CLOUD_PROJECT"]).IsEqualTo("proj");
        await Assert.That(env["GOOGLE_GENAI_USE_VERTEXAI"]).IsEqualTo("true");
    }

    // ── exact-value contract: KCAP_CONSENT_SEED_DEFAULT / KCAP_EXPECT_SERVER_URL ─────────────
    // An empty value for either of these is a deliberate refusal (spec), not absence — unlike
    // every other key, a present-but-empty value must still be baked so it propagates and fails
    // closed at the gate/daemon instead of silently vanishing from the unit.

    [Test]
    public async Task Build_bakes_a_present_but_empty_consent_seed_directive_verbatim() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_CONSENT_SEED_DEFAULT"] = "" },
            isWindows: false);

        await Assert.That(env.ContainsKey("KCAP_CONSENT_SEED_DEFAULT")).IsTrue();
        await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("");
    }

    [Test]
    public async Task Build_bakes_a_present_but_empty_expect_server_url_verbatim() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_EXPECT_SERVER_URL"] = "" },
            isWindows: false);

        await Assert.That(env.ContainsKey("KCAP_EXPECT_SERVER_URL")).IsTrue();
        await Assert.That(env["KCAP_EXPECT_SERVER_URL"]).IsEqualTo("");
    }

    [Test]
    public async Task Build_still_omits_the_seed_directive_and_expectation_when_truly_absent() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
            isWindows: false);

        await Assert.That(env.ContainsKey("KCAP_CONSENT_SEED_DEFAULT")).IsFalse();
        await Assert.That(env.ContainsKey("KCAP_EXPECT_SERVER_URL")).IsFalse();
    }

    [Test]
    public async Task Build_omits_absent_google_variables_rather_than_writing_empties() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["GOOGLE_CLOUD_PROJECT"] = "" },
            isWindows: false);

        await Assert.That(env.ContainsKey("GOOGLE_CLOUD_PROJECT")).IsFalse();
        await Assert.That(env.ContainsKey("GOOGLE_CLOUD_LOCATION")).IsFalse();
    }

    // ── unattended-reviewer consent flags ─────────────────────────────────────
    //
    // These have no config-file or profile binding: the daemon reads them from its own environment and
    // nowhere else. A unit that drops them is therefore a reviewer that cannot be turned on at all for a
    // supervised daemon, silently — which is the failure this whole group exists to pin.

    static Dictionary<string, string> ConsentSource() => new() {
        ["PATH"]                                 = "/usr/bin",
        ["KCAP_GEMINI_UNATTENDED_REVIEWER"]      = "1",
        ["KCAP_KIRO_UNATTENDED_REVIEWER"]        = "true",
        ["KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER"] = "yes",
    };

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Build_carries_every_reviewer_consent_flag_on_every_platform(bool isWindows) {
        var env = ServiceEnvironment.Build(profileName: null, source: ConsentSource(), isWindows: isWindows);

        await Assert.That(env["KCAP_GEMINI_UNATTENDED_REVIEWER"]).IsEqualTo("1")
            .Because("a supervised daemon inherits nothing from the installing shell");
        await Assert.That(env["KCAP_KIRO_UNATTENDED_REVIEWER"]).IsEqualTo("true")
            .Because("binding one reviewer's consent and not another just moves the hole");
        await Assert.That(env["KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER"]).IsEqualTo("yes")
            .Because("Antigravity's consent has no config or profile binding either — dropping it here "
                   + "makes the reviewer unreachable for a service-installed daemon, silently");
    }

    /// <summary>A flag the installing environment never set must not appear — capture carries an
    /// existing choice into the unit, it never manufactures one.</summary>
    [Test]
    public async Task Build_never_invents_a_consent_flag() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
            isWindows: false);

        foreach (var key in ServiceEnvironment.ReviewerConsentKeys)
            await Assert.That(env.ContainsKey(key)).IsFalse();
    }

    /// <summary>
    /// EVERY reviewer's opt-out reaches a service unit, on BOTH platforms, with a DISABLING value.
    ///
    /// <para>This is the one test standing behind the ungating's load-bearing claim: unattended reviewers
    /// default to enabled, so the operator's explicit opt-out is the compensating control, and it is only
    /// real if it survives the supported install path. Before the flip a dropped variable meant a reviewer
    /// that could not be turned on — safe. Now it means one that cannot be turned OFF.</para>
    ///
    /// <para>Ranges over the registry rather than four literals deliberately: a vendor added later is
    /// covered here the day it is added, which a hardcoded list would not do. The sibling test above
    /// asserts the enabling direction; this one asserts the direction that now matters.</para>
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Every_reviewer_opt_out_survives_a_service_install(bool isWindows) {
        var source = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        foreach (var key in ServiceEnvironment.ReviewerConsentKeys) source[key] = "0";

        var env = ServiceEnvironment.Build(profileName: null, source: source, isWindows: isWindows);

        await Assert.That(ServiceEnvironment.ReviewerConsentKeys).IsNotEmpty()
            .Because("an empty registry would make every assertion below vacuously true");

        foreach (var key in ServiceEnvironment.ReviewerConsentKeys)
            await Assert.That(env.TryGetValue(key, out var v) ? v : null).IsEqualTo("0")
                .Because($"{key} is the operator's only lever for turning that reviewer off; a supervised "
                       + "daemon reads it from the unit or not at all");
    }

    [Test]
    public async Task CarriedConsentFlags_names_what_the_unit_actually_got() {
        var env = ServiceEnvironment.Build(profileName: null, source: ConsentSource(), isWindows: false);

        await Assert.That(ServiceEnvironment.CarriedConsentFlags(env))
            .IsEquivalentTo(new[] {
                "KCAP_GEMINI_UNATTENDED_REVIEWER",
                "KCAP_KIRO_UNATTENDED_REVIEWER",
                "KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER"
            });
    }

    /// <summary>Reads the BUILT environment, not the ambient one — so the install notice cannot claim a
    /// capture that an empty value (or a future platform exclusion) dropped on the way in.</summary>
    [Test]
    public async Task CarriedConsentFlags_reports_nothing_when_the_flag_was_blank() {
        var env = ServiceEnvironment.Build(
            profileName: null,
            source: new Dictionary<string, string> {
                ["PATH"] = "/usr/bin", ["KCAP_GEMINI_UNATTENDED_REVIEWER"] = "",
            },
            isWindows: false);

        await Assert.That(ServiceEnvironment.CarriedConsentFlags(env)).IsEmpty();
    }

    [Test]
    public async Task Agy_adc_auth_is_carried_as_a_non_secret_config_key() {
        var env = ServiceEnvironment.Build("prof", new Dictionary<string, string> {
            ["PATH"]                  = "/usr/bin",
            ["AGY_ADC_AUTH"]          = "1",
            ["GOOGLE_CLOUD_PROJECT"]  = "proj"
        });

        // AGY_ADC_AUTH is a boolean switch, not a credential, so it belongs in the
        // always-carried config list beside GOOGLE_GENAI_USE_VERTEXAI — not in the
        // secret-capable list that is withheld on Windows.
        await Assert.That(env["AGY_ADC_AUTH"]).IsEqualTo("1");
    }

    [Test]
    public async Task Agy_adc_auth_is_carried_on_windows_too() {
        var env = ServiceEnvironment.Build("prof",
            new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["AGY_ADC_AUTH"] = "1" },
            isWindows: true);

        await Assert.That(env["AGY_ADC_AUTH"]).IsEqualTo("1");
    }
}
