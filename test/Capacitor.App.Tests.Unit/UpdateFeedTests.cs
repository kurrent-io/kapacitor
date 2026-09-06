using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

public class UpdateFeedTests {
    [Test]
    public async Task Default_is_the_kurrent_desktop_feed() {
        await Assert.That(UpdateFeed.Resolve(_ => null)).IsEqualTo("https://www.kurrent.io/download/desktop/osx-arm64/");
    }

    [Test]
    public async Task Override_variable_replaces_the_feed_url() {
        await Assert.That(UpdateFeed.Resolve(k => k == "KCAP_APP_UPDATE_URL" ? " http://127.0.0.1:8080/feed/ " : null))
            .IsEqualTo("http://127.0.0.1:8080/feed/");
    }

    [Test]
    public async Task Blank_override_is_ignored() {
        await Assert.That(UpdateFeed.Resolve(_ => "   ")).IsEqualTo(UpdateFeed.BaseUrl);
    }
}
