using System.Collections.Immutable;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Thrown when branch-authored vendor config is present in a worktree and could not be removed.
/// Fail-closed on purpose: the alternative is handing the tree to a vendor that executes it.</summary>
public sealed class WorkspaceMcpNeutralizationException(string path, Exception inner)
    : Exception($"Refusing to use this worktree: branch-authored vendor MCP config at '{path}' could not "
              + "be removed. Some vendors execute the command it declares at session setup, so launching "
              + "with it still present would run branch-controlled code as the daemon user.", inner) {
    public string Path { get; } = path;
}

public partial class WorktreeManager {
    /// <summary>
    /// Workspace-scoped vendor MCP configuration, relative to a worktree root. Declaring a server in one
    /// of these files is, for some vendors, enough to get its <c>command</c> executed — so in an agent
    /// worktree these are attacker-controlled process launches, not configuration.
    ///
    /// <para><b>Measured, not assumed.</b> Kiro spawns the server declared in
    /// <c>.kiro/settings/mcp.json</c> at session setup — no prompt, no model involvement, no tool call.
    /// Gemini spawns its <c>.gemini/settings.json</c> server too, and is saved only by
    /// <c>--allowed-mcp-server-names</c>, which gates the spawn itself. Cursor and Copilot were measured
    /// NOT to read their workspace files on the ACP path, each against an in-run positive control.</para>
    ///
    /// <para><b>Why the list is wider than the vendors known to read them.</b> The protected vendors are
    /// protected by their own argv, a property of each launcher rather than of the worktree. Kiro arrived
    /// with no gate at all and nobody noticed, so the list covers every hosted vendor's file plus the
    /// editor-generic ones — the point is that the next vendor is safe before anyone thinks about it.</para>
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
    /// <para><b>Never follow a link; remove the ROUTING ENTRY instead.</b> An earlier revision resolved each
    /// path physically and skipped anything landing outside the worktree. Review found that fails open: a
    /// branch committing <c>.kiro</c> as a symlink pointing outside keeps its symlink — we decline to touch
    /// it — and the vendor follows it anyway. Resolving at all was the mistake. This walks components
    /// WITHOUT following, and unlinks the first component that is a link. Removing a link never touches its
    /// target, so the operator's own <c>~/.gemini</c> is safe by construction rather than by a containment
    /// check, and the branch's routing into it is gone. The containment comparison this replaces also
    /// inferred case sensitivity from the OS, which is wrong on a case-sensitive APFS volume — deleting the
    /// question removes that too.</para>
    ///
    /// <para><b>Fail closed.</b> A path that is present but cannot be removed throws. Continuing would hand
    /// the vendor a tree it executes; a launch that fails loudly is strictly better than one that runs
    /// branch-controlled code. Absence from the returned list is NOT a report of failure, which is why this
    /// throws rather than relying on the caller to notice.</para>
    ///
    /// <para><b>Residual, accepted:</b> between the link check and the delete, a concurrent process could
    /// swap a component. Closing it needs <c>unlinkat</c>-style handle semantics that .NET does not expose.
    /// The window is narrow here specifically: this runs at creation, before any agent exists in this
    /// worktree, so an attacker needs an already-compromised process on the host — which has this authority
    /// regardless.</para>
    /// </summary>
    /// <exception cref="WorkspaceMcpNeutralizationException">A listed path exists and could not be removed.</exception>
    internal static IReadOnlyList<string> NeutralizeWorkspaceMcpConfig(string worktreePath) {
        var removed = new List<string>();

        foreach (var relative in WorkspaceMcpConfigPaths) {
            var victim = FirstRemovableComponent(worktreePath, relative);
            if (victim is null) continue;

            try {
                Unlink(victim);
                removed.Add(relative);
            } catch (Exception ex) {
                throw new WorkspaceMcpNeutralizationException(victim, ex);
            }
        }

        return removed;
    }

    /// <summary>Walks <paramref name="relative"/> one component at a time WITHOUT following links, and
    /// returns the first component that is itself a link (remove the branch's routing entry), or the leaf
    /// when the whole path is ordinary. Null when nothing along the path exists.</summary>
    static string? FirstRemovableComponent(string worktreePath, string relative) {
        var current = worktreePath;
        var parts   = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++) {
            current = Path.Combine(current, parts[i]);

            if (IsLink(current)) return current;          // routing entry — unlink it, never its target
            if (!Path.Exists(current)) return null;       // nothing here, and nothing below it either
        }

        return current;                                    // ordinary file, all ancestors ordinary
    }

    /// <summary>Whether the path itself is a symlink/junction. Deliberately does NOT follow: it reads the
    /// link attribute of this component alone.</summary>
    static bool IsLink(string path) {
        try {
            // LinkTarget is null for a non-link and does not resolve the target, so a dangling link still
            // reports as a link — which is the case that matters, since the vendor would follow it too.
            return new FileInfo(path).LinkTarget is not null
                || new DirectoryInfo(path).LinkTarget is not null;
        } catch {
            return false;                                  // unreadable: treated as ordinary, then Unlink reports
        }
    }

    /// <summary>
    /// Removes a file, a link of either kind, or a real directory sitting at a config pathname — without
    /// ever recursing into a LINK's target.
    ///
    /// <para>The kind is read from the attributes rather than from <c>Directory.Exists</c>, which FOLLOWS
    /// links and so reports false for a dangling directory symlink; that would fall through to
    /// <c>File.Delete</c>, which a Windows directory reparse point rejects. With fail-closed in place, a
    /// hostile branch could have used that to refuse every launch.</para>
    ///
    /// <para>A real directory at, say, <c>.cursor/mcp.json/</c> is removed recursively rather than
    /// refused. No vendor can parse a directory as its JSON config, so it is not itself the hazard — but
    /// under fail-closed, throwing on it would let any repo (hostile or merely odd) block worktree
    /// creation entirely. It is branch content at a path we do not permit, so it goes.</para>
    /// </summary>
    static void Unlink(string path) {
        var attributes = new FileInfo(path).Attributes;    // does not follow: reports the link's own bits
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var isLink      = attributes.HasFlag(FileAttributes.ReparsePoint);

        if (isDirectory && isLink) {
            Directory.Delete(path);                        // removes the LINK; the target is untouched
            return;
        }

        if (isDirectory) {
            Directory.Delete(path, recursive: true);       // real directory at a config pathname
            return;
        }

        File.Delete(path);                                 // file, or a file symlink (removes the link)
    }
}
