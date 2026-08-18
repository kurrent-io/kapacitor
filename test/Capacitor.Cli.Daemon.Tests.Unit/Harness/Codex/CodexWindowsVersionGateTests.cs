using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// Covers the Windows-version floor for hosted Codex. Codex only supports its Windows sandbox
/// on Windows 10 1809 (build 17763) and newer, so older hosts must not advertise the vendor —
/// and a launch that slips through anyway (in-flight command, stale dashboard vendor list) has
/// to fail with the version requirement rather than an opaque spawn error.
///
/// <para>The rejection branch itself can't be exercised here: the gate reads the real OS
/// version and there's no seam to fake it (deliberately — the check is one expression, and an
/// injectable version provider would be more machinery than the thing it guards). What these
/// tests do pin down is the far likelier failure: the gate mis-rejecting a *supported* host,
/// which would silently hide Codex from every launch dialog on Windows.</para>
/// </summary>
public class CodexWindowsVersionGateTests {
    static CodexLauncher NewLauncher(string cliPath) =>
        new(new DaemonConfig { CodexPath = cliPath }, NullLogger<CodexLauncher>.Instance);

    /// <summary>CI runs Windows Server 2022 / Windows 11 and Linux — all supported. A false here
    /// means the gate is wrong, not that the host is genuinely too old.</summary>
    [Test]
    public async Task Current_host_is_reported_supported() {
        await Assert.That(CodexLauncher.WindowsVersionSupported).IsTrue();
    }

    /// <summary>The gate must not swallow the PATH probe: on a supported host, availability is
    /// still decided by whether the configured CLI resolves.</summary>
    [Test]
    public async Task IsAvailable_is_false_for_unresolvable_cli_on_supported_host() {
        await Assert.That(NewLauncher($"kcap-codex-absent-{Guid.NewGuid():N}").IsAvailable()).IsFalse();
    }

    [Test]
    public async Task Rejection_message_carries_the_build_number_and_doc_link() {
        await Assert.That(CodexLauncher.UnsupportedWindowsMessage).Contains("17763");
        await Assert.That(CodexLauncher.UnsupportedWindowsMessage)
            .Contains("https://developers.openai.com/codex/windows");
    }

    /// <summary>The orchestrator only converts an allowlisted set of preflight exceptions into a
    /// user-visible <c>LaunchFailed</c>; anything else becomes a generic failure. Pin the type so
    /// the message can't quietly stop reaching the dashboard.</summary>
    [Test]
    public async Task Unsupported_windows_exception_carries_its_message() {
        var ex = new CodexUnsupportedWindowsException(CodexLauncher.UnsupportedWindowsMessage);

        await Assert.That(ex.Message).IsEqualTo(CodexLauncher.UnsupportedWindowsMessage);
        await Assert.That(ex).IsAssignableTo<Exception>();
    }
}
