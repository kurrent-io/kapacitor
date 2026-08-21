using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexPathsTests {
    [Test]
    public async Task Discover_returns_empty_when_root_missing() {
        var result = CodexPaths.Discover(Path.Combine(Path.GetTempPath(), $"codex-missing-{Guid.NewGuid():N}"));
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task Discover_finds_rollouts_across_date_dirs() {
        using var root = new TempDir();

        CreateRollout(root.Path, "2026/03/03", "rollout-2026-03-03T12-58-16-019cb390-2ea9-7541-b633-464c3536a262");
        CreateRollout(root.Path, "2026/05/07", "rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861");

        var result = CodexPaths.Discover(root.Path);

        await Assert.That(result.Count).IsEqualTo(2);

        var ids = result.Select(r => r.SessionId).OrderBy(s => s, StringComparer.Ordinal).ToList();
        await Assert.That(ids[0]).IsEqualTo("019cb3902ea97541b633464c3536a262");
        await Assert.That(ids[1]).IsEqualTo("019e032205fc7570be6575719c3ea861");
    }

    [Test]
    public async Task Discover_with_since_prunes_earlier_directories() {
        using var root = new TempDir();

        CreateRollout(root.Path, "2026/03/03", "rollout-2026-03-03T12-58-16-019cb390-2ea9-7541-b633-464c3536a262");
        CreateRollout(root.Path, "2026/05/01", "rollout-2026-05-01T01-00-00-019d0000-0000-7000-8000-000000000001");
        CreateRollout(root.Path, "2026/05/07", "rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861");

        var result = CodexPaths.Discover(root.Path, since: new DateOnly(2026, 5, 1));

        await Assert.That(result.Count).IsEqualTo(2);
        // Normalise to forward slashes so the assertion works regardless of platform
        // path separator (Windows would otherwise produce \03\03\ here).
        await Assert.That(result.Any(r => r.FilePath.Replace('\\', '/').Contains("/03/03/"))).IsFalse();
    }

    [Test]
    public async Task Discover_with_since_keeps_same_day() {
        using var root = new TempDir();

        CreateRollout(root.Path, "2026/05/07", "rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861");

        var result = CodexPaths.Discover(root.Path, since: new DateOnly(2026, 5, 7));

        await Assert.That(result.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Discover_leaves_EncodedCwd_empty_so_decode_returns_null() {
        using var root = new TempDir();

        CreateRollout(root.Path, "2026/05/07", "rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861");

        var result = CodexPaths.Discover(root.Path);

        await Assert.That(result.Count).IsEqualTo(1);
        // The day folder name ("07") would be a misleading non-empty cwd encoding.
        // Empty makes SessionImporter.DecodeCwdFromDirName return null so callers
        // skip repo detection on metadata-extraction failure.
        await Assert.That(result[0].EncodedCwd).IsEqualTo("");
    }

    [Test]
    public async Task ExtractSessionIdFromFileName_returns_null_for_non_rollout_pattern() {
        var sid = CodexPaths.ExtractSessionIdFromFileName("/tmp/random.jsonl");
        await Assert.That(sid).IsNull();
    }

    [Test]
    public async Task ExtractSessionIdFromFileName_normalizes_to_dashless_guid() {
        var sid = CodexPaths.ExtractSessionIdFromFileName(
            "/tmp/rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861.jsonl"
        );
        await Assert.That(sid).IsEqualTo("019e032205fc7570be6575719c3ea861");
    }

    static void CreateRollout(string root, string subPath, string fileNameStem) {
        var dir = Path.Combine(root, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileNameStem + ".jsonl"), "{}");
    }

    [Test]
    public async Task UserHooksJson_resolves_under_home_codex() {
        var expected = Path.Combine(PathHelpers.HomeDirectory, ".codex", "hooks.json");
        await Assert.That(CodexPaths.UserHooksJson).IsEqualTo(expected);
    }
}
