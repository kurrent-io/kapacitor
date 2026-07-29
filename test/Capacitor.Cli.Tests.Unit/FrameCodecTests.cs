using System.IO;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

public class FrameCodecTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, default);
        ms.Position = 0;
        var read = await FrameCodec.ReadAsync(ms, default);
        return read!;
    }

    [Test]
    public async Task Stdin_round_trips_raw_bytes() {
        var f = LocalFrame.Stdin([0x00, 0x1b, 0x5b, 0x41, 0xff]);
        var r = await RoundTrip(f);
        await Assert.That(r.Type).IsEqualTo(FrameType.Stdin);
        await Assert.That(r.Bytes).IsEquivalentTo(new byte[] { 0x00, 0x1b, 0x5b, 0x41, 0xff });
    }

    [Test]
    public async Task Resize_round_trips_dimensions() {
        var r = await RoundTrip(LocalFrame.Resize(203, 51));
        await Assert.That(r.Cols).IsEqualTo((ushort)203);
        await Assert.That(r.Rows).IsEqualTo((ushort)51);
    }

    [Test]
    public async Task Spawn_round_trips_vendor_cwd_args_worklocation_and_private() {
        foreach (var priv in new[] { false, true }) {
            var built = FrameCodec.Spawn("codex", WorkLocation.OwnedWorktree, priv, "/repo", ["--model", "opus", "fix it"], 100, 30);
            var r = await RoundTrip(built);
            await Assert.That(r.Type).IsEqualTo(FrameType.Spawn);
            var (vendor, work, isPrivate, cwd, args, cols, rows) = FrameCodec.Spawn(r);
            await Assert.That(vendor).IsEqualTo("codex");
            await Assert.That(work).IsEqualTo(WorkLocation.OwnedWorktree);
            await Assert.That(isPrivate).IsEqualTo(priv);
            await Assert.That(cwd).IsEqualTo("/repo");
            await Assert.That(args).IsEquivalentTo(new[] { "--model", "opus", "fix it" });
            await Assert.That(cols).IsEqualTo((ushort)100);
            await Assert.That(rows).IsEqualTo((ushort)30);
        }
    }

    [Test]
    public async Task Spawn_without_trailing_flag_defaults_to_private() {
        // An older CLI's Spawn frame carries no trailing private byte; ParseSpawn must default to private.
        using var ms = new MemoryStream();
        ms.WriteByte((byte)WorkLocation.BorrowedCwd);
        ms.Write([0, 80]); ms.Write([0, 24]);            // cols=80, rows=24 (BE)
        ms.Write([0, 0, 0, 6]); ms.Write("claude"u8);    // vendorLen=6, "claude"
        ms.Write([0, 0, 0, 0]);                          // cwdLen=0
        ms.Write([0, 0, 0, 0]);                          // argCount=0  (no trailing private byte)
        var frame = new LocalFrame(FrameType.Spawn) { Bytes = ms.ToArray() };

        var (vendor, _, isPrivate, _, _, _, _) = FrameCodec.Spawn(frame);
        await Assert.That(vendor).IsEqualTo("claude");
        await Assert.That(isPrivate).IsTrue();
    }

    [Test]
    public async Task Attached_round_trips_agent_id_and_snapshot() {
        var built = FrameCodec.Attached("abc123", [9, 8, 7]);
        var r = await RoundTrip(built);
        var (id, snapshot) = FrameCodec.Attached(r);
        await Assert.That(id).IsEqualTo("abc123");
        await Assert.That(snapshot).IsEquivalentTo(new byte[] { 9, 8, 7 });
    }

    [Test]
    public async Task ReadAsync_returns_null_on_clean_eof() {
        using var ms = new MemoryStream();
        await Assert.That(await FrameCodec.ReadAsync(ms, default)).IsNull();
    }

    [Test]
    public async Task ReadAsync_reassembles_across_short_reads() {
        var f = LocalFrame.Stdout(new byte[5000]); // > one socket read
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, default);
        using var choppy = new ChoppyStream(ms.ToArray(), chunk: 7);
        var r = await FrameCodec.ReadAsync(choppy, default);
        await Assert.That(r!.Bytes.Length).IsEqualTo(5000);
    }

    [Test]
    public async Task Spawn_with_bogus_arg_count_throws_protocol_error_not_oom() {
        // work(1)=0, cols(2), rows(2), vendorLen(4)=0, cwdLen(4)=0, argCount(4)=int.MaxValue
        using var ms = new MemoryStream();
        ms.WriteByte(0);
        ms.Write([0, 80]); ms.Write([0, 24]);           // cols, rows (BE)
        ms.Write([0, 0, 0, 0]); ms.Write([0, 0, 0, 0]); // vendorLen=0, cwdLen=0
        ms.Write([0x7f, 0xff, 0xff, 0xff]);             // argCount = int.MaxValue
        var frame = new LocalFrame(FrameType.Spawn) { Bytes = ms.ToArray() };

        await Assert.That(() => FrameCodec.Spawn(frame)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Spawn_with_truncated_string_length_throws_protocol_error() {
        // work(1)=0, cols(2), rows(2), vendorLen(4)=1000 but no bytes follow
        using var ms = new MemoryStream();
        ms.WriteByte(0);
        ms.Write([0, 80]); ms.Write([0, 24]);
        ms.Write([0, 0, 0x03, 0xe8]); // vendorLen = 1000, payload ends here
        var frame = new LocalFrame(FrameType.Spawn) { Bytes = ms.ToArray() };

        await Assert.That(() => FrameCodec.Spawn(frame)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Stop_round_trips_the_agent_id() {
        var r = await RoundTrip(LocalFrame.Stop("ab12cd34ef56ab78cd90ef12ab34cd56"));
        await Assert.That(r.Type).IsEqualTo(FrameType.Stop);
        await Assert.That(r.Text).IsEqualTo("ab12cd34ef56ab78cd90ef12ab34cd56");
    }

    [Test]
    public async Task Stop_with_an_empty_id_means_all_agents() {
        var r = await RoundTrip(LocalFrame.Stop(""));
        await Assert.That(r.Type).IsEqualTo(FrameType.Stop);
        await Assert.That(r.Text).IsEqualTo("");
    }

    [Test]
    public async Task StopAck_round_trips_a_newline_separated_id_status_list() {
        var r = await RoundTrip(LocalFrame.StopAck("id-one\tstopped\nid-two\tfailed"));
        await Assert.That(r.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(r.Text.Split('\n')).IsEquivalentTo(new[] { "id-one\tstopped", "id-two\tfailed" });
    }

    [Test]
    [Arguments(true,  "ab12")]
    [Arguments(false, "ab12")]
    [Arguments(false, "")]
    public async Task StopV2_round_trips_mode_and_id(bool force, string agentId) {
        var r = await RoundTrip(LocalFrame.StopV2(force, agentId));
        await Assert.That(r.Type).IsEqualTo(FrameType.StopV2);

        var (gotForce, gotId) = FrameCodec.StopV2(r);
        await Assert.That(gotForce).IsEqualTo(force);
        await Assert.That(gotId).IsEqualTo(agentId);
    }

    [Test]
    public async Task AttachedReadOnly_round_trips_id_reason_and_snapshot() {
        var snapshot = new byte[] { 0x1b, 0x5b, 0x41, 0x00, 0xff };
        var r = await RoundTrip(FrameCodec.AttachedReadOnly("ab12", "review-flow reviewer", snapshot));
        await Assert.That(r.Type).IsEqualTo(FrameType.AttachedReadOnly);

        var (id, reason, got) = FrameCodec.AttachedReadOnly(r);
        await Assert.That(id).IsEqualTo("ab12");
        await Assert.That(reason).IsEqualTo("review-flow reviewer");
        await Assert.That(got).IsEquivalentTo(snapshot);
    }

    [Test]
    public async Task AttachedReadOnly_round_trips_an_empty_snapshot() {
        var r = await RoundTrip(FrameCodec.AttachedReadOnly("ab12", "review agent", []));
        var (_, _, got) = FrameCodec.AttachedReadOnly(r);
        await Assert.That(got).IsEmpty();
    }
}

/// Stream that returns at most `chunk` bytes per ReadAsync to simulate partial socket reads.
sealed class ChoppyStream(byte[] data, int chunk) : Stream {
    int _pos;
    public override int Read(byte[] buffer, int offset, int count) {
        if (_pos >= data.Length) return 0;
        var n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) {
        if (_pos >= data.Length) return ValueTask.FromResult(0);
        var n = Math.Min(Math.Min(chunk, b.Length), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(b.Span); _pos += n; return ValueTask.FromResult(n);
    }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => data.Length; public override long Position { get => _pos; set => _pos = (int)value; }
    public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0;
    public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
}
