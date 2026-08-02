using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    /// <summary>
    /// Workspace-scoped vendor MCP configuration, relative to a worktree root. Declaring a server in one
    /// of these files is, for some vendors, enough to get its <c>command</c> executed — so in an agent
    /// worktree these are attacker-controlled process launches, not configuration.
    ///
    /// <para><b>Measured, not assumed.</b> Kiro spawns the server declared in
    /// <c>.kiro/settings/mcp.json</c> at session setup — no prompt, no model involvement, no tool call.
    /// Gemini spawns its <c>.gemini/settings.json</c> server too, and is saved only by
    /// <c>--allowed-mcp-server-names</c>, which turns out to gate the spawn itself. Cursor and Copilot were
    /// measured NOT to read their workspace files on the ACP path, each against an in-run positive control.
    /// The full result table is on the tracking issue.</para>
    ///
    /// <para><b>Why the list is wider than the vendors that are known to read them.</b> The two vendors
    /// currently protected are protected by their own argv, which is a property of each launcher rather
    /// than of the worktree. Kiro arrived with no gate at all and nobody noticed, so the list covers every
    /// hosted vendor's file plus the editor-generic ones — the point is that the next vendor is safe
    /// before anyone thinks about it.</para>
    /// </summary>
    internal static readonly ImmutableArray<string> WorkspaceMcpConfigPaths = [
        ".mcp.json",                    // Claude Code / generic
        ".cursor/mcp.json",
        ".gemini/settings.json",
        ".kiro/settings/mcp.json",
        ".vscode/mcp.json",             // editor-generic; several CLIs read it
        ".github/copilot/mcp.json",
        ".copilot/mcp.json",
        ".codex/config.toml"
    ];

    /// <summary>
    /// Removes <see cref="WorkspaceMcpConfigPaths"/> from a freshly created worktree, returning what was
    /// removed. Called once at creation, before any agent can be launched into the tree.
    ///
    /// <para><b>Why this exists at the worktree layer.</b> Worktrees are placed under the repo's own
    /// <c>.capacitor/</c> precisely so they INHERIT the repo's workspace trust (see
    /// <see cref="CreateAsync"/>) — that is what stops an agent stalling on a trust prompt. The same
    /// inheritance is what makes a config file committed to the branch under review load as trusted. So the
    /// exposure is created here and is fixed here, once, rather than in each vendor's argv where a new
    /// vendor starts unprotected.</para>
    ///
    /// <para><b>Symlinks are the sharp edge, because the content is hostile.</b> Deleting
    /// <c>.gemini/settings.json</c> naively, when a branch has made <c>.gemini</c> a symlink to the user's
    /// real <c>~/.gemini</c>, would destroy the operator's own configuration — turning a containment
    /// measure into the attack. So every ancestor is resolved to a physical path first and the file is
    /// removed ONLY when it still lands inside the worktree. That also closes the inverse trick, a branch
    /// symlinking <c>.cursor</c> to another directory it controls inside the same tree: the resolved path
    /// is inside, so it is still removed. A path resolving outside is left alone — whatever it points at
    /// belongs to the operator, not to the branch.</para>
    ///
    /// <para>Removal rather than emptying: an empty <c>mcpServers</c> would still leave a file whose other
    /// keys the vendor honours, and <c>.gemini/settings.json</c> and <c>.codex/config.toml</c> carry much
    /// more than MCP entries — all of it equally branch-controlled.</para>
    /// </summary>
    internal static IReadOnlyList<string> NeutralizeWorkspaceMcpConfig(string worktreePath) {
        var removed = new List<string>();
        var root    = RealPath(worktreePath);

        if (root is null) return removed;

        foreach (var relative in WorkspaceMcpConfigPaths) {
            var target = PhysicalTargetInside(root, relative);
            if (target is null) continue;

            try {
                if (!File.Exists(target)) continue;

                // File.Delete on a symlink removes the LINK, never its target, so a final component that
                // is a link is safe by construction here — the ancestor walk above is what matters.
                File.Delete(target);
                removed.Add(relative);
            } catch (Exception) {
                // A file we cannot remove is reported by absence from `removed`; failing the whole worktree
                // creation over one unreadable path would take out hosting for the repo entirely.
            }
        }

        return removed;
    }

    /// <summary>The physical path of <paramref name="relative"/> under <paramref name="physicalRoot"/>, or
    /// null when any ancestor resolves outside the worktree.</summary>
    static string? PhysicalTargetInside(string physicalRoot, string relative) {
        var combined = Path.Combine(physicalRoot,
            relative.Replace('/', Path.DirectorySeparatorChar));
        var parent   = Path.GetDirectoryName(combined);

        if (parent is null) return null;

        var physicalParent = RealPath(parent);

        return physicalParent is not null && IsInside(physicalRoot, physicalParent)
            ? Path.Combine(physicalParent, Path.GetFileName(combined))
            : null;
    }

    /// <summary>Fully resolves every symlinked component. <see cref="ResolveFirstLinkComponent"/> replaces
    /// one component per call, so this iterates it; the bound stops a symlink cycle from hanging the
    /// daemon rather than expressing a real depth limit.</summary>
    static string? RealPath(string path) {
        var current = Path.GetFullPath(path);

        for (var hops = 0; hops < 40; hops++) {
            var next = ResolveFirstLinkComponent(current);
            if (next is null) return current;
            current = Path.GetFullPath(next);
        }

        return null;   // cycle or pathological nesting: treat as unresolvable, therefore untouchable
    }

    static bool IsInside(string root, string candidate) {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidate.Equals(normalizedRoot, FileSystemPathComparison)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, FileSystemPathComparison);
    }
}
