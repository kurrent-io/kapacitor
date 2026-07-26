namespace Capacitor.Cli.Core;

/// <summary>
/// Copies kcap skills from a source tree to the user's
/// <c>~/.agents/skills/</c> directory, prefixing each folder name with
/// <c>kcap-</c> and rewriting the <c>name:</c> field in SKILL.md to
/// match. Also handles cleanup of legacy <c>~/.codex/skills/kcap-*</c>
/// folders left by prior installer versions.
/// </summary>
public static class AgentsSkillsInstaller {
    /// <summary>
    /// File name written into the target directory after a successful install.
    /// Holds the CLI version that produced the skills, used by the npm
    /// postinstall hook to detect when an upgrade-time refresh is needed.
    /// </summary>
    public const string MarkerFileName = ".kcap-version";

    /// <summary>
    /// Source folder names under <c>capacitor/skills/</c>. On install each
    /// becomes <c>kcap-&lt;name&gt;</c> under the target directory.
    /// Add a new skill here when adding it to <c>capacitor/skills/</c>.
    /// </summary>
    public static readonly string[] SourceNames = [
        "recap",
        "errors",
        "hide",
        "disable",
        "validate-plan",
        "review-flows",
        "guided-tour"
    ];

    /// <summary>
    /// Folder names that prior installer versions wrote to
    /// <c>~/.codex/skills/</c>. Fixed list — only these names are removed
    /// during legacy cleanup; user-authored skills are never touched.
    /// </summary>
    public static readonly string[] LegacyCodexSkillNames = [
        "kcap-recap",
        "kcap-errors",
        "kcap-hide",
        "kcap-disable",
        "kcap-validate-plan"
    ];

    public static bool Install(string sourceDir, string targetDir) {
        if (!Directory.Exists(sourceDir)) return false;

        var missing = SourceNames
            .Where(n => !File.Exists(Path.Combine(sourceDir, n, "SKILL.md")))
            .ToList();

        if (missing.Count > 0) {
            Console.Error.WriteLine(
                $"Cannot install agent skills: missing SKILL.md for skill(s) under {sourceDir}: "
              + string.Join(", ", missing)
            );

            return false;
        }

        try {
            Directory.CreateDirectory(targetDir);

            foreach (var name in SourceNames) {
                var src    = Path.Combine(sourceDir, name);
                var prefix = "kcap-" + name;
                var dst    = Path.Combine(targetDir, prefix);

                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                CopyDirectoryWithFrontmatterRewrite(src, dst, prefix);
            }

            WriteMarker(targetDir);

            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not install agent skills: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    /// True when the user has previously installed kcap skills via setup
    /// or <c>kcap plugin install --skills</c>. The npm postinstall hook
    /// uses this to decide whether to refresh on upgrade (it does) vs. install
    /// for the first time (it doesn't — that's setup's job).
    /// </summary>
    /// <remarks>
    /// Detection is the marker file OR any owned <c>kcap-&lt;name&gt;</c>
    /// folder under <paramref name="targetDir"/>. The folder fallback covers
    /// users whose install predates the marker — without it, the very first
    /// upgrade onto a marker-aware build would no-op and leave stale skills
    /// untouched, defeating the auto-refresh entirely. After a successful
    /// refresh <see cref="Install"/> writes the marker, so subsequent upgrades
    /// take the fast path.
    /// </remarks>
    public static bool IsInstalled(string targetDir) {
        if (File.Exists(Path.Combine(targetDir, MarkerFileName))) return true;

        return Directory.Exists(targetDir) && SourceNames.Any(name => Directory.Exists(Path.Combine(targetDir, "kcap-" + name)));
    }

    /// <summary>
    /// Returns the version string from the marker file, or null when the
    /// marker is absent or unreadable.
    /// </summary>
    public static string? ReadMarker(string targetDir) {
        var path = Path.Combine(targetDir, MarkerFileName);

        try {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        } catch {
            return null;
        }
    }

    static void WriteMarker(string targetDir) {
        try {
            File.WriteAllText(Path.Combine(targetDir, MarkerFileName), CurrentVersion());
        } catch {
            // Best effort — failure to stamp the marker is non-fatal. The
            // worst case is that the next CLI upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    /// <summary>
    /// The version string stamped into the marker on a successful install.
    /// Exposed so callers can short-circuit a refresh when the marker already
    /// matches.
    /// </summary>
    public static string CurrentVersion() => CapacitorVersion.Current();

    /// <summary>
    /// True when the on-disk skills already match this build: the version
    /// marker equals <see cref="CurrentVersion"/> AND every owned
    /// <c>kcap-&lt;name&gt;</c> folder is present. A matching marker whose skill
    /// folders were deleted or corrupted reads as NOT current, so callers
    /// reinstall and self-heal — the marker alone can't be trusted to mean the
    /// skills are actually on disk (mirrors the hooks "marker present but host
    /// file missing" guard). Reinstall is non-destructive: <see cref="Install"/>
    /// overwrites only the owned folders and rewrites the marker.
    /// </summary>
    public static bool IsCurrent(string targetDir) =>
        ReadMarker(targetDir) == CurrentVersion()
        && SourceNames.All(name => Directory.Exists(Path.Combine(targetDir, "kcap-" + name)));

    /// <summary>
    /// Deletes every <c>kcap-&lt;name&gt;</c> folder this installer owns
    /// from <paramref name="targetDir"/>. User-authored folders are left alone.
    /// Returns a <see cref="RemovalResult"/> indicating whether any folder was
    /// removed and whether any deletion failed.
    /// </summary>
    public static RemovalResult Remove(string targetDir) {
        if (!Directory.Exists(targetDir)) return new RemovalResult(false, false);

        var removed = false;
        var errors  = false;

        foreach (var name in SourceNames) {
            var dst = Path.Combine(targetDir, "kcap-" + name);

            if (!Directory.Exists(dst)) continue;

            try {
                Directory.Delete(dst, recursive: true);
                removed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove agent skill 'kcap-{name}': {ex.Message}");
                errors = true;
            }
        }

        // Drop the marker so the next upgrade doesn't silently re-install
        // skills the user just removed.
        var marker = Path.Combine(targetDir, MarkerFileName);

        if (File.Exists(marker)) {
            try { File.Delete(marker); } catch {
                /* non-fatal */
            }
        }

        return new RemovalResult(removed, errors);
    }

    /// <summary>
    /// Removes legacy <c>kcap-*</c> folders that prior installer versions
    /// wrote to <c>~/.codex/skills/</c>. List of folder names is fixed
    /// (<see cref="LegacyCodexSkillNames"/>); user-authored skills are never
    /// touched. If the parent directory becomes empty, it is removed too.
    /// Returns a <see cref="RemovalResult"/> indicating whether any folder was
    /// removed and whether any deletion failed.
    /// </summary>
    public static RemovalResult CleanLegacyCodexSkills(string legacySkillsDir) {
        if (!Directory.Exists(legacySkillsDir)) return new RemovalResult(false, false);

        var removed = false;
        var errors  = false;

        foreach (var name in LegacyCodexSkillNames) {
            var dst = Path.Combine(legacySkillsDir, name);

            if (!Directory.Exists(dst)) continue;

            try {
                Directory.Delete(dst, recursive: true);
                Console.Out.WriteLine($"Removed legacy Codex skill folder: {dst}");
                removed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove legacy Codex skill '{name}': {ex.Message}");
                errors = true;
            }
        }

        // Clean up empty parent.
        try {
            if (Directory.Exists(legacySkillsDir) && !Directory.EnumerateFileSystemEntries(legacySkillsDir).Any()) {
                Directory.Delete(legacySkillsDir);
            }
        } catch {
            // Best effort — leaving an empty dir behind isn't harmful.
        }

        return new RemovalResult(removed, errors);
    }

    static void CopyDirectoryWithFrontmatterRewrite(string source, string destination, string targetName) {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source)) {
            var fileName = Path.GetFileName(file);
            var dstPath  = Path.Combine(destination, fileName);

            if (fileName == "SKILL.md") {
                File.WriteAllText(dstPath, RewriteNameFrontmatter(File.ReadAllText(file), targetName));
            } else {
                File.Copy(file, dstPath, overwrite: true);
            }
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            CopyDirectoryWithFrontmatterRewrite(dir, Path.Combine(destination, Path.GetFileName(dir)), targetName);
        }
    }

    /// <summary>
    /// Replaces the first <c>name:</c> line inside the YAML frontmatter
    /// block of a SKILL.md. The block is delimited by lines containing
    /// only <c>---</c>. If no frontmatter is found, the content is
    /// returned unchanged.
    /// </summary>
    internal static string RewriteNameFrontmatter(string content, string newName) {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        if (lines.Length < 2 || lines[0].Trim() != "---") return content;

        for (var i = 1; i < lines.Length; i++) {
            if (lines[i].Trim() == "---") break;

            if (lines[i].StartsWith("name:", StringComparison.Ordinal)) {
                lines[i] = $"name: {newName}";

                break;
            }
        }

        return string.Join('\n', lines);
    }
}

/// <summary>
/// Describes the outcome of a removal operation performed by
/// <see cref="AgentsSkillsInstaller"/>.
/// </summary>
public readonly record struct RemovalResult(bool RemovedAny, bool HadErrors);
