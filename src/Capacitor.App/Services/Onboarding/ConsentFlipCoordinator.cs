using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Onboarding;

/// spec §6 coordinator path: applies a pending decision-7 claim to a PRE-EXISTING daemon on every
/// transition to Connected — the sibling of ShimOfferCoordinator, but re-triggered every time
/// rather than once-ever, since a claim or a daemon restart can both arrive later.
public sealed class ConsentFlipCoordinator(
        IDaemonClientService client, ILocalControlOps ops, ConsentFlipClaims claims,
        Func<(string Profile, string Server, string DaemonName)> resolveIdentityUnderConfigLock,
        ILifecycleSurface surface, IAppStateStore appState, CancellationToken lifetime) {
    internal const string ConsentV3Capability = "consent/3";

    readonly SemaphoreSlim _gate = new(1, 1);

    /// Test seam: counts settled RunAsync passes so tests can await one deterministically.
    internal int PassCount;

    /// Subscribes client.Status for the app lifetime; also fires the once-only quarantine surface.
    public void Start() {
        client.Status.Subscribe(OnStatus);
        _ = SurfaceQuarantineOnceAsync();
    }

    void OnStatus(AttachStatus status) {
        if (status.State != AttachState.Connected) return;
        if (status.Capabilities is not { } caps || !caps.Contains(ConsentV3Capability)) return; // no put — claim stays pending
        _ = RunAsync();
    }

    // Single-flight per transition (ConsentService's lane discipline): overlapping Connected
    // transitions serialize onto this semaphore instead of running concurrent passes.
    async Task RunAsync() {
        try {
            await _gate.WaitAsync(lifetime).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }
        try {
            await ApplyAsync().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown mid-pass
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent-flip coordinator pass failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
            Interlocked.Increment(ref PassCount);
        }
    }

    // Both this coordinator's own match AND TryConsume's later re-resolve (called through this
    // same wrapper) must observe identical canonicalization — the store never re-canonicalizes.
    (string Profile, string Server, string DaemonName) ResolveCanonical() {
        var (profile, server, daemonName) = resolveIdentityUnderConfigLock();
        return (profile, ServerIdentity.Canonicalize(server) ?? server, daemonName);
    }

    async Task ApplyAsync() {
        var pending = await Task.Run(claims.Pending, lifetime).ConfigureAwait(false);
        if (pending.Count == 0) return;

        var identity = ResolveCanonical(); // one plain read — TryConsume re-resolves under lock at clear time
        var claim = pending.FirstOrDefault(c => c.Profile == identity.Profile && c.CanonicalServer == identity.Server);
        if (claim is null) return; // no claim for the currently resolved identity

        ConsentPolicyDto policy;
        try {
            policy = await ops.GetConsentPolicyAsync(lifetime).ConfigureAwait(false);
        } catch (LocalControlOpsException) {
            return; // claim stays pending, retried on the next Connected transition
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: consent-flip coordinator get failed unexpectedly: {ex.Message}");
            return;
        }

        var isFactory = policy.Default == "allow" && policy.Rules.Count == 0; // factory guard: only a factory-looking policy flips
        // Resurrected claim: still routes through the identity-conditional put below (unchanged, a no-op) — Get's answer alone proves nothing about which daemon instance answered.
        var alreadyApplied = policy.Default == "prompt";
        if (!isFactory && !alreadyApplied) return; // deny, or allow-with-rules: not factory-looking, retain

        var putPolicy = isFactory ? policy with { Default = "prompt" } : policy;
        var put = new ConsentPolicyPutV2Dto(identity.DaemonName, claim.CanonicalServer, putPolicy);

        ConsentAckDto ack;
        try {
            ack = await ops.PutConsentPolicyV2Async(put, lifetime).ConfigureAwait(false);
        } catch (LocalControlOpsException) {
            return; // claim retained
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: consent-flip coordinator put failed unexpectedly: {ex.Message}");
            return;
        }

        if (!ack.Ok) return; // identity_mismatch or any other rejection — claim retained, nothing consumed

        // Two-lock conditional clear; a false return leaves the claim pending for the next graph.
        await Task.Run(() => claims.TryConsume(claim, ResolveCanonical, identity.DaemonName), lifetime).ConfigureAwait(false);
    }

    /// Shared with the wizard's Sign-in step so both surfaces disclose the same recovery.
    internal static string QuarantineDisclosure(string preservedPath) =>
        $"A corrupted consent-flip claims file was found and preserved at {preservedPath}. " +
        "Pre-existing daemons may need `kcap daemon consent set-default prompt`, or re-run onboarding.";

    async Task SurfaceQuarantineOnceAsync() {
        try {
            // A read is what discovers corruption (ConsentFlipClaims quarantines lazily, on any
            // read) — force one here so surfacing never depends on some other path having run first.
            await Task.Run(claims.Pending, lifetime).ConfigureAwait(false);
            if (claims.Quarantine() is not { } quarantine) return;
            var state = await appState.LoadAsync().ConfigureAwait(false);
            if (state.ConsentQuarantineAcked) return;

            var prompt = new LifecyclePrompt(
                LifecyclePrompt.KindQuarantine, null, null, false, QuarantineDisclosure(quarantine.PreservedPath));
            // true → explicit Acknowledge; false (declined) or null (never shown) must NOT ack — re-surfaces next start.
            var accepted = await surface.TryConfirmAsync(prompt, lifetime).ConfigureAwait(false);
            if (accepted == true) await AckQuarantineAsync().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown before the surface could complete
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: consent quarantine surfacing failed unexpectedly: {ex.Message}");
        }
    }

    /// Persists the ack so a later launch never re-surfaces this quarantine — called only from
    /// SurfaceQuarantineOnceAsync on an explicit true from TryConfirmAsync.
    public Task<bool> AckQuarantineAsync() =>
        appState.UpdateAsync(s => s.ConsentQuarantineAcked ? s : s with { ConsentQuarantineAcked = true });
}
