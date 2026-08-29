namespace Capacitor.Cli.Core.Tests.Unit;

public class ClaudePermissionsTests {
    [Test]
    public async Task Always_allow_is_the_web_ui_shape() {
        var el = ClaudePermissions.AlwaysAllow("Bash");
        await Assert.That(el.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }
}
