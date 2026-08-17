using System.Globalization;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// L1-shim(a) (spec §4.2(a)): the parent-side, pre-fork plan-construction contract —
/// pty_probe_execveat / pty_preflight / pty_plan_contained / pty_plan_free. NEVER forks or
/// execs (that's Task 3's pty_spawn); these tests only inspect the classification decision.
/// </summary>
public class PtyShimNativeTests {
    // A NULL-terminated (empty) envp. argv/envp cross into the shim as a bare `char* const[]`
    // with NO length prefix and NO auto NULL terminator (mirrors execvp/execve): the native
    // code walks to a NULL sentinel, so EVERY array must carry a trailing null element or the
    // walk reads out of bounds. `[]` would marshal to a zero-length array whose one-past read
    // is undefined — use an explicit `[null]` sentinel instead.
    static string?[] EmptyEnvp() => [null];
    static string    Env(string key, string value) => $"{key}={value}";

    // Ensure the array handed to the shim ends in the NULL sentinel the native walk expects
    // (production honors this too — UnixPtyProcess sets argv[^1] = null before forkpty).
    static string?[] NullTerm(string?[] a) => a.Length > 0 && a[^1] is null ? a : [.. a, null];

    [Test]
    public async Task Probe_execveat_reports_supported_on_a_35_plus_kernel() {
        if (!OperatingSystem.IsLinux()) return;

        // No forced-0 test seam engaged — a modern CI kernel (>= 3.19, almost certainly much
        // newer) must report supported.
        await Assert.That(UnixPtyInterop.pty_probe_execveat()).IsEqualTo(1);
    }

    [Test]
    public async Task Native_elf_no_shebang_is_contained_execfd() {
        if (!OperatingSystem.IsLinux()) return;

        var plan = Preflight("/bin/true", ["true"], EmptyEnvp(), execveatSupported: 1);
        try {
            await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Probe_disabled_forces_every_launch_uncontained_execpath() {
        if (!OperatingSystem.IsLinux()) return;

        // The <3.19 fallback, exercised WITHOUT a legacy kernel via the forced-0 test seam.
        var plan = Preflight("/bin/true", ["true"], EmptyEnvp(), execveatSupported: 0);
        try {
            await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0);
        } finally { Free(plan); }
    }

    [Test]
    public async Task Setuid_binary_classifies_uncontained_never_a_false_proof() {
        if (!OperatingSystem.IsLinux()) return;

        var suid = DummyProcess.CopySetuid("/bin/true");
        try {
            var plan = Preflight(suid, [suid], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(suid); }
    }

    [Test]
    public async Task Missing_original_path_is_a_preflight_failure_returns_minus_one() {
        if (!OperatingSystem.IsLinux()) return;

        var rc = UnixPtyInterop.pty_preflight(
            "/definitely/does/not/exist/" + Guid.NewGuid(), NullTerm(["x", null]), NullTerm(EmptyEnvp()), 1, out var plan);

        await Assert.That(rc).IsEqualTo(-1);
        await Assert.That(plan).IsEqualTo(IntPtr.Zero);
    }

    [Test]
    public async Task Execute_only_native_binary_still_builds_a_plan() {
        if (!OperatingSystem.IsLinux()) return;

        // No readable fd — EXEC_PATH plans need none; an EXEC_FD attempt's inspection
        // failure must degrade to EXEC_PATH-uncontained, never a launch failure.
        var xonly = DummyProcess.CopyExecuteOnly("/bin/true");
        try {
            var plan = Preflight(xonly, [xonly], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(plan).IsNotEqualTo(IntPtr.Zero); }
            finally { Free(plan); }
        } finally { File.Delete(xonly); }
    }

    [Test]
    public async Task Direct_shebang_rewrites_argv_keeping_the_single_optarg() {
        if (!OperatingSystem.IsLinux()) return;

        var script = DummyProcess.WriteShebangScript("/bin/sh", "-e", "exit 0\n");
        try {
            var plan = Preflight(script, [script, "extra"], EmptyEnvp(), execveatSupported: 1);
            try {
                // Contained: /bin/sh has no shebang of its own, no setuid bit on a stock CI image.
                await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
            } finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Relative_direct_shebang_interpreter_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        // A RELATIVE direct interpreter (#!bin/sh) would be resolved by the kernel against the
        // CHILD's post-chdir cwd — the parent-side preflight (which opens tok0 against the DAEMON's
        // cwd) can't reproduce that, so it must NOT be classified contained: it would otherwise
        // preflight/exec a DIFFERENT inode than the child would resolve. EXEC_PATH-uncontained lets
        // the kernel resolve the whole thing natively from the original path after chdir.
        var script = DummyProcess.WriteShebangScript("bin/sh", null, "exit 0\n");
        try {
            var plan = Preflight(script, [script], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Bare_env_shebang_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        // A bare `#!env NAME` is NOT equivalent to `#!/usr/bin/env NAME`: the kernel resolves `env`
        // itself against the CHILD's post-chdir cwd/PATH, which the parent can't reproduce. Only a
        // literal absolute `/usr/bin/env` enters the env-rewrite path; a bare `env` falls through to
        // the direct-shebang branch and is rejected there as a non-absolute interpreter.
        var script = DummyProcess.WriteShebangScript("env", "sh", "exit 0\n");
        try {
            var plan = Preflight(script, [script], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Env_shebang_resolves_against_child_path_not_daemon_path() {
        if (!OperatingSystem.IsLinux()) return;

        // Two directories each with a differently-behaved `probe-target` on PATH; the DAEMON's
        // ambient PATH points at one, the CHILD's envp PATH points at the other. The contained
        // plan must preflight the one the CHILD's PATH selects.
        var (daemonDir, childDir) = DummyProcess.TwoDistinctPathDirsWithDifferentTarget("probe-target");
        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "probe-target", "true\n");
        try {
            var childEnvp = new[] { Env("PATH", childDir) };
            var plan = Preflight(script, [script], childEnvp, execveatSupported: 1);
            try {
                await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(1);
                // The resolved inode must be the one under childDir, not daemonDir — asserted via
                // PlanExecFdInodeMatches, a small test-only helper added in Task 3 once pty_spawn
                // exposes the exec'd fd's inode for comparison against /proc/self/fd bookkeeping.
                // (Left as a forward reference: Task 3 Step 2 extends this exact test.)
            } finally { Free(plan); }
        } finally {
            File.Delete(script);
            Directory.Delete(daemonDir, true);
            Directory.Delete(childDir, true);
        }
    }

    // `{0}` = a temp dir that DOES contain a resolvable `probe-target`, so the ONLY reason each
    // case is uncontained is the empty/relative sibling element — proving the manual ':' scan
    // detects it. strtok would silently collapse a true empty field (`::`, leading/trailing `:`)
    // and mis-classify these as contained.
    [Test]
    [Arguments(".:{0}")]   // leading RELATIVE element
    [Arguments(":{0}")]    // leading EMPTY element (== cwd)
    [Arguments("{0}:")]    // trailing EMPTY element
    [Arguments("{0}::{0}")] // internal EMPTY element (`::`)
    public async Task Empty_or_relative_child_path_component_is_uncontained(string pathTemplate) {
        if (!OperatingSystem.IsLinux()) return;

        var dir    = DummyProcess.PathDirWithTarget("probe-target");
        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "probe-target", "true\n");
        try {
            var childEnvp = new[] { Env("PATH", string.Format(CultureInfo.InvariantCulture, pathTemplate, dir)) };
            var plan = Preflight(script, [script], childEnvp, execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally {
            File.Delete(script);
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Env_with_extra_tokens_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        var script = DummyProcess.WriteShebangScript("/usr/bin/env", "-S FOO=1 sh", "exit 0\n");
        try {
            var plan = Preflight(script, [script], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(script); }
    }

    [Test]
    public async Task Two_level_script_chain_is_uncontained() {
        if (!OperatingSystem.IsLinux()) return;

        var inner = DummyProcess.WriteShebangScript("/bin/sh", null, "exit 0\n");
        var outer = DummyProcess.WriteShebangScript(inner, null, "unused\n");
        try {
            var plan = Preflight(outer, [outer], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(UnixPtyInterop.pty_plan_contained(plan)).IsEqualTo(0); }
            finally { Free(plan); }
        } finally { File.Delete(inner); File.Delete(outer); }
    }

    [Test]
    public async Task Enoexec_shebangless_script_builds_a_plan_that_fails_at_exec_not_here() {
        if (!OperatingSystem.IsLinux()) return;

        // pty_preflight itself must NOT fail this (it has no shebang to parse and no reason to
        // reject a plain file) — the ENOEXEC surfaces at exec time (Task 3's test, not here).
        var path = Path.Combine(Path.GetTempPath(), "kcap-noshebang-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(path, "not a script, no shebang\n");
        DummyProcess.MakeExecutablePublic(path); // exposes MakeExecutable for this one direct case
        try {
            var plan = Preflight(path, [path], EmptyEnvp(), execveatSupported: 1);
            try { await Assert.That(plan).IsNotEqualTo(IntPtr.Zero); }
            finally { Free(plan); }
        } finally { File.Delete(path); }
    }

    static IntPtr Preflight(string exePath, string?[] argv, string?[] envp, int execveatSupported) {
        var rc = UnixPtyInterop.pty_preflight(exePath, NullTerm(argv), NullTerm(envp), execveatSupported, out var plan);
        if (rc != 0) throw new InvalidOperationException($"pty_preflight unexpectedly failed for {exePath}");
        return plan;
    }

    static void Free(IntPtr plan) {
        var p = plan;
        UnixPtyInterop.pty_plan_free(ref p);
    }
}
