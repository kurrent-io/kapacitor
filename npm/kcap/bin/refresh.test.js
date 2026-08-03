const assert = require("node:assert");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

// ── require-order regression ─────────────────────────────────────────────────
// refresh.js is required by runUpdate BEFORE kcap.js's final module.exports
// assignment, so it must never require kcap.js (it would see partial exports).
// This require runs first in this process; the cache proves the independence.
const {
  patchPluginMcpConfig,
  patchPluginMcpServers,
  isPatchableKcapCommand,
} = require("./refresh.js");
assert(
  !Object.keys(require.cache).some((k) => k.endsWith(`${path.sep}kcap.js`)),
  "refresh.js must not (transitively) require kcap.js",
);

// The REAL shipped plugin config — the release workflow copies the repo's
// kcap/ dir into the wrapper package verbatim, so testing against this file
// keeps the fixture honest.
const shippedMcpJson = path.join(__dirname, "..", "..", "..", "kcap", ".mcp.json");
assert(fs.existsSync(shippedMcpJson), `expected the shipped plugin config at ${shippedMcpJson}`);

const BINARY = process.platform === "win32"
  ? "C:\\npm\\node_modules\\@kurrent\\kcap-win-x64\\bin\\kcap.exe"
  : "/opt/npm/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap";

// Builds the released package layout (bin/ + sibling kcap/ plugin dir) in a
// temp root — a plain `npm pack` can't produce it, release.yml assembles it.
// Note the space in the directory name: the patch must survive spaced prefixes.
function makeFakePackage(mcpJsonContent) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "kcap refresh test-"));
  const binDir = path.join(root, "node_modules", "@kurrent", "kcap", "bin");
  const pluginDir = path.join(root, "node_modules", "@kurrent", "kcap", "kcap");
  fs.mkdirSync(binDir, { recursive: true });
  fs.mkdirSync(pluginDir, { recursive: true });
  const launcher = path.join(binDir, "kcap.js");
  fs.writeFileSync(launcher, "// fake launcher\n");
  if (mcpJsonContent !== null) {
    fs.writeFileSync(path.join(pluginDir, ".mcp.json"), mcpJsonContent);
  }
  return { root, launcher, mcpJson: path.join(pluginDir, ".mcp.json") };
}

function withCapturedWarnings(body) {
  const warnings = [];
  const original = console.warn;
  console.warn = (m) => warnings.push(String(m));
  try {
    body();
  } finally {
    console.warn = original;
  }
  return warnings;
}

// ── isPatchableKcapCommand ───────────────────────────────────────────────────
assert.strictEqual(isPatchableKcapCommand("kcap"), true);
assert.strictEqual(isPatchableKcapCommand("/old/prefix/bin/kcap"), true);          // stale POSIX path
assert.strictEqual(isPatchableKcapCommand("C:\\old\\npm\\bin\\kcap.exe"), true);   // stale Windows path
assert.strictEqual(isPatchableKcapCommand("/old/prefix/bin/KCAP.EXE"), true);      // case-insensitive exe
assert.strictEqual(isPatchableKcapCommand("npx"), false);                          // foreign command
assert.strictEqual(isPatchableKcapCommand("/usr/bin/other-tool"), false);          // foreign path
assert.strictEqual(isPatchableKcapCommand("relative/kcap"), false);                // not absolute
assert.strictEqual(isPatchableKcapCommand("/opt/kcap-wrapper"), false);            // basename must BE kcap
assert.strictEqual(isPatchableKcapCommand(undefined), false);
assert.strictEqual(isPatchableKcapCommand(["kcap"]), false);

// ── happy path: the real shipped config, all six entries patched ────────────
{
  const { root, launcher, mcpJson } = makeFakePackage(fs.readFileSync(shippedMcpJson, "utf8"));
  try {
    const before = JSON.parse(fs.readFileSync(mcpJson, "utf8"));
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher, BINARY));
    assert.deepStrictEqual(warnings, []);

    const after = JSON.parse(fs.readFileSync(mcpJson, "utf8"));
    const names = Object.keys(after.mcpServers);
    assert.strictEqual(names.length, 6);
    for (const name of names) {
      assert.strictEqual(after.mcpServers[name].command, BINARY, `${name} should point at the native binary`);
      // Everything except command is preserved verbatim.
      const { command: _a, ...restAfter } = after.mcpServers[name];
      const { command: _b, ...restBefore } = before.mcpServers[name];
      assert.deepStrictEqual(restAfter, restBefore, `${name} must only have its command rewritten`);
    }

    // Idempotence: a second run leaves the file byte-identical.
    const patched = fs.readFileSync(mcpJson, "utf8");
    patchPluginMcpConfig(launcher, BINARY);
    assert.strictEqual(fs.readFileSync(mcpJson, "utf8"), patched);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── stale absolute path from a previous install is re-patched ───────────────
{
  const stale = process.platform === "win32" ? "C:\\old\\npm\\bin\\kcap.exe" : "/old/prefix/bin/kcap";
  const cfg = { mcpServers: { "kcap-review": { command: stale, args: ["mcp", "review"] } } };
  assert.strictEqual(patchPluginMcpServers(cfg, BINARY), true);
  assert.strictEqual(cfg.mcpServers["kcap-review"].command, BINARY);
}

// ── customized entries are preserved (command or args diverge) ──────────────
{
  const cfg = {
    mcpServers: {
      "kcap-review":   { command: "/usr/bin/my-own-tool", args: ["mcp", "review"] }, // foreign command
      "kcap-sessions": { command: "kcap", args: ["mcp", "sessions", "--extra"] },    // extra arg
      "kcap-flows":    { command: "kcap", args: ["mcp", "memory"] },                 // wrong args for name
      "not-kcap":      { command: "kcap", args: ["mcp", "review"] },                 // unknown name
      "kcap-memory":   { command: "kcap", args: ["mcp", "memory"] },                 // canonical → patched
    },
  };
  assert.strictEqual(patchPluginMcpServers(cfg, BINARY), true);
  assert.strictEqual(cfg.mcpServers["kcap-review"].command, "/usr/bin/my-own-tool");
  assert.strictEqual(cfg.mcpServers["kcap-sessions"].command, "kcap");
  assert.strictEqual(cfg.mcpServers["kcap-flows"].command, "kcap");
  assert.strictEqual(cfg.mcpServers["not-kcap"].command, "kcap");
  assert.strictEqual(cfg.mcpServers["kcap-memory"].command, BINARY);
}

// ── malformed input: ONE warning, file untouched ─────────────────────────────
{
  const { root, launcher, mcpJson } = makeFakePackage("{ not json at all");
  try {
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher, BINARY));
    assert.strictEqual(warnings.length, 1, `expected exactly one warning, got: ${warnings}`);
    assert(warnings[0].includes("Claude plugin MCP entries"));
    assert.strictEqual(fs.readFileSync(mcpJson, "utf8"), "{ not json at all");
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── wrong shape (mcpServers not an object): ONE warning, file untouched ─────
{
  const original = JSON.stringify({ mcpServers: [1, 2] });
  const { root, launcher, mcpJson } = makeFakePackage(original);
  try {
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher, BINARY));
    assert.strictEqual(warnings.length, 1);
    assert.strictEqual(fs.readFileSync(mcpJson, "utf8"), original);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── missing .mcp.json (dev checkout): silent no-op ───────────────────────────
{
  const { root, launcher } = makeFakePackage(null);
  try {
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher, BINARY));
    assert.deepStrictEqual(warnings, []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── no resolvable binary: silent no-op, file untouched ──────────────────────
{
  const original = fs.readFileSync(shippedMcpJson, "utf8");
  const { root, launcher, mcpJson } = makeFakePackage(original);
  try {
    // No platform package exists in this fake layout, and resolve.js's default
    // resolution (from the repo) finds none either → null → leave the wrapper.
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher));
    assert.deepStrictEqual(warnings, []);
    assert.strictEqual(fs.readFileSync(mcpJson, "utf8"), original);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── unwritable plugin dir: ONE warning, original preserved (POSIX only) ─────
if (process.platform !== "win32" && typeof process.getuid === "function" && process.getuid() !== 0) {
  const original = fs.readFileSync(shippedMcpJson, "utf8");
  const { root, launcher, mcpJson } = makeFakePackage(original);
  const pluginDir = path.dirname(mcpJson);
  try {
    fs.chmodSync(pluginDir, 0o555);
    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(launcher, BINARY));
    assert.strictEqual(warnings.length, 1, `expected exactly one warning, got: ${warnings}`);
    assert.strictEqual(fs.readFileSync(mcpJson, "utf8"), original);
    // No temp litter left behind next to the config.
    assert.deepStrictEqual(fs.readdirSync(pluginDir), [".mcp.json"]);
  } finally {
    fs.chmodSync(pluginDir, 0o755);
    fs.rmSync(root, { recursive: true, force: true });
  }
}

// ── symlinked prefix: patch through a symlink resolves and writes fine ───────
if (process.platform !== "win32") {
  const { root, mcpJson } = makeFakePackage(fs.readFileSync(shippedMcpJson, "utf8"));
  try {
    const link = path.join(root, "linked-prefix");
    fs.symlinkSync(path.join(root, "node_modules"), link);
    const linkedLauncher = path.join(link, "@kurrent", "kcap", "bin", "kcap.js");

    const warnings = withCapturedWarnings(() => patchPluginMcpConfig(linkedLauncher, BINARY));
    assert.deepStrictEqual(warnings, []);
    const after = JSON.parse(fs.readFileSync(mcpJson, "utf8"));
    assert.strictEqual(after.mcpServers["kcap-review"].command, BINARY);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

console.log("ok");
