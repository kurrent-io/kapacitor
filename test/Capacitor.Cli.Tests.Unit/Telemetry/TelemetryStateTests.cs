using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Lock on TelemetryState.PathOverride (the shared resource) rather than this class name, so that
// other test classes in Tasks 7, 9, and 11 which also use PathOverride can reuse the same lock key.
[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class TelemetryStateTests {
    static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-telemetry-{Guid.NewGuid():N}", "telemetry.json");

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        TelemetryState.PathOverride = NewTempPath();

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Device_id_is_created_once_and_is_stable() {
        TelemetryState.PathOverride = NewTempPath();

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryState.Read().Id).IsEqualTo(first);
    }

    [Test]
    public async Task Device_id_is_a_bare_guid_with_no_hyphens() {
        TelemetryState.PathOverride = NewTempPath();

        var id = TelemetryState.GetOrCreateDeviceId()!;

        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.Contains('-')).IsFalse();
    }

    [Test]
    public async Task Second_get_device_id_does_not_rewrite_file_when_id_exists() {
        var path = NewTempPath();
        TelemetryState.PathOverride = path;

        // First call creates the ID and writes the file.
        TelemetryState.GetOrCreateDeviceId();
        await System.Threading.Tasks.Task.Delay(10);   // ensure timestamp granularity

        var timestampAfterFirstCall = File.GetLastWriteTimeUtc(path);
        await System.Threading.Tasks.Task.Delay(10);   // ensure time passes before second call

        // Second call should return the same ID without rewriting.
        TelemetryState.GetOrCreateDeviceId();

        var timestampAfterSecondCall = File.GetLastWriteTimeUtc(path);

        await Assert.That(timestampAfterSecondCall).IsEqualTo(timestampAfterFirstCall);
    }

    // GetOrCreateDeviceId no longer re-decides precedence: an earlier revision independently
    // vetoed minting when state.Enabled was false, which meant KCAP_TELEMETRY=1 could never
    // override a persisted opt-out (CliTelemetry.Initialize would resolve enabled, call this
    // method, and this method would silently disable the facade right back). Precedence is
    // TelemetrySettings.Resolve's job alone; Initialize's own gate is what skips this call
    // entirely when disabled — see CliTelemetryTests for that property asserted through the real
    // gate, and Kcap_telemetry_env_var_overrides_a_persisted_off_and_mints_a_device_id for the
    // regression this fixes. Called directly, this method mints unconditionally.
    [Test]
    public async Task Get_or_create_device_id_mints_unconditionally_regardless_of_the_persisted_flag() {
        TelemetryState.PathOverride = NewTempPath();
        TelemetryState.SetEnabled(false);

        var id = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(id).IsNotNull();
        await Assert.That(TelemetryState.Read().Id).IsEqualTo(id);
    }

    // Closes a documented gap: the spec justifies telemetry.json being separate from
    // machine.json on the grounds that opt-out can delete the analytics id outright, not merely
    // stop minting new ones.
    [Test]
    public async Task Set_enabled_false_deletes_an_existing_device_id() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();
        await Assert.That(id).IsNotNull();

        TelemetryState.SetEnabled(false);

        await Assert.That(TelemetryState.Read().Id).IsNull();
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);
    }

    // Re-enabling must not resurrect the discarded id — GetOrCreateDeviceId mints a fresh one,
    // which is the more private behaviour the spec calls for.
    [Test]
    public async Task Re_enabling_after_disable_mints_a_fresh_device_id() {
        TelemetryState.PathOverride = NewTempPath();
        var original = TelemetryState.GetOrCreateDeviceId();

        TelemetryState.SetEnabled(false);
        TelemetryState.SetEnabled(true);
        var fresh = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(fresh).IsNotNull();
        await Assert.That(fresh).IsNotEqualTo(original);
    }

    [Test]
    public async Task Set_enabled_persists_and_survives_reread() {
        TelemetryState.PathOverride = NewTempPath();

        TelemetryState.SetEnabled(false);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);

        TelemetryState.SetEnabled(true);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)true);
    }

    // Superseded by Set_enabled_false_deletes_an_existing_device_id above: disabling now clears
    // the id (finding fix), so SetEnabled(true) — not (false) — is the preserving case.
    [Test]
    public async Task Set_enabled_true_preserves_existing_device_id() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();

        TelemetryState.SetEnabled(true);

        await Assert.That(TelemetryState.Read().Id).IsEqualTo(id);
    }

    [Test]
    public async Task Notice_shown_marker_persists() {
        TelemetryState.PathOverride = NewTempPath();

        await Assert.That(TelemetryState.Read().NoticeShown).IsFalse();
        TelemetryState.MarkNoticeShown();
        await Assert.That(TelemetryState.Read().NoticeShown).IsTrue();
    }

    [Test]
    public async Task Mark_notice_shown_preserves_existing_device_id_and_enabled() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();
        // Enabled=true here (not false): disabling now clears Id (finding fix), so pairing
        // MarkNoticeShown with a disabled state would make Id's expected value ambiguous. The
        // property this test cares about — MarkNoticeShown doesn't clobber Id or Enabled — reads
        // just as well from the enabled case.
        TelemetryState.SetEnabled(true);

        TelemetryState.MarkNoticeShown();

        var state = TelemetryState.Read();
        await Assert.That(state.Id).IsEqualTo(id);
        await Assert.That(state.Enabled).IsEqualTo((bool?)true);
        await Assert.That(state.NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    // Guards the litter risk the temp-file-then-rename write introduced: a failed or half-finished
    // write must not leave a stray file next to telemetry.json.
    //
    // It deliberately does NOT prove atomicity, and would have passed against the old
    // File.WriteAllText too — interleaving a read with a write to observe a torn file isn't
    // something a unit test can do deterministically across platforms. Atomicity rests on
    // File.Move(overwrite: true) being a same-volume rename; this test only covers the cleanup
    // half of that change.
    [Test]
    public async Task Write_leaves_no_temp_file_behind_after_a_successful_mutation() {
        var path = NewTempPath();
        TelemetryState.PathOverride = path;

        TelemetryState.SetEnabled(false);

        var dir     = Path.GetDirectoryName(path)!;
        var entries = Directory.GetFiles(dir);
        await Assert.That(entries).IsEquivalentTo(new[] { path });
    }

    [Test]
    public async Task Corrupt_file_heals_on_device_id_creation() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
    }
}
