namespace Capacitor.Cli.Core.Tests.Unit;

public class AgentsSkillsInstallerTests {
    static readonly string[] SourceNames = ["recap", "errors", "disable", "hide", "validate-plan", "review-flows", "guided-tour"];

    [Test]
    public async Task Mirror_of_SourceNames_matches_the_installer() {
        // Every test below builds its fixture from the local mirror, so a skill added to the
        // installer but not here would be silently uncovered. Pin the two together.
        await Assert.That(SourceNames).IsEquivalentTo(AgentsSkillsInstaller.SourceNames);
    }

    [Test]
    public async Task Install_copies_each_source_to_kcap_prefixed_target() {
        using var src = new TempDir();
        using var dst = new TempDir();

        foreach (var name in SourceNames) {
            src.CreateDir(name);
            src.CreateFile([name, "SKILL.md"],
                $"---\nname: {name}\ndescription: x\n---\nbody\n");
        }

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(ok).IsTrue();

        foreach (var name in SourceNames) {
            var path = dst.PathTo($"kcap-{name}", "SKILL.md");
            await Assert.That(File.Exists(path)).IsTrue();
        }
    }

    [Test]
    public async Task Install_rewrites_name_frontmatter_to_kcap_prefix() {
        using var src = new TempDir();
        using var dst = new TempDir();

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            src.CreateDir(name);
            src.CreateFile([name, "SKILL.md"], $"---\nname: {name}\n---\nbody\n");
        }
        src.CreateDir("recap");
        src.CreateFile(["recap", "SKILL.md"],
            "---\nname: recap\ndescription: |\n  long desc\n  with newlines\n---\nbody content\n");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var written = await File.ReadAllTextAsync(dst.PathTo("kcap-recap", "SKILL.md"));
        await Assert.That(written).Contains("name: kcap-recap");
        await Assert.That(written).DoesNotContain("name: recap\n");
        await Assert.That(written).Contains("description: |");
        await Assert.That(written).Contains("body content");
    }

    [Test]
    public async Task Install_copies_nested_files_verbatim() {
        using var src = new TempDir();
        using var dst = new TempDir();

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            src.CreateDir(name);
            src.CreateFile([name, "SKILL.md"], $"---\nname: {name}\n---\nbody\n");
        }
        var refsDir = src.CreateDir("recap", "references");
        refsDir.CreateFile("examples.md", "raw content $not-rewritten");
        src.CreateFile(["recap", "SKILL.md"],
            "---\nname: recap\n---\nbody");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var copied = await File.ReadAllTextAsync(
            dst.PathTo("kcap-recap", "references", "examples.md"));
        await Assert.That(copied).IsEqualTo("raw content $not-rewritten");
    }

    [Test]
    public async Task Install_leaves_user_authored_folders_untouched() {
        using var src = new TempDir();
        using var dst = new TempDir();

        var foreign = dst.CreateDir("user-skill");
        foreign.CreateFile("SKILL.md", "user content");

        foreach (var name in SourceNames) {
            src.CreateDir(name);
            src.CreateFile([name, "SKILL.md"], $"---\nname: {name}\n---\nbody");
        }

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(File.Exists(foreign.PathTo("SKILL.md"))).IsTrue();
        var content = await File.ReadAllTextAsync(foreign.PathTo("SKILL.md"));
        await Assert.That(content).IsEqualTo("user content");
    }

    [Test]
    public async Task Install_replaces_existing_kcap_folder_atomically() {
        using var src = new TempDir();
        using var dst = new TempDir();

        var stale = dst.CreateDir("kcap-recap");
        stale.CreateFile("SKILL.md", "old version");
        stale.CreateFile("leftover.md", "delete me");

        foreach (var name in SourceNames.Where(n => n != "recap")) {
            src.CreateDir(name);
            src.CreateFile([name, "SKILL.md"], $"---\nname: {name}\n---\nbody\n");
        }
        src.CreateDir("recap");
        src.CreateFile(["recap", "SKILL.md"],
            "---\nname: recap\n---\nnew body");

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var newSkill = await File.ReadAllTextAsync(stale.PathTo("SKILL.md"));
        await Assert.That(newSkill).Contains("new body");
        await Assert.That(File.Exists(stale.PathTo("leftover.md"))).IsFalse();
    }

    [Test]
    public async Task Install_returns_false_when_a_source_folder_is_missing() {
        using var src = new TempDir();
        using var dst = new TempDir();

        src.CreateDir("recap");
        src.CreateFile(["recap", "SKILL.md"],
            "---\nname: recap\n---\nbody");

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(ok).IsFalse();
        await Assert.That(Directory.Exists(dst.PathTo("kcap-recap"))).IsFalse();
    }

    [Test]
    public async Task Install_returns_false_when_SKILL_md_is_missing() {
        using var src = new TempDir();
        using var dst = new TempDir();

        // Every folder exists, but one is missing its SKILL.md
        foreach (var name in SourceNames) {
            src.CreateDir(name);
            if (name != "validate-plan") {
                src.CreateFile([name, "SKILL.md"],
                    $"---\nname: {name}\n---\nbody");
            }
        }

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(ok).IsFalse();
        foreach (var name in SourceNames) {
            await Assert.That(Directory.Exists(dst.PathTo($"kcap-{name}"))).IsFalse();
        }
    }

    [Test]
    public async Task Remove_deletes_kcap_prefixed_folders_only() {
        using var dst = new TempDir();

        foreach (var src in SourceNames) {
            dst.CreateDir($"kcap-{src}");
        }
        dst.CreateDir("user-skill");

        var result = AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(result.RemovedAny).IsTrue();
        await Assert.That(result.HadErrors).IsFalse();
        foreach (var src in SourceNames) {
            await Assert.That(Directory.Exists(dst.PathTo($"kcap-{src}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(dst.PathTo("user-skill"))).IsTrue();
    }

    [Test]
    public async Task Remove_returns_false_when_no_kcap_folders_present() {
        using var dst = new TempDir();
        dst.CreateDir("someone-elses-skill");

        var result = AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(result.RemovedAny).IsFalse();
        await Assert.That(result.HadErrors).IsFalse();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_removes_only_known_kcap_folders() {
        using var fakeHome = new TempDir();
        var legacy = fakeHome.CreateDir(".codex", "skills");

        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            legacy.CreateDir(name);
        }
        legacy.CreateDir("user-codex-skill");

        var result = AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(result.RemovedAny).IsTrue();
        await Assert.That(result.HadErrors).IsFalse();
        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            await Assert.That(Directory.Exists(legacy.PathTo(name))).IsFalse();
        }
        await Assert.That(Directory.Exists(legacy.PathTo("user-codex-skill"))).IsTrue();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_removes_empty_parent_dir() {
        using var fakeHome = new TempDir();
        var legacy = fakeHome.CreateDir(".codex", "skills");
        foreach (var name in AgentsSkillsInstaller.LegacyCodexSkillNames) {
            legacy.CreateDir(name);
        }

        AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(Directory.Exists(legacy)).IsFalse();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_preserves_non_empty_parent_dir() {
        using var fakeHome = new TempDir();
        var legacy = fakeHome.CreateDir(".codex", "skills");
        legacy.CreateDir("kcap-recap");
        legacy.CreateDir("user-codex-skill");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(Directory.Exists(legacy)).IsTrue();
        await Assert.That(Directory.Exists(legacy.PathTo("user-codex-skill"))).IsTrue();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_is_noop_when_parent_dir_missing() {
        using var fakeHome = new TempDir();
        var legacy = fakeHome.PathTo(".codex", "skills");
        // legacy dir not created

        var result = AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(result.RemovedAny).IsFalse();
        await Assert.That(result.HadErrors).IsFalse();
    }

    [Test]
    public async Task Install_writes_version_marker_at_target_root() {
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var marker = dst.PathTo(AgentsSkillsInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();

        var content = (await File.ReadAllTextAsync(marker)).Trim();
        await Assert.That(content).IsNotEmpty();
    }

    [Test]
    public async Task IsInstalled_is_false_before_install_and_true_after() {
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsFalse();

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsTrue();
    }

    [Test]
    public async Task ReadMarker_returns_null_when_marker_missing() {
        using var dst = new TempDir();
        var marker = AgentsSkillsInstaller.ReadMarker(dst.Path);
        await Assert.That(marker).IsNull();
    }

    [Test]
    public async Task ReadMarker_returns_written_version_after_install() {
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        var version = AgentsSkillsInstaller.ReadMarker(dst.Path);
        await Assert.That(version).IsNotNull();
        await Assert.That(version!).IsNotEmpty();
    }

    [Test]
    public async Task IsCurrent_is_true_after_a_fresh_install() {
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        AgentsSkillsInstaller.Install(src.Path, dst.Path);

        await Assert.That(AgentsSkillsInstaller.IsCurrent(dst.Path)).IsTrue();
    }

    [Test]
    public async Task IsCurrent_is_false_when_marker_missing() {
        using var dst = new TempDir();
        // No install, no marker.
        await Assert.That(AgentsSkillsInstaller.IsCurrent(dst.Path)).IsFalse();
    }

    [Test]
    public async Task IsCurrent_is_false_when_a_skill_folder_deleted_despite_marker() {
        // Self-heal guard: a matching marker whose skill folders were deleted must NOT
        // read as current, or the install/refresh would skip and leave the agent without skills.
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(AgentsSkillsInstaller.IsCurrent(dst.Path)).IsTrue();  // marker + folders present

        // Delete one owned folder but leave the marker in place.
        Directory.Delete(dst.PathTo("kcap-recap"), recursive: true);

        await Assert.That(AgentsSkillsInstaller.IsCurrent(dst.Path)).IsFalse();
    }

    [Test]
    public async Task Remove_deletes_version_marker() {
        using var src = new TempDir();
        using var dst = new TempDir();
        await SeedSourceSkills(src.Path);

        AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsTrue();

        AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsFalse();
    }

    [Test]
    public async Task IsInstalled_returns_true_for_pre_marker_install() {
        // Regression: users whose skills were installed before the marker
        // existed must still be detected as "installed" so the first upgrade
        // onto a marker-aware build refreshes them instead of no-opping.
        using var dst = new TempDir();
        dst.CreateDir("kcap-recap");

        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsTrue();
    }

    [Test]
    public async Task IsInstalled_returns_false_when_only_unrelated_folders_present() {
        using var dst = new TempDir();
        dst.CreateDir("user-skill");
        dst.CreateDir("kcap-something-else");

        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsFalse();
    }

    [Test]
    public async Task Install_failure_does_not_write_marker() {
        using var src = new TempDir();
        using var dst = new TempDir();
        // Empty source — Install should fail.

        var ok = AgentsSkillsInstaller.Install(src.Path, dst.Path);
        await Assert.That(ok).IsFalse();
        await Assert.That(AgentsSkillsInstaller.IsInstalled(dst.Path)).IsFalse();
    }

    static async Task SeedSourceSkills(string sourcePath) {
        foreach (var name in SourceNames) {
            Directory.CreateDirectory(Path.Combine(sourcePath, name));
            await File.WriteAllTextAsync(
                Path.Combine(sourcePath, name, "SKILL.md"),
                $"---\nname: {name}\ndescription: x\n---\nbody\n");
        }
    }

    [Test]
    public async Task Install_failure_does_not_trigger_legacy_cleanup() {
        // Asserts the contract: install runs first, legacy cleanup runs only on success.
        // The contract lives in PluginCommand (caller); here we verify the unit
        // primitives are independent and the caller can sequence them safely.
        using var src      = new TempDir();
        using var fakeHome = new TempDir();
        var legacy = fakeHome.CreateDir(".codex", "skills");
        legacy.CreateDir("kcap-recap");

        // sourceDir empty -> Install returns false without throwing.
        var ok = AgentsSkillsInstaller.Install(src.Path, fakeHome.PathTo(".agents", "skills"));
        await Assert.That(ok).IsFalse();

        // Caller would skip cleanup. Verify directly that legacy dir is still present.
        await Assert.That(Directory.Exists(legacy.PathTo("kcap-recap"))).IsTrue();
    }

    [Test]
    public async Task Remove_returns_RemovedAny_false_HadErrors_false_when_no_kcap_folders_in_populated_dir() {
        using var dst = new TempDir();
        dst.CreateDir("someone-elses-skill");

        var result = AgentsSkillsInstaller.Remove(dst.Path);

        await Assert.That(result.RemovedAny).IsFalse();
        await Assert.That(result.HadErrors).IsFalse();
    }

    [Test]
    public async Task CleanLegacyCodexSkills_returns_RemovedAny_false_HadErrors_false_when_dir_missing() {
        using var fakeHome = new TempDir();
        var legacy = fakeHome.PathTo(".codex", "skills");
        // legacy dir not created — simulates never having had Codex installed

        var result = AgentsSkillsInstaller.CleanLegacyCodexSkills(legacy);

        await Assert.That(result.RemovedAny).IsFalse();
        await Assert.That(result.HadErrors).IsFalse();
    }
}
