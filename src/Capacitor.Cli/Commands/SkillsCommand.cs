using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Skills;
using Capacitor.Cli.Harness.Claude;

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

    // The background refresh keys off the manifest's synced_at, so a burst of session starts
    // costs one network round-trip per interval, not one per session.
    static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(6);

    public async Task<int> HandleSync(bool dryRun, bool auto = false) {
        void Info(string line) { if (!auto) Console.WriteLine(line); }
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
        if (!TryLoadManifest(manifestPath, out var manifest)) return 1;
        if (auto && AutoThrottled(manifest, DateTimeOffset.UtcNow)) return 0;

        // Metadata alone cannot prove a skill is served: a deleted or hand-edited SKILL.md must be
        // re-materialized, so local drift forfeits the conditional request — a 304 would otherwise
        // report "up to date" over a missing file forever.
        var drifted = (manifest?.Skills ?? []).Where(ClaudeSkillsMaterializer.HasDrifted)
            .Select(e => e.DocId).ToHashSet();

        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(config, profiles, baseUrl);
        var url = $"{baseUrl}/api/repositories/{hash}/skills?vendor={Vendor}"
                + (HostPlatform.Normalized is { } platform ? $"&platform={platform}" : "");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (drifted.Count == 0 && manifest?.Etag is { Length: > 0 } etag)
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
            Info($"Skills up to date ({manifest?.Skills?.Length ?? 0} materialized).");
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
        // Whole-snapshot validation BEFORE any filesystem mutation: acting on a partially-valid
        // snapshot and recording its etag would prune real skills, write no replacements, and
        // 304 forever after — refusing outright leaves everything intact and retried in full.
        var unsafeSlugs = snapshot.Where(s => !SkillsSyncPlanner.IsSafeSlug(s.Slug)).ToList();
        if (unsafeSlugs.Count > 0) {
            foreach (var u in unsafeSlugs)
                await Console.Error.WriteLineAsync($"Refusing snapshot: unsafe slug '{u.Slug}'.");
            return 1;
        }
        var plan   = SkillsSyncPlanner.Plan(manifest, snapshot);
        var root   = ClaudeSkillsMaterializer.SkillsRoot();
        var writes = plan.Writes.Concat(plan.Unchanged.Where(u => drifted.Contains(u.DocId))).ToList();

        if (writes.Count == 0 && plan.Prunes.Count == 0) {
            if (!dryRun) SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
            Info($"Skills up to date ({snapshot.Length} materialized).");
            return 0;
        }

        foreach (var w in writes)
            Info($"{(dryRun ? "would write" : "write"),-12} {ClaudeSkillsMaterializer.SkillDirFor(root, w.Slug)} (v{w.Version})");
        foreach (var p in plan.Prunes)
            Info($"{(dryRun ? "would prune" : "prune"),-12} {p.Path}");
        if (dryRun) return 0;

        foreach (var w in writes) ClaudeSkillsMaterializer.Write(root, w);
        foreach (var p in plan.Prunes) ClaudeSkillsMaterializer.Prune(root, p);
        SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
        Info($"Synced {writes.Count} skill(s), pruned {plan.Prunes.Count}; {snapshot.Length} materialized.");
        return 0;
    }

    internal static bool AutoThrottled(SkillsManifest? manifest, DateTimeOffset now) =>
        manifest?.SyncedAt is { } syncedAt && now - syncedAt < AutoSyncInterval;

    static SkillsManifest BuildManifest(string? etag, SkillSnapshotItem[] snapshot, string root) => new() {
        Etag = etag, SyncedAt = DateTimeOffset.UtcNow,
        Skills = [.. snapshot.Select(s => new SkillsManifestEntry {
            DocId = s.DocId, Slug = s.Slug, Version = s.Version, ContentHash = s.ContentHash,
            Path = ClaudeSkillsMaterializer.SkillDirFor(root, s.Slug),
            FileHash = ClaudeSkillsMaterializer.FileHash(SkillsSyncPlanner.RenderSkillFile(s)),
        })],
    };

    // The manifest is the ownership ledger, and the two failure classes diverge: an unreadable
    // file may be transient (sharing violation), so the sync ABORTS rather than reconciling
    // ledger-less over a still-valid file; a corrupt file proceeds from scratch only once the
    // evidence is genuinely preserved aside — a failed preserve also aborts.
    static bool TryLoadManifest(string path, out SkillsManifest? manifest) {
        manifest = null;
        if (!File.Exists(path)) return true;
        string text;
        try {
            text = File.ReadAllText(path);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Cannot read skills manifest ({ex.Message}); aborting sync.");
            return false;
        }
        try {
            manifest = JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.SkillsManifest);
        } catch (JsonException) {
            manifest = null;
        }
        // A parseable `null` or a missing skills collection is no ledger either — under a stored
        // etag it would 304 forever with zero owned paths, stranding every prior directory. Same
        // recovery route as unparseable content.
        if (manifest?.Skills is not null) return true;
        manifest = null;
        try {
            File.Move(path, path + ".corrupt", overwrite: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Corrupt skills manifest could not be preserved ({ex.Message}); aborting sync.");
            return false;
        }
        Console.Error.WriteLine($"Warning: corrupt skills manifest moved aside ({path}.corrupt); re-syncing from scratch.");
        return true;
    }

    static void SaveManifest(string path, SkillsManifest manifest) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic replace: a crash mid-write must never truncate the ownership ledger in place.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
        File.Move(tmp, path, overwrite: true);
    }
}
