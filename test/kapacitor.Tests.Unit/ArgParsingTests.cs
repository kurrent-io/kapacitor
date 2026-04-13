namespace kapacitor.Tests.Unit;

public class ArgParsingTests {
    [Test]
    public async Task ResolveSessionId_returns_first_positional_when_no_flags() {
        var id = ArgParsing.ResolveSessionId(["eval", "sess-123"]);

        await Assert.That(id).IsEqualTo("sess-123");
    }

    [Test]
    public async Task ResolveSessionId_skips_boolean_flags_and_picks_positional() {
        var id = ArgParsing.ResolveSessionId(["recap", "--chain", "--full", "sess-abc"]);

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    [Test]
    public async Task ResolveSessionId_skips_value_of_known_value_bearing_flag() {
        // The bug: without valueFlags, "sonnet" (the value of --model) was
        // returned as the sessionId.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "sonnet", "sess-xyz"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-xyz");
    }

    [Test]
    public async Task ResolveSessionId_skips_multiple_value_flags() {
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "opus", "--threshold", "5000", "sess-zzz"],
            valueFlags: ["--model", "--threshold"]
        );

        await Assert.That(id).IsEqualTo("sess-zzz");
    }

    [Test]
    public async Task ResolveSessionId_handles_flags_after_positional() {
        // `kapacitor eval sess-abc --model sonnet` — positional before flags.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "sess-abc", "--model", "sonnet"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }

    // These two tests mutate KAPACITOR_SESSION_ID and must not run in parallel
    // with each other (TUnit parallelizes by default).
    [Test]
    [NotInParallel(nameof(KapacitorSessionIdEnvVar))]
    public async Task ResolveSessionId_returns_env_fallback_when_only_flags_present() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);

        var id = ArgParsing.ResolveSessionId(
            ["eval", "--model", "sonnet", "--chain"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsNull();
    }

    [Test]
    [NotInParallel(nameof(KapacitorSessionIdEnvVar))]
    public async Task ResolveSessionId_honors_env_when_no_positional() {
        Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, "env-sess");

        try {
            var id = ArgParsing.ResolveSessionId(
                ["eval", "--model", "sonnet"],
                valueFlags: ["--model"]
            );

            await Assert.That(id).IsEqualTo("env-sess");
        } finally {
            Environment.SetEnvironmentVariable(KapacitorSessionIdEnvVar, null);
        }
    }

    const string KapacitorSessionIdEnvVar = "KAPACITOR_SESSION_ID";

    [Test]
    public async Task ResolveSessionId_does_not_eat_next_arg_when_flag_is_not_value_bearing() {
        // --chain is boolean; sess-abc must still be returned even if it
        // sits right after --chain.
        var id = ArgParsing.ResolveSessionId(
            ["eval", "--chain", "sess-abc"],
            valueFlags: ["--model"]
        );

        await Assert.That(id).IsEqualTo("sess-abc");
    }
}
