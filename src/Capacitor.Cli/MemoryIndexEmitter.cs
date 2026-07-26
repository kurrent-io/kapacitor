using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli;

/// <summary>
/// builds the SessionStart "team memory" index
/// fragment from the <c>GET /api/memories/index</c> response body: a JSON array of
/// <c>{memory_id, slug, audience, description, kind}</c>, already capped and
/// most-recently-updated first by the server.
/// <para>
/// Entries are grouped by <c>audience</c> (org → team → user), preserving the
/// server's order within each group, and rendered as <c>slug: description</c> — one
/// line per memory — under a lead-in that tells the agent to call
/// <c>get_memory</c> / <c>search_memories</c> for full content. Bodies are NEVER
/// injected: this mirrors a local <c>MEMORY.md</c> index so the injected token cost
/// stays roughly flat as the pool grows.
/// </para>
/// <para>
/// Returns <c>null</c> when disabled, empty, or malformed — the caller
/// (<c>SessionStartAdditionalContext</c>) drops null fragments, so a failed or empty
/// fetch emits nothing (fail-open, same as guideline injection).
/// </para>
/// </summary>
static class MemoryIndexEmitter {
    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "preference", "feedback", "project", "reference" };

    public static string? BuildFragment(IEnumerable<SessionStartMemoryEntry> entries) {
        var org = new List<string>();
        var team = new List<string>();
        var user = new List<string>();
        var inspected = 0;
        foreach (var entry in entries) {
            if (++inspected > SessionStartMemoryConstants.MaxEntries) break;
            if (string.IsNullOrWhiteSpace(entry.MemoryId) || ScalarCount(entry.MemoryId.Trim()) > 256 ||
                !Kinds.Contains(entry.Kind ?? "")) continue;
            var slug = Normalize(entry.Slug, 128);
            var description = Normalize(entry.Description, 512);
            if (slug.Length == 0 || description.Length == 0) continue;
            var line = $"- {slug}: {description}";
            switch (entry.Audience) {
                case "org": org.Add(line); break;
                case "team": team.Add(line); break;
                case "user": user.Add(line); break;
            }
        }
        if (org.Count == 0 && team.Count == 0 && user.Count == 0) return null;

        var prefix = "<!-- kcap-memory-index:v1 -->\n## Team memory\n" +
            "Durable memories for this repo/context. Call `get_memory <slug>` for the full content of any entry, or `search_memories` to find more.";
        var sb = new StringBuilder(prefix);
        var currentBytes = Encoding.UTF8.GetByteCount(prefix);
        if (!AppendBoundedGroup(sb, "Org", org, ref currentBytes)) return sb.ToString();
        if (!AppendBoundedGroup(sb, "Team", team, ref currentBytes)) return sb.ToString();
        AppendBoundedGroup(sb, "Yours", user, ref currentBytes);
        return sb.ToString();
    }

    /// <param name="indexNode">The <c>/api/memories/index</c> response body parsed as a <see cref="JsonNode"/> (expected: a JSON array).</param>
    /// <param name="disabled">True when the user set <c>disable_memory_index</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? indexNode, bool disabled) {
        if (disabled) return null;
        if (indexNode is not JsonArray entries || entries.Count == 0) return null;
        var typed = new List<SessionStartMemoryEntry>();
        foreach (var node in entries) {
            if (node is not JsonObject o) continue;
            try {
                typed.Add(new SessionStartMemoryEntry(
                    o["memory_id"]?.GetValue<string>() ?? "legacy",
                    o["slug"]?.GetValue<string>(),
                    o["audience"]?.GetValue<string>(),
                    o["description"]?.GetValue<string>(),
                    o["kind"]?.GetValue<string>() ?? "feedback"));
            } catch {
                continue; // skip a malformed entry rather than dropping the whole block
            }
        }
        return BuildFragment(typed);
    }

    static bool AppendBoundedGroup(StringBuilder sb, string heading, List<string> lines, ref int currentBytes) {
        if (lines.Count == 0) return true;
        var headerWritten = false;
        foreach (var line in lines) {
            var addition = (headerWritten ? "\n" : $"\n\n### {heading}\n") + line;
            var additionBytes = Encoding.UTF8.GetByteCount(addition);
            if (currentBytes + additionBytes > SessionStartMemoryConstants.MaxFragmentBytes)
                return false;
            sb.Append(addition);
            currentBytes += additionBytes;
            headerWritten = true;
        }
        return true;
    }

    static string Normalize(string? value, int maxScalars) {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder();
        var whitespace = false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes()) {
            if (Rune.IsWhiteSpace(rune)) { whitespace = sb.Length > 0; continue; }
            if (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Control) continue;
            if (count++ >= maxScalars) break;
            if (whitespace) { sb.Append(' '); whitespace = false; }
            sb.Append(rune.ToString());
        }
        return sb.ToString().Trim();
    }

    static int ScalarCount(string value) => value.EnumerateRunes().Count();
}
