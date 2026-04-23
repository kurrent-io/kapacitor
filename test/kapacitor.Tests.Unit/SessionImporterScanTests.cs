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
