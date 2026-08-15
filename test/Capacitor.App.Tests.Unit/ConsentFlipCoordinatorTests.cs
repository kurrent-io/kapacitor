using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// spec §6 coordinator path: capability gate → get → factory guard → identity-conditional v2 put
/// → two-lock conditional clear, against a real `ConsentFlipClaims` on temp paths (so the actual
/// TryConsume re-resolve/compare logic is exercised, not a mock of it) plus a scripted
/// `ILocalControlOps` and a scripted resolver.
public class ConsentFlipCoordinatorTests {
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static readonly string CanonicalServer = ServerIdentity.Canonicalize("https://example.test")!;
    static readonly ConsentFlipClaim Claim = new("default", CanonicalServer);

    static AttachStatus Connected(params string[] capabilities) =>
        new(AttachState.Connected, null, capabilities);

    /// Deliberately returns the server in a non-canonical form by default (no explicit port) —
    /// the happy-path test therefore also proves the coordinator canonicalizes before matching.
    sealed class ScriptedResolver {
        public string Profile = "default";
        public string Server = "https://example.test";
        public string DaemonName = "kcap-daemon";
        public int Calls;

        /// Invoked AFTER the return value for this call has been captured, so a hook mutating
        /// these fields only affects calls that happen afterwards, never the call in progress.
        public Action<int>? OnCall;

        public (string Profile, string Server, string DaemonName) Resolve() {
            var n = Interlocked.Increment(ref Calls);
            var result = (Profile, Server, DaemonName);
            OnCall?.Invoke(n);
            return result;
        }
    }

    sealed class FakeAppStateStore : IAppStateStore {
        public AppState State = new();

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            State = mutate(State);
            return Task.FromResult(true);
        }
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Client = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeAppStateStore Store = new();
        public readonly ScriptedResolver Resolver = new();
        public readonly string TempDir = Directory.CreateTempSubdirectory("kcap-flipcoord-").FullName;
        public readonly ConsentFlipClaims Claims;
        public readonly ConsentFlipCoordinator Coordinator;

        public Harness() {
            Claims = new ConsentFlipClaims(
                Path.Combine(TempDir, "consent-flip-claims.json"), Path.Combine(TempDir, "config.json"));
            Coordinator = new ConsentFlipCoordinator(
                Client, Ops, Claims, Resolver.Resolve, Surface, Store, CancellationToken.None);
        }

        public void Dispose() {
            try { Directory.Delete(TempDir, recursive: true); } catch { /* best-effort test cleanup */ }
        }
    }

    // ---- happy path ----

    [Test]
    public async Task Connected_with_capability_and_factory_policy_gets_puts_and_consumes() {
        using var h = new Harness();
        await Assert.That(h.Claims.Arm(Claim)).IsTrue();
        h.Ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        h.Ops.QueuePutV2(true, null);

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Claims.Pending().Count == 0, what: "the claim to be consumed");

        await Assert.That(h.Ops.GetCalls).IsEqualTo(1);
        await Assert.That(h.Ops.PutV2Calls).IsEqualTo(1);
        var put = h.Ops.PutV2Payloads[0];
        await Assert.That(put.ExpectedName).IsEqualTo("kcap-daemon");
        await Assert.That(put.ExpectedServerUrl).IsEqualTo(CanonicalServer);
        await Assert.That(put.Policy.Default).IsEqualTo("prompt");
        await Assert.That(put.Policy.PromptTimeoutSeconds).IsEqualTo(30);
        await Assert.That(put.Policy.Rules).IsEmpty();
    }

    // ---- factory guard ----

    [Test]
    public async Task Policy_with_rules_skips_the_put_and_leaves_the_claim_pending() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        h.Ops.QueueGet(new ConsentPolicyDto("allow", 30, [new ConsentRuleDto("allow", "someone", null, null, null)]));

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Ops.GetCalls == 1, what: "the get to run");
        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        await Assert.That(h.Ops.PutV2Calls).IsEqualTo(0);
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Default_prompt_skips_the_put_and_leaves_the_claim_pending() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        h.Ops.QueueGet(new ConsentPolicyDto("prompt", 30, []));

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Ops.GetCalls == 1, what: "the get to run");
        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        await Assert.That(h.Ops.PutV2Calls).IsEqualTo(0);
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    // ---- capability gate ----

    [Test]
    public async Task Missing_consent3_capability_does_nothing() {
        using var h = new Harness();
        h.Claims.Arm(Claim);

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected("consent/2"));

        await Task.Delay(150); // give a wrongly-firing get every chance to appear
        await Assert.That(h.Ops.GetCalls).IsEqualTo(0);
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    // ---- ack failure ----

    [Test]
    public async Task IdentityMismatch_ack_retains_the_claim() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        h.Ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        h.Ops.QueuePutV2(false, "identity_mismatch");

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Ops.PutV2Calls == 1, what: "the put to run");
        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    // ---- §10 rename-injection rows: a rename landing at each point of the sequence must retain ----

    [Test]
    public async Task Rename_landing_right_after_the_initial_resolve_retains_the_claim() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        h.Resolver.OnCall = n => { if (n == 1) h.Resolver.DaemonName = "renamed-daemon"; };
        h.Ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        h.Ops.QueuePutV2(true, null);

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        // The put itself still used the ORIGINALLY resolved name (captured once, before the rename) —
        // only the later conditional-clear re-resolve observes the rename and retains the claim.
        await Assert.That(h.Ops.PutV2Calls).IsEqualTo(1);
        await Assert.That(h.Ops.PutV2Payloads[0].ExpectedName).IsEqualTo("kcap-daemon");
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Rename_landing_after_get_before_put_retains_the_claim() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        var getTcs = h.Ops.ArmGet();
        h.Ops.QueuePutV2(true, null);

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Ops.GetCalls == 1, what: "the get to be issued");
        h.Resolver.DaemonName = "renamed-daemon"; // lands while the get round-trip is outstanding
        getTcs.SetResult(new ConsentPolicyDto("allow", 30, []));

        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        await Assert.That(h.Ops.PutV2Calls).IsEqualTo(1);
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    [Test]
    public async Task Rename_landing_after_put_before_consume_retains_the_claim() {
        using var h = new Harness();
        h.Claims.Arm(Claim);
        h.Ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        var putTcs = h.Ops.ArmPutV2();

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await WaitUntilAsync(() => h.Ops.PutV2Calls == 1, what: "the put to be issued");
        h.Resolver.DaemonName = "renamed-daemon"; // lands while the put round-trip is outstanding
        putTcs.SetResult(new ConsentAckDto(true, null, null));

        await WaitUntilAsync(() => h.Coordinator.PassCount >= 1, what: "the pass to settle");

        await Assert.That(h.Claims.Pending()).IsEquivalentTo([Claim]);
    }

    // ---- non-matching claim ----

    [Test]
    public async Task NonMatching_claim_is_inert() {
        using var h = new Harness();
        var other = new ConsentFlipClaim("other-profile", ServerIdentity.Canonicalize("https://other.test")!);
        h.Claims.Arm(other);

        h.Coordinator.Start();
        h.Client.StatusSubject.OnNext(Connected(ConsentFlipCoordinator.ConsentV3Capability));

        await Task.Delay(150); // give a wrongly-firing get every chance to appear
        await Assert.That(h.Ops.GetCalls).IsEqualTo(0);
        await Assert.That(h.Claims.Pending()).IsEquivalentTo([other]);
    }

    // ---- quarantine surfacing + ack ----

    [Test]
    public async Task Quarantine_surfaces_once_as_attention_naming_the_preserved_path() {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.TempDir, "consent-flip-claims.json"), "{not json");

        h.Coordinator.Start(); // Start()'s own read discovers the corruption — no pre-read needed

        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the quarantine attention line");
        await Assert.That(h.Claims.Quarantine()).IsNotNull();
        await Assert.That(h.Surface.AttentionMessages[0]).Contains(h.Claims.Quarantine()!.PreservedPath);
        await Assert.That(h.Surface.AttentionMessages[0]).Contains("kcap daemon consent set-default prompt");

        await Task.Delay(150); // give a duplicate surfacing every chance to appear
        await Assert.That(h.Surface.AttentionMessages.Count).IsEqualTo(1);
    }

    // ConsentFlipClaims.Quarantine() is in-memory/per-instance and its evidence (the corrupt file)
    // is consumed by the first read that discovers it — an actual process restart can never
    // rediscover the SAME corruption. The behavior under test here is therefore the ack GATE
    // itself: a fresh coordinator over the SAME long-lived claims store + the now-persisted
    // AppState must not re-surface, which is exactly what a real relaunch (fresh coordinator,
    // durable AppState, in-memory ConsentFlipClaims singleton) would observe.
    [Test]
    public async Task Acked_quarantine_is_not_re_surfaced_by_a_fresh_coordinator() {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.TempDir, "consent-flip-claims.json"), "{not json");

        h.Coordinator.Start();
        await WaitUntilAsync(() => h.Surface.AttentionMessages.Count == 1, what: "the first surfacing");

        await Assert.That(await h.Coordinator.AckQuarantineAsync()).IsTrue();
        await Assert.That(h.Store.State.ConsentQuarantineAcked).IsTrue();

        var freshSurface = new FakeLifecycleSurface();
        var freshCoordinator = new ConsentFlipCoordinator(
            h.Client, h.Ops, h.Claims, h.Resolver.Resolve, freshSurface, h.Store, CancellationToken.None);
        freshCoordinator.Start();

        await Task.Delay(150); // give a wrongly-firing re-surfacing every chance to appear
        await Assert.That(freshSurface.AttentionMessages).IsEmpty();
    }
}
