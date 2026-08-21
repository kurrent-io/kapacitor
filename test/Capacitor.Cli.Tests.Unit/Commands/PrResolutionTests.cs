using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PrResolutionTests {
    static readonly PrIdentity SessionDefault = new("owner-default", "repo-default", 100);

    [Test]
    public async Task Tool_arg_shorthand_wins_over_session_default() {
        var args = new JsonObject { ["pr"] = "kurrent-io/kcap#42" };

        var result = PrResolution.Resolve(args, SessionDefault);

        await Assert.That(result.Identity).IsEqualTo(new PrIdentity("kurrent-io", "kcap", 42));
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Tool_arg_url_form_is_parsed() {
        var args = new JsonObject { ["pr"] = "https://github.com/kurrent-io/kcap-server/pull/717" };

        var result = PrResolution.Resolve(args, sessionDefault: null);

        await Assert.That(result.Identity).IsEqualTo(new PrIdentity("kurrent-io", "kcap-server", 717));
    }

    [Test]
    public async Task Malformed_tool_arg_returns_parse_error_and_does_not_fall_through() {
        var args = new JsonObject { ["pr"] = "this is not a PR ref" };

        var result = PrResolution.Resolve(args, SessionDefault);

        await Assert.That(result.Identity).IsNull();
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.Error!).Contains("Could not parse");
    }

    [Test]
    public async Task No_tool_arg_falls_back_to_session_default() {
        var args = new JsonObject { ["query"] = "retry logic" };

        var result = PrResolution.Resolve(args, SessionDefault);

        await Assert.That(result.Identity).IsEqualTo(SessionDefault);
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Null_args_falls_back_to_session_default() {
        var result = PrResolution.Resolve(toolArgs: null, SessionDefault);

        await Assert.That(result.Identity).IsEqualTo(SessionDefault);
    }

    [Test]
    public async Task No_sources_returns_actionable_error() {
        var result = PrResolution.Resolve(toolArgs: null, sessionDefault: null);

        await Assert.That(result.Identity).IsNull();
        await Assert.That(result.Error!).Contains("Pass `pr` as a tool argument");
        await Assert.That(result.Error!).DoesNotContain("kcap review");
    }

    [Test]
    public async Task Whitespace_pr_arg_is_treated_as_absent() {
        var args = new JsonObject { ["pr"] = "   " };

        var result = PrResolution.Resolve(args, SessionDefault);

        await Assert.That(result.Identity).IsEqualTo(SessionDefault);
    }

    [Test]
    public async Task Non_string_pr_arg_is_treated_as_absent() {
        var args = new JsonObject { ["pr"] = 42 };

        var result = PrResolution.Resolve(args, SessionDefault);

        await Assert.That(result.Identity).IsEqualTo(SessionDefault);
    }
}
