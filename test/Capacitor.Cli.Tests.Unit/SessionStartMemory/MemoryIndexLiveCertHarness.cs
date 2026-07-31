using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Shared scaffolding for the env-gated, per-harness "did the model actually receive the team-memory
/// index?" certifications: gate, throwaway nonce memory, the real <c>disable_memory_index</c> flag, a
/// bounded child process, and a defensive answer parse.
///
/// <para>These exist because the unit suites prove the BYTES an adapter emits, not that the harness
/// surfaces them to the model — Cursor IDE emits byte-perfect output the model may never see.</para>
///
/// <para><b>Cost.</b> Zero in CI: <see cref="SkipUnlessLiveGateReady"/> is the first statement in every
/// cert. A run spends real model turns and mutates the REAL server and profile config, so it is
/// deliberately manual.</para>
///
/// <para>Claude's and Cursor's certs keep their own copies of this scaffold: they are gated, hence
/// never exercised by CI, so refactoring them blind is the bigger risk. Migration is a follow-up.</para>
/// </summary>
internal static class MemoryIndexLiveCertHarness {
    public const string ServerUrlEnvVar = "KCAP_URL";

    static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The nonce shape every cert looks for. Deliberately distinctive so a match cannot be
    /// coincidental, and regex-free so the model is asked only to echo a literal.
    /// </summary>
    public static string NewNonce() => $"kcap-live-nonce-{Guid.NewGuid():N}";

    /// <summary>
    /// Asks the model to echo the nonce if — and only if — the injected index actually reached it.
    /// The nonce is embedded in the memory's DESCRIPTION, and the index injects one
    /// <c>slug: description</c> line per memory, so this is answerable from the injected context
    /// alone: no MCP memory server, no tool call, and no repo required. That keeps a failure
    /// attributable to injection rather than to tool wiring.
    /// </summary>
    public static string PositivePrompt =>
        "A block headed '## Team memory' may have been injected into your context. If it contains a "
      + "string of the form kcap-live-nonce- followed by 32 hex characters, reply with ONLY that "
      + "exact string and nothing else. If there is no such block or no such string, reply with "
      + "ONLY the word NONE.";

    /// <summary>
    /// The negative control's prompt. Same question, so a false positive can only come from the
    /// index genuinely being present — not from a differently-worded ask.
    /// </summary>
    public static string NegativePrompt => PositivePrompt;

    /// <summary>
    /// Gate. MUST be the first statement in every cert: it returns before any process launch, HTTP
    /// call, or memory write, which is what keeps CI spend at exactly zero.
    /// </summary>
    public static void SkipUnlessLiveGateReady(string liveGateEnvVar, string spendDescription, string preconditions) {
        Skip.Unless(
            Environment.GetEnvironmentVariable(liveGateEnvVar) == "1",
            $"Gated live model-receipt certification — set {liveGateEnvVar}=1 and {ServerUrlEnvVar}=<reachable kcap server> "
          + $"to run (spends {spendDescription}; requires {preconditions}, and `kcap login` already done against that server).");
        Skip.Unless(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerUrlEnvVar)),
            $"{ServerUrlEnvVar} must point at a reachable kcap server exposing GET /api/memories/index.");

        // AFTER the skips, so CI (where nothing ran and nothing can be dirty) still skips rather than
        // throws. A cert that runs against a possibly-polluted index can pass on another run's nonce,
        // so refusing to start is the only safe response — and it must be an error, not a skip, because
        // a skip reads as "not run today" rather than "your live index may need cleaning".
        if (_indexCleanlinessUnconfirmed is { } reason) {
            throw new InvalidOperationException(
                "Refusing to start a live cert: an earlier case in this run could not confirm its nonce "
              + $"memory was removed, so the injected index may carry a stale nonce and a positive case "
              + $"could pass on it. Clean up, then re-run. Reason: {reason}");
        }
    }

    public static string RequiredServerUrl() => Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;

    /// <summary>
    /// Bootstraps CLI config resolution, then returns the resolved server URL. **Every cert must call
    /// this before building an authenticated client.**
    ///
    /// <para>A cert runs inside the TEST assembly, not the `kcap` binary, so nothing has done what
    /// <c>Program.cs</c> does on startup: without it <c>AppConfig.ResolvedProfile</c> is null,
    /// credential resolution cannot find the profile's token, the request goes out unauthenticated,
    /// and the server answers <c>401</c> — even though `kcap whoami` succeeds in a shell a second
    /// earlier. That failure mode is indistinguishable from "you are not logged in", so it is worth
    /// naming: it is a missing bootstrap, not a missing login.</para>
    ///
    /// <para>Returns the URL the CLI itself resolves (honouring <c>KCAP_URL</c>, which the gate
    /// already requires) rather than the raw environment variable, so a cert talks to exactly the
    /// server the harness's own hook will talk to.</para>
    /// </summary>
    public static async Task<string> InitializeAndResolveServerUrlAsync() {
        var resolved = await AppConfig.ResolveServerUrl([]);

        return resolved ?? RequiredServerUrl();
    }

    /// <summary>
    /// Drives one <c>kcap mcp memory</c> tool call as a SUBPROCESS and returns its raw stdout.
    ///
    /// <para><b>Why a subprocess and not an in-process HttpClient.</b> This assembly redirects
    /// <c>KCAP_CONFIG_DIR</c> to a throwaway directory for its whole lifetime
    /// (<c>RepoPathStoreTests</c>'s <c>[Before(Assembly)]</c> hook), so in-process credential
    /// resolution reads an EMPTY config: every authenticated call 401s with "Not authenticated" even
    /// though `kcap whoami` succeeds in a shell a second earlier. A real `kcap` child reads the real
    /// config, so routing the memory lifecycle through the CLI is the only way a cert in this assembly
    /// can authenticate at all. It is also closer to what we are certifying — the same binary the
    /// harness hook invokes.</para>
    ///
    /// <para>Speaks just enough MCP: initialize, initialized, then one <c>tools/call</c>, all written
    /// up front. The server processes them in order and exits on stdin EOF, which is exactly the
    /// write-then-close shape <see cref="RunProcessAsync"/> already provides.</para>
    /// </summary>
    static async Task<string> CallMemoryToolAsync(string toolName, JsonObject arguments) {
        var stdin = string.Join('\n', [
            new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"]    = new JsonObject(),
                    ["clientInfo"]      = new JsonObject { ["name"] = "kcap-live-cert", ["version"] = "1" }
                }
            }.ToJsonString(),
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString(),
            new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
            }.ToJsonString()
        ]) + "\n";

        // Interactive, NOT write-all-then-close: an MCP server dispatches per line and stops at stdin
        // EOF, so closing the stream up front raced the tools/call and only the initialize response
        // ever came back. Stdin stays open until the id-2 response is read, then the child is killed —
        // there is no point asking it to shut down cleanly when we already have the answer.
        // Resolved against PATH for the same reason RunProcessAsync does it — this call site builds its
        // own ProcessStartInfo and would otherwise pick up the `kcap` sitting in this assembly's output
        // directory. The nonce memory would then be saved by one binary and the index served to a hook
        // running another.
        var psi = new ProcessStartInfo(ResolveOnPath("kcap")) {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        psi.ArgumentList.Add("mcp");
        psi.ArgumentList.Add("memory");

        // The child MUST NOT inherit this assembly's redirected KCAP_CONFIG_DIR (RepoPathStoreTests
        // [Before(Assembly)]), or `kcap` reads the same empty throwaway config the in-process path did
        // and answers "Not logged in" — the 401 in a different costume. Removing it lets the child
        // resolve the real config, which is the whole point of going out-of-process.
        psi.Environment.Remove("KCAP_CONFIG_DIR");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start `kcap mcp memory`.");
        OnProcessStarted?.Invoke(process.Id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // Drained CONCURRENTLY with the stdout loop, not after it. Both pipes are redirected, so
        // reading only one is the classic deadlock: a chatty child fills the stderr buffer, blocks on
        // the write, and never emits the id-2 response — which would present as an unexplained 90s
        // stall rather than as a pipe problem.
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try {
            await process.StandardInput.WriteAsync(stdin);
            await process.StandardInput.FlushAsync(cts.Token);

            while (await process.StandardOutput.ReadLineAsync(cts.Token) is { } line) {
                if (line.Length == 0 || line[0] != '{') continue;

                try {
                    if (JsonNode.Parse(line) is JsonObject frame && frame["id"]?.GetValue<int>() == 2) return line;
                } catch {
                    // Not a JSON-RPC frame — keep reading.
                }
            }

            throw new InvalidOperationException(
                $"`kcap mcp memory` {toolName} closed stdout before answering: {await stderrTask}");
        } finally {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already exited */ }

            // Observed so a faulted/cancelled drain cannot surface later as an unobserved task
            // exception, and so the pipe finishes draining before the process handle is disposed.
            try { await stderrTask; } catch { /* the real failure is whatever the caller is throwing */ }
        }
    }

    /// <summary>
    /// Saves a user-scoped, repo-independent ("global") memory whose description embeds the nonce.
    /// Global sidesteps needing a real repo hash for a throwaway worktree, and the description is
    /// where the nonce must live: the injected index carries <c>slug: description</c> lines, so this
    /// is what makes the cert answerable from injected context alone.
    /// </summary>
    public static async Task<string> SaveNonceMemoryAsync(string vendorLabel, string nonce) {
        var slug = SlugFor(nonce);

        var stdout = await CallMemoryToolAsync("save_memory", new JsonObject {
            ["audience"]    = "user",
            ["slug"]        = slug,
            ["description"] = $"kcap {vendorLabel} memory live-cert nonce: {nonce}",
            ["content"]     = $"kcap {vendorLabel} memory live-cert nonce: {nonce}. Safe to archive after the run.",
            ["kind"]        = "reference",
            ["global"]      = true
        });

        // An MCP tool result nests its payload as a JSON *string* inside result.content[].text, so the
        // id needs two parses, not a regex over the outer frame: the inner document's quotes arrive
        // "-escaped, so no pattern matching a literal `"memory_id"` will ever fire.
        if (ExtractMemoryId(stdout) is { } memoryId) return memoryId;

        // The save may have SUCCEEDED and only its response gone unusable, in which case a real memory
        // now exists with no id to archive it by — and a leaked nonce lands in the real injected index,
        // where it makes a later positive case pass on stale evidence. Naming the slug in the exception
        // makes that diagnosable but leaves the pollution in place, so recover the id by its (unique,
        // nonce-derived) slug and archive it here.
        var (recovered, lookupFailed) = await FindMemoryIdBySlugAsync(slug);

        if (recovered is not null) {
            await ArchiveMemoryAsync(vendorLabel, recovered);

            throw new InvalidOperationException(
                $"save_memory returned no memory_id for slug {slug}; the memory HAD been created "
              + $"({recovered}) and has been archived, so the index is clean. stdout: {stdout}");
        }

        // "The search failed" and "the search confirmed nothing is there" are different facts and must
        // not collapse into one message: only the second means the index is definitely clean.
        if (lookupFailed) {
            MarkIndexPossiblyDirty(
                $"a save for slug {slug} returned no id and the recovery lookup itself failed, so it is "
              + "unknown whether a nonce memory exists");

            throw new InvalidOperationException(
                $"save_memory returned no memory_id for slug {slug}, AND the recovery lookup failed — it "
              + $"is unknown whether a memory was created. Check for that slug and archive it if present. "
              + $"stdout: {stdout}");
        }

        throw new InvalidOperationException(
            $"save_memory returned no memory_id for slug {slug}, and repeated lookups confirmed no memory "
          + $"with that slug exists — nothing was created, so the index is clean. stdout: {stdout}");
    }

    /// <summary>The slug a cert's nonce memory is saved under. Unique per run by construction, which is
    /// what makes recovery-by-slug unambiguous.</summary>
    internal static string SlugFor(string nonce) => $"live-cert-{nonce}";

    /// <summary>
    /// Looks a memory up by EXACT slug, for the one path that has no id: a save whose response could not
    /// be parsed.
    ///
    /// <para>Polled rather than asked once. A memory that WAS created may not be searchable the instant
    /// after — the read model behind search is not documented as strongly consistent — and a single
    /// lookup would then report "nothing there" about a memory that appears a second later and pollutes
    /// every subsequent cert. A transient search failure has the same shape. So: bounded retries, and a
    /// return that distinguishes <b>confirmed absent</b> from <b>could not tell</b>, because only the
    /// first means the index is clean.</para>
    /// </summary>
    static async Task<(string? MemoryId, bool LookupFailed)> FindMemoryIdBySlugAsync(string slug) {
        var anyLookupSucceeded = false;

        for (var attempt = 0; attempt < 3; attempt++) {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(2));

            try {
                var frame = await CallMemoryToolAsync("search_memories", new JsonObject { ["query"] = slug });
                var text  = JsonNode.Parse(frame)?["result"]?["content"]?[0]?["text"]?.GetValue<string>();

                if (text is null || JsonNode.Parse(text) is not JsonArray hits) continue;   // unusable: not an answer

                anyLookupSucceeded = true;

                foreach (var hit in hits)
                    if (hit?["slug"]?.GetValue<string>() == slug && hit["memory_id"]?.GetValue<string>() is { } id)
                        return (id, false);
            } catch {
                // Transient or terminal — indistinguishable from here, so keep trying and let the
                // caller treat "never got a usable answer" as unknown rather than as absent.
            }
        }

        return (null, !anyLookupSucceeded);
    }

    /// <summary>
    /// Records that this run may have left a nonce memory in the REAL injected index.
    ///
    /// <para>This is not a log line, it is a stop: a stale nonce makes a later positive case pass on
    /// evidence from an earlier run, which is the one failure a cert must never produce. Once set,
    /// <see cref="SkipUnlessLiveGateReady"/> refuses to start any further live case in this process.</para>
    /// </summary>
    static string? _indexCleanlinessUnconfirmed;

    internal static void MarkIndexPossiblyDirty(string reason) => _indexCleanlinessUnconfirmed ??= reason;

    /// <summary>Digs the saved memory's id out of an MCP <c>tools/call</c> frame. Null if absent.</summary>
    internal static string? ExtractMemoryId(string frame) {
        try {
            var text = JsonNode.Parse(frame)?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            if (text is null) return null;

            var payload = JsonNode.Parse(text);

            return payload?["memory"]?["memory_id"]?.GetValue<string>()
                ?? payload?["memory_id"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Cleanup. A leaked cert memory is not cosmetic: it lands in the REAL injected index and every
    /// later cert then sees a stale nonce, so a positive test can pass on the wrong evidence.
    ///
    /// <para>The parameter is <c>id</c>, NOT <c>memory_id</c> — <c>save_memory</c> RETURNS
    /// <c>memory_id</c> and <c>archive_memory</c> ACCEPTS <c>id</c>, and sending the save's name back
    /// silently archived nothing. 13 cert memories leaked into the live index before that was caught,
    /// because the failure was swallowed here.</para>
    ///
    /// <para>Failures are therefore reported loudly AND verified: the tool's own <c>ok</c> is checked
    /// rather than assuming a returned frame means success. Still non-throwing — a cert must not fail
    /// on cleanup and mask its real verdict — but it can no longer fail in silence, and it now also
    /// marks the run so that no LATER case starts against an index that may hold this nonce. A stderr
    /// line the operator may not read is not a sufficient response to the failure mode this exists to
    /// prevent.</para>
    /// </summary>
    public static async Task ArchiveMemoryAsync(string vendorLabel, string memoryId) {
        try {
            var frame = await CallMemoryToolAsync("archive_memory", new JsonObject { ["id"] = memoryId });

            if (!ArchiveSucceeded(frame)) {
                MarkIndexPossiblyDirty($"archive_memory did not confirm success for {memoryId}");
                await Console.Error.WriteLineAsync(
                    $"[{vendorLabel}-memory-live] LEAKED live-cert memory {memoryId} — archive_memory did not "
                  + $"confirm success. Archive it manually or later certs may read a stale nonce. Frame: {frame}");
            }
        } catch (Exception ex) {
            MarkIndexPossiblyDirty($"archive_memory threw for {memoryId}: {ex.Message}");
            await Console.Error.WriteLineAsync(
                $"[{vendorLabel}-memory-live] LEAKED live-cert memory {memoryId} — archive threw: {ex.Message}");
        }
    }

    /// <summary>True only on an explicit success from the tool; an error frame or a missing/false
    /// <c>ok</c> both count as failure, so "no news" is never mistaken for "archived".</summary>
    internal static bool ArchiveSucceeded(string frame) {
        try {
            var result = JsonNode.Parse(frame)?["result"];
            if (result?["isError"]?.GetValue<bool>() == true) return false;

            var text = result?["content"]?[0]?["text"]?.GetValue<string>();

            return text is not null && JsonNode.Parse(text)?["ok"]?.GetValue<bool>() == true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Reads the active profile's <c>disable_memory_index</c>. Null means the flag is **genuinely
    /// absent**; an unreadable config, unparseable output or missing active profile THROWS.
    ///
    /// <para>That distinction is the whole point. Collapsing "could not read" into the same null as
    /// "not set" means a restore writes <c>false</c> over a real <c>true</c> — silently re-enabling
    /// memory injection for a developer who deliberately opted out. Failing closed on an unknown
    /// starting value is the only safe option, because this method's result is what the restore
    /// writes back to a real machine.</para>
    /// </summary>
    public static async Task<bool?> ReadDisableMemoryIndexAsync() {
        var (exitCode, stdout, stderr) = await RunProcessAsync("kcap", ["config", "show"], workingDirectory: null);

        if (exitCode != 0) {
            throw new InvalidOperationException(
                $"`kcap config show` failed (exit {exitCode}) — refusing to guess the current "
              + $"disable_memory_index, since restoring the wrong value changes a real setting: {stderr}");
        }

        JsonNode? root;

        try {
            root = JsonNode.Parse(ExtractLeadingJsonBlock(stdout));
        } catch (Exception ex) {
            throw new InvalidOperationException(
                $"could not parse `kcap config show` output — refusing to guess disable_memory_index: {ex.Message}");
        }

        var activeProfile = root?["active_profile"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "`kcap config show` reported no active_profile — refusing to guess disable_memory_index.");

        // Null HERE is meaningful: the flag really is unset on the active profile.
        return root?["profiles"]?[activeProfile]?["disable_memory_index"]?.GetValue<bool>();
    }

    /// <summary>
    /// Isolates the leading JSON from <c>kcap config show</c> (JSON, blank line, then a "Path:"
    /// line). CRLF-tolerant: on Windows a \n-only split would return the whole blob and fail to parse.
    /// </summary>
    public static string ExtractLeadingJsonBlock(string stdout) =>
        stdout.Replace("\r\n", "\n").Split("\n\n", 2)[0];

    /// <summary>
    /// Restores the flag to what was observed, then READS IT BACK to confirm. `kcap config` has no
    /// unset primitive, so an originally-absent (null) flag is restored as <c>false</c> — observably
    /// identical, since both read as not-disabled via <c>is true</c>.
    ///
    /// <para>The read-back exists because a silent restore failure is this file's worst outcome: it
    /// leaves memory injection DISABLED on a real machine while the cert reports success, and nothing
    /// downstream would notice for days.</para>
    /// </summary>
    public static async Task RestoreDisableMemoryIndexAsync(bool? original) {
        var target = original ?? false;

        await SetDisableMemoryIndexAsync(target);

        var readBack = await ReadDisableMemoryIndexAsync() ?? false;

        if (readBack != target) {
            throw new InvalidOperationException(
                $"disable_memory_index did NOT restore: wanted {target}, read back {readBack}. "
              + "Fix this by hand — `kcap config set disable_memory_index "
              + $"{(target ? "true" : "false")}` — memory injection may be left in the wrong state.");
        }
    }

    /// <summary>
    /// Sets the REAL profile flag, and fails loudly if the subprocess did not succeed.
    ///
    /// <para>Discarding this exit code is not a cosmetic gap. A failed <c>true</c> makes the negative
    /// control vacuous (it observes no injection because injection was never disabled, and passes for
    /// the wrong reason); a failed restore leaves a developer's machine with memory injection off. The
    /// pre-existing Claude cert asserts this exit code, and an earlier draft of this shared harness
    /// dropped that guard.</para>
    /// </summary>
    public static async Task SetDisableMemoryIndexAsync(bool value) {
        var (exitCode, _, stderr) = await RunProcessAsync(
            "kcap", ["config", "set", "disable_memory_index", value ? "true" : "false"], workingDirectory: null);

        if (exitCode != 0) {
            throw new InvalidOperationException(
                $"`kcap config set disable_memory_index {(value ? "true" : "false")}` failed (exit {exitCode}): {stderr}. "
              + (value
                  ? "Aborting rather than run a negative control that would pass vacuously."
                  : "THE REAL PROFILE MAY STILL HAVE MEMORY INJECTION DISABLED — check `kcap config show`."));
        }
    }

    public static async Task RecordVersionAsync(string vendorLabel, string fileName, IReadOnlyList<string> args) {
        try {
            var (exitCode, stdout, _) = await RunProcessAsync(fileName, args, workingDirectory: null);
            await Console.Out.WriteLineAsync(
                $"[{vendorLabel}-memory-live] {fileName} {string.Join(' ', args)} (exit {exitCode}): {stdout.Trim()}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[{vendorLabel}-memory-live] could not record {fileName} version: {ex.Message}");
        }
    }

    /// <summary>
    /// Records the harness CLI's version and — the one that actually matters — the <c>kcap</c> the
    /// harness HOOK resolves from PATH, plus its resolved path.
    ///
    /// <para>PATH points at the npm install, not the tree the cert was compiled from, so a cert can run
    /// against a <c>kcap</c> predating the adapter under test and report a confident, meaningless
    /// failure. That happened for all three adapters and cost two sessions to find. Called by every
    /// cert including the negative controls, which a stale binary makes pass vacuously.</para>
    /// </summary>
    public static async Task RecordCertEnvironmentAsync(
            string vendorLabel, string harnessExe, IReadOnlyList<string> harnessVersionArgs) {
        await RecordVersionAsync(vendorLabel, harnessExe, harnessVersionArgs);

        // The hook's kcap, not the cert's assembly: version AND path, because the path is what reveals
        // an npm install shadowing a locally built binary.
        await RecordVersionAsync(vendorLabel, "kcap", ["--version"]);

        try {
            var (_, which, _) = await RunProcessAsync(
                OperatingSystem.IsWindows() ? "where" : "which", ["kcap"], workingDirectory: null);

            await Console.Out.WriteLineAsync(
                $"[{vendorLabel}-memory-live] hooks will resolve kcap at: {which.Trim()}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync(
                $"[{vendorLabel}-memory-live] could not resolve the kcap on PATH: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a bare command name against PATH, explicitly, before handing it to
    /// <see cref="ProcessStartInfo"/>.
    ///
    /// <para><b>This is not belt-and-braces — without it the harness runs the wrong binary.</b>
    /// <c>Process.Start</c> tries a separator-free filename against the WORKING DIRECTORY before it
    /// consults PATH, and this assembly's working directory is its own output folder, which contains a
    /// <c>kcap</c> copied there by the <c>Capacitor.Cli</c> project reference. So every
    /// <c>RunProcessAsync("kcap", …)</c> silently ran the test build while
    /// <see cref="RecordCertEnvironmentAsync"/>'s sibling <c>which kcap</c> reported the PATH one — the
    /// two lines disagreed, and the version line that exists precisely to pin the binary under test was
    /// describing a different binary from the one the hook would run. Observed: a cert recorded
    /// <c>+e2b2821</c> (the test build) while the hook ran <c>+998ec34</c> (the PATH build).</para>
    ///
    /// <para>Windows is left alone deliberately: correct resolution there needs PATHEXT handling, these
    /// certs are gated and are not run on Windows, and a half-right implementation would be worse than
    /// the documented status quo.</para>
    /// </summary>
    internal static string ResolveOnPath(string fileName) =>
        ResolveOnPath(fileName, Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows());

    /// <summary>Pure overload: PATH and platform are passed rather than probed, so the resolution order
    /// is testable without mutating this process's environment.</summary>
    internal static string ResolveOnPath(string fileName, string? pathValue, bool isWindows) {
        if (isWindows || Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar))
            return fileName;

        foreach (var dir in (pathValue ?? "").Split(Path.PathSeparator)) {
            if (dir.Length == 0) continue;

            var candidate = Path.Combine(dir, fileName);
            if (IsExecutableFile(candidate)) return candidate;
        }

        // Unresolved: hand the bare name back so Process.Start produces its own, clearer error.
        return fileName;
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    static extern int LibcAccess(string pathname, int mode);

    const int X_OK = 1;

    /// <summary>
    /// Existence is NOT the test a shell applies. <c>which</c> and <c>execvp</c> skip a PATH entry whose
    /// match is not executable and keep looking, so resolving on <c>File.Exists</c> alone would stop at a
    /// non-executable same-named file and either fail the launch or — worse for this harness —
    /// disagree with the <c>which kcap</c> line recorded beside it, which is the exact disagreement the
    /// resolver exists to remove.
    ///
    /// <para><b>The question is EFFECTIVE executability, so ask the kernel.</b> An earlier version tested
    /// "any of the three execute bits is set", justified by "over-accepting can only fall back to the
    /// platform's own error" — which is wrong, and review caught it: this resolver returns an ABSOLUTE
    /// path, so there is no fallback to a later PATH entry. A file that is owner-executable but owned by
    /// someone else, or sits on a <c>noexec</c> mount, would be selected here and would simply fail to
    /// launch, while a shell would have kept walking. <c>access(path, X_OK)</c> answers for the current
    /// identity, which is the same question <c>execvp</c> asks.</para>
    ///
    /// <para><c>File.Exists</c> stays as the first gate, and not merely as an optimisation:
    /// <c>access(X_OK)</c> succeeds on a DIRECTORY, so dropping it would let a directory named
    /// <c>kcap</c> shadow the binary.</para>
    /// </summary>
    static bool IsExecutableFile(string path) {
        if (!File.Exists(path)) return false;

        try {
            return LibcAccess(path, X_OK) == 0;
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) {
            // No libc to ask (a platform these gated certs are not run on). Fall back to the mode bits:
            // weaker, but strictly better than resolving on existence alone.
            try {
                const UnixFileMode anyExecute =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

                return (File.GetUnixFileMode(path) & anyExecute) != 0;
            } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
                return false;
            }
        }
    }

    /// <summary>Test-only seam: notified with the PID of every spawned process, so the cleanup test
    /// can confirm a timed-out child is actually gone. Never set by production code.</summary>
    internal static Action<int>? OnProcessStarted;

    /// <summary>
    /// Runs a child process with its output captured and a hard timeout, killing the whole tree on
    /// expiry so a hung harness CLI is never orphaned. <paramref name="stdin"/> is written and the
    /// stream closed when supplied — Codex reads its prompt that way.
    /// </summary>
    /// <param name="environment">Extra variables layered over the inherited environment, applied AFTER
    /// the <c>KCAP_CONFIG_DIR</c> scrub. The one caller is the non-zero-exit cert, which redirects a
    /// harness CLI's <c>KCAP_URL</c> at a proxy that fails exactly the lifecycle POST — the variable has
    /// to reach a grandchild (agent → its hook), which is why it is set on the environment rather than
    /// passed as an argument.</param>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory,
            TimeSpan? timeout = null, string? stdin = null,
            IReadOnlyDictionary<string, string>? environment = null) {
        var psi = new ProcessStartInfo(ResolveOnPath(fileName)) {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = stdin is not null,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        // Same reason as CallMemoryToolAsync: a `kcap config` child — and every harness CLI, which
        // invokes `kcap hook` itself — must see the REAL config, not this assembly's throwaway one.
        psi.Environment.Remove("KCAP_CONFIG_DIR");

        // AFTER the scrub, so an explicit override is never silently dropped by it.
        if (environment is not null)
            foreach (var (key, value) in environment) psi.Environment[key] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        OnProcessStarted?.Invoke(process.Id);

        using var timeoutCts = new CancellationTokenSource(timeout ?? ProcessTimeout);
        try {
            if (stdin is not null) {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return (process.ExitCode, await stdoutTask, await stderrTask);
        } finally {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }

    /// <summary>
    /// Pulls the assistant's answer out of a harness CLI's stdout without assuming a shape: tries
    /// newline-delimited JSON, then a single JSON document, then falls back to the raw trimmed text.
    /// Each harness's real output shape is confirmed on its first live run and recorded on the cert.
    /// </summary>
    public static string ExtractAssistantAnswer(string stdout) {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return trimmed;

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (line.Length == 0 || line[0] is not ('{' or '[')) continue;
            try {
                if (JsonNode.Parse(line) is { } node && FindFirstTextField(node) is { } text) return text;
            } catch {
                // Not JSON — fall through.
            }
        }

        try {
            if (JsonNode.Parse(trimmed) is { } whole && FindFirstTextField(whole) is { } text) return text;
        } catch {
            // Plain text — return as-is.
        }

        return trimmed;
    }

    static string? FindFirstTextField(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var key in new[] { "text", "message", "content", "answer", "result" }) {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
                }
                foreach (var (_, child) in obj) {
                    if (FindFirstTextField(child) is { } nested) return nested;
                }
                return null;
            case JsonArray arr:
                foreach (var item in arr) {
                    if (FindFirstTextField(item) is { } nested) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Creates a throwaway working directory for a cert turn. It must be a fresh directory so the
    /// harness genuinely starts a NEW session and the sessionStart path under test actually fires.
    /// </summary>
    public static DirectoryInfo NewCertWorktree(string vendorLabel) =>
        Directory.CreateTempSubdirectory($"kcap-{vendorLabel}-memory-live-");
}
