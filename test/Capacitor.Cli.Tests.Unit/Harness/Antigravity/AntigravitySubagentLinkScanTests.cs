using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Unit tests for <see cref="WatchCommand.ExtractAndPostSubagentLinks"/> (redesign): the
/// live-watcher scan that links Antigravity subagents from the parent transcript's
/// INVOKE_SUBAGENT steps (the spawn-time signal) instead of the child-reports-back
/// <c>messages/*.json</c> scan. Drives the extraction+POST composition directly since
/// <c>ScanAntigravitySubagentLinks</c> itself is now a thin wrapper over drained lines.
/// </summary>
public class AntigravitySubagentLinkScanTests {
    [Test]
    public async Task Scan_posts_each_invoke_child_once_and_dedupes() {
        var posted = new HashSet<string>(StringComparer.Ordinal);
        var sent   = new List<string>();
        var lines = new[] {
            """{"type":"PLANNER_RESPONSE","content":"thinking"}""",
            """{"type":"INVOKE_SUBAGENT","content":"Created the following subagents:\n{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}"}""",
        };
        await WatchCommand.ExtractAndPostSubagentLinks(lines, posted, child => { sent.Add(child); return Task.FromResult(true); });
        await WatchCommand.ExtractAndPostSubagentLinks(lines, posted, child => { sent.Add(child); return Task.FromResult(true); }); // re-scan
        await Assert.That(sent).IsEquivalentTo(new List<string> { "6111e615-3caa-4fe8-9d55-b85c43f2cf1f" }); // once
    }

    [Test]
    public async Task Scan_failed_post_is_retried_next_scan() {
        var posted = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;
        var lines = new[] { """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}"}""" };
        await WatchCommand.ExtractAndPostSubagentLinks(lines, posted, _ => { attempts++; return Task.FromResult(false); }); // fail
        await WatchCommand.ExtractAndPostSubagentLinks(lines, posted, _ => { attempts++; return Task.FromResult(true); });  // retry, succeed
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(posted).Contains("6111e615-3caa-4fe8-9d55-b85c43f2cf1f");
    }
}
