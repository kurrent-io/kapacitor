using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

/// <summary>
/// Drives `kcap config set telemetry` through the real public entry point,
/// <see cref="ConfigCommand.HandleAsync"/>, to cover the COMPOSITION of
/// <see cref="ConfigCommand.TryApplyTelemetry"/> and <see cref="ConfigCommand.Set"/> that
/// <see cref="ConfigTelemetryKeyTests"/> cannot: that class calls <c>TryApplyTelemetry</c>
/// directly, so it stays green even if the early <c>return 0;</c> right after the telemetry
/// branch in <c>Set</c> went missing. Without that return, execution would fall through into
/// <c>ApplySet(profile, "telemetry", …)</c> — which throws "Unknown config key" — AFTER the
/// telemetry flag had already been persisted. A user would see a confusing crash right after
/// their opt-out silently took effect, and every existing test would stay green.
///
/// <c>AppConfig</c>'s config path (like <c>PathHelpers.ConfigDir</c>) is <c>static readonly</c>,
/// captured once per process — see <c>ConfigDirIsolationTests</c> — so, unlike
/// <see cref="ConfigTelemetryKeyTests"/>, this cannot point at a private scratch directory;
/// there is no <c>PathOverride</c>-equivalent seam for the profile config path. Instead it
/// follows the existing shared-directory convention used by
/// <c>TokenStoreProfileTests</c>/<c>MachineIdFileTests</c>/<c>LoginDiscoverTests</c>: touch the
/// one real <c>config.json</c> under the assembly-wide <c>KCAP_CONFIG_DIR</c> that
/// <c>RepoPathStoreGlobalSetup</c>'s <c>[ModuleInitializer]</c> pins for the whole process, and
/// share that class's <see cref="NotInParallelAttribute"/> key so nothing else deletes or
/// rewrites <c>config.json</c> underneath this test.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ConfigSetTelemetryCompositionTests {
    static string ConfigPath => AppConfig.GetConfigPath();

    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(ConfigPath));
        AppConfig.ResetResolvedStateForTesting();

        // Isolate telemetry state too, so this test doesn't depend on whatever PathOverride a
        // sibling ConfigTelemetryKeyTests test happened to leave behind.
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-cfg-composition-{Guid.NewGuid():N}", "telemetry.json");
    }

    [Test]
    public async Task Set_telemetry_off_returns_zero_and_leaves_an_existing_profile_on_disk_byte_for_byte_unchanged() {
        // Seed a distinctive profile so ANY write — not only a crash — would show up in the diff.
        var seeded = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new Profile { ServerUrl = "https://sentinel.invalid" }
            }
        };
        await AppConfig.SaveProfileConfig(seeded);
        var before = await File.ReadAllTextAsync(ConfigPath);

        var exit = await ConfigCommand.HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        var after = await File.ReadAllTextAsync(ConfigPath);
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task Set_telemetry_off_never_creates_a_profile_config_when_none_existed() {
        // No config.json at all going in — proves the telemetry path never reaches
        // LoadProfileConfig/SaveProfileConfig, not merely that it round-trips one unchanged.
        await Assert.That(File.Exists(ConfigPath)).IsFalse();

        var exit = await ConfigCommand.HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }
}
