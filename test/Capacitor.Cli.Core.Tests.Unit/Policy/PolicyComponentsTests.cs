namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyComponentsTests {
    static CanonicalAction Shell(bool analyzed, params string[][] segments) => new() {
        Kind = ActionKind.Shell, Vendor = "claude", Command = "raw text",
        Analyzed = analyzed, Segments = [.. segments.Select(s => new ShellSegment(s))],
    };

    [Test]
    public async Task Analyzed_shell_restriction_and_coverage_are_the_segments() {
        var a = Shell(analyzed: true, ["git", "status"], ["rm", "-rf", "x"]);
        await Assert.That(PolicyComponents.RestrictionOf(a)).IsEquivalentTo(
            new ActionComponent[] {
                new ShellSegmentComponent(new ShellSegment(["git", "status"])),
                new ShellSegmentComponent(new ShellSegment(["rm", "-rf", "x"])),
            });
        await Assert.That(PolicyComponents.CoverageOf(a).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Unanalyzed_shell_has_raw_restriction_and_empty_coverage() {
        var a = Shell(analyzed: false);
        await Assert.That(PolicyComponents.RestrictionOf(a))
            .IsEquivalentTo(new ActionComponent[] { new RawShellComponent("raw text") });
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEmpty();
    }

    [Test]
    public async Task Other_without_tool_name_gets_sentinel_restriction_and_empty_coverage() {
        var a = new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude" };
        await Assert.That(PolicyComponents.RestrictionOf(a))
            .IsEquivalentTo(new ActionComponent[] { new SentinelComponent() });
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEmpty();
    }

    [Test]
    public async Task Other_with_tool_name_is_coverable() {
        var a = new CanonicalAction { Kind = ActionKind.Other, Vendor = "claude", RawToolName = "TodoWrite" };
        await Assert.That(PolicyComponents.CoverageOf(a))
            .IsEquivalentTo(new ActionComponent[] { new OtherToolComponent("TodoWrite") });
    }

    [Test]
    public async Task No_action_kind_yields_an_empty_restriction_set() {
        CanonicalAction[] all = [
            Shell(analyzed: false),
            new() { Kind = ActionKind.FileEdit, Vendor = "v", Paths = ["/a"] },
            new() { Kind = ActionKind.FileRead, Vendor = "v", Paths = ["/a", "/b"] },
            new() { Kind = ActionKind.Network, Vendor = "v", Host = "example.com" },
            new() { Kind = ActionKind.McpTool, Vendor = "v", Server = "kcap-flows", Tool = "start_review_flow" },
            new() { Kind = ActionKind.Other, Vendor = "v" },
        ];
        foreach (var a in all)
            await Assert.That(PolicyComponents.RestrictionOf(a)).IsNotEmpty();
    }

    [Test]
    public async Task Multi_path_file_action_yields_one_component_per_path() {
        var a = new CanonicalAction { Kind = ActionKind.FileRead, Vendor = "v", Paths = ["/a", "/b"] };
        await Assert.That(PolicyComponents.CoverageOf(a)).IsEquivalentTo(
            new ActionComponent[] { new PathComponent("/a"), new PathComponent("/b") });
    }
}
