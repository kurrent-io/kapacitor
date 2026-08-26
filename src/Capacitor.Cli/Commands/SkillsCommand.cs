using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap skills sync</c> — materializes the server's versioned skill-doc snapshot for this repo
/// into the harness's user-level skills root (Claude, the one harness with a skills mechanism).
/// Server-canonical and centrally revocable: files land under a kcap namespace, a manifest in the
/// kcap config root records every path kcap owns, and pruning walks the manifest — never the
/// skills root — so user-authored skills are untouchable. Nothing is ever written into a repo.
/// </summary>
class SkillsCommand(ConfigRoot config, ProfileContext profiles) {
    const string Vendor = "claude";

    public async Task<int> HandleSync(bool dryRun) {
        var baseUrl = profiles.Resolution.ServerUrl!;
        var cwd = Environment.CurrentDirectory;

        if (GitRepository.FindRoot(cwd) is null) {
            await Console.Error.WriteLineAsync("Not inside a git repository — run `kcap skills sync` from a repo.");
            return 1;
        }
        var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd);
        if (repo?.Owner is null || repo.RepoName is null) {
            await Console.Error.WriteLineAsync("Could not determine the repo's owner/name from its git remote.");
            return 1;
        }
        var hash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);

        var manifestPath = config.Path("skills", hash, Vendor, "manifest.json");
        var manifest = LoadManifest(manifestPath);

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(config, profiles, baseUrl);
        var url = $"{baseUrl}/api/repositories/{hash}/skills?vendor={Vendor}"
                + (HostPlatform.Normalized is { } platform ? $"&platform={platform}" : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (manifest?.Etag is { Length: > 0 } etag)
            request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{etag}\"");

        HttpResponseMessage resp;
        try {
            resp = await client.SendAsync(request);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }

        if (resp.StatusCode == HttpStatusCode.NotModified) {
            if (!dryRun) SaveManifest(manifestPath, manifest! with { SyncedAt = DateTimeOffset.UtcNow });
            Console.WriteLine($"Skills up to date ({manifest?.Skills?.Length ?? 0} materialized).");
            return 0;
        }
        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) return 1;
        if (resp.StatusCode == HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return 1;
        }
        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");
            return 1;
        }

        SkillsSnapshotResponse? dto;
        try {
            dto = JsonSerializer.Deserialize(
                await resp.Content.ReadAsStringAsync(), CapacitorJsonContext.Default.SkillsSnapshotResponse);
        } catch (JsonException) {
            dto = null;
        }
        if (dto is null) {
            await Console.Error.WriteLineAsync("Malformed response from server (could not parse skills snapshot).");
            return 1;
        }

        var snapshot = dto.Skills ?? [];
        var plan     = SkillsSyncPlanner.Plan(manifest, snapshot);
        var root     = SkillsRoot();

        if (plan.Writes.Count == 0 && plan.Prunes.Count == 0) {
            if (!dryRun) SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
            Console.WriteLine($"Skills up to date ({snapshot.Length} materialized).");
            return 0;
        }

        foreach (var w in plan.Writes)
            Console.WriteLine($"{(dryRun ? "would write" : "write"),-12} {SkillDirFor(root, w.Slug)} (v{w.Version})");
        foreach (var p in plan.Prunes)
            Console.WriteLine($"{(dryRun ? "would prune" : "prune"),-12} {p.Path}");
        if (dryRun) return 0;

        foreach (var w in plan.Writes) {
            // The slug becomes a path segment, so it must BE one — a server (or tampered response)
            // handing out separators or dots must not reach a filesystem operation.
            if (!SkillsSyncPlanner.IsSafeSlug(w.Slug)) {
                await Console.Error.WriteLineAsync($"Skipping skill with unsafe slug: {w.Slug}");
                continue;
            }
            var dir = SkillDirFor(root, w.Slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), SkillsSyncPlanner.RenderSkillFile(w));
        }
        foreach (var p in plan.Prunes) {
            // Only ever a manifest-recorded path, and only a DIRECT kcap-* child of the skills
            // root — a manifest edited by hand must not aim the delete anywhere else (prefix
            // checks admit siblings like skills-backup/ and nested user directories; parent
            // EQUALITY does not).
            var full = Path.GetFullPath(p.Path);
            if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.Ordinal)
                    && Path.GetFileName(full).StartsWith("kcap-", StringComparison.Ordinal)
                    && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
        Console.WriteLine($"Synced {plan.Writes.Count} skill(s), pruned {plan.Prunes.Count}; {snapshot.Length} materialized.");
        return 0;
    }

    static string SkillsRoot() =>
        Path.Combine(PathHelpers.HomeDirectory, ".claude", "skills");

    // The server's slug is already doc-id-anchored and unique; the kcap- prefix namespaces the
    // materialized set inside the shared user-level skills root.
    static string SkillDirFor(string root, string slug) => Path.Combine(root, "kcap-" + slug);

    static SkillsManifest BuildManifest(string? etag, SkillSnapshotItem[] snapshot, string root) => new() {
        Etag = etag, SyncedAt = DateTimeOffset.UtcNow,
        // Unsafe-slug items are skipped by the write loop, so they must not be claimed here either.
        Skills = [.. snapshot.Where(s => SkillsSyncPlanner.IsSafeSlug(s.Slug)).Select(s => new SkillsManifestEntry {
            DocId = s.DocId, Slug = s.Slug, Version = s.Version, ContentHash = s.ContentHash,
            Path = SkillDirFor(root, s.Slug),
        })],
    };

    static SkillsManifest? LoadManifest(string path) {
        if (!File.Exists(path)) return null;
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.SkillsManifest);
        } catch {
            // The manifest is the ownership ledger — losing it silently would strand revoked
            // directories forever. Keep the evidence aside; the next full sync rebuilds ownership.
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch { /* best effort */ }
            Console.Error.WriteLine($"Warning: corrupt skills manifest moved aside ({path}.corrupt); re-syncing from scratch.");
            return null;
        }
    }

    static void SaveManifest(string path, SkillsManifest manifest) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic replace: a crash mid-write must never truncate the ownership ledger in place.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
        File.Move(tmp, path, overwrite: true);
    }
}
