using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Drives `kcap config set telemetry` through the real public entry point,
/// <see cref="ConfigCommand.HandleAsync"/>, to cover the COMPOSITION of
/// <see cref="ConfigCommand.TryApplyTelemetry"/> and <c>ConfigCommand.Set</c> that
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
/// <see cref="TelemetryDeviceId.PathOverride"/> — the point of this class is the DEFAULT path
/// resolution under the pinned <c>KCAP_CONFIG_DIR</c>. It must still HOLD both telemetry lock keys
/// (alongside <c>TokenStoreProfileTests</c> for the config dir it shares), because the production
/// path it drives — <c>TryApplyTelemetry("telemetry", "off")</c> →
/// <see cref="TelemetryState.SetEnabled"/> → <see cref="TelemetryDeviceId.Delete"/> plus
/// <see cref="CliTelemetry.DiscardAndDisable"/> — dereferences whatever those statics point at the
/// instant it runs. TUnit schedules [NotInParallel] tests with disjoint key sets CONCURRENTLY, and
/// its constraint-key scheduler does not consult <c>--maximum-parallel-tests</c>, so CI's serial
/// flag never prevented the overlap: without these keys, this class's side effects landed inside
/// concurrently-running telemetry tests — deleting the device-id file a test had just created
/// under its own <c>PathOverride</c> (the #524 timestamp flake) and clearing the live
/// <c>CliTelemetry.TestSink</c> mid-test (the empty-sink funnel flakes). Both files are cleaned up
/// in <c>[After(Test)]</c> so neither can leak into a later test that reads persisted telemetry
/// state without an override.
/// </summary>
[NotInParallel([
    "TokenStoreProfileTests",
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
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
        await ConfigMutator.MutateAsync(_ => seeded);
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
