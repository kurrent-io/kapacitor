using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class UpdateChannelResolveTests {
    [Test]
    public async Task Defaults_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], null)).IsEqualTo("latest");

    [Test]
    public async Task Config_beta_is_honoured() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "beta")).IsEqualTo("beta");

    [Test]
    public async Task Beta_flag_overrides_config() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--beta"], "latest")).IsEqualTo("beta");

    [Test]
    public async Task Stable_flag_overrides_config_beta() =>
        await Assert.That(UpdateCommand.ResolveChannel(["--stable"], "beta")).IsEqualTo("latest");

    [Test]
    public async Task Unknown_configured_channel_falls_back_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "garbage")).IsEqualTo("latest");

    // A corrupted/hand-edited channel must never reach the cache path or registry URL.
    [Test]
    public async Task Path_traversal_configured_channel_falls_back_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "../../etc/passwd")).IsEqualTo("latest");

    [Test]
    public async Task Rooted_path_configured_channel_falls_back_to_latest() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "/tmp/pwned")).IsEqualTo("latest");

    [Test]
    public async Task Configured_channel_is_normalized_case_insensitively() =>
        await Assert.That(UpdateCommand.ResolveChannel([], "BETA")).IsEqualTo("beta");
}
