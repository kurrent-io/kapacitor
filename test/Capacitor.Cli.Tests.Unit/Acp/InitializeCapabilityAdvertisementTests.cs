// test/Capacitor.Cli.Tests.Unit/Acp/InitializeCapabilityAdvertisementTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The daemon's <c>initialize</c> advertisement is a wire contract: agents decide whether they may
/// send <c>elicitation/create</c> from exactly this JSON. These tests serialize through
/// <c>CapacitorJsonContext.Default.InitializeParams</c> — the same source-generated type info both
/// runtime call sites use — and pin the FULL payload, so an accidental member rename, a dropped
/// capability, or a stray <c>url</c> advertisement fails here before it ships.
/// </summary>
public class InitializeCapabilityAdvertisementTests {
    /// <summary>Byte-for-byte the construction both <c>AcpHostedAgentRuntime</c> initialize call
    /// sites use (StartAsync and the reconnect candidate path — which must advertise the SAME set,
    /// or a reconnect would silently flip the agent back to never asking).</summary>
    static JsonElement RuntimeInitializeParams() => JsonSerializer.SerializeToElement(
        new InitializeParams(
            ProtocolVersion: 1,
            ClientCapabilities: new ClientCapabilities(
                Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                Terminal: false,
                Elicitation: new ElicitationCapabilities(Form: new ElicitationFormCapabilities()))),
        CapacitorJsonContext.Default.InitializeParams);

    [Test]
    public async Task Initialize_AdvertisesFormElicitation_ExactWireShape() {
        var json = RuntimeInitializeParams().GetRawText();

        // Full-payload pin, not substring probes: the schema's "supported" signal is the bare {}.
        await Assert.That(json).IsEqualTo(
            """{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}}}}""");
    }

    [Test]
    public async Task Initialize_NeverAdvertisesUrlElicitation() {
        // url-mode would commit the daemon to opening arbitrary agent-supplied URLs on the host —
        // omission is the spec's "unsupported" signal, and the bridge cancels url-mode frames.
        await Assert.That(RuntimeInitializeParams().GetRawText()).DoesNotContain("url");
    }

    [Test]
    public async Task ClientCapabilities_TwoArgumentShape_OmitsElicitationEntirely() {
        // The trailing-default construction (every pre-existing call site's shape) must keep
        // its old wire form: absent member, not "elicitation":null — WhenWritingNull is what
        // keeps an old-shaped payload byte-identical.
        var json = JsonSerializer.SerializeToElement(
            new InitializeParams(
                ProtocolVersion: 1,
                ClientCapabilities: new ClientCapabilities(
                    Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                    Terminal: false)),
            CapacitorJsonContext.Default.InitializeParams).GetRawText();

        await Assert.That(json).DoesNotContain("elicitation");
    }
}
