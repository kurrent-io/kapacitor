# Kiro CLI as an unattended review-flow reviewer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a review flow run `vendor: "kiro"` unattended — daemon-owned worktree, the operator's own authenticated `kiro-cli`, and no OS sandbox — with containment by MCP source suppression and a scoped, per-launch tool-trust list.

**Architecture:** Follows the shipped Gemini reviewer shape (`GeminiReviewerCapability`): the security decision is a daemon-local operator consent flag, not a containment mechanism. Kiro differs in three ways — its MCP containment is *source suppression* (an empty per-launch `KIRO_HOME` plus AI-1632's worktree deletion) rather than a name allowlist; its trust list is **scoped** (`fs_read`, `thinking`, plus the injected servers' tools) rather than blanket `yolo`, which forces a per-launch derivation Gemini never needed; and version fragility is handled by an **operator affirmation** that fails closed on a `kiro-cli` version change, not by a maintainer-curated certified set.

**Tech Stack:** .NET 10, NativeAOT, TUnit on Microsoft Testing Platform, ACP over stdio JSON-RPC.

**Spec:** `docs/superpowers/specs/2026-07-30-ai1410-kiro-unattended-reviewer-design.md` (rev 4).
**Probe evidence:** `docs/probes/2026-08-05-kiro-reviewer-trust/findings.md`.

## Global Constraints

- **Repository:** kurrent-io/kcap-cli. Do NOT put Linear issue ids (`AI-####`) in `.cs` files — use the GitHub issue number if a reference is unavoidable. Run `rg -n "AI-[0-9]+" src/ test/ --type cs` before every push.
- **Platform:** the Kiro reviewer is advertised on POSIX hosts only (`!OperatingSystem.IsWindows()`). Windows is excluded because §7 requires `0700` at creation and `UnixFileMode` is a no-op there.
- **Measured against:** `kiro-cli 2.16.0`, macOS/arm64. Native tool names are exactly `fs_read`, `fs_write`, `execute_bash`, `use_aws`, `knowledge`, `thinking`, `introspect`, `todo_list`, `gh_issue`, `web_search`. A misspelling is a **warning**, not an error, and degrades to "nothing trusted".
- **Never trust `fs_write` or `execute_bash`** in a reviewer trust list.
- **AOT:** run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` before pushing. `dotnet build` does NOT surface these.
- **Tests:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "<glob>"`. Use `--treenode-filter`, never `--filter`. A `total: 0` result is a MISS, not a pass.
- **Local baseline:** ~42 of 5663 unit tests fail on clean `main` on macOS. Do not chase those; compare against the baseline.
- **README:** any user-facing CLI surface change (Task 8's `kcap daemon reviewer affirm`) must update `README.md` in the same PR.
- **Every negative assertion needs a positive control.** This spec exists partly because a containment test passed vacuously.

---

## File Structure

**Create:**
- `src/Capacitor.Cli.Daemon/Acp/KiroReviewerHome.cs` — per-launch isolated `KIRO_HOME`: create `0700`, epoch-keyed sweep, reap-before-delete.
- `src/Capacitor.Cli.Daemon/Acp/KiroReviewerVersionStore.cs` — the affirmed-version record under the daemon state dir.
- `src/Capacitor.Cli.Daemon/Acp/KiroReviewerCapability.cs` — the pure enable/deny decision + denial reasons.
- `src/Capacitor.Cli.Daemon/Acp/KiroReviewerTrustList.cs` — builds `--trust-tools` from the launch's injected MCP specs.
- `src/Capacitor.Cli.Daemon/Acp/KiroMcpSurfaceMonitor.cs` — the §5 tripwire over Kiro's own MCP notifications.
- `src/Capacitor.Cli.Daemon/Acp/VendorVersionResolver.cs` — `ResolveGeminiVersion` generalized.
- Tests: `test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewer{Home,VersionStore,Capability,TrustList}Tests.cs`, `KiroMcpSurfaceMonitorTests.cs`, `test/Capacitor.Cli.Tests.Unit/Services/KiroReviewerLaunchTests.cs`.

**Modify:**
- `src/Capacitor.Cli.Daemon/Acp/AcpVendorDescriptor.cs` — add `UnattendedTrustArgvBuilder`; flip the Kiro descriptor (Task 8).
- `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` — split aliasing from the Gemini allowlist argv; per-vendor capability gate; Kiro env + trust list.
- `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs` — the §6.1 launch deadline; wire the tripwire.
- `src/Capacitor.Cli.Daemon/DaemonConfig.cs` — `KiroUnattendedReviewerEnabled`.
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — construct the store, register the sweep.
- `README.md` — the new verb.

---

## Task 1: `KiroReviewerHome` — the isolated, transcript-bearing home

Kiro writes the reviewer's own conversation JSONL into `{KIRO_HOME}/sessions/cli`, so this directory carries the caller's diff and source excerpts. Disposal is a security requirement, not hygiene.

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Acp/KiroReviewerHome.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerHomeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `internal sealed class KiroReviewerHome` with
  `static string RootFor(string stateDir)`,
  `static string Create(string stateDir, string daemonEpoch, string launchId)`,
  `static void SweepStale(string stateDir, string currentEpoch, ILogger log)`,
  `static void Delete(string homePath, string stateDir, ILogger log)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class KiroReviewerHomeTests {
    static string TempStateDir() {
        var d = Path.Combine(Path.GetTempPath(), "kcap-kiro-home-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Test]
    public async Task Create_makes_an_empty_owner_only_directory() {
        var stateDir = TempStateDir();
        var home = KiroReviewerHome.Create(stateDir, "epochA", "launch1");

        await Assert.That(Directory.Exists(home)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(home)).IsEmpty();

        if (!OperatingSystem.IsWindows()) {
            var mode = File.GetUnixFileMode(home);
            await Assert.That(mode).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task Sweep_deletes_a_previous_epoch_home() {
        var stateDir = TempStateDir();
        var stale = KiroReviewerHome.Create(stateDir, "epochA", "launch1");

        KiroReviewerHome.SweepStale(stateDir, "epochB", NullLogger.Instance);

        await Assert.That(Directory.Exists(stale)).IsFalse();
    }

    // The control for the test above: without it, a sweep that deleted EVERYTHING would pass.
    [Test]
    public async Task Sweep_keeps_a_current_epoch_home() {
        var stateDir = TempStateDir();
        var live = KiroReviewerHome.Create(stateDir, "epochB", "launch2");

        KiroReviewerHome.SweepStale(stateDir, "epochB", NullLogger.Instance);

        await Assert.That(Directory.Exists(live)).IsTrue();
    }

    // The multi-daemon case the previous spec revision got backwards. Roots are per daemon, so a
    // peer's home lives under a DIFFERENT stateDir and this daemon's sweep must not see it at all —
    // even though its epoch differs, which is exactly what a shared-root rule would have deleted.
    [Test]
    public async Task Sweep_never_reaches_a_peer_daemons_root() {
        var mine = TempStateDir();
        var peer = TempStateDir();
        var peerLive = KiroReviewerHome.Create(peer, "peerEpoch", "launch9");

        KiroReviewerHome.SweepStale(mine, "myEpoch", NullLogger.Instance);

        await Assert.That(Directory.Exists(peerLive)).IsTrue();
    }

    [Test]
    public async Task Delete_refuses_a_path_outside_the_state_dir() {
        var stateDir = TempStateDir();
        var outside  = TempStateDir();
        var victim   = Path.Combine(outside, "not-ours");
        Directory.CreateDirectory(victim);

        KiroReviewerHome.Delete(victim, stateDir, NullLogger.Instance);

        await Assert.That(Directory.Exists(victim)).IsTrue();
    }

    [Test]
    public async Task Delete_does_not_follow_a_symlink_out_of_the_root() {
        if (OperatingSystem.IsWindows()) return;

        var stateDir = TempStateDir();
        var outside  = TempStateDir();
        var canary   = Path.Combine(outside, "canary.txt");
        await File.WriteAllTextAsync(canary, "keep me");

        var home = KiroReviewerHome.Create(stateDir, "epochA", "launch1");
        Directory.CreateSymbolicLink(Path.Combine(home, "escape"), outside);

        KiroReviewerHome.Delete(home, stateDir, NullLogger.Instance);

        await Assert.That(Directory.Exists(home)).IsFalse();
        await Assert.That(File.Exists(canary)).IsTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerHomeTests/*"`
Expected: FAIL — `KiroReviewerHome` does not exist (compile error).

- [ ] **Step 3: Implement**

```csharp
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The per-launch isolated <c>KIRO_HOME</c> for an unattended Kiro reviewer.
///
/// <para><b>Why an isolated home at all.</b> Kiro inherits the operator's GLOBAL
/// <c>~/.kiro/settings/mcp.json</c> servers into every ACP session — measured, with a positive
/// control, in <c>docs/probes/2026-08-05-kiro-reviewer-trust/</c>. One of them is the flows server,
/// which would let a reviewer start nested review flows. Pointing <c>KIRO_HOME</c> at an empty
/// directory initializes ZERO global servers while the injected result channel still starts. The
/// credential is NOT under <c>KIRO_HOME</c>, so this suppresses configuration without touching
/// authentication — which is the whole reason this approach works where an OS sandbox did not.</para>
///
/// <para><b>Why disposal is a security requirement.</b> <c>KiroPaths.ConfigRoot</c> reads
/// <c>KIRO_HOME</c> first, so the reviewer's own conversation JSONL lands in
/// <c>{KIRO_HOME}/sessions/cli</c> — carrying the caller's diff and source excerpts. The home is
/// read-empty but WRITE-SENSITIVE.</para>
///
/// <para><b>The root is per daemon, and that is load-bearing.</b> An earlier design specified one
/// shared root swept by epoch, reasoning that the epoch key made it safe for a second daemon. The
/// opposite is true: with a shared root, daemon A's rule "delete every home whose epoch is not mine"
/// selects daemon B's CURRENT, LIVE home. Per-daemon roots remove the question instead of
/// adjudicating it — every directory in this root belongs to an incarnation of THIS daemon, so a
/// non-current epoch is by definition dead.</para>
/// </summary>
internal static class KiroReviewerHome {
    const string Prefix = "kcap-kiro-reviewer-";

    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "kiro-reviewers");

    /// <summary>Creates an empty, owner-only home. Empty is what makes the suppression work, so
    /// nothing may be seeded into it.</summary>
    internal static string Create(string stateDir, string daemonEpoch, string launchId) {
        var root = RootFor(stateDir);
        Directory.CreateDirectory(root);
        HardenDirectory(root);

        var home = Path.Combine(root, $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}");
        Directory.CreateDirectory(home);

        // Set the mode on the directory we just created, before anything can be written into it.
        // A world-readable window between mkdir and chmod is long enough to leak a transcript.
        HardenDirectory(home);
        return home;
    }

    /// <summary>Deletes every home in THIS daemon's root whose epoch is not the current one. Safe by
    /// construction: the root is not shared, so a non-current epoch is a dead incarnation of us.</summary>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger log) {
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        foreach (var dir in Directory.EnumerateDirectories(root)) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(live, StringComparison.Ordinal)) continue;

            Delete(dir, stateDir, log);
        }
    }

    /// <summary>Removes a reviewer home. Never follows a link out of the root, and refuses a path
    /// that does not resolve inside it — the transcript content is why these are requirements here
    /// rather than generic recursive-delete hygiene.</summary>
    internal static void Delete(string homePath, string stateDir, ILogger log) {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homePath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("Kiro reviewer home {Path} is outside {Root}; refusing to delete", full, root);
            return;
        }

        try {
            DeleteTreeNoFollow(full);
        } catch (Exception ex) {
            // Log and continue: an undeletable home must not fail a round or block startup. But it
            // IS undisposed review context, so it is never silent.
            log.LogWarning(ex, "Failed to delete Kiro reviewer home {Path}", full);
        }
    }

    static void DeleteTreeNoFollow(string path) {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
            var info = new FileInfo(entry);
            var isDirectory = info.Attributes.HasFlag(FileAttributes.Directory);
            var isLink      = info.Attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isDirectory && isLink) Directory.Delete(entry);        // the link, never its target
            else if (isDirectory)      DeleteTreeNoFollow(entry);
            else                       File.Delete(entry);
        }

        Directory.Delete(path);
    }

    static void HardenDirectory(string path) {
        if (OperatingSystem.IsWindows()) return;

        try {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        } catch { /* best-effort, as LaunchConsentStore does for the same reason */ }
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerHomeTests/*"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Mutation-check the symlink guard**

Temporarily replace `DeleteTreeNoFollow(full)` with `Directory.Delete(full, recursive: true)` and re-run. `Delete_does_not_follow_a_symlink_out_of_the_root` must still pass (current .NET declines to follow) — if it does, the test is not proving our implementation. Add to the test a nested real directory containing a file, then a link, and assert the real nested content is gone and the canary survives. Revert the mutation.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Acp/KiroReviewerHome.cs test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerHomeTests.cs
git commit -m "feat(kiro): per-launch isolated reviewer home with epoch-keyed sweep"
```

---

## Task 2: Version resolution + the affirmed-version record

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Acp/VendorVersionResolver.cs`, `src/Capacitor.Cli.Daemon/Acp/KiroReviewerVersionStore.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs` (delete `ResolveGeminiVersion`/`ExtractVersionToken`, call the shared resolver)
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerVersionStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `VendorVersionResolver.Resolve(string binaryPath) -> string?` (null = unknown);
  `KiroReviewerVersionStore(string stateDir)` with `string? Affirmed { get; }` and `void Affirm(string version)`.

- [ ] **Step 1: Move the resolver, do not copy it**

`AcpHostedAgentRuntimeFactory.ResolveGeminiVersion` already implements the bounded shape this needs, and its comments record two traps a second copy would reintroduce (a deadlock from reading before waiting, and requiring whole-output equality when the vendor prints banner lines). Move it verbatim into `VendorVersionResolver.Resolve`, taking the binary path only, and have the Gemini call site delegate.

```csharp
namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// A vendor binary's own reported version, or <see langword="null"/> when it cannot be determined —
/// which every caller treats as unknown, and therefore denies.
///
/// <para>Generalized from the Gemini reviewer's resolver rather than copied. Two traps its history
/// records and this shape avoids: reading a stream to completion BEFORE the bounded wait deadlocks
/// on a vendor that never closes stdout, and an undrained stderr wedges the child when its buffer
/// fills; and requiring the whole trimmed output to equal a version makes the gate fail closed the
/// day a vendor adds an "update available" banner.</para>
/// </summary>
internal static class VendorVersionResolver {
    internal static string? Resolve(string binaryPath) {
        /* body moved verbatim from AcpHostedAgentRuntimeFactory.ResolveGeminiVersion,
           including ExtractVersionToken */
    }
}
```

- [ ] **Step 2: Write the failing store tests**

```csharp
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class KiroReviewerVersionStoreTests {
    static string TempStateDir() {
        var d = Path.Combine(Path.GetTempPath(), "kcap-kiro-ver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Test]
    public async Task Absent_record_reads_null() {
        var store = new KiroReviewerVersionStore(TempStateDir());
        await Assert.That(store.Affirmed).IsNull();
    }

    [Test]
    public async Task Affirm_then_read_round_trips() {
        var dir = TempStateDir();
        new KiroReviewerVersionStore(dir).Affirm("2.16.0");
        await Assert.That(new KiroReviewerVersionStore(dir).Affirmed).IsEqualTo("2.16.0");
    }

    [Test]
    public async Task Record_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var dir = TempStateDir();
        new KiroReviewerVersionStore(dir).Affirm("2.16.0");

        var mode = File.GetUnixFileMode(Path.Combine(dir, "kiro-reviewer-affirmed-version"));
        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Unreadable_record_reads_null_rather_than_throwing() {
        var dir = TempStateDir();
        Directory.CreateDirectory(Path.Combine(dir, "kiro-reviewer-affirmed-version"));
        await Assert.That(new KiroReviewerVersionStore(dir).Affirmed).IsNull();
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerVersionStoreTests/*"`
Expected: FAIL — type does not exist.

- [ ] **Step 4: Implement the store**

```csharp
namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The <c>kiro-cli</c> version this daemon last ran an unattended reviewer under.
///
/// <para><b>Why this is state and not configuration.</b> A value the operator could set from a shell
/// profile would be re-affirmed by their dotfiles rather than by them, which is the same
/// "consent that isn't consent" failure the enable flag exists to avoid. It is written by the daemon
/// and cleared only by an explicit operator command.</para>
///
/// <para><b>Why not a maintainer-curated certified set</b> (the Gemini shape): that set takes the
/// reviewer offline on every vendor release until we ship a re-certification PR, and kiro-cli moved
/// 2.12.1 → 2.15.2 → 2.16.0 inside a week. Fail-closed on CHANGE, cleared by the operator who is
/// already the consenting party, gets the same direction without the treadmill.</para>
/// </summary>
internal sealed class KiroReviewerVersionStore(string stateDir) {
    readonly string _path = Path.Combine(stateDir, "kiro-reviewer-affirmed-version");

    internal string? Affirmed {
        get {
            try {
                var text = File.ReadAllText(_path).Trim();
                return text.Length == 0 ? null : text;
            } catch {
                // Missing, unreadable, or a directory at the pathname: all "not affirmed", which is
                // the fail-closed direction. Never throws — a boot must not brick on this.
                return null;
            }
        }
    }

    internal void Affirm(string version) {
        Directory.CreateDirectory(stateDir);

        // Mode set BEFORE any content exists, as LaunchConsentStore does: a chmod after the write
        // leaves a readable window.
        var options = new FileStreamOptions {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(_path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(version.Trim());
    }
}
```

- [ ] **Step 5: Run tests + the Gemini regression suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerVersionStoreTests/*"` → PASS
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiReviewerCapabilityTests/*"` → PASS (the resolver move must not change Gemini behaviour)

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Acp/VendorVersionResolver.cs src/Capacitor.Cli.Daemon/Acp/KiroReviewerVersionStore.cs src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerVersionStoreTests.cs
git commit -m "feat(kiro): share the vendor version resolver, add the affirmed-version record"
```

---

## Task 3: `KiroReviewerCapability` — the pure decision

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Acp/KiroReviewerCapability.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerCapabilityTests.cs`

**Interfaces:**
- Consumes: `KiroReviewerVersionStore.Affirmed`, `VendorVersionResolver.Resolve`.
- Produces: `KiroReviewerCapability.Decide(bool operatorEnabled, string? installedVersion, string? affirmedVersion) -> KiroReviewerDecision` where
  `internal enum KiroReviewerDecision { Allowed, Disabled, VersionUnresolved, VersionUnaffirmed, UnsupportedPlatform }`,
  and `KiroReviewerCapability.DenialReason(KiroReviewerDecision, string? installedVersion, string? affirmedVersion) -> string`.

- [ ] **Step 1: Add the config flag**

```csharp
/// <summary>
/// Whether THIS daemon may run Kiro as an unattended review-flow reviewer. Off by default, and
/// enabling it is the operator's consent event — see KiroReviewerCapability for what is consented to.
/// Overridable via KCAP_KIRO_UNATTENDED_REVIEWER.
/// </summary>
public bool KiroUnattendedReviewerEnabled { get; set; }
```

- [ ] **Step 2: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class KiroReviewerCapabilityTests {
    [Test]
    public async Task Disabled_when_the_operator_has_not_opted_in() =>
        await Assert.That(KiroReviewerCapability.Decide(false, "2.16.0", "2.16.0"))
                    .IsEqualTo(KiroReviewerDecision.Disabled);

    [Test]
    public async Task Allowed_when_enabled_and_the_installed_version_is_affirmed() =>
        await Assert.That(KiroReviewerCapability.Decide(true, "2.16.0", "2.16.0"))
                    .IsEqualTo(KiroReviewerDecision.Allowed);

    [Test]
    public async Task Unaffirmed_when_the_installed_version_changed() =>
        await Assert.That(KiroReviewerCapability.Decide(true, "2.17.0", "2.16.0"))
                    .IsEqualTo(KiroReviewerDecision.VersionUnaffirmed);

    // The control for the seeding behaviour in Task 8: an absent record is NOT silently allowed.
    [Test]
    public async Task Unaffirmed_when_no_version_has_ever_been_affirmed() =>
        await Assert.That(KiroReviewerCapability.Decide(true, "2.16.0", null))
                    .IsEqualTo(KiroReviewerDecision.VersionUnaffirmed);

    [Test]
    public async Task Unresolved_version_is_denied() =>
        await Assert.That(KiroReviewerCapability.Decide(true, null, "2.16.0"))
                    .IsEqualTo(KiroReviewerDecision.VersionUnresolved);

    [Test]
    public async Task The_unaffirmed_reason_names_both_versions() {
        var reason = KiroReviewerCapability.DenialReason(
            KiroReviewerDecision.VersionUnaffirmed, "2.17.0", "2.16.0");

        await Assert.That(reason).Contains("2.17.0");
        await Assert.That(reason).Contains("2.16.0");
        await Assert.That(reason).StartsWith("kiro_reviewer_version_unaffirmed");
    }

    // The consent text is the acceptance artifact, so its CONTENT is the assertion.
    [Test]
    public async Task The_disabled_reason_states_the_trust_domain_condition() {
        var reason = KiroReviewerCapability.DenialReason(KiroReviewerDecision.Disabled, null, null);

        await Assert.That(reason).StartsWith("kiro_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("every file this daemon user can read");
        await Assert.That(reason).Contains("one trust domain");
        await Assert.That(reason).Contains("KiroUnattendedReviewerEnabled");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerCapabilityTests/*"`
Expected: FAIL — type does not exist.

- [ ] **Step 4: Implement**

```csharp
namespace Capacitor.Cli.Daemon.Acp;

internal enum KiroReviewerDecision { Allowed, Disabled, VersionUnresolved, VersionUnaffirmed, UnsupportedPlatform }

/// <summary>
/// Whether THIS daemon may run Kiro as an unattended review-flow reviewer. Pure, so every arm is
/// testable without a vendor or a process.
///
/// <para><b>What enabling it consents to.</b> An unattended reviewer runs in a daemon-owned worktree
/// with the daemon's own HOME and a trusted read tool that is NOT path-scoped — measured. So a
/// review can read every file this daemon user can read, its own credentials included, and can
/// return what it read to whoever requested the review. That risk lands on the daemon OPERATOR, who
/// is not necessarily the requester, which is why the decision is daemon-local configuration and
/// enabling it is the consent event.</para>
///
/// <para><b>Why a version affirmation and not a certified set.</b> Containment is source suppression
/// — an empty per-launch KIRO_HOME plus the worktree-layer deletion of branch-authored config. The
/// second is ours; the first is not: that Kiro honours KIRO_HOME, and reads no other global config
/// source, are behaviours of the build. A maintainer-curated certified set would take the reviewer
/// offline on every vendor release, so instead this fails closed when the installed version CHANGES
/// and the operator clears it.</para>
/// </summary>
internal static class KiroReviewerCapability {
    internal static KiroReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? affirmedVersion) {
        // Windows has no 0700, so the transcript-bearing home cannot be made owner-only and the
        // disposal requirement cannot be met. Fail closed rather than advertise it.
        if (OperatingSystem.IsWindows())         return KiroReviewerDecision.UnsupportedPlatform;

        // Operator flag FIRST and short-circuiting: a disabled daemon must never interrogate the
        // vendor binary, or an installed-but-wedged Kiro can hang startup on a switched-off feature.
        if (!operatorEnabled)                    return KiroReviewerDecision.Disabled;
        if (installedVersion is not { Length: > 0 }) return KiroReviewerDecision.VersionUnresolved;
        if (affirmedVersion is not { Length: > 0 }) return KiroReviewerDecision.VersionUnaffirmed;

        return string.Equals(installedVersion.Trim(), affirmedVersion.Trim(), StringComparison.Ordinal)
            ? KiroReviewerDecision.Allowed
            : KiroReviewerDecision.VersionUnaffirmed;
    }

    /// <summary>Separated from <see cref="Decide"/> so the two cannot disagree about WHY.</summary>
    internal static string DenialReason(
            KiroReviewerDecision decision, string? installedVersion, string? affirmedVersion) =>
        decision switch {
            KiroReviewerDecision.UnsupportedPlatform =>
                "kiro_reviewer_unsupported_platform: the Kiro unattended reviewer is POSIX-only. Its "
              + "isolated home holds the reviewer's transcript, including the review context, and "
              + "cannot be made owner-only on this platform.",
            KiroReviewerDecision.Disabled =>
                "kiro_unattended_reviewer_disabled: this daemon has not enabled Kiro as an unattended "
              + "review-flow reviewer. Enabling it grants a review read access to every file this "
              + "daemon user can read — including its own credentials — with no filesystem boundary, "
              + "and a reviewer can return what it read to whoever requested the review. Enable it "
              + "only on a daemon whose operator and review requesters are in one trust domain: set "
              + "KiroUnattendedReviewerEnabled on the daemon (not on the server).",
            KiroReviewerDecision.VersionUnresolved =>
                "kiro_reviewer_version_unresolved: the installed kiro-cli version could not be "
              + "determined, so it cannot be matched against the version this daemon affirmed. A "
              + "build we cannot identify is refused rather than assumed compatible.",
            _ =>
                $"kiro_reviewer_version_unaffirmed: kiro-cli {installedVersion ?? "<unknown>"} is "
              + $"installed but this daemon affirmed {affirmedVersion ?? "<none>"}. The reviewer's "
              + "MCP containment depends on this build honouring KIRO_HOME and reading no other "
              + "global config source, so a changed build is refused until an operator confirms it: "
              + "run `kcap daemon reviewer affirm --vendor kiro`."
        };
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerCapabilityTests/*"`
Expected: PASS, 7 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Acp/KiroReviewerCapability.cs src/Capacitor.Cli.Daemon/DaemonConfig.cs test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerCapabilityTests.cs
git commit -m "feat(kiro): reviewer capability gate — operator consent plus version affirmation"
```

---

## Task 4: Split aliasing from the Gemini allowlist argv

Turning aliasing on for Kiro (Task 5 needs per-launch wire names) must NOT turn on Gemini's `--allowed-mcp-server-names` substitution, its canonical-argv assertion, or its capability gate. Today one predicate — `AliasesResultChannel` — drives all four.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/GeminiReviewerLaunchTests.cs` (regression), `test/Capacitor.Cli.Tests.Unit/Acp/AcpVendorDescriptorTests.cs`

**Interfaces:**
- Produces: `static bool AliasesResultChannel(AcpVendorDescriptor)` — now Gemini **and** Kiro;
  `static bool UsesMcpNameAllowlistArgv(AcpVendorDescriptor)` — Gemini only;
  `static void RequireReviewerCapability(AcpVendorDescriptor, DaemonConfig, bool isReviewFlow, Func<string,string?>? resolveVersion, KiroReviewerVersionStore? kiroVersions)`.

- [ ] **Step 1: Write the failing regression test**

```csharp
// in AcpVendorDescriptorTests
[Test]
public async Task Kiro_aliases_its_result_channel_but_carries_no_mcp_name_allowlist_argv() {
    var kiro = AcpVendorDescriptors.Kiro;

    // Aliasing is ON (the tripwire compares launch-unique names).
    await Assert.That(AcpHostedAgentRuntimeFactory.AliasesResultChannel(kiro)).IsTrue();

    // But Gemini's name-allowlist argv machinery is NOT: Kiro has no such flag, and running the
    // substitution or the canonical-argv assertion against it would be a wiring bug.
    await Assert.That(AcpHostedAgentRuntimeFactory.UsesMcpNameAllowlistArgv(kiro)).IsFalse();
    await Assert.That(kiro.Argv).DoesNotContain(AcpVendorDescriptors.UnmatchableMcpNamePlaceholder);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AcpVendorDescriptorTests/Kiro_aliases_its_result_channel*"`
Expected: FAIL — `UsesMcpNameAllowlistArgv` does not exist.

- [ ] **Step 3: Split the predicate**

```csharp
/// <summary>Vendors whose injected MCP servers carry PER-LAUNCH wire names.
///
/// <para>Two different reasons, deliberately served by one mechanism. Gemini needs unguessable names
/// because its MCP gate is an exact-name allowlist a repository could declare a server under. Kiro
/// needs them because its MCP surface tripwire compares reported server names against the injected
/// set, and a canonical public id is a string any other source could also produce — aliasing is what
/// makes that comparison close to an identity check.</para></summary>
static bool AliasesResultChannel(AcpVendorDescriptor descriptor) =>
    descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor
 || descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor;

/// <summary>Vendors whose argv carries an exact-name MCP allowlist that a review launch must widen
/// to exactly its injected set — Gemini alone. Split OUT of <see cref="AliasesResultChannel"/>:
/// Kiro aliases but has no such flag, so running the placeholder substitution or the canonical-argv
/// assertion for it would assert against machinery it does not have.</summary>
static bool UsesMcpNameAllowlistArgv(AcpVendorDescriptor descriptor) =>
    descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor;
```

Then, in `BuildProcessStartInfo`, change the two Gemini-specific arms to test `UsesMcpNameAllowlistArgv`:
- the `if (AliasesResultChannel(descriptor))` block that computes `reviewGate` and substitutes,
- the final `if (AliasesResultChannel(descriptor) && psi.FileName != ...) AssertGeminiArgvIsCanonical(...)`.

`LaunchIdentity.ForLaunch(AliasesResultChannel(descriptor))` stays on the aliasing predicate — that is the one place both vendors want.

- [ ] **Step 4: Generalize the capability gate**

Rename `RequireGeminiReviewerCapability` → `RequireReviewerCapability` and dispatch per vendor. Keep Gemini's body byte-identical; add the Kiro arm.

```csharp
static void RequireReviewerCapability(
        AcpVendorDescriptor descriptor, DaemonConfig config, bool isReviewFlow,
        Func<string, string?>? resolveVersion, KiroReviewerVersionStore? kiroVersions) {
    if (!isReviewFlow) return;

    if (descriptor.Vendor == AcpVendorDescriptors.Gemini.Vendor) {
        /* existing Gemini body, unchanged */
        return;
    }

    if (descriptor.Vendor != AcpVendorDescriptors.Kiro.Vendor) return;

    // Operator flag first and short-circuiting, for the same reason Gemini does it: a disabled
    // daemon must never execute the vendor binary.
    var enabled = config.KiroUnattendedReviewerEnabled;
    var installed = enabled
        ? (resolveVersion ?? VendorVersionResolver.Resolve)(descriptor.ResolveBinaryPath(config))
        : null;
    var affirmed = enabled ? kiroVersions?.Affirmed : null;

    var decision = KiroReviewerCapability.Decide(enabled, installed, affirmed);
    if (decision != KiroReviewerDecision.Allowed)
        throw new InvalidOperationException(
            KiroReviewerCapability.DenialReason(decision, installed, affirmed));
}
```

Update both call sites (`StartAsync`, `BuildProcessStartInfo`) and `SupportsUnattended` to route Kiro through the same decision.

- [ ] **Step 5: Run the affected suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AcpVendorDescriptorTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiReviewerLaunchTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/GeminiReviewerCapabilityTests/*"
```
Expected: all PASS. Gemini behaviour must be unchanged — this task is a refactor plus one new arm.

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "refactor(acp): split result-channel aliasing from the MCP-name allowlist argv"
```

---

## Task 5: `KiroReviewerTrustList` and the launch wiring

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Acp/KiroReviewerTrustList.cs`
- Modify: `src/Capacitor.Cli.Daemon/Acp/AcpVendorDescriptor.cs`, `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerTrustListTests.cs`, `test/Capacitor.Cli.Tests.Unit/Services/KiroReviewerLaunchTests.cs`

**Interfaces:**
- Consumes: `AcpMcpServerSpec` list from `AcpReviewFlowMcp.Build`, `KcapMcpRegistry.ReviewFlowUnattendedSafeTools`.
- Produces: `KiroReviewerTrustList.Build(IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) -> string`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class KiroReviewerTrustListTests {
    static AcpMcpServerSpec Spec(string name) => new(name, "kcap", ["mcp", "x"], []);

    [Test]
    public async Task Carries_the_native_read_and_think_tools_and_never_write_or_shell() {
        var value = KiroReviewerTrustList.Build([Spec("chan")], LaunchIdentity.ForLaunch(true));
        var entries = value.Split(',');

        await Assert.That(entries).Contains("fs_read");
        await Assert.That(entries).Contains("thinking");
        await Assert.That(entries).DoesNotContain("fs_write");
        await Assert.That(entries).DoesNotContain("execute_bash");
    }

    [Test]
    public async Task Namespaces_the_result_channel_tool_under_its_wire_name() {
        var identity = LaunchIdentity.ForLaunch(true);
        var wire = identity.ResultChannelWireName;

        var value = KiroReviewerTrustList.Build([Spec(wire)], identity);

        await Assert.That(value.Split(',')).Contains($"@{wire}/submit_review_result");
    }

    // The defect a FIXED list would have shipped: an injected allowlist server's tools must be
    // trusted too, or every call raises a frame and the Fail policy kills the round.
    [Test]
    public async Task Includes_every_tool_of_every_injected_allowlist_server() {
        var identity = LaunchIdentity.ForLaunch(true);
        var reviewWire = identity.AllowlistWireName("kcap-review");

        var value = KiroReviewerTrustList.Build(
            [Spec(identity.ResultChannelWireName), Spec(reviewWire)], identity);
        var entries = value.Split(',');

        foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-review"])
            await Assert.That(entries).Contains($"@{reviewWire}/{tool}");
    }

    [Test]
    public async Task Rejects_an_injected_server_with_no_safe_tool_table_entry() {
        var identity = LaunchIdentity.ForLaunch(true);

        await Assert.That(() => KiroReviewerTrustList.Build(
                [Spec(identity.ResultChannelWireName), Spec("totally-unknown")], identity))
            .Throws<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerTrustListTests/*"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The `--trust-tools` value for one unattended Kiro reviewer launch.
///
/// <para><b>Why this is derived per launch and not a fixed descriptor array.</b> A review can carry
/// an MCP allowlist, and those servers are injected into session/new alongside the result channel.
/// Their tools appear in no fixed list, so under the Fail interaction policy every call raises a
/// permission frame and kills the round — for exactly the reviews that need repository context most,
/// and it presents as a vendor bug. Gemini never had this problem because its blanket approval mode
/// trusts whatever is injected; scoping the surface is what creates the obligation to enumerate it.</para>
///
/// <para><b>One derivation, from the injected specs themselves.</b> Building this from server IDS
/// rather than from the built list would be a second derivation of the same names, and that failure
/// is silent: the reviewer starts normally and cannot call its own channel.</para>
/// </summary>
internal static class KiroReviewerTrustList {
    /// <summary>Measured native names. `fs_write` and `execute_bash` are deliberately absent —
    /// trusting shell in particular would let a write execute with no permission frame at all, so the
    /// read-only posture would be fiction.</summary>
    internal static readonly string[] NativeTools = ["fs_read", "thinking"];

    internal static string Build(IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) {
        var entries = new List<string>(NativeTools);

        foreach (var server in injected) {
            if (server.Name == identity.ResultChannelWireName) {
                entries.Add($"@{server.Name}/{KcapMcpRegistry.ResultChannelToolName}");
                continue;
            }

            var canonical = KcapMcpRegistry.ReviewFlowUnattendedSafeTools.Keys
                .FirstOrDefault(id => identity.AllowlistWireName(id) == server.Name)
                ?? throw new InvalidOperationException(
                    $"kiro_reviewer_trust_list_unknown_server: injected MCP server '{server.Name}' has no "
                  + "entry in the review-flow safe-tool table, so its tools cannot be trusted. Failing the "
                  + "launch rather than injecting it untrusted.");

            foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools[canonical]
                                                .Order(StringComparer.Ordinal))
                entries.Add($"@{server.Name}/{tool}");
        }

        return string.Join(",", entries);
    }
}
```

Add `public const string ResultChannelToolName = "submit_review_result";` to `KcapMcpRegistry` if it is not already exposed (it is currently a literal inside `ReservedResultChannelTools`).

- [ ] **Step 4: Add the descriptor hook**

Add to `AcpVendorDescriptor`:

```csharp
/// <summary>Builds this vendor's unattended trust argv from the launch's own injected MCP specs.
/// Null for a vendor whose trust argv is fixed (<see cref="UnattendedTrustArgv"/>). Present for Kiro,
/// whose scoped trust list must name the per-launch wire names of everything injected — see
/// <see cref="KiroReviewerTrustList"/>.</summary>
public Func<IReadOnlyList<Core.Acp.AcpMcpServerSpec>, LaunchIdentity, ImmutableArray<string>>?
    UnattendedTrustArgvBuilder { get; }
```

Wire it in `BuildProcessStartInfo`, replacing the unconditional `argv.AddRange(descriptor.UnattendedTrustArgv)`:

```csharp
if (descriptor.UnattendedTrustArgvBuilder is { } buildTrust) {
    var reviewMcp = ValidateAndBuildReviewFlowMcp(ctx, descriptor, resolved)!;
    argv.AddRange(buildTrust(reviewMcp, identity));
} else {
    argv.AddRange(descriptor.UnattendedTrustArgv);
}
```

- [ ] **Step 5: Wire `KIRO_HOME` into the spawn**

In `BuildProcessStartInfo`, after the `KCAP_URL` assignment:

```csharp
// The isolated home is what suppresses the operator's global MCP servers — the flows server among
// them. Created here rather than by the vendor: it must exist, and be owner-only, before the child
// starts writing its transcript into it.
if (ctx.IsReviewFlow && descriptor.Vendor == AcpVendorDescriptors.Kiro.Vendor)
    psi.Environment["KIRO_HOME"] = KiroReviewerHome.Create(
        ReviewerStateDir(config), config.DaemonEpoch, ctx.AgentId);
```

- [ ] **Step 6: Write the launch-level test**

```csharp
namespace Capacitor.Cli.Tests.Unit.Services;

public class KiroReviewerLaunchTests {
    [Test]
    public async Task A_review_launch_trusts_exactly_the_injected_servers_tools() {
        // Build a review context with a non-empty allowlist, call BuildProcessStartInfo directly,
        // and assert the --trust-tools VALUE names every injected server's wire name. Asserting the
        // argv is what pins the entry even when a model declines to call the tool.
    }

    [Test]
    public async Task A_review_launch_sets_an_empty_owner_only_KIRO_HOME() { /* … */ }

    [Test]
    public async Task An_interactive_launch_sets_no_KIRO_HOME_and_no_trust_argv() { /* control */ }
}
```

- [ ] **Step 7: Run, then commit**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerTrustListTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KiroReviewerLaunchTests/*"
git add -u && git add src/Capacitor.Cli.Daemon/Acp/KiroReviewerTrustList.cs test/Capacitor.Cli.Tests.Unit/Acp/KiroReviewerTrustListTests.cs test/Capacitor.Cli.Tests.Unit/Services/KiroReviewerLaunchTests.cs
git commit -m "feat(kiro): per-launch scoped trust list and isolated KIRO_HOME on review launches"
```

---

## Task 6: The MCP surface tripwire

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Acp/KiroMcpSurfaceMonitor.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/KiroMcpSurfaceMonitorTests.cs`

**Interfaces:**
- Consumes: `AcpConnection.OnNotification`.
- Produces: `KiroMcpSurfaceMonitor(IReadOnlySet<string> injectedNames)` with
  `void Observe(AcpNotification n)`, `string? Violation { get; }`, `bool ResultChannelReady { get; }`.

- [ ] **Step 1: Write the failing tests**

```csharp
public class KiroMcpSurfaceMonitorTests {
    static AcpNotification Init(string name) => /* build _kiro.dev/mcp/server_initialized {serverName} */;
    static AcpNotification Fail(string name) => /* build _kiro.dev/mcp/server_init_failure {serverName} */;

    [Test]
    public async Task An_injected_server_is_admitted() { /* Violation stays null */ }

    [Test]
    public async Task A_server_outside_the_injected_set_is_a_violation() {
        var m = new KiroMcpSurfaceMonitor(new HashSet<string> { "chan" });
        m.Observe(Init("kcap-flows"));
        await Assert.That(m.Violation).StartsWith("kiro_reviewer_mcp_surface_unexpected");
    }

    // The COUNT rule. A duplicate of an injected name is INSIDE the set, so a membership-only
    // check admits it — which is why §5.1 counts rather than testing membership.
    [Test]
    public async Task A_second_initialization_of_an_injected_name_is_a_violation() {
        var m = new KiroMcpSurfaceMonitor(new HashSet<string> { "chan" });
        m.Observe(Init("chan"));
        m.Observe(Init("chan"));
        await Assert.That(m.Violation).StartsWith("kiro_reviewer_mcp_surface_unexpected");
    }

    [Test]
    public async Task A_late_initialization_is_still_a_violation() { /* observe after ResultChannelReady */ }

    [Test]
    public async Task Result_channel_failure_is_its_own_code() {
        var m = new KiroMcpSurfaceMonitor(new HashSet<string> { "chan" });
        m.Observe(Fail("chan"));
        await Assert.That(m.Violation).StartsWith("kiro_reviewer_result_channel_unavailable");
    }

    [Test]
    public async Task Silence_is_not_readiness() {
        var m = new KiroMcpSurfaceMonitor(new HashSet<string> { "chan" });
        await Assert.That(m.ResultChannelReady).IsFalse();
    }
}
```

- [ ] **Step 2–4: Run (fail) → implement → run (pass)**

The monitor holds `injectedNames`, a `HashSet<string> _seen`, and sets `Violation` on: a name not in `injectedNames`; a name already in `_seen`; a `server_init_failure` naming the result channel. `ResultChannelReady` is `_seen.Contains(resultChannelWireName)`.

- [ ] **Step 5: Wire into the runtime**

Subscribe in `AcpHostedAgentRuntime.StartAsync` for Kiro review launches only; check `Violation` before accepting any result, and fail the round with the coded message. Enforcement runs for the whole session, not a sample — subscribing to `OnNotification` gives that for free.

- [ ] **Step 6: Commit**

```bash
git add -u && git add src/Capacitor.Cli.Daemon/Acp/KiroMcpSurfaceMonitor.cs test/Capacitor.Cli.Tests.Unit/Acp/KiroMcpSurfaceMonitorTests.cs
git commit -m "feat(kiro): MCP surface tripwire over the vendor's own init notifications"
```

---

## Task 7: The bounded launch deadline

The production failure this exists for: an expired credential leaves `kiro-cli` **alive and silent** on a browser prompt. Nothing in `AcpHostedAgentRuntime` bounds spawn → `initialize` → `session/new` → first prompt today (only `support.SettlementWait`).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs`, `src/Capacitor.Cli.Daemon/DaemonConfig.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeTests.cs`

- [ ] **Step 1: Write the failing test — an alive, never-responding peer**

```csharp
[Test]
public async Task An_alive_but_silent_peer_hits_the_deadline_and_is_reaped() {
    // A fake IAcpProcess whose streams never yield a frame and whose process stays alive.
    // Assert: StartAsync throws with "kiro_reviewer_launch_timeout", the process was killed,
    // and the reviewer home no longer exists.
    // This is the shape a terminating fixture CANNOT produce, which is why the two existing
    // synthetic failures (unresolvable binary, peer exits before initialize) do not cover it.
}
```

- [ ] **Step 2: Run (fail) → implement**

One absolute deadline computed once at entry (`config.KiroReviewerLaunchTimeoutSeconds`, default 120), linked into the token every stage awaits — never a fresh timeout per stage, or a slow sequence approaches a multiple of the budget. On expiry: kill the child, confirm exit, `KiroReviewerHome.Delete(...)`, throw `kiro_reviewer_launch_timeout`.

- [ ] **Step 3: Keep the existing coded-failure tests green**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AcpHostedAgentRuntimeTests/*"
```

- [ ] **Step 4: Commit**

```bash
git add -u && git commit -m "feat(acp): bound a Kiro reviewer launch and reap on expiry"
```

---

## Task 8: Enable — descriptor flip, the affirm verb, docs

This task is LAST on purpose: until it lands, nothing above is reachable, so a partial merge cannot expose an unbounded reviewer.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Acp/AcpVendorDescriptor.cs`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs`, `src/Capacitor.Cli/Commands/DaemonCommand.cs`, `README.md`
- Test: `test/Capacitor.Cli.Tests.Unit/Acp/AcpVendorDescriptorTests.cs`

- [ ] **Step 1: Flip the descriptor**

```csharp
UnattendedTrustArgv:        [],                                     // built per launch — see below
UnattendedTrustArgvBuilder: (specs, identity) =>
    ["--trust-tools", KiroReviewerTrustList.Build(specs, identity)],
SupportsUnattended:         true,
UnattendedInteractionPolicy: AcpUnattendedInteractionPolicy.Fail,
ReviewFlowMcpTransport:      AcpReviewFlowMcpTransport.SessionNew,
```

- [ ] **Step 2: Seed the affirmation on enable, and add the verb**

In `DaemonRunner`, when `KiroUnattendedReviewerEnabled` is true and the store has no record, affirm the currently-installed version. This is what stops an operator who has just turned the reviewer on from being refused over an upgrade that never happened.

Add `kcap daemon reviewer affirm --vendor kiro`: resolve the installed version, write it, print both the old and new values.

- [ ] **Step 3: Register the startup sweep**

`KiroReviewerHome.SweepStale(ReviewerStateDir(config), config.DaemonEpoch, log)` at daemon start, before any launch.

- [ ] **Step 4: Update README**

Add to the daemon section: the `KiroUnattendedReviewerEnabled` / `KCAP_KIRO_UNATTENDED_REVIEWER` flag with the trust-domain warning, and the `kcap daemon reviewer affirm` verb.

- [ ] **Step 5: Full verification**

```bash
rg -n "AI-[0-9]+" src/ test/ --type cs                       # must be empty
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

- [ ] **Step 6: Live end-to-end (the seeded-defect round)**

With `KCAP_KIRO_UNATTENDED_REVIEWER=1`, run `start_review_flow(kind="spec-review", vendor="kiro")` against a document carrying one unique planted defect. Assert the finding names it; remove only that defect and assert `clean`. Budget: keep to `deepseek-3.2`. **A round that merely reaches `clean` proves nothing** — completion, zero interactions, reap and channel-invoked are jointly satisfiable by an inert reviewer.

- [ ] **Step 7: Commit**

```bash
git add -u && git commit -m "feat(kiro): enable Kiro as an unattended review-flow reviewer"
```

---

## Deferred to their own issues

- A **general** ACP launch deadline for every vendor. Task 7 is Kiro-scoped because Kiro is where the alive-but-silent hang is measured; if a foundation deadline lands first, Task 7's requirement should be satisfied by it rather than duplicated.
- Reviewer model override (no ACP vendor has a `ReviewerModelResolver`).
- Borrowed review for Kiro (needs a containment-token decision).
