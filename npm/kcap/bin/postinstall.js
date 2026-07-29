#!/usr/bin/env node

// Runs after `npm install -g @kurrent/kcap` (including upgrades).
//
// Refreshes kcap's user-scope coding-agent installs so users pick up new or updated
// skills, hook commands, and Claude plugin registration without manually
// re-running `kcap setup`. The actual refresh logic lives in refresh.js, shared
// with the `kcap update` path.
//
// Contract:
// - Only runs on global installs. Skipping non-global installs avoids touching
//   ~/.agents/, ~/.codex/, ~/.cursor/, or ~/.claude/ during unrelated
//   local/transitive installs on already-opted-in machines.
// - Always exits 0 — a failed refresh must never break `npm install`.
//
// Note: modern package managers gate install scripts behind an "allowed
// scripts" allowlist, so this hook may not run on upgrade. `kcap update` runs
// the same refresh and is not subject to that gate (the user invokes it
// explicitly).

const path = require("path");

const isGlobal =
  process.env.npm_config_global === "true" ||
  process.env.npm_config_location === "global";

if (isGlobal) {
  require("./refresh").runRefreshes(path.join(__dirname, "kcap.js"));
}

process.exit(0);
