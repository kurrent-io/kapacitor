using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
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
///
/// Deliberately does NOT set <see cref="TelemetryState.PathOverride"/> or
/// <see cref="TelemetryDeviceId.PathOverride"/>. Those statics have their own dedicated lock keys
/// (<c>nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride)</c> and the
/// <c>TelemetryDeviceId</c> equivalent, see <c>TelemetryStateTests</c>/<c>TelemetryDeviceIdTests</c>),
/// shared by every class that mutates them (<c>TelemetryStateTests</c>, <c>TelemetryDeviceIdTests</c>,
/// <c>SetupFunnelTests</c>, <c>CliTelemetryTests</c>, <c>McpTelemetryTests</c>,
/// <c>ConfigTelemetryKeyTests</c>). This class locks under <c>TokenStoreProfileTests</c> instead,
/// for the config dir it genuinely shares — setting either override too would race those telemetry
/// classes under local (non-CI) parallelism, a gap CI's <c>--maximum-parallel-tests 1</c> would
/// never expose. It doesn't need to: left unset, telemetry state and the device id both fall back
/// to their own defaults (<c>PathHelpers.ConfigPath("telemetry.json")</c> and
/// <c>PathHelpers.ConfigPath("telemetry-device.json")</c>), which already resolve inside the same
/// <c>KCAP_CONFIG_DIR</c> the module initializer pinned. Both files are cleaned up in
/// <c>[After(Test)]</c> so neither can leak into a later test that reads persisted telemetry state
/// without an override.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class ConfigSetTelemetryCompositionTests {
    static string ConfigPath    => AppConfig.GetConfigPath();
    static string TelemetryPath => PathHelpers.ConfigPath("telemetry.json");
    static string DeviceIdPath  => PathHelpers.ConfigPath("telemetry-device.json");

    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearWithRetry("config.json", () => File.Delete(ConfigPath));
        AppConfig.ResetResolvedStateForTesting();
    }

    [After(Test)]
    public void CleanupTelemetryState() {
        SharedConfigDirCleanup.ClearWithRetry("telemetry.json", () => File.Delete(TelemetryPath));
        SharedConfigDirCleanup.ClearWithRetry("telemetry-device.json", () => File.Delete(DeviceIdPath));
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
