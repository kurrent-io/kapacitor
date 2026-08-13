using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>`kcap daemon start -d` gates the spawn on the embedded daemon digest,
/// but only when the boot carrier (<c>KCAP_CONSENT_SEED_DEFAULT</c>) shows this was an app-managed
/// start — a manual `kcap daemon start -d` from a terminal carries no directive and is never gated.
/// Dev/test builds carry <see cref="Capacitor.Cli.DaemonDigest.Placeholder"/>, so
/// <see cref="Capacitor.Cli.DaemonDigest.Matches"/> always reports false here — gated cases always
/// fail closed to exit 43 in this suite (see DaemonDigestTests for the build-time-embedded path).</summary>
public class DetachedDigestGateTests {
    [Test]
    public async Task No_directive_means_no_gate() {
        var exit = DaemonCommands.DetachedDigestGate("/nonexistent", _ => null);
        await Assert.That(exit).IsNull();
    }

    [Test]
    public async Task Directive_with_placeholder_digest_fails_closed_exit_43() {
        // dev/test builds carry the placeholder → Matches() is false → gate refuses
        var exit = DaemonCommands.DetachedDigestGate("/nonexistent",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(exit).IsEqualTo(43);
    }

    [Test, NotInParallel]
    public async Task Directive_with_placeholder_digest_writes_the_stderr_line_exactly_once() {
        var originalErr = Console.Error;
        var capturedErr = new StringWriter();

        try {
            Console.SetError(capturedErr);

            var exit = DaemonCommands.DetachedDigestGate("/nonexistent",
                k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            await Assert.That(exit).IsEqualTo(43);

            var lines = capturedErr.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            await Assert.That(lines).IsEquivalentTo(["daemon_start_reason=package_inconsistent"]);
        } finally {
            Console.SetError(originalErr);
        }
    }
}
