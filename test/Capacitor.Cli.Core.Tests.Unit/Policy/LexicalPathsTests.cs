namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class LexicalPathsTests {
    [Test]
    [Arguments("/repo", "src/a.cs", "/repo/src/a.cs")]
    [Arguments("/repo", "./src/../a.cs", "/repo/a.cs")]
    [Arguments("/repo", "/abs/./b/../x", "/abs/x")]
    [Arguments("/repo/sub", "../a", "/repo/a")]
    [Arguments("/repo", "../../../etc/passwd", "/etc/passwd")]
    [Arguments(null, "/abs/x", "/abs/x")]
    public async Task Resolves_lexically(string? cwd, string path, string expected) =>
        await Assert.That(LexicalPaths.TryResolve(cwd, path)).IsEqualTo(expected);

    [Test]
    public async Task Relative_path_without_cwd_is_unresolvable() =>
        await Assert.That(LexicalPaths.TryResolve(null, "src/a.cs")).IsNull();
}
