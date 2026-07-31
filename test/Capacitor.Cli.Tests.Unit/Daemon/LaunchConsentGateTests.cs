using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class LaunchConsentGateTests {
    static (LaunchConsentGate gate, LaunchConsentStore store, string dir) Build(
        LaunchConsentDefault def = LaunchConsentDefault.Allow, ILaunchConsentPrompter? prompter = null) {
        var dir = Directory.CreateTempSubdirectory("kcap-gate-").FullName;
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(def, 5, []), out _);
        var log = new LaunchConsentDecisionLog(dir, NullLogger.Instance);
        var gate = new LaunchConsentGate(store, log, prompter, NullLogger<LaunchConsentGate>.Instance);
        return (gate, store, dir);
    }

    static LaunchConsentInput Input(bool owner = false) =>
        new("user_x", owner, "agent", "/tmp/repo", "claude");

    sealed class FakePrompter(bool? answer, bool hasSubscriber = true) : ILaunchConsentPrompter {
        public LaunchConsentPromptRequest? Seen;
        public bool HasSubscriber => hasSubscriber;
        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct) {
            Seen = req;
            return Task.FromResult(answer);
        }
    }

    [Test]
    public async Task Allow_default_allows_and_logs() {
        var (gate, _, dir) = Build(LaunchConsentDefault.Allow);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        var lines = File.ReadAllLines(Path.Combine(dir, "consent-decisions.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"outcome\":\"allowed\"");
    }

    [Test]
    public async Task Deny_default_denies_with_source_default() {
        var (gate, _, _) = Build(LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Prompt_without_subscriber_denies_no_ui() {
        var (gate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(true, hasSubscriber: false));
        var o = await gate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(o.Allowed).IsFalse();
        await Assert.That(o.Source).IsEqualTo("prompt_no_ui");
    }

    [Test]
    public async Task Prompt_user_allow_and_deny_and_timeout() {
        var (allowGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(true));
        await Assert.That((await allowGate.DecideAsync("a1", Input(), CancellationToken.None)).Allowed).IsTrue();

        var (denyGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(false));
        var denied = await denyGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(denied.Allowed).IsFalse();
        await Assert.That(denied.Source).IsEqualTo("prompt_user");

        var (timeoutGate, _, _) = Build(LaunchConsentDefault.Prompt, new FakePrompter(null));
        var timedOut = await timeoutGate.DecideAsync("a1", Input(), CancellationToken.None);
        await Assert.That(timedOut.Allowed).IsFalse();
        await Assert.That(timedOut.Source).IsEqualTo("prompt_timeout");
    }

    [Test]
    public async Task Owner_bypasses_deny_default() {
        var (gate, _, _) = Build(LaunchConsentDefault.Deny);
        var o = await gate.DecideAsync("a1", Input(owner: true), CancellationToken.None);
        await Assert.That(o.Allowed).IsTrue();
        await Assert.That(o.Source).IsEqualTo("owner");
    }
}
