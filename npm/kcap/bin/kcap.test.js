const assert = require("node:assert");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {
  resolveInstallSpec,
  probeArgs,
  trashDirFor,
  trashDirFromLauncher,
  filterKcapProcesses,
  describeRole,
  restoreMoved,
} = require("./kcap.js");

assert.strictEqual(resolveInstallSpec({ install_tag: "beta" }), "@kurrent/kcap@beta");
assert.strictEqual(resolveInstallSpec({ install_tag: "latest" }), "@kurrent/kcap@latest");
assert.strictEqual(resolveInstallSpec({}), "@kurrent/kcap@latest");          // missing → latest
assert.strictEqual(resolveInstallSpec(null), "@kurrent/kcap@latest");        // no probe → latest
assert.strictEqual(resolveInstallSpec({ install_tag: "" }), "@kurrent/kcap@latest");

assert.deepStrictEqual(probeArgs([]), ["update", "--check", "--no-update-check"]);
assert.deepStrictEqual(probeArgs(["--beta"]), ["update", "--check", "--no-update-check", "--beta"]);
assert.deepStrictEqual(probeArgs(["--stable"]), ["update", "--check", "--no-update-check", "--stable"]);
assert.deepStrictEqual(probeArgs(["--foo", "--beta", "-x"]), ["update", "--check", "--no-update-check", "--beta"]); // only channel flags forwarded
assert.deepStrictEqual(probeArgs(undefined), ["update", "--check", "--no-update-check"]); // defensive

// Absolute fixture roots must be host-native: trashDirFromLauncher path.resolves
// its input (a POSIX host treats "C:\…" as relative and cwd-prefixes it) and
// filterKcapProcesses path.basenames it — so this test runs on every CI leg.
const NPM_PREFIX = process.platform === "win32" ? "C:\\npm" : "/npm";

// trashDirFor: sibling of node_modules (same volume as the install tree).
assert.strictEqual(
  trashDirFor(path.join(NPM_PREFIX, "node_modules")),
  path.join(NPM_PREFIX, ".kcap-trash"),
);

// trashDirFromLauncher: derives the same dir from the launcher's location…
assert.strictEqual(
  trashDirFromLauncher(path.join(NPM_PREFIX, "node_modules", "@kurrent", "kcap", "bin")),
  path.join(NPM_PREFIX, ".kcap-trash"),
);
// …and refuses non-node_modules layouts (dev checkout, packed tarball).
assert.strictEqual(trashDirFromLauncher(path.join(NPM_PREFIX, "..", "git", "kcap", "npm", "kcap", "bin")), null);

// filterKcapProcesses: keeps only processes under the install root
// (case-insensitive), tolerates a bare object (ConvertTo-Json unwraps
// single-element arrays), null entries, and missing ExecutablePath.
const installRoot = process.platform === "win32"
  ? "C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@kurrent"
  : "/home/u/.npm-global/node_modules/@kurrent";
const exePath = path.join(installRoot, "kcap", "node_modules", "@kurrent", "kcap-win-x64", "bin", "kcap.exe");
const foreignExePath = process.platform === "win32" ? "C:\\other\\kcap.exe" : "/other/kcap.exe";
assert.deepStrictEqual(
  filterKcapProcesses(
    [
      { ProcessId: 11, ExecutablePath: exePath.toUpperCase(), CommandLine: `"${exePath}" mcp sessions` },
      { ProcessId: 22, ExecutablePath: foreignExePath, CommandLine: "kcap.exe mcp review" }, // foreign install
      { ProcessId: 33 },       // no path (access denied)
      null,                    // defensive
    ],
    installRoot,
  ),
  [{ pid: 11, name: "KCAP.EXE", role: "MCP server — open Claude Code/agent session" }],
);
assert.deepStrictEqual(
  filterKcapProcesses({ ProcessId: 7, ExecutablePath: exePath, CommandLine: `"${exePath}" hook --claude` }, installRoot),
  [{ pid: 7, name: "kcap.exe", role: "kcap process" }],
);
assert.deepStrictEqual(filterKcapProcesses(null, installRoot), []);

// describeRole: daemon beats mcp (the daemon exe name matches first), mcp
// detected as a word, everything else generic.
assert.strictEqual(describeRole("C:\\x\\kcap-daemon.exe run --name main"), "daemon");
assert.strictEqual(describeRole("kcap.exe daemon status"), "daemon");
assert.strictEqual(describeRole("kcap.exe mcp memory"), "MCP server — open Claude Code/agent session");
assert.strictEqual(describeRole("kcap.exe watch"), "kcap process");

// restoreMoved: successful restores are reported as no failures; restore
// failures are surfaced so users can recover files left in trash manually.
{
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "kcap-restore-test-"));
  try {
    const from = path.join(dir, "bin", "kcap.exe");
    const to = path.join(dir, ".kcap-trash", "kcap.exe.1");
    fs.mkdirSync(path.dirname(from), { recursive: true });
    fs.mkdirSync(path.dirname(to), { recursive: true });
    fs.writeFileSync(to, "exe");

    assert.deepStrictEqual(restoreMoved([{ from, to }]), []);
    assert.strictEqual(fs.readFileSync(from, "utf8"), "exe");
    assert.strictEqual(fs.existsSync(to), false);

    const missing = path.join(dir, ".kcap-trash", "missing.exe");
    const errors = [];
    const originalError = console.error;
    console.error = (message) => errors.push(String(message));
    try {
      const failures = restoreMoved([{ from: path.join(dir, "bin", "missing.exe"), to: missing }]);
      assert.strictEqual(failures.length, 1);
    } finally {
      console.error = originalError;
    }
    assert(errors.some((m) => m.includes("Could not restore one or more kcap binaries")));
    assert(errors.some((m) => m.includes(".kcap-trash")));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

// runUpdate's post-refresh version report to the server: `execFileSync` is destructured from
// `child_process` at module load (`const { execFileSync, spawnSync } = require("child_process")`),
// so there is no seam to intercept it short of restructuring the module or adding a mocking
// dependency this package doesn't otherwise carry (no proxyquire/sinon/jest here — see
// kcap.test.js's existing style, plain node:assert only). Actually running `runUpdate` would mean
// actually running `npm install -g` — unacceptable in a unit test. So this asserts the SOURCE
// shape instead: the call exists, is scoped to the exact command this fix depends on, is guarded
// by its own try/catch (a throw here must never fail `kcap update`), and sits after the refresh
// step succeeds and before runUpdate's own process.exit(0) — matching the design: report the new
// version only once the update itself is done, and never let that report block or fail the exit.
{
  const src = fs.readFileSync(path.join(__dirname, "kcap.js"), "utf8");

  const updateStart = src.indexOf("function runUpdate(");
  assert(updateStart >= 0, "expected a runUpdate function in kcap.js");
  const updateEnd = src.indexOf("\nfunction ", updateStart + 1);
  const runUpdateBody = src.slice(updateStart, updateEnd >= 0 ? updateEnd : undefined);

  const refreshIdx = runUpdateBody.indexOf("runRefreshes(");
  const reportIdx  = runUpdateBody.indexOf('execFileSync(binaryPath, ["report-version", "--no-update-check"]');
  const exitIdx    = runUpdateBody.lastIndexOf("process.exit(0)");

  assert(refreshIdx >= 0, "expected runUpdate to call runRefreshes(...)");
  assert(reportIdx >= 0, "expected runUpdate to spawn the new binary's report-version, --no-update-check");
  assert(exitIdx >= 0, "expected runUpdate to still exit 0 on its success path");
  assert(refreshIdx < reportIdx, "report-version must be spawned AFTER the refresh step");
  assert(reportIdx < exitIdx, "report-version must be spawned BEFORE runUpdate's process.exit(0)");

  // The report-version call must be inside its own try/catch (the closest preceding `try {`
  // ahead of any intervening `catch` — i.e. no catch closes it before the call itself appears).
  const precedingSlice  = runUpdateBody.slice(0, reportIdx);
  const lastTryIdx      = precedingSlice.lastIndexOf("try {");
  const lastCatchIdx    = precedingSlice.lastIndexOf("} catch");
  assert(lastTryIdx >= 0 && lastTryIdx > lastCatchIdx,
    "report-version must be guarded by its own try/catch — a throw here must never fail `kcap update`");
}

console.log("ok");
