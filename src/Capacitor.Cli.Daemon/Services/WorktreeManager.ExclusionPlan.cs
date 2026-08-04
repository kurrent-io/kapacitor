using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace Capacitor.Cli.Daemon.Services;

public partial class WorktreeManager {
    /// <summary>Ceiling on the raw <c>rev-parse --show-prefix</c> capture, applied at the read.
    /// <para>The depth and aggregate-byte caps below would eventually reject an absurd prefix, but only
    /// after the capture helper had already allocated it. Bounding here is cheaper and does not depend on
    /// a later stage noticing.</para></summary>
    internal const int MaxCwdPrefixCaptureBytes = 4 * 1024;

    /// <summary>Ancestor components admitted between the repository root and the execution cwd.</summary>
    internal const int MaxCwdDepth = 32;

    /// <summary>Ceiling on the summed UTF-8 length of the expanded vendor paths.
    /// <para>The expansion is <c>canonical paths × (depth + 1)</c> entries but <b>O(depth²)</b> aggregate
    /// bytes, because each deeper level repeats the whole prefix. Capping the count alone would not bound
    /// the bytes.</para></summary>
    internal const int MaxVendorPathAggregateBytes = 64 * 1024;

    /// <summary>
    /// The exclusion decision for one snapshot build: which concrete vendor-config paths are reserved,
    /// given where the reviewer will actually execute.
    ///
    /// <para><b>Why a plan rather than a static list.</b> Vendors resolve workspace MCP config along the
    /// ancestor chain of their cwd — Codex layers <c>.codex/config.toml</c> from the repository root down
    /// to the cwd, Copilot and Claude Code walk from the cwd upward — so a root-relative list is only
    /// complete when the cwd IS the root. A borrowed snapshot may execute below the root, and a root-only
    /// list left <c>src/.codex/config.toml</c> live in the tree the reviewer runs in.</para>
    ///
    /// <para><b>One list, one classifier.</b> The vendor paths deliberately do NOT appear in
    /// <see cref="SnapshotExclusions"/>: they are matched only through <c>ClassifyReservedPath</c>, which
    /// is also what the review-context extractor uses. Two matchers over the same set is how "contained
    /// but not reviewable" gets created — they folded case differently (<c>OrdinalIgnoreCase</c> versus
    /// ASCII-only), which was unobservable while the list was ASCII constants and stops being so the
    /// moment a cwd prefix can vary.</para>
    ///
    /// <para><b>Not a security boundary in itself.</b> <c>byte[]</c> and <c>string[]</c> members are not
    /// deeply immutable and this type does not pretend otherwise. The guarantee is procedural: exactly one
    /// plan is built per build attempt, inside that attempt, and passed to the consumers — see
    /// <see cref="PlanSnapshotExclusions"/>.</para>
    /// </summary>
    internal sealed record SnapshotExclusionPlan(
        string GitRelativeCwd,
        ImmutableArray<string> VendorConfigPaths,
        (string Canonical, byte[] Bytes)[] Reserved,
        string[] SnapshotExclusions);

    /// <summary>
    /// Reads the execution cwd's path as <b>git</b> spells it, relative to the work-tree top. Empty when
    /// the cwd is the repository root.
    ///
    /// <para><b>Why git and not the filesystem.</b> A prefix taken from .NET is not in the same pathname
    /// namespace as the paths <c>ls-files</c> reports, and concatenating one onto the other produces a
    /// comparison that can silently fail to match: macOS reports NFD for a directory created as NFC (and
    /// <see cref="NormalizeRelativePath"/> rejects non-NFC git paths, so only the prefix side could
    /// diverge); <c>Path.GetRelativePath</c> returns a ROOTED path across Windows volumes; and separator
    /// conventions differ. Reading it from git deletes all three classes rather than guarding them.</para>
    ///
    /// <para><b>The one prefix.</b> The value returned here locates the execution directory
    /// (<c>ContainedPath(snapshot, prefix)</c>) as well as driving the plan. An earlier revision used the
    /// filesystem spelling for the launch and this one for classification; two independent derivations can
    /// disagree, and then an unexcluded path materialises the alternate-spelling directory itself — so the
    /// vendor launches in a directory whose config was never excluded. Do not reintroduce a second
    /// derivation.</para>
    ///
    /// <para><c>core.quotePath=false</c> is required, or non-ASCII components come back C-quoted.</para>
    /// </summary>
    internal static async Task<string> ReadGitRelativeCwdAsync(
            string sourceRepoRoot, string sourceCwd, CancellationToken ct) {
        // The prefix is only meaningful against the repository whose manifest it will filter, and
        // `rev-parse` reports whatever repository git DISCOVERS at the cwd. A cwd inside a nested
        // repository — or in an entirely different one — would otherwise yield a prefix in a foreign
        // namespace that is then matched against this source's `ls-files` output, which is precisely the
        // "one namespace" invariant this derivation exists to hold. So the work-tree top is captured and
        // required to be the source root before the prefix is trusted.
        var topRaw = await RunGitCaptureBoundedAsync(
            sourceCwd, GitTimeout, MaxCwdPrefixCaptureBytes, ct,
            "-c", "core.quotePath=false", "rev-parse", "--show-toplevel");
        var top = ParseSingleLine(topRaw);
        if (top.Length == 0 ||
            !ResolveDeepestExisting(top).Equals(
                ResolveDeepestExisting(sourceRepoRoot), FileSystemPathComparison))
            throw new InvalidOperationException("borrowed_snapshot_cwd_foreign_repository");

        var raw = await RunGitCaptureBoundedAsync(
            sourceCwd, GitTimeout, MaxCwdPrefixCaptureBytes, ct,
            "-c", "core.quotePath=false", "rev-parse", "--show-prefix");

        return ParseGitRelativeCwd(raw);
    }

    /// <summary>Strips exactly one trailing LF and refuses CR or any embedded LF — the same framing rules
    /// as the prefix parse, for a command whose output is one path rather than a repository-relative
    /// path.</summary>
    static string ParseSingleLine(ReadOnlySpan<byte> raw) {
        if (raw.Length == 0 || raw[^1] != (byte)'\n')
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");
        var body = raw[..^1];
        if (body.IndexOf((byte)'\n') >= 0 || body.IndexOf((byte)'\r') >= 0)
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");
        try { return StrictUtf8.GetString(body); }
        catch (DecoderFallbackException ex) {
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed", ex);
        }
    }

    /// <summary>Byte-exact parse of <c>rev-parse --show-prefix</c> output. Split out so it is testable
    /// against captured bytes without a live repository, and so the framing rules are stated once.</summary>
    internal static string ParseGitRelativeCwd(ReadOnlySpan<byte> raw) {
        // Exactly one trailing LF, and no CR or embedded LF anywhere. Anything else is not this
        // command's output shape and is refused rather than repaired.
        if (raw.Length == 0 || raw[^1] != (byte)'\n')
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");
        var body = raw[..^1];
        if (body.IndexOf((byte)'\n') >= 0 || body.IndexOf((byte)'\r') >= 0)
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");

        // The repository root. Deliberately returned BEFORE the path validator, which rejects the empty
        // string — this is the common case and it is not a path.
        if (body.Length == 0) return "";

        if (body[^1] != (byte)'/')
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");
        body = body[..^1];
        if (body.Length == 0)
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");

        string decoded;
        try { decoded = StrictUtf8.GetString(body); }
        catch (DecoderFallbackException ex) {
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed", ex);
        }

        // Normalisation runs BEFORE the case-sensitivity rule in PlanSnapshotExclusions, so that rule can
        // assume an NFC operand. A consequence worth knowing: an NFD prefix fails here as
        // borrowed_snapshot_invalid_path, not as the non-ASCII rejection.
        return NormalizeRelativePath(decoded);
    }

    /// <summary>
    /// Expands the canonical vendor-config list across the ancestor chain of
    /// <paramref name="gitRelativeCwd"/>, inclusive of both the repository root and the cwd itself.
    ///
    /// <para><b>Not every directory in the tree.</b> A sibling of the cwd is deliberately left alone: no
    /// supported vendor is documented to discover config there, and stripping it would delete content the
    /// launch cannot reach — including this repository's own committed <c>kcap/.mcp.json</c>. The property
    /// this delivers is scoped to the vendor the daemon launches, at the cwd it launches it in, without
    /// model involvement; a model that deliberately changes directory and starts another CLI is the OS
    /// sandbox's problem, not this list's.</para>
    ///
    /// <para><b>Case.</b> <paramref name="caseSensitive"/> is probed on the DESTINATION, which is the
    /// volume the vendor executes on, and that is what makes it the right input. On a case-insensitive
    /// destination <c>SRC</c> and <c>src</c> are one directory, so folding is correct and no case-varying
    /// sibling can exist; on a case-sensitive one they are distinct, so NOT folding is correct. An earlier
    /// revision folded unconditionally and thereby handed a hostile branch a launch-refusal primitive:
    /// tracked <c>a/.mcp.json</c> and <c>A/.mcp.json</c> both collapsed onto one canonical candidate and
    /// the extractor's collision check refused every launch of that repository.</para>
    ///
    /// <para><b>Non-ASCII prefixes.</b> Admitted on a case-sensitive destination, where both sides are NFC
    /// and an exact comparison is sound. Refused on a case-insensitive one, because that volume also
    /// equates pairs such as <c>Å</c>/<c>å</c> which the ASCII-only matcher would miss — and proving the
    /// equivalence would require a second, Unicode-aware matcher, which is the defect this design removes.
    /// This is a stated compatibility limitation on one platform class, not a security property.</para>
    /// </summary>
    internal static SnapshotExclusionPlan PlanSnapshotExclusions(
            string gitRelativeCwd, bool caseSensitive, IEnumerable<string>? additional = null) {
        if (!caseSensitive && !IsAscii(gitRelativeCwd))
            throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_non_ascii");

        var components = gitRelativeCwd.Length == 0
            ? []
            : gitRelativeCwd.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length > MaxCwdDepth)
            throw new InvalidOperationException("borrowed_snapshot_cwd_too_deep");

        var directoryPrefixes = new List<string>(components.Length + 1) { "" };
        var accumulated = "";
        foreach (var component in components) {
            accumulated += component + "/";
            directoryPrefixes.Add(accumulated);
        }

        var paths = ImmutableArray.CreateBuilder<string>(
            directoryPrefixes.Count * WorkspaceMcpConfigPaths.Length);
        var reserved = new List<(string, byte[])>(paths.Capacity);
        long aggregate = 0;
        foreach (var prefix in directoryPrefixes)
            foreach (var canonical in WorkspaceMcpConfigPaths) {
                var path = prefix + canonical;
                var bytes = Encoding.UTF8.GetBytes(path);
                aggregate += bytes.Length;
                if (aggregate > MaxVendorPathAggregateBytes)
                    throw new InvalidOperationException("borrowed_snapshot_cwd_too_deep");
                paths.Add(path);
                reserved.Add((path, bytes));
            }

        // .capacitor and .attached only — plus whatever the caller added. The vendor paths are NOT here:
        // they are matched exclusively through ClassifyReservedPath. These are ASCII daemon-supplied
        // constants, so the namespace question the vendor paths raise does not arise for them.
        string[] exclusions = [".capacitor", ".attached", .. additional ?? []];

        return new SnapshotExclusionPlan(
            gitRelativeCwd, paths.ToImmutable(), [.. reserved], exclusions);
    }

    static bool IsAscii(string value) {
        foreach (var c in value) if (c > 0x7F) return false;
        return true;
    }

    /// <summary>Captures a git command's stdout, refusing rather than truncating past
    /// <paramref name="maxBytes"/>. Reads <c>BaseStream</c>, not the <c>StreamReader</c>, because
    /// <c>StandardOutputEncoding</c> alone does not disable the reader's BOM detection.</summary>
    static async Task<byte[]> RunGitCaptureBoundedAsync(
            string cwd, TimeSpan timeout, int maxBytes, CancellationToken ct, params string[] args) {
        var psi = NewGitPsi(cwd, args, sourceReadOnly: true);
        using var process = Process.Start(psi)!;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var stdout = new MemoryStream();
        var stderrTask = ReadAllDecodedAsync(process.StandardError.BaseStream, timeoutCts.Token);
        try {
            var buffer = new byte[4096];
            while (true) {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeoutCts.Token);
                if (read == 0) break;
                if (stdout.Length + read > maxBytes)
                    throw new InvalidOperationException("borrowed_snapshot_cwd_prefix_malformed");
                stdout.Write(buffer, 0, read);
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        } catch (OperationCanceledException) {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s");
        } finally {
            // Every abnormal exit ran through here: the overflow throw and the cancellation branch both
            // used to kill inline and leave the stderr pump unobserved and the child unreaped.
            // Process.Dispose is not a termination guarantee.
            await TerminateAndDrainAsync(process, stderrTask);
        }
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.ToArray();
    }

    /// <summary>Kills the process if it is still running, waits for it to be reaped, and observes the
    /// supplied pump tasks so a faulted read cannot surface as an unobserved exception.
    /// <para>Every failure here is swallowed deliberately: this runs in a <c>finally</c>, and the
    /// original exception is more useful to an operator than whatever went wrong tidying up after it.</para>
    /// </summary>
    static async Task TerminateAndDrainAsync(Process process, params Task[] pumps) {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* already reaped */ }
        foreach (var pump in pumps)
            try { await pump; } catch { /* observed, not handled */ }
    }

    /// <summary>Runs a git command feeding <paramref name="lines"/> as NUL-separated stdin.
    /// <para>Used for the <c>skip-worktree</c> batch. Passing the paths as argv would hit <c>ARG_MAX</c>
    /// for a deep cwd — the expansion is O(depth²) aggregate bytes — and would also let an ancestor
    /// directory whose name begins with <c>:</c> or <c>-</c> be read as pathspec syntax. <c>--stdin</c>
    /// paths are literal.</para></summary>
    static async Task RunGitWithNulStdinAsync(
            string cwd, TimeSpan timeout, IReadOnlyCollection<string> lines,
            CancellationToken ct, params string[] args) {
        var psi = NewGitPsi(cwd, args);
        psi.RedirectStandardInput = true;
        using var process = Process.Start(psi)!;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stderrTask = ReadAllDecodedAsync(process.StandardError.BaseStream, timeoutCts.Token);
        var stdoutTask = ReadAllDecodedAsync(process.StandardOutput.BaseStream, timeoutCts.Token);
        try {
            // Disposing the stream is the EOF signal git waits for; it must happen even if a write
            // faults part-way, or the child blocks on a read that will never complete.
            await using (var input = process.StandardInput.BaseStream) {
                foreach (var line in lines) {
                    await input.WriteAsync(StrictUtf8.GetBytes(line), timeoutCts.Token);
                    await input.WriteAsync(new byte[] { 0 }, timeoutCts.Token);
                }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
            await stdoutTask;
        } catch (OperationCanceledException) {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:F0}s");
        } finally {
            // Covers the cancellation branch AND an IOException mid-write, which previously left a
            // running child and two unobserved pumps behind.
            await TerminateAndDrainAsync(process, stdoutTask, stderrTask);
        }
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
