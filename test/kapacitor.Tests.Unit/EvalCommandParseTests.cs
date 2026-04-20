using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

// Covers Qodo PR #23 finding #1: distinguishing "flag absent" (null) from
// "flag present, empty value" (empty array). The latter must NOT silently
// fall through to the full-catalog resolver path.
public class EvalCommandParseTests {
    [Test]
    public async Task Null_csv_returns_null_flag_absent() {
        await Assert.That(EvalCommand.Parse(null)).IsNull();
    }

    [Test]
    public async Task Empty_csv_returns_empty_array_flag_present_no_tokens() {
        var parsed = EvalCommand.Parse("");
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Commas_only_csv_returns_empty_array() {
        var parsed = EvalCommand.Parse(",,, , ,");
        await Assert.That(parsed).IsNotNull();
        await Assert.That(parsed!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Whitespace_around_tokens_is_trimmed() {
        var parsed = EvalCommand.Parse("  safety  ,  tests_written  ");
        await Assert.That(parsed!.Count).IsEqualTo(2);
        await Assert.That(parsed[0]).IsEqualTo("safety");
        await Assert.That(parsed[1]).IsEqualTo("tests_written");
    }
}
