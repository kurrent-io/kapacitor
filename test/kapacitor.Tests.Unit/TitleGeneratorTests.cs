using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class TitleGeneratorTests {
    [Test]
    public async Task CleanTitle_strips_markdown_and_trailing_punctuation() {
        var result = TitleGenerator.CleanTitle("**Fix auth timeout in login flow?**");

        await Assert.That(result).IsEqualTo("Fix auth timeout in login flow");
    }

    [Test]
    public async Task CleanTitle_caps_length_at_120() {
        var longTitle = new string('a', 200);

        var result = TitleGenerator.CleanTitle(longTitle);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Length).IsEqualTo(120);
    }

    [Test]
    [Arguments("I cannot load the session recap-my instructions restrict me to generating summaries")]
    [Arguments("I can only produce titles, not execute commands")]
    [Arguments("I'm sorry, but I cannot fulfill that request")]
    [Arguments("I am unable to load session data")]
    [Arguments("My instructions prohibit tool use")]
    [Arguments("Sorry, I can't do that")]
    [Arguments("As an AI, I cannot access the filesystem")]
    [Arguments("Unfortunately, I'm unable to help with that")]
    public async Task CleanTitle_returns_null_for_refusal_preamble(string refusal) {
        var result = TitleGenerator.CleanTitle(refusal);

        await Assert.That(result).IsNull();
    }

    [Test]
    [Arguments("Fix authentication timeout in login flow")]
    [Arguments("Resume prior session from recap")]
    [Arguments("Explain how the watcher component works")]
    [Arguments("Add retry logic to SignalR reconnection")]
    public async Task CleanTitle_accepts_valid_titles(string title) {
        var result = TitleGenerator.CleanTitle(title);

        await Assert.That(result).IsEqualTo(title);
    }

    [Test]
    public async Task CleanTitle_returns_null_for_empty_input() {
        var result = TitleGenerator.CleanTitle("");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CleanTitle_returns_null_for_whitespace_only_input() {
        var result = TitleGenerator.CleanTitle("   \n\t  ");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task TitlePromptPrefix_is_stable_and_non_empty() {
        await Assert.That(TitleGenerator.TitlePromptPrefix).IsNotEmpty();
        await Assert.That(TitleGenerator.TitlePromptPrefix).EndsWith(". ");
    }

    [Test]
    public async Task IsKnownKapacitorPrompt_detects_title_prompt() {
        var sample = TitleGenerator.TitlePromptPrefix + "trailing content";

        await Assert.That(TitleGenerator.IsKnownKapacitorPrompt(sample)).IsTrue();
    }

    [Test]
    public async Task IsKnownKapacitorPrompt_rejects_unrelated_text() {
        await Assert.That(TitleGenerator.IsKnownKapacitorPrompt("hello world")).IsFalse();
    }

    [Test]
    public async Task SanitizeForLog_escapes_newlines() {
        var result = TitleGenerator.SanitizeForLog("one\ntwo\r\nthree", 200);

        await Assert.That(result).IsEqualTo("one\\ntwo\\n\\nthree");
    }

    [Test]
    public async Task SanitizeForLog_strips_control_chars_but_keeps_visible_chars() {
        var result = TitleGenerator.SanitizeForLog("a\tbc!d", 200);

        await Assert.That(result).IsEqualTo("abc!d");
    }

    [Test]
    public async Task SanitizeForLog_caps_output_length() {
        var raw = new string('a', 500);

        var result = TitleGenerator.SanitizeForLog(raw, 64);

        await Assert.That(result.Length).IsEqualTo(64);
    }
}
