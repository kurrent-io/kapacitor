using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

public class WorkContextLabelTests {
    [Test]
    public async Task A_keyed_label_splits_on_the_em_dash_separator() {
        var (key, display) = WorkContextLabel.Split("AI-2198 — Desktop shell: work-context sidebar");
        await Assert.That(key).IsEqualTo("AI-2198");
        await Assert.That(display).IsEqualTo("Desktop shell: work-context sidebar");
    }

    [Test]
    public async Task A_label_without_the_separator_is_display_only() {
        var (key, display) = WorkContextLabel.Split("Daemon tests flake under the full suite");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("Daemon tests flake under the full suite");
    }

    [Test]
    public async Task A_bare_key_is_display_only() {
        var (key, display) = WorkContextLabel.Split("#412");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("#412");
    }

    [Test]
    public async Task A_separator_with_an_empty_half_does_not_split() {
        var (key, display) = WorkContextLabel.Split(" — only a title");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("— only a title");
    }
}
