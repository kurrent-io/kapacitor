using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Core.Tests.Unit.Acp;

/// <summary>
/// Model-selection gap 1: pure unit tests for <see cref="AcpModelResolver.Resolve"/>'s match precedence
/// (exact <c>modelId</c> -&gt; prefix <c>modelId</c> -&gt; <c>name</c> equals/contains -&gt;
/// <see langword="null"/>). No ACP wire/process involved — see
/// <c>AcpHostedAgentRuntimeModelSelectionTests</c> for the end-to-end
/// <c>session/set_config_option</c> wiring this helper feeds.
/// </summary>
public class AcpModelResolverTests {
    static readonly AvailableModelDto[] Models = [
        new("composer-2.5[fast=true]", "composer-2.5"),
        new("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
        new("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
    ];

    [Test]
    public async Task Resolve_ExactModelIdMatch_ReturnsIt() {
        var resolved = AcpModelResolver.Resolve("composer-2.5[fast=true]", Models);

        await Assert.That(resolved).IsEqualTo("composer-2.5[fast=true]");
    }

    [Test]
    public async Task Resolve_FamilyPrefix_ReturnsTheFullParameterizedModelId() {
        var resolved = AcpModelResolver.Resolve("claude-sonnet-4-5", Models);

        await Assert.That(resolved).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
    }

    [Test]
    public async Task Resolve_NameEqualsRequested_ReturnsItsModelId() {
        var resolved = AcpModelResolver.Resolve("claude-opus-4-8", Models);

        await Assert.That(resolved).IsEqualTo("claude-opus-4-8[thinking=true]");
    }

    [Test]
    public async Task Resolve_IsCaseInsensitive() {
        var resolved = AcpModelResolver.Resolve("CLAUDE-SONNET-4-5", Models);

        await Assert.That(resolved).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
    }

    [Test]
    public async Task Resolve_NoMatch_ReturnsNull() {
        var resolved = AcpModelResolver.Resolve("gpt-5", Models);

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task Resolve_NullRequested_ReturnsNull() {
        await Assert.That(AcpModelResolver.Resolve(null, Models)).IsNull();
    }

    [Test]
    public async Task Resolve_EmptyOrWhitespaceRequested_ReturnsNull() {
        await Assert.That(AcpModelResolver.Resolve("", Models)).IsNull();
        await Assert.That(AcpModelResolver.Resolve("   ", Models)).IsNull();
    }

    [Test]
    public async Task Resolve_NullAvailableModels_ReturnsNull() {
        await Assert.That(AcpModelResolver.Resolve("claude-sonnet-4-5", null)).IsNull();
    }

    [Test]
    public async Task Resolve_EmptyAvailableModels_ReturnsNull() {
        await Assert.That(AcpModelResolver.Resolve("claude-sonnet-4-5", [])).IsNull();
    }

    [Test]
    public async Task Resolve_ExactModelIdMatchTakesPrecedenceOverANameMatchOnAnotherEntry() {
        // "composer-2.5" is both an exact modelId for the first entry AND would otherwise be
        // reachable via a name-based match on the second entry — exact modelId must win.
        AvailableModelDto[] models = [
            new("composer-2.5", "composer-2.5 (legacy alias)"),
            new("composer-2.5[fast=true]", "composer-2.5"),
        ];

        var resolved = AcpModelResolver.Resolve("composer-2.5", models);

        await Assert.That(resolved).IsEqualTo("composer-2.5");
    }
}
