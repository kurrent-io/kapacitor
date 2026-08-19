namespace Capacitor.Tests.Helpers;

/// <summary>
/// Locates files that ship in the repo rather than in a test fixture — the skills tree, the bundled
/// MCP manifests — for guard tests that pin code against them.
/// </summary>
/// <remarks>
/// Walks up from the test binary rather than using <c>[CallerFilePath]</c>, which several older
/// guards here do: that bakes the authoring machine's absolute path into the assembly, so it resolves
/// to nothing once the repo is checked out somewhere else. Both approaches need the repo on disk;
/// neither survives a test host published outside the tree.
/// Named for the checkout's layout, not for the daemon's <c>AllowedRepoPaths</c> config — this type is
/// a global using in every test project, so a name that reads as either would be a trap.
/// </remarks>
public static class RepoTree {
    /// <summary>
    /// The repo root: the nearest ancestor of the test binary holding <c>kcap/skills/</c>. Throws
    /// rather than returning null — a guard test that silently skips because it could not find the
    /// tree it guards is worse than no guard.
    /// </summary>
    public static string Root() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null && !Directory.Exists(Path.Combine(dir, "kcap", "skills")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new DirectoryNotFoundException("Could not locate the repo root from the test binary.");
    }

    /// <summary>The shipped plugin directory — skills, and the bundled MCP manifests.</summary>
    public static string KcapDir() => Path.Combine(Root(), "kcap");

    /// <summary>The shipped skills tree, the source every skills install copies from.</summary>
    public static string SkillsSource() => Path.Combine(KcapDir(), "skills");
}
