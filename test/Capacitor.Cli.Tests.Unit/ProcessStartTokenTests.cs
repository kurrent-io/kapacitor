using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests for <see cref="ProcessStartToken"/> — the cross-process-stable start
/// token that the daemon PID file uses to tell a live daemon from a recycled
/// PID. The bug it fixes only manifests <i>across processes</i>: on
/// Linux <see cref="Process.StartTime"/> is recomputed per process from a
/// boot-time estimate, so the daemon and a later <c>status</c> invocation
/// disagreed and every live daemon looked "stale". The cross-process test
/// below is therefore the one that actually guards the regression.
/// </summary>
public class ProcessStartTokenTests {
    [Test]
    public async Task ForCurrent_ReturnsNonEmptyToken() {
        var token = ProcessStartToken.ForCurrent();

        await Assert.That(token).IsNotNull();
        await Assert.That(token).IsNotEmpty();
    }

    [Test]
    public async Task ForCurrent_EqualsForPidOfSelf() {
        var viaCurrent = ProcessStartToken.ForCurrent();
        var viaPid     = ProcessStartToken.ForPid(Environment.ProcessId);

        await Assert.That(viaPid).IsEqualTo(viaCurrent);
    }

    [Test]
    public async Task ForPid_OnNonexistentProcess_ReturnsNull() {
        // PID 0x7FFFFFFF is effectively never a live process; even if it were,
        // a null result is the contract we assert callers can rely on.
        var token = ProcessStartToken.ForPid(int.MaxValue);

        await Assert.That(token).IsNull();
    }

    /// <summary>
    /// The core guarantee on Linux: the token is built from the kernel's
    /// boot-relative <c>starttime</c> (field 22 of <c>/proc/&lt;pid&gt;/stat</c>)
    /// plus the per-boot id, NOT <see cref="Process.StartTime"/>. The kernel
    /// value is byte-identical for every reader and never recomputed, which is
    /// what makes it stable across the daemon-writer and CLI-reader processes.
    /// If someone reverts to <c>Process.StartTime.Ticks</c> this fails, because
    /// that's a ~6.4e17 tick count, not the <c>lx:&lt;boot&gt;:&lt;jiffies&gt;</c> shape.
    /// </summary>
    [Test]
    public async Task ForPid_OnLinux_ReturnsBootScopedProcStarttime() {
        if (!OperatingSystem.IsLinux()) return;

        var pid  = Environment.ProcessId;
        var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat");

        // Fields after the (possibly space/paren-containing) comm begin at the
        // last ')'; field 22 (starttime) is index 19 of those.
        var fields    = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var starttime = fields[19];
        var bootId    = (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id")).Trim();

        await Assert.That(ProcessStartToken.ForPid(pid)).IsEqualTo($"lx:{bootId}:{starttime}");
    }

    [Test]
    public async Task ForPid_OnMacOS_ReturnsMacScheme() {
        if (!OperatingSystem.IsMacOS()) return;

        var token = ProcessStartToken.ForPid(Environment.ProcessId);

        await Assert.That(token).IsNotNull();
        await Assert.That(token!.StartsWith("mac:", StringComparison.Ordinal)).IsTrue();
        // Shape: mac:{uuid}:{digits} — a boot-session UUID (has dashes) then a plain integer.
        var parts = token.Split(':');
        await Assert.That(parts.Length).IsEqualTo(3);
        await Assert.That(parts[1].Contains('-')).IsTrue();
        await Assert.That(long.TryParse(parts[2], out var uniqueId)).IsTrue();
        await Assert.That(uniqueId).IsGreaterThan(0);
    }

    [Test]
    public async Task ForPid_OnMacOS_IsStableAcrossCalls() {
        if (!OperatingSystem.IsMacOS()) return;

        // Same live process, called twice — must be byte-identical (unlike the old tk: scheme,
        // which was never actually unstable within a process either, but this guards the NEW
        // kernel-counter-based path specifically).
        var a = ProcessStartToken.ForPid(Environment.ProcessId);
        var b = ProcessStartToken.ForPid(Environment.ProcessId);
        await Assert.That(a).IsEqualTo(b);
    }

    [Test]
    public async Task ForPid_OnMacOS_TwoDistinctProcessesGetDistinctUniqueIds() {
        if (!OperatingSystem.IsMacOS()) return;

        using var dummy = Capacitor.Tests.Helpers.DummyProcess.StartSleep(5);
        var       mine  = ProcessStartToken.ForPid(Environment.ProcessId);
        var       other = ProcessStartToken.ForPid(dummy.Pid);

        await Assert.That(mine).IsNotNull();
        await Assert.That(other).IsNotNull();
        await Assert.That(mine).IsNotEqualTo(other);
        // Same boot-session UUID (same machine, same boot) — only the p_uniqueid half differs.
        await Assert.That(mine!.Split(':')[1]).IsEqualTo(other!.Split(':')[1]);
    }

    [Test]
    public async Task Matches_MacSchemeVsLegacyTkScheme_IsNullCrossScheme() {
        if (!OperatingSystem.IsMacOS()) return;

        // A pre-M1-A tk: token compared against the live (now mac:-producing) process — must be
        // "can't tell" (null), never a false match and never a false "definitely different"
        // (that would let something wrongly treat a live legacy-recorded process as gone).
        var result = ProcessStartToken.Matches(Environment.ProcessId, "tk:123456789");
        await Assert.That(result).IsNull();
    }

    /// <summary>
    /// The strongest cross-implementation check available for THIS task's duplication: captures
    /// the CURRENT process's mac: token via the exact same native path pty_spawn's internal
    /// capture uses (<c>UnixPtyInterop.pty_capture_mac_identity</c>, the shim export the
    /// NativeTestHost's "mac-identity-smoke" mode also calls directly for self) and via this
    /// task's independent C# <see cref="ProcessStartToken.ForPid"/> re-deriver, for the SAME live
    /// pid — they must be byte-identical, or the reaper's native-vs-C#-rederived comparison would
    /// spuriously disagree about a live process's own identity.
    /// </summary>
    [Test]
    public async Task ForPid_OnMacOS_MatchesNativeShimCaptureForSelf() {
        if (!OperatingSystem.IsMacOS()) return;

        var native = CaptureSelfIdentityViaNativeShim();
        await Assert.That(native).IsNotNull();

        var managed = ProcessStartToken.ForPid(Environment.ProcessId);
        await Assert.That(managed).IsEqualTo(native);
    }

    static unsafe string? CaptureSelfIdentityViaNativeShim() {
        Span<byte> buf = stackalloc byte[128];
        fixed (byte* p = buf) {
            if (Capacitor.Cli.Daemon.Pty.Unix.UnixPtyInterop.pty_capture_mac_identity(Environment.ProcessId, p, (nuint) buf.Length) == 0)
                return null;

            var len = 0;
            while (len < buf.Length && p[len] != 0) len++;
            return System.Text.Encoding.UTF8.GetString(p, len);
        }
    }

    [Test]
    public async Task Matches_SelfWithOwnToken_IsTrue() {
        var token = ProcessStartToken.ForCurrent();

        await Assert.That(token).IsNotNull();
        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, token!)).IsTrue();
    }

    [Test]
    public async Task Matches_SameSchemeDifferentValue_IsFalse() {
        // Build a same-scheme token with a deliberately wrong final field, so the
        // scheme matches but the value doesn't — the conclusive "recycled PID" case.
        var token  = ProcessStartToken.ForCurrent()!;
        var lastSep = token.LastIndexOf(':');
        var wrong   = token[..(lastSep + 1)] + "999999999999";

        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, wrong)).IsFalse();
    }

    /// <summary>
    /// A legacy PID file stored a bare <see cref="Process.StartTime"/> tick
    /// count (no <c>scheme:</c> prefix). It must compare as "can't tell" (null)
    /// so the daemon-identity check falls back to the name match instead of
    /// stranding a still-running old daemon across an upgrade.
    /// </summary>
    [Test]
    public async Task Matches_LegacyBareTicksToken_IsNull() {
        await Assert.That(ProcessStartToken.Matches(Environment.ProcessId, "639168740000000000")).IsNull();
    }

    [Test]
    public async Task Matches_NonexistentProcess_IsNull() {
        await Assert.That(ProcessStartToken.Matches(int.MaxValue, "tk:123")).IsNull();
    }
}
