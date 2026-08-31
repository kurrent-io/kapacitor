using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LinkPolicyTests {
    [Test]
    public async Task Only_absolute_http_and_https_open() {
        await Assert.That(LinkPolicy.IsOpenable("https://example.com/x?y=1")).IsTrue();
        await Assert.That(LinkPolicy.IsOpenable("http://example.com")).IsTrue();
        foreach (var refused in new[] { "file:///etc/passwd", "javascript:alert(1)", "kcap://open", "docs/readme.md", "not a url", "", null })
            await Assert.That(LinkPolicy.IsOpenable(refused)).IsFalse().Because(refused ?? "null");
    }
}
