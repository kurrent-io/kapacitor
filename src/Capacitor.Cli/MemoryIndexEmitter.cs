using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli;

/// <summary>
/// builds the SessionStart "team memory" index
/// fragment from the <c>GET /api/memories/index</c> response: entries of
/// <c>{memory_id, slug, audience, description, kind, scope_kind, project_slug}</c>,
/// already capped and ranked by the server, plus the cwd repo's projects.
/// <para>
/// Entries are grouped by <c>audience</c> (org → team → user), preserving the
/// server's order within each group, and rendered as <c>slug [scope]: description</c>
/// — one line per memory — under a heading that tells the agent to call
/// <c>get_memory</c> / <c>search_memories</c> for full content. The <c>[scope]</c> tag
/// annotates the memory's home scope (a project-scoped memory shows <c>[project: {slug}]</c>,
/// a repo one <c>[repo]</c>, org-scoped and older servers render untagged). Bodies are
/// NEVER injected: this mirrors a local <c>MEMORY.md</c> index so the injected token cost
/// stays roughly flat as the pool grows.
/// </para>
/// <para>
/// Above that heading sits a lead-in naming the projects the cwd repo belongs to — the slug an
/// agent needs on the place axis of a save, which nothing else in the session tells it. A repo in
/// no project (or an older server, which reports none) gets no lead-in, and projects alone are
/// enough to emit a fragment: a project with no memories yet is exactly when the agent most needs
/// its slug.
/// </para>
/// <para>
/// Returns <c>null</c> when disabled, empty, or malformed — the caller
/// (<c>SessionStartAdditionalContext</c>) drops null fragments, so a failed or empty
/// fetch emits nothing (fail-open, same as guideline injection).
/// </para>
/// </summary>
static class MemoryIndexEmitter {
    /// <summary>
    /// The stable leading marker on every fragment this emitter produces. Named because a CONSUMER
    /// needs to recognise an already-injected fragment: OpenCode's plugin appends into a system-prompt
    /// array it does not own, so "have I already put mine in here?" has to be answerable from the text
    /// itself. An invisible HTML comment, so it costs nothing a reader would notice.
    ///
    /// <para>Versioned (<c>:v1</c>) deliberately: a future fragment shape can change the marker and a
    /// consumer keyed on the old one simply stops recognising it, which fails toward a duplicate rather
    /// than toward silently matching a different format.</para>
    /// </summary>
    internal const string FragmentMarker = "<!-- kcap-memory-index:v1 -->";

    static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "preference", "feedback", "project", "reference" };

    public static string? BuildFragment(
            IEnumerable<SessionStartMemoryEntry> entries,
            IEnumerable<SessionStartMemoryProject>? projects = null) {
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
            var line = $"- {slug}{ScopeTag(entry)}: {description}";
            switch (entry.Audience) {
                case "org": org.Add(line); break;
                case "team": team.Add(line); break;
                case "user": user.Add(line); break;
            }
        }
        var leadIn = BuildLeadIn(projects);
        if (org.Count == 0 && team.Count == 0 && user.Count == 0)
            return leadIn is null ? null : FragmentMarker + "\n" + leadIn;

        var prefix = FragmentMarker + "\n" + (leadIn is null ? "" : leadIn + "\n\n") + "## Team memory\n" +
            "Durable memories for this repo/context. Call `get_memory <slug>` for the full content of any entry, or `search_memories` to find more.";
        var sb = new StringBuilder(prefix);
        var currentBytes = Encoding.UTF8.GetByteCount(prefix);
        if (!AppendBoundedGroup(sb, "Org", org, ref currentBytes)) return sb.ToString();
        if (!AppendBoundedGroup(sb, "Team", team, ref currentBytes)) return sb.ToString();
        AppendBoundedGroup(sb, "Yours", user, ref currentBytes);
        return sb.ToString();
    }

    /// <summary>Renders the bare-array index body — the form a server that does not honour
    /// <c>include=projects</c> answers with, which carries no projects and so no lead-in. Anything
    /// but an array yields null; the object form is read by
    /// <see cref="SessionStartMemory.SessionStartMemoryContextProvider"/>, which knows which shape it asked for.</summary>
    /// <param name="indexNode">The response body parsed as a <see cref="JsonNode"/>.</param>
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
                    o["kind"]?.GetValue<string>() ?? "feedback",
                    o["scope_kind"]?.GetValue<string>(),
                    o["project_slug"]?.GetValue<string>()));
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

    // One line per project, each naming the slug twice: once as the repo's membership and once as the
    // literal argument value, so an agent that reads only the second half still has what it must pass.
    static string? BuildLeadIn(IEnumerable<SessionStartMemoryProject>? projects) {
        if (projects is null) return null;
        var lines = new List<string>();
        foreach (var project in projects) {
            if (lines.Count == SessionStartMemoryConstants.MaxProjects) break;
            var slug = Normalize(project.Slug, 128);
            if (slug.Length == 0) continue;
            var name = Normalize(project.Name, 128);
            var label = name.Length == 0 || name == slug ? "" : $" ({name})";
            lines.Add($"This repo belongs to project \"{slug}\"{label}. " +
                      $"Save learnings that span its repos with project: \"{slug}\".");
        }
        return lines.Count == 0 ? null : string.Join('\n', lines);
    }

    // Annotates the memory's home scope after the slug. A project-scoped memory applies across the
    // project's repos, so it carries the resolved slug; a repo memory is tagged plainly; org (the
    // broadest home, and the fallback for an older server that sends no scope) renders untagged.
    static string ScopeTag(SessionStartMemoryEntry entry) => entry.ScopeKind switch {
        "project" when Normalize(entry.ProjectSlug, 128) is { Length: > 0 } s => $" [project: {s}]",
        "repo" => " [repo]",
        _      => "",
    };

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
