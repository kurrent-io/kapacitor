namespace Capacitor.Cli.Core.Instructions;

/// <summary>
/// Writes kcap's marker-delimited block into a harness's markdown agent-instructions file,
/// preserving user content around it. Non-destructive, idempotent, atomic, fail-closed; the inline
/// markers are self-identifying, so no sidecar ownership file is needed.
/// </summary>
public static class AgentInstructionsWriter {
    public enum Change { Unchanged, Updated, Failed }

    enum BlockState { Absent, Present, Malformed }

    // HTML comments so the block is valid Markdown and clearly delimited. Kept stable across
    // versions so an older block is always found and replaced/removed rather than duplicated.
    public const string BeginMarker = "<!-- BEGIN kcap (managed by `kcap` — do not edit inside this block) -->";
    public const string EndMarker   = "<!-- END kcap -->";

    /// <summary>Insert or refresh kcap's block containing <paramref name="body"/> in the file at
    /// <paramref name="path"/>. Creates the file if absent; preserves any content outside the block.</summary>
    public static Change Write(string path, string body) {
        var block = BeginMarker + "\n" + body.Trim('\n') + "\n" + EndMarker;
        try {
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var (state, start, end) = FindBlock(existing);
            if (state == BlockState.Malformed) return Change.Failed;   // BEGIN without END — refuse rather than duplicate

            var updated = state == BlockState.Present
                ? existing[..start] + block + existing[end..]          // replace in place
                : AppendBlock(existing, block);
            if (updated == existing) return Change.Unchanged;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteAtomic(path, updated);
            return Change.Updated;
        } catch {
            return Change.Failed;
        }
    }

    /// <summary>Strip kcap's block from the file. Deletes the file if nothing but the block (and
    /// whitespace) remains. No-op (Unchanged) if the file or block is absent.</summary>
    public static Change Remove(string path) {
        try {
            if (!File.Exists(path)) return Change.Unchanged;
            var existing = File.ReadAllText(path);
            var (state, start, end) = FindBlock(existing);
            if (state == BlockState.Malformed) return Change.Failed;   // BEGIN without END — can't safely strip
            if (state == BlockState.Absent) return Change.Unchanged;

            var stripped = StripBlock(existing, start, end);
            if (stripped == existing) return Change.Unchanged;

            if (string.IsNullOrWhiteSpace(stripped)) {
                File.Delete(path);
                return Change.Updated;
            }
            WriteAtomic(path, stripped);
            return Change.Updated;
        } catch {
            return Change.Failed;
        }
    }

    static string AppendBlock(string content, string block) {
        if (content.Length == 0) return block + "\n";        // fresh file
        var sep = content.EndsWith('\n') ? "\n" : "\n\n";    // one blank line before ours
        return content + sep + block + "\n";
    }

    static string StripBlock(string content, int start, int end) {
        var before = content[..start].TrimEnd('\n');
        var after  = content[end..].TrimStart('\n');
        if (before.Length == 0) return after;
        if (after.Length == 0)  return before + "\n";
        return before + "\n\n" + after;
    }

    // Malformed = ambiguous ownership we refuse to touch rather than risk corrupting user content:
    //  - a BEGIN marker with no following END (truncated write / merge conflict / hand-edit); or
    //  - more than one BEGIN marker (a second block, or the marker quoted in user prose above/below
    //    ours) — replacing/stripping "first BEGIN … first END" would span unrelated content.
    static (BlockState State, int Start, int End) FindBlock(string content) {
        var start = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return (BlockState.Absent, -1, -1);
        if (content.IndexOf(BeginMarker, start + BeginMarker.Length, StringComparison.Ordinal) >= 0)
            return (BlockState.Malformed, -1, -1);
        var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return (BlockState.Malformed, -1, -1);
        return (BlockState.Present, start, end + EndMarker.Length);
    }

    static void WriteAtomic(string path, string content) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content);
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { /* best-effort */ } throw; }
    }
}
