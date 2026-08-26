using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// Tests for the <c>auth_provider</c> stamp (Task 14, Plan B — additive, read-only this plan;
/// nothing writes it yet). Pure in-memory serialization, no config.json I/O, so these don't need
/// the <c>TempConfigRoot</c> isolation the disk-touching config tests use.
/// </summary>
public class ProfileAuthProviderStampTests {
    [Test]
    public async Task Absent_by_default_old_configs_load_with_null() {
        // An old, already-on-disk config.json — no "auth_provider" key anywhere in it.
        const string json = """
            {"version":2,"active_profile":"acme","profiles":{"acme":{"server_url":"https://acme.example"}}}
            """;

        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Profiles["acme"].AuthProvider).IsNull();
    }

    [Test]
    public async Task Round_trips_through_serialization() {
        var config = new ProfileConfig {
            Profiles = new() {
                ["acme"] = new Profile {
                    ServerUrl = "https://acme.example",
                    AuthProvider = new AuthProviderStamp("none", "https://acme.example:443")
                }
            }
        };

        var json     = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        var restored = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        var stamp = restored!.Profiles["acme"].AuthProvider;
        await Assert.That(stamp).IsNotNull();
        await Assert.That(stamp!.Provider).IsEqualTo("none");
        await Assert.That(stamp.ServerUrl).IsEqualTo("https://acme.example:443");
    }

    [Test]
    public async Task Serializes_with_documented_json_property_names() {
        var config = new ProfileConfig {
            Profiles = new() {
                ["acme"] = new Profile { AuthProvider = new AuthProviderStamp("workos", "https://acme.example") }
            }
        };

        var json = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(json).Contains("\"auth_provider\"");
        await Assert.That(json).Contains("\"provider\":\"workos\"");
        await Assert.That(json).Contains("\"server_url\":\"https://acme.example\"");
    }
}
