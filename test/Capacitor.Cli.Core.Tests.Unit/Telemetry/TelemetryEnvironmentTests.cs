using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

// Pure over an injected environment, so these run without touching the real process env and
// without [NotInParallel] — the same seam TelemetrySettings uses for the opt-out precedence table.
public class TelemetryEnvironmentTests {
    static Dictionary<string, string?> Env(string key, string? value) => new() { [key] = value };

    // Every provider below is one whose runners the CLI can plausibly execute on. Jenkins,
    // TeamCity and Azure Pipelines are the load-bearing entries: none of them sets the generic
    // `CI` variable, so before this list they were counted as human machines.
    [Test]
    [Arguments("CI")]
    [Arguments("GITHUB_ACTIONS")]
    [Arguments("GITLAB_CI")]
    [Arguments("BUILDKITE")]
    [Arguments("CIRCLECI")]
    [Arguments("TRAVIS")]
    [Arguments("JENKINS_URL")]
    [Arguments("TEAMCITY_VERSION")]
    [Arguments("TF_BUILD")]
    [Arguments("APPVEYOR")]
    [Arguments("BITBUCKET_BUILD_NUMBER")]
    [Arguments("CODEBUILD_BUILD_ID")]
    [Arguments("DRONE")]
    [Arguments("WOODPECKER_CI")]
    [Arguments("SEMAPHORE")]
    [Arguments("HEROKU_TEST_RUN_ID")]
    [Arguments("NETLIFY")]
    [Arguments("VERCEL")]
    public async Task Known_provider_variable_marks_the_run_as_ci(string variable) =>
        await Assert.That(TelemetryEnvironment.IsCi(Env(variable, "1"))).IsTrue();

    [Test]
    public async Task Empty_environment_is_not_ci() =>
        await Assert.That(TelemetryEnvironment.IsCi(new Dictionary<string, string?>())).IsFalse();

    // Tools that export `CI=false` to opt OUT are common enough (and the value is meaningful
    // rather than incidental) that presence alone must not decide it. A blanket
    // "non-empty means CI" reading would tag exactly the machines trying to say the opposite.
    [Test]
    [Arguments("false")]
    [Arguments("False")]
    [Arguments("0")]
    public async Task Explicitly_negative_ci_flag_is_not_ci(string value) =>
        await Assert.That(TelemetryEnvironment.IsCi(Env("CI", value))).IsFalse();

    [Test]
    public async Task Blank_provider_variable_is_not_ci() =>
        await Assert.That(TelemetryEnvironment.IsCi(Env("GITHUB_ACTIONS", "  "))).IsFalse();

    // A provider-specific variable is presence-based, unlike the generic `CI`: GitHub sets
    // GITHUB_ACTIONS=true and nothing sets it to "false" to mean "not GitHub Actions".
    [Test]
    public async Task Provider_variable_set_to_false_is_still_ci() =>
        await Assert.That(TelemetryEnvironment.IsCi(Env("GITHUB_ACTIONS", "false"))).IsTrue();

    // `build_channel` exists so insights can exclude dev-loop noise with a property filter
    // instead of a `cli_version NOT LIKE '%alpha%'` string match every future query must remember.
    [Test]
    [Arguments("0.11.17+8d933b0fb1de7ada3316880867a17f0b3d23bd8a", "release")]
    [Arguments("0.11.17", "release")]
    [Arguments("0.11.17-alpha.0.6+cceb5ef9b3299645c4e59f7fd7685ec649702421", "prerelease")]
    [Arguments("0.11.14-alpha.0.48", "prerelease")]
    [Arguments("0.7.0-beta.1", "prerelease")]
    public async Task Build_channel_splits_release_from_prerelease(string version, string expected) =>
        await Assert.That(TelemetryEnvironment.BuildChannel(version)).IsEqualTo(expected);

    // Version() falls back to the literal "unknown" when the assembly attribute is missing; a
    // hyphen-free unparseable string must not be silently reported as a release build.
    [Test]
    [Arguments("unknown")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Unresolvable_version_reports_an_unknown_channel(string? version) =>
        await Assert.That(TelemetryEnvironment.BuildChannel(version)).IsEqualTo("unknown");

    // Build metadata may itself contain a hyphen; splitting on the first '-' without dropping
    // '+…' first would misread a release build as a prerelease.
    [Test]
    public async Task Hyphen_inside_build_metadata_does_not_imply_prerelease() =>
        await Assert.That(TelemetryEnvironment.BuildChannel("0.11.17+feature-branch.1")).IsEqualTo("release");
}
