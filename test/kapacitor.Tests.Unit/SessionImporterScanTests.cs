using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class SessionImporterScanTests {
    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromAsyncLaunchedInvocation() {
        var transcript = """
                         {"type":"user","message":{"content":"go"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer","prompt":"review this"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, transcript);

            var scan = SessionImporter.ScanAgentLifecycle(path);

            await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-abc")).IsTrue();
            await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromForegroundToolUseResult() {
        var transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, transcript);

            var scan = SessionImporter.ScanAgentLifecycle(path);

            await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromLaterInvocation() {
        // Regression: when agent_progress for an agentId arrives before the assistant
        // tool_use + async_launched pair, the early-return on FirstLineByAgent.ContainsKey
        // used to prevent subagent_type from being copied into AgentTypeByAgent. (Qodo#29)
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-abc"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, transcript);

            var scan = SessionImporter.ScanAgentLifecycle(path);

            await Assert.That(scan.FirstLineByAgent["agent-abc"]).IsEqualTo(0); // progress line wins the position
            await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromForegroundToolUseResult() {
        // Same regression as above, but for the foreground toolUseResult path.
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-xyz"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, transcript);

            var scan = SessionImporter.ScanAgentLifecycle(path);

            await Assert.That(scan.FirstLineByAgent["agent-xyz"]).IsEqualTo(0);
            await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentWithoutInvocation_HasNoType() {
        // progress-only reference — no parent tool_use, so the subagent_type is unknown.
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-orphan"}}
                         """;

        var path = Path.GetTempFileName();

        try {
            await File.WriteAllTextAsync(path, transcript);

            var scan = SessionImporter.ScanAgentLifecycle(path);

            await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-orphan")).IsTrue();
            await Assert.That(scan.AgentTypeByAgent.ContainsKey("agent-orphan")).IsFalse();
        } finally {
            File.Delete(path);
        }
    }
}
