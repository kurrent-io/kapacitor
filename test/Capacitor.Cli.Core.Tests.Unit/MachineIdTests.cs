using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit;

public class MachineIdTests {
    [Test]
    public async Task Generated_id_has_stable_format() {
        var id = MachineIdProvider.Generate();

        await Assert.That(id).Matches("^mach-[0-9a-f]{12}$");
    }

    [Test]
    public async Task Machine_id_round_trips_through_config_serialization() {
        var config = new ProfileConfig { MachineId = "mach-abc123def456" };

        var json     = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        var restored = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(json).Contains("\"machine_id\"");
        await Assert.That(restored!.MachineId).IsEqualTo("mach-abc123def456");
    }

    [Test]
    public async Task Missing_machine_id_deserializes_as_null() {
        var restored = JsonSerializer.Deserialize("""{"version":2}""", ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(restored!.MachineId).IsNull();
    }
}
