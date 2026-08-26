using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// The Claude half of skills materialization: where skills live for this harness, how a slug maps
/// to a directory, and the write/prune/drift file operations. The vendor-neutral snapshot models
/// and reconciliation planner stay in <c>Cli.Core/Skills</c>.
/// </summary>
static class ClaudeSkillsMaterializer {
    public static string SkillsRoot() =>
        Path.Combine(PathHelpers.HomeDirectory, ".claude", "skills");

    // The server's slug is already doc-id-anchored and unique; the kcap- prefix namespaces the
    // materialized set inside the shared user-level skills root.
    public static string SkillDirFor(string root, string slug) => Path.Combine(root, "kcap-" + slug);

    public static string SkillFileFor(string dir) => Path.Combine(dir, "SKILL.md");

    public static string FileHash(string rendered) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rendered)));

    /// <summary>A manifest entry whose materialized file is missing or edited. A drifted entry means
    /// metadata alone cannot prove the skill is served — the snapshot must be re-applied.</summary>
    public static bool HasDrifted(SkillsManifestEntry entry) {
        var file = SkillFileFor(entry.Path);
        if (entry.FileHash is null || !File.Exists(file)) return true;
        try {
            return FileHash(File.ReadAllText(file)) != entry.FileHash;
        } catch {
            return true;
        }
    }

    public static void Write(string root, SkillSnapshotItem item) {
        var dir = SkillDirFor(root, item.Slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(SkillFileFor(dir), SkillsSyncPlanner.RenderSkillFile(item));
    }

    /// <summary>Deletes one manifest-recorded directory — and only a DIRECT kcap-* child of the
    /// skills root: a manifest edited by hand must not aim the delete anywhere else (prefix checks
    /// admit siblings like <c>skills-backup/</c> and nested user directories; parent EQUALITY does
    /// not).</summary>
    public static void Prune(string root, SkillsManifestEntry entry) {
        var full = Path.GetFullPath(entry.Path);
        if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.Ordinal)
                && Path.GetFileName(full).StartsWith("kcap-", StringComparison.Ordinal)
                && Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }
}
