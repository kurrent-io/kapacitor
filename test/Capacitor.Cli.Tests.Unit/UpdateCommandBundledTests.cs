using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateCommandBundledTests {
    [Test]
    public async Task Check_json_reports_not_newer_and_the_app_install_tag() {
        var json = JsonNode.Parse(UpdateCommand.BundledCheckJson("0.12.0-beta.2", "beta"))!.AsObject();

        await Assert.That(json["current"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["channel"]!.GetValue<string>()).IsEqualTo("beta");
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("app");
    }

    [Test]
    public async Task Bundled_message_names_the_app_and_its_menu_item() {
        await Assert.That(UpdateCommand.BundledMessage).Contains("Kurrent Capacitor");
        await Assert.That(UpdateCommand.BundledMessage).Contains("Check for Updates…");
    }
}
