using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts.Tests.Unit;

/// Each derivation is a persistence contract: the server dedups by these ids, so the bytes hashed
/// are pinned here twice — once by framing (the exact byte layout) and once by fixed vectors.
public class TranscriptIdsTests {
    static readonly Guid Primary = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    [Test]
    public async Task Sibling_hashes_the_primary_guid_bytes_then_the_utf8_suffix() {
        var expectedInput = new byte[16 + 6];
        Primary.TryWriteBytes(expectedInput);
        Encoding.UTF8.GetBytes("result").CopyTo(expectedInput, 16);

        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsNotEqualTo(TranscriptIds.Sibling(Primary, "usage-backfill"));
    }

    [Test]
    public async Task Claude_fallback_hashes_line_number_space_line() {
        var expected = new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("12 {\"type\":\"user\"}")));
        await Assert.That(TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}")).IsEqualTo(expected);
        await Assert.That(TranscriptIds.ClaudeFallback(13, "{\"type\":\"user\"}")).IsNotEqualTo(expected);
    }

    [Test]
    public async Task Claude_block_is_the_sibling_with_a_block_suffix() {
        await Assert.That(TranscriptIds.ClaudeBlock(Primary, 2)).IsEqualTo(TranscriptIds.Sibling(Primary, "block:2"));
    }

    [Test]
    public async Task Claude_attachment_hashes_scope_then_record_guid_then_little_endian_index() {
        var scope = Encoding.UTF8.GetBytes("sess:agent");
        var expectedInput = new byte[scope.Length + 20];
        scope.CopyTo(expectedInput, 0);
        Primary.TryWriteBytes(expectedInput.AsSpan(scope.Length));
        BinaryPrimitives.WriteInt32LittleEndian(expectedInput.AsSpan(scope.Length + 16), 3);

        await Assert.That(TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3)).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
    }

    [Test]
    public async Task Codex_record_hashes_the_utf8_line() {
        const string line = "{\"type\":\"response_item\",\"payload\":{}}";
        await Assert.That(TranscriptIds.CodexRecord(line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
    }

    /// Fixed vectors: a later change to any derivation fails here even if the framing tests are edited too.
    [Test]
    [Arguments("sibling", "580a4b24-18bc-bc19-9676-ed805dff4bdd")]
    [Arguments("claude-fallback", "076b76f4-34c5-66d5-20c5-52bcc518f62c")]
    [Arguments("claude-block", "de376eef-af14-5d28-b930-bbbef251635d")]
    [Arguments("claude-attachment", "c0331d1c-5d1f-a123-49d3-820902c4d48b")]
    [Arguments("codex-record", "b17e302c-b826-0247-6fda-5634e74c45ce")]
    [Arguments("codex-usage-backfill", "5bccf7a4-6eee-83b2-b58c-f10627871a82")]
    public async Task Vectors_are_fixed(string name, string expected) {
        await Assert.That(Vector(name).ToString("D")).IsEqualTo(expected);
    }

    internal static Guid Vector(string name) => name switch {
        "sibling"           => TranscriptIds.Sibling(Primary, "result"),
        "claude-fallback"   => TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}"),
        "claude-block"      => TranscriptIds.ClaudeBlock(Primary, 2),
        "claude-attachment" => TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3),
        "codex-record"      => TranscriptIds.CodexRecord("{\"type\":\"response_item\",\"payload\":{}}"),
        "codex-usage-backfill" => TranscriptIds.Sibling(Primary, "usage-backfill"),
        _                   => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
