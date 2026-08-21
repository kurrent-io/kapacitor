using System.Text.Json;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Core.Tests.Unit.Acp;

/// <summary>
/// Test plan item 6(d) (Round 2 Finding 1): <see cref="AcpMcpServerSpec"/>'s explicit
/// constructor coalesces <c>null</c> <c>Args</c>/<c>Env</c> to empty arrays at its OWN boundary,
/// so the ACP-illegal wire shape <c>"args":null</c>/<c>"env":null</c> can never be produced
/// regardless of what any caller passes in — not just when a caller happens to pass well-formed
/// values (as every other exact-JSON case in <c>AcpHostedAgentRuntimeFactoryTests</c>
/// test item 6(a)/(b)/(c) does).
/// </summary>
public class AcpMcpServerSpecTests {
    [Test]
    public async Task Constructor_NullArgsAndEnv_NormalizesToEmptyArrays() {
        var spec = new AcpMcpServerSpec(Name: "fs", Command: "npx", Args: null, Env: null);

        await Assert.That(spec.Args).IsNotNull();
        await Assert.That(spec.Args).IsEmpty();
        await Assert.That(spec.Env).IsNotNull();
        await Assert.That(spec.Env).IsEmpty();
    }

    [Test]
    public async Task Constructor_NullArgsAndEnv_SerializesAsEmptyArrays_NeverNull() {
        var spec = new AcpMcpServerSpec(Name: "fs", Command: "npx", Args: null, Env: null);

        var json = JsonSerializer.Serialize(spec, CapacitorJsonContext.Default.AcpMcpServerSpec);

        await Assert.That(json).IsEqualTo("""{"name":"fs","command":"npx","args":[],"env":[]}""");
    }

    /// <summary>Qodo finding 4: the constructor must clone caller-supplied <c>Args</c>/<c>Env</c>
    /// arrays rather than store them by reference, so mutating the original array after
    /// construction can't silently change what this record serializes onto the ACP wire.</summary>
    [Test]
    public async Task Constructor_ClonesArgsAndEnv_MutatingOriginalArrayDoesNotAffectSpec() {
        var args = new[] { "--flag" };
        var env  = new[] { new AcpMcpServerEnvVar("KEY", "value") };

        var spec = new AcpMcpServerSpec(Name: "fs", Command: "npx", Args: args, Env: env);

        args[0] = "--mutated";
        env[0]  = new AcpMcpServerEnvVar("KEY", "mutated");

        await Assert.That(spec.Args[0]).IsEqualTo("--flag");
        await Assert.That(spec.Env[0].Value).IsEqualTo("value");

        var json = JsonSerializer.Serialize(spec, CapacitorJsonContext.Default.AcpMcpServerSpec);
        await Assert.That(json).IsEqualTo("""{"name":"fs","command":"npx","args":["--flag"],"env":[{"name":"KEY","value":"value"}]}""");
    }
}
