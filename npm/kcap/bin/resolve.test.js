const assert = require("node:assert");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { PLATFORM_PACKAGES, platformKey, resolveNativeBinary } = require("./resolve.js");

// platformKey has the `<platform>[-musl]-<arch>` shape and covers the current
// process's values.
assert(platformKey().startsWith(process.platform));
assert(platformKey().endsWith(`-${process.arch}`));

// The supported-platform table is exactly the six shipped native packages.
assert.deepStrictEqual(Object.keys(PLATFORM_PACKAGES).sort(), [
  "darwin-arm64",
  "linux-arm64",
  "linux-musl-arm64",
  "linux-musl-x64",
  "linux-x64",
  "win32-x64",
]);

// resolveNativeBinary against a fake package layout. Only meaningful when the
// current platform is in the table (dev on an unsupported platform skips).
const packageName = PLATFORM_PACKAGES[platformKey()];
if (packageName) {
  // realpath: require.resolve reports canonical paths, and macOS's tmpdir is a
  // symlink (/var → /private/var).
  const root = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), "kcap-resolve-test-")));
  try {
    const pkgDir = path.join(root, "node_modules", ...packageName.split("/"));
    const ext = process.platform === "win32" ? ".exe" : "";
    const binaryPath = path.join(pkgDir, "bin", `kcap${ext}`);

    // Package not installed → null.
    assert.strictEqual(resolveNativeBinary(root), null);

    // Package present but the binary file missing → null.
    fs.mkdirSync(path.join(pkgDir, "bin"), { recursive: true });
    fs.writeFileSync(path.join(pkgDir, "package.json"), JSON.stringify({ name: packageName, version: "0.0.0" }));
    assert.strictEqual(resolveNativeBinary(root), null);

    // Binary present → its absolute path.
    fs.writeFileSync(binaryPath, "native");
    assert.strictEqual(resolveNativeBinary(root), binaryPath);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

console.log("ok");
