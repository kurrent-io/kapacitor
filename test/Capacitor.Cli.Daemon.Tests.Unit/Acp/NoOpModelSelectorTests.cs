using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Test plan item 4: <see cref="NoOpModelSelector"/> never touches the wire and never
/// inspects <c>sessionNewResult</c> — used by a descriptor whose vendor has no model-selection
/// hook at all.
/// </summary>
public class NoOpModelSelectorTests {
    /// <summary>Fails the test if <c>RequestAsync</c>/<c>NotifyAsync</c> is ever called — proves
    /// <see cref="NoOpModelSelector"/> never touches the connection.</summary>
    sealed class ThrowingStream : Stream {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int  Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("NoOpModelSelector must never read from the connection.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("NoOpModelSelector must never write to the connection.");
    }

    [Test]
    public async Task TrySelectAsync_NonEmptyRequestedModel_ReturnsNull_NeverTouchesConnection() {
        var connection = new AcpConnection(new ThrowingStream(), new ThrowingStream(), NullLogger.Instance);

        // A deliberately malformed JsonElement — NoOpModelSelector must not even attempt
        // TryGetProperty("models", ...) on it.
        var malformed = JsonDocument.Parse("""{"unexpected":"shape"}""").RootElement;

        var result = await NoOpModelSelector.Instance
            .TrySelectAsync(connection, sessionId: "any-session", malformed, requestedModel: "claude-sonnet-4-5", NullLogger.Instance, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task TrySelectAsync_NoRequestedModel_ReturnsNull_NeverTouchesConnection() {
        var connection = new AcpConnection(new ThrowingStream(), new ThrowingStream(), NullLogger.Instance);
        var malformed  = JsonDocument.Parse("""{"unexpected":"shape"}""").RootElement;

        var result = await NoOpModelSelector.Instance
            .TrySelectAsync(connection, sessionId: "any-session", malformed, requestedModel: null, NullLogger.Instance, CancellationToken.None);

        await Assert.That(result).IsNull();
    }
}
