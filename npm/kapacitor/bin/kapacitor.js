#!/usr/bin/env node

// Resolves and exec's the native kapacitor binary for the current platform.

const { execFileSync } = require("child_process");
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
  "darwin-arm64":      "@kurrent/kapacitor-darwin-arm64",
  "linux-x64":         "@kurrent/kapacitor-linux-x64",
  "linux-arm64":       "@kurrent/kapacitor-linux-arm64",
  "linux-musl-x64":    "@kurrent/kapacitor-linux-musl-x64",
  "linux-musl-arm64":  "@kurrent/kapacitor-linux-musl-arm64",
  "win32-x64":         "@kurrent/kapacitor-win-x64",
};

const musl = process.platform === "linux" && isMusl() ? "-musl" : "";
const platformKey = `${process.platform}${musl}-${process.arch}`;
const packageName = PLATFORM_PACKAGES[platformKey];

if (!packageName) {
  console.error(`Unsupported platform: ${platformKey}`);
  console.error(`Supported: ${Object.keys(PLATFORM_PACKAGES).join(", ")}`);
  process.exit(1);
}

// Resolve the platform package
let binaryDir;
try {
  binaryDir = path.dirname(require.resolve(`${packageName}/package.json`));
} catch {
  console.error(`Platform package ${packageName} is not installed.`);
  console.error(`Try: npm install -g @kurrent/kapacitor`);
  process.exit(1);
}

const ext = process.platform === "win32" ? ".exe" : "";
const binaryPath = path.join(binaryDir, "bin", `kapacitor${ext}`);

if (!fs.existsSync(binaryPath)) {
  console.error(`Binary not found at ${binaryPath}`);
  process.exit(1);
}

// Ensure the binary is executable (npm doesn't always preserve permissions)
try {
  fs.accessSync(binaryPath, fs.constants.X_OK);
} catch {
  try { fs.chmodSync(binaryPath, 0o755); } catch {}
}

// Exec the native binary, replacing this process
try {
  execFileSync(binaryPath, process.argv.slice(2), {
    stdio: "inherit",
    env: process.env,
  });
  process.exit(0);
} catch (e) {
  if (e.status !== null) {
    process.exit(e.status);
  }
  process.exit(1);
}
