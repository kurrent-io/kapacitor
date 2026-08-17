using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>
/// Covers the repo/path exclusion gate (<see cref="ClaudeHookCommand.IsSessionExcludedAsync"/>)
/// that guards the permission-request watcher self-heal — so a permission prompt in an
/// excluded project does not start a transcript-uploading watcher that session-start
/// intentionally skipped.
/// </summary>
public class ClaudeHookExclusionGateTests {
    static string Body(string cwd) => new JsonObject { ["cwd"] = cwd }.ToJsonString();

    [Test]
    public async Task ExcludedPath_ReturnsTrue() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(Path.Combine(excludedDir, "project"));

        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            profile, body, Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task NonExcludedPath_ReturnsFalse() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");
        var otherDir    = tmp.CreateDir("other");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(Path.Combine(otherDir, "project"));

        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            profile, body, Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task NullProfile_ReturnsFalse() {
        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            profile: null, Body("/tmp/anything"), Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task ProfileWithoutExclusions_ReturnsFalse() {
        var excluded = await ClaudeHookCommand.IsSessionExcludedAsync(
            new Profile(), Body("/tmp/anything"), Stopwatch.GetTimestamp(), "permission-request");

        await Assert.That(excluded).IsFalse();
    }
}
