using System.Text;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Skills;

/// <summary>One row of the server's versioned skills snapshot.</summary>
public sealed record SkillSnapshotItem {
    [JsonPropertyName("doc_id")]       public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]         public required string Slug        { get; init; }
    [JsonPropertyName("title")]        public required string Title       { get; init; }
    [JsonPropertyName("description")]  public string?         Description { get; init; }
    [JsonPropertyName("body")]         public required string Body        { get; init; }
    [JsonPropertyName("version")]      public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")] public required string ContentHash { get; init; }
}

public sealed record SkillsSnapshotResponse {
    [JsonPropertyName("etag")]   public string?              Etag   { get; init; }
    [JsonPropertyName("skills")] public SkillSnapshotItem[]? Skills { get; init; }
}

/// <summary>The sync ledger for one (repo, harness): which harness paths kcap owns. Pruning walks
/// THIS, never the skills root — user-authored skills and other plugins are untouchable.</summary>
public sealed record SkillsManifest {
    [JsonPropertyName("etag")]      public string?                Etag     { get; init; }
    [JsonPropertyName("synced_at")] public DateTimeOffset?        SyncedAt { get; init; }
    [JsonPropertyName("skills")]    public SkillsManifestEntry[]? Skills   { get; init; }
}

public sealed record SkillsManifestEntry {
    [JsonPropertyName("doc_id")]       public required Guid   DocId       { get; init; }
    [JsonPropertyName("slug")]         public required string Slug        { get; init; }
    [JsonPropertyName("version")]      public required int    Version     { get; init; }
    [JsonPropertyName("content_hash")] public required string ContentHash { get; init; }
    [JsonPropertyName("path")]         public required string Path        { get; init; }
    // Hash of the rendered file as written, so a later sync can tell an edited or deleted
    // materialization from a served one. Null (an older manifest) reads as drifted.
    [JsonPropertyName("file_hash")]    public string?         FileHash    { get; init; }
}

/// <summary>One harness tree skills materialize into. A null <see cref="Vendor"/> marks a tree
/// several harnesses read: the snapshot is fetched WITHOUT a vendor, so unknown-excludes keeps
/// every vendor-restricted doc out of it — such docs reach their harness via a vendored tree.</summary>
public sealed record SkillsTarget(string Key, string Root, string? Vendor);

public sealed record SkillsSyncPlan(
    IReadOnlyList<SkillSnapshotItem>   Writes,
    IReadOnlyList<SkillsManifestEntry> Prunes,
    IReadOnlyList<SkillSnapshotItem>   Unchanged);

/// <summary>
/// Pure reconciliation of the manifest against a fresh snapshot. Identity is <c>doc_id</c> — the
/// server's stable manifest key — so a retitled doc (same id, new slug) is a rewrite at the new
/// path plus a prune of the old one, never an orphaned directory.
/// </summary>
public static class SkillsSyncPlanner {
    public static SkillsSyncPlan Plan(SkillsManifest? manifest, IReadOnlyList<SkillSnapshotItem> snapshot) {
        var owned = (manifest?.Skills ?? []).ToDictionary(e => e.DocId);
        var writes    = new List<SkillSnapshotItem>();
        var unchanged = new List<SkillSnapshotItem>();
        foreach (var item in snapshot) {
            if (owned.TryGetValue(item.DocId, out var have)
                    && have.Version == item.Version && have.ContentHash == item.ContentHash
                    && have.Slug == item.Slug)
                unchanged.Add(item);
            else
                writes.Add(item);
        }
        var live   = snapshot.Select(s => s.DocId).ToHashSet();
        var prunes = (manifest?.Skills ?? [])
            .Where(e => !live.Contains(e.DocId)
                        || snapshot.First(s => s.DocId == e.DocId).Slug != e.Slug)
            .ToList();
        return new SkillsSyncPlan(writes, prunes, unchanged);
    }

    /// <summary>The materialized SKILL.md: YAML frontmatter (name + when-to-use description as a
    /// double-quoted scalar) over the approved body.</summary>
    public static string RenderSkillFile(SkillSnapshotItem item) {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(item.Slug).Append('\n');
        var description = string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description!;
        sb.Append("description: ").Append(YamlQuote(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(item.Body);
        if (!item.Body.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>A slug usable as a single path segment: lowercase alphanumerics and dashes only —
    /// exactly the server's slug alphabet. Anything else (separators, dots, empty) is refused
    /// before it can reach a filesystem operation.</summary>
    public static bool IsSafeSlug(string slug) =>
        slug.Length is > 0 and <= 100 && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    static string YamlQuote(string value) {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
            sb.Append(c switch {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}
