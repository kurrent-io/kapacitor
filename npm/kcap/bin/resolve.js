// Side-effect-free platform/native-binary resolution, shared by kcap.js (the
// launcher) and refresh.js (postinstall / `kcap update` refresh).
//
// This lives in its OWN module — refresh.js must never require kcap.js: during
// `kcap update`, runUpdate executes BEFORE kcap.js's final module.exports
// assignment, so a refresh.js → kcap.js require would observe partial exports
// exactly on that path. Requiring this module has no side effects.

const path = require("path");
const fs = require("fs");

function isMusl() {
  if (process.platform !== "linux") return false;
  try {
    return fs.readFileSync("/usr/bin/ldd", "utf8").includes("musl");
  } catch {
    try {
      return fs.readdirSync("/lib").some((f) => f.startsWith("ld-musl-"));
    } catch {
      return false;
    }
  }
}

const PLATFORM_PACKAGES = {
  "darwin-arm64":      "@kurrent/kcap-darwin-arm64",
  "linux-x64":         "@kurrent/kcap-linux-x64",
  "linux-arm64":       "@kurrent/kcap-linux-arm64",
  "linux-musl-x64":    "@kurrent/kcap-linux-musl-x64",
  "linux-musl-arm64":  "@kurrent/kcap-linux-musl-arm64",
  "win32-x64":         "@kurrent/kcap-win-x64",
};

function platformKey() {
  const musl = process.platform === "linux" && isMusl() ? "-musl" : "";
  return `${process.platform}${musl}-${process.arch}`;
}

// Absolute path to the native kcap binary for the current platform, or null
// when the platform is unsupported, the platform package isn't installed, or
// the binary file is missing. `basedir` (optional) overrides where module
// resolution starts — tests point it at a fake package layout; callers inside
// the real install tree omit it (resolution then starts from this file).
function resolveNativeBinary(basedir) {
  const packageName = PLATFORM_PACKAGES[platformKey()];
  if (!packageName) return null;

  let binaryDir;
  try {
    const opts = basedir ? { paths: [basedir] } : undefined;
    binaryDir = path.dirname(require.resolve(`${packageName}/package.json`, opts));
  } catch {
    return null;
  }

  const ext = process.platform === "win32" ? ".exe" : "";
  const binaryPath = path.join(binaryDir, "bin", `kcap${ext}`);
  return fs.existsSync(binaryPath) ? binaryPath : null;
}

module.exports = { PLATFORM_PACKAGES, isMusl, platformKey, resolveNativeBinary };
