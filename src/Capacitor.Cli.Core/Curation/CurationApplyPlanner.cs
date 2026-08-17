namespace Capacitor.Cli.Core.Curation;

public static class CurationApplyPlanner {
    public static ApplyPlan BuildPlan(
            string repoRoot,
            IReadOnlyDictionary<string, string?> contentByPath,
            IReadOnlyList<CuratedGuideline> guidelines) {

        var claude = Path.Combine(repoRoot, CuratedTargets.ClaudeMdName);
        var agents = Path.Combine(repoRoot, CuratedTargets.AgentsMdName);

        var claudeExists = contentByPath.TryGetValue(claude, out var cc) && cc is not null;
        var agentsExists = contentByPath.TryGetValue(agents, out var ac) && ac is not null;

        var hasContent = guidelines.Count > 0;
        var block      = CuratedBlock.Render(guidelines);   // null when empty
        var newBullets = guidelines.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : CuratedBlock.ExtractBullets(block!).ToHashSet(StringComparer.Ordinal);

        var targets = CuratedTargets.Resolve(repoRoot, claudeExists, agentsExists, hasContent);
        var files   = new List<FilePlan>();

        foreach (var path in targets) {
            contentByPath.TryGetValue(path, out var current);  // null when absent
            var oldBullets = current is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : CuratedBlock.ExtractBullets(current).ToHashSet(StringComparer.Ordinal);

            var added   = newBullets.Except(oldBullets).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var removed = oldBullets.Except(newBullets).OrderBy(x => x, StringComparer.Ordinal).ToList();

            var newContent = CuratedBlock.Splice(current ?? "", block);   // may throw → caller fails closed

            CurateAction action;
            if (current is null)                          action = CurateAction.Create;
            else if (!hasContent)                         action = oldBullets.Count > 0 ? CurateAction.Remove : CurateAction.NoOp;
            else if (newContent == current)               action = CurateAction.NoOp;
            else                                          action = CurateAction.Update;

            files.Add(new FilePlan(
                path, action,
                action == CurateAction.NoOp ? null : newContent,
                added, removed));
        }

        return new ApplyPlan(files);
    }
}
