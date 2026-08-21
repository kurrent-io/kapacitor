using Capacitor.Cli.Daemon.Pty;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

public class PtyEnvScrubTests {
    [Test]
    public async Task Claude_session_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.ClaudeSessionVars).IsEquivalentTo(new[] {
            "CLAUDECODE",
            "CLAUDE_CODE_ENTRYPOINT",
            "ANTHROPIC_API_KEY",
            "CLAUDE_CODE_CHILD_SESSION",
            "CLAUDE_CODE_SESSION_ID",
            "CLAUDE_ENV_FILE",
            "ANTHROPIC_API_KEY_CLAUDE_CODE_BACKUP"
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Daemon_supervision_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.DaemonSupervisionVars).IsEquivalentTo(new[] {
            "KCAP_DAEMON_SUPERVISED",
            "XPC_SERVICE_NAME",
            "INVOCATION_ID",
            "SYSTEMD_EXEC_PID"
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Hosted_agent_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.HostedAgentVars).IsEquivalentTo(new[] {
            "KCAP_AGENT_ID",
            "KCAP_RENDERED_AGENT",
            "KCAP_DAEMON_URL"
        }, CollectionOrdering.Matching);
    }
}
