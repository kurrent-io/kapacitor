using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Tests.Unit;

public class ReasonLineTests {
    const string Prefix = "start_gate_reason=";

    [Test]
    public async Task Single_matching_line_returns_its_token() {
        var stderr = $"{Prefix}directive_missing\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsEqualTo("directive_missing");
    }

    [Test]
    public async Task No_matching_line_returns_null() {
        var stderr = "some unrelated line\nanother line\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task Two_lines_with_the_same_token_still_returns_null() {
        var stderr = $"{Prefix}directive_missing\n{Prefix}directive_missing\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task Two_lines_with_different_tokens_return_null() {
        var stderr = $"{Prefix}directive_missing\n{Prefix}foreign_binary\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task Matching_line_surrounded_by_unrelated_stderr_returns_its_token() {
        var stderr = $"booting...\nsome noise here\n{Prefix}package_inconsistent\ntrailing noise\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsEqualTo("package_inconsistent");
    }

    [Test]
    public async Task Prefix_appearing_mid_line_is_not_a_match() {
        var stderr = $"noise {Prefix}directive_missing\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task CarriageReturn_newline_input_is_handled() {
        var stderr = $"noise\r\n{Prefix}identity_mismatch\r\nmore noise\r\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsEqualTo("identity_mismatch");
    }

    // An empty-token match is conflicting evidence, not absence — fails closed to null.
    [Test]
    public async Task Single_matching_line_with_an_empty_token_returns_null() {
        var stderr = $"{Prefix}\n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task Single_matching_line_with_a_whitespace_only_token_returns_null() {
        var stderr = $"{Prefix}   \n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsNull();
    }

    [Test]
    public async Task Token_is_trimmed_of_surrounding_whitespace() {
        var stderr = $"{Prefix}  directive_missing  \n";
        await Assert.That(ReasonLine.TrySingle(stderr, Prefix)).IsEqualTo("directive_missing");
    }

    [Test]
    public async Task Empty_stderr_returns_null() {
        await Assert.That(ReasonLine.TrySingle("", Prefix)).IsNull();
    }
}
