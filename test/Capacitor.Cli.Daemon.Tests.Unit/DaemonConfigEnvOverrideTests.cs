namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Test plan item 8: verifies <c>KCAP_COPILOT_PATH</c>/<c>KCAP_KIRO_PATH</c>/
/// <c>KCAP_OPENCODE_PATH</c>/<c>KCAP_GEMINI_PATH</c> each override their respective
/// <see cref="DaemonConfig"/> property, mirroring <c>DaemonRunner.RunAsync</c>'s
/// <c>KCAP_CURSOR_PATH</c>/<c>KCAP_CURSOR_MODEL</c> env-override block.
///
/// <b>Deviation from the spec's literal instruction</b> ("mirror whatever test already covers
/// KCAP_CURSOR_PATH"): no existing test in this project exercises that env-var-read block directly
/// — it lives inline in <c>DaemonRunner.RunAsync</c>, which builds a full DI host, making it
/// impractical to invoke in isolation without a larger refactor out of this spec's scope. Instead
/// this mirrors the ESTABLISHED "simulate the DaemonRunner logic block in isolation" pattern this
/// same test directory already uses for the profile-wiring block
/// (<see cref="DaemonConfigProfileTests"/>'s <c>ApplyProfileSettings</c> helper), applied here to
/// the env-override block instead — a verbatim copy of <c>DaemonRunner.RunAsync</c>'s four new
/// conditionals, so a future edit to either one is a visible diff against the other, same as that
/// established pattern already guarantees for the profile block.
/// </summary>
public class DaemonConfigEnvOverrideTests {
    // Mirrors DaemonRunner.RunAsync's four new KCAP_*_PATH env-override conditionals verbatim.
    static DaemonConfig ApplyEnvOverrides(DaemonConfig config,
            string? copilotPath, string? kiroPath, string? openCodePath, string? geminiPath) {
        if (copilotPath is { Length: > 0 })
            config.CopilotPath = copilotPath;

        if (kiroPath is { Length: > 0 })
            config.KiroPath = kiroPath;

        if (openCodePath is { Length: > 0 })
            config.OpenCodePath = openCodePath;

        if (geminiPath is { Length: > 0 })
            config.GeminiPath = geminiPath;

        return config;
    }

    // ── defaults ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Defaults_AreBareCommands() {
        var config = new DaemonConfig();

        await Assert.That(config.CopilotPath).IsEqualTo("copilot");
        // Still a bare command, but "kiro-cli" — the shipped binary name — now that an ACP descriptor
        // consumes this field and availability is CliResolver.Exists(KiroPath).
        await Assert.That(config.KiroPath).IsEqualTo("kiro-cli");
        await Assert.That(config.OpenCodePath).IsEqualTo("opencode");
        await Assert.That(config.GeminiPath).IsEqualTo("gemini");
    }

    // ── KCAP_COPILOT_PATH ─────────────────────────────────────────────────────

    [Test]
    public async Task CopilotPath_EnvVarSet_OverridesDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: "/opt/copilot/bin/copilot", kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.CopilotPath).IsEqualTo("/opt/copilot/bin/copilot");
    }

    [Test]
    public async Task CopilotPath_EnvVarEmpty_KeepsDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: "", kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.CopilotPath).IsEqualTo("copilot");
    }

    [Test]
    public async Task CopilotPath_EnvVarUnset_KeepsDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.CopilotPath).IsEqualTo("copilot");
    }

    // ── KCAP_KIRO_PATH ────────────────────────────────────────────────────────

    [Test]
    public async Task KiroPath_EnvVarSet_OverridesDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: "/opt/kiro/bin/kiro", openCodePath: null, geminiPath: null);

        await Assert.That(config.KiroPath).IsEqualTo("/opt/kiro/bin/kiro");
    }

    [Test]
    public async Task KiroPath_EnvVarUnset_KeepsDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.KiroPath).IsEqualTo("kiro-cli");
    }

    // ── KCAP_KIRO_MODEL ───────────────────────────────────────────────────────

    // Mirrors DaemonRunner.RunAsync's KCAP_KIRO_MODEL conditional verbatim, same pattern as
    // ApplyEnvOverrides above (kept separate so the four-path helper's signature stays a verbatim
    // copy of the block it mirrors).
    static DaemonConfig ApplyKiroModelOverride(DaemonConfig config, string? kiroModel) {
        if (kiroModel is { Length: > 0 })
            config.KiroModel = kiroModel;

        return config;
    }

    /// <summary>Unlike <c>CursorModel</c>'s <c>"claude-sonnet-4-5"</c>, the default is NULL:
    /// zero-configuration Kiro hosting keeps the vendor's own default model with nothing requested
    /// and nothing reported, exactly the pre-override behaviour.</summary>
    [Test]
    public async Task KiroModel_DefaultsToNull_NoDaemonWideDefaultModel() {
        await Assert.That(new DaemonConfig().KiroModel).IsNull();
    }

    [Test]
    public async Task KiroModel_EnvVarSet_OverridesDefault() {
        var config = ApplyKiroModelOverride(new DaemonConfig(), kiroModel: "claude-haiku-4.5");

        await Assert.That(config.KiroModel).IsEqualTo("claude-haiku-4.5");
    }

    [Test]
    public async Task KiroModel_EnvVarEmpty_KeepsNull() {
        var config = ApplyKiroModelOverride(new DaemonConfig(), kiroModel: "");

        await Assert.That(config.KiroModel).IsNull();
    }

    // ── KCAP_OPENCODE_PATH ────────────────────────────────────────────────────

    [Test]
    public async Task OpenCodePath_EnvVarSet_OverridesDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: "/opt/opencode/bin/opencode", geminiPath: null);

        await Assert.That(config.OpenCodePath).IsEqualTo("/opt/opencode/bin/opencode");
    }

    [Test]
    public async Task OpenCodePath_EnvVarUnset_KeepsDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.OpenCodePath).IsEqualTo("opencode");
    }

    // ── KCAP_GEMINI_PATH ──────────────────────────────────────────────────────

    [Test]
    public async Task GeminiPath_EnvVarSet_OverridesDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: null, geminiPath: "/opt/gemini/bin/gemini");

        await Assert.That(config.GeminiPath).IsEqualTo("/opt/gemini/bin/gemini");
    }

    [Test]
    public async Task GeminiPath_EnvVarUnset_KeepsDefault() {
        var config = ApplyEnvOverrides(new DaemonConfig(), copilotPath: null, kiroPath: null, openCodePath: null, geminiPath: null);

        await Assert.That(config.GeminiPath).IsEqualTo("gemini");
    }

    // ── all four simultaneously ───────────────────────────────────────────────

    [Test]
    public async Task AllFourPaths_EnvVarsSet_OverrideAllDefaults() {
        var config = ApplyEnvOverrides(new DaemonConfig(),
            copilotPath: "/a/copilot", kiroPath: "/b/kiro", openCodePath: "/c/opencode", geminiPath: "/d/gemini");

        await Assert.That(config.CopilotPath).IsEqualTo("/a/copilot");
        await Assert.That(config.KiroPath).IsEqualTo("/b/kiro");
        await Assert.That(config.OpenCodePath).IsEqualTo("/c/opencode");
        await Assert.That(config.GeminiPath).IsEqualTo("/d/gemini");
    }
}
