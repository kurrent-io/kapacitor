namespace Capacitor.Cli.Core.Tests.Unit;

public class RepoRefTests {
    [Test]
    public async Task Owner_slash_name_hashes_like_ComputeRepoHash() {
        var ok = RepoHashHelper.TryParseRepoRef("Kurrent-IO/kcap-cli", out var hash);

        await Assert.That(ok).IsTrue();
        await Assert.That(hash).IsEqualTo(RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli"));
    }

    [Test]
    public async Task Sixteen_lowercase_hex_passes_through() {
        var ok = RepoHashHelper.TryParseRepoRef("da9c523c68aee2f1", out var hash);

        await Assert.That(ok).IsTrue();
        await Assert.That(hash).IsEqualTo("da9c523c68aee2f1");
    }

    [Test]
    [Arguments("all")]
    [Arguments("owner")]
    [Arguments("DA9C523C68AEE2F1")]
    [Arguments("da9c523c")]
    [Arguments("owner /name")]
    [Arguments("/name")]
    [Arguments("a//b")]
    [Arguments("owner/")]
    [Arguments("")]
    public async Task Other_shapes_are_rejected(string value) {
        await Assert.That(RepoHashHelper.TryParseRepoRef(value, out _)).IsFalse();
    }

    [Test]
    public async Task Nested_group_owner_keeps_the_group_path_in_the_hash() {
        var ok = RepoHashHelper.TryParseRepoRef("group/subgroup/project", out var hash);

        await Assert.That(ok).IsTrue();
        await Assert.That(hash).IsEqualTo(RepoHashHelper.ComputeRepoHash("group/subgroup", "project"));
    }
}
