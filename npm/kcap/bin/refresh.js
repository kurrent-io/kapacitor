// Refreshes kcap's user-scope coding-agent installs so users pick up new or updated
// skills, Codex/Cursor/Copilot hook commands, the Pi live-ingest extension, and
// Claude plugin registration.
//
// Shared by:
// - postinstall.js — runs after `npm install -g @kurrent/kcap` (incl. upgrades),
//   when the package manager allows install scripts to run.
// - kcap.js `update` — runs after a user-initiated `kcap update`, which works
//   even when the package manager blocks postinstall scripts.
//
// This file must never require kcap.js: during `kcap update`, runUpdate executes
// before kcap.js's final module.exports assignment, so it would observe partial
// exports. Shared platform/binary resolution lives in resolve.js instead.

const { spawnSync } = require("child_process");
const path = require("path");
const fs = require("fs");

// One entry per agent. Order is independent — each refresh is gated by its own
// marker via `--if-installed`, which no-ops unless the user has previously
// opted in (marker file present OR pre-marker install detected). A vendor entry
// also refreshes the shared ~/.agents/skills tree, but only tops up one that is
// already there: `plugin remove --skills` must survive an upgrade.
const REFRESHES = [
  ["plugin", "install", "--skills",  "--if-installed"],
  ["plugin", "install", "--codex",   "--if-installed"],
  ["plugin", "install", "--cursor",  "--if-installed"],
  ["plugin", "install", "--copilot", "--if-installed"],
  ["plugin", "install", "--pi",      "--if-installed"], // Pi extension (~/.pi/agent/extensions/kcap.ts)
  ["plugin", "install", "--opencode", "--if-installed"], // OpenCode plugin (~/.config/opencode/plugins/kcap.ts)
  ["plugin", "install",              "--if-installed"], // Claude
];

// Runs each refresh via the given launcher (an absolute path to kcap.js).
// Each refresh runs independently: a failure, timeout, or unexpected exit code
// from one never prevents the others and never throws to the caller — a failed
// refresh must never break `npm install` or report `kcap update` as failed.
function runRefreshes(launcherPath) {
  // Point the shipped Claude plugin's MCP entries at the native binary. Runs
  // here because runRefreshes fires at exactly the two moments the plugin dir
  // has just been (re)written by npm: postinstall and `kcap update`.
  patchPluginMcpConfig(launcherPath);

  for (const argv of REFRESHES) {
    try {
      spawnSync(process.execPath, [launcherPath, ...argv], {
        stdio: "ignore",
        env: process.env,
        // Hard ceiling so a stalled child can never hang the caller.
        timeout: 60_000,
        killSignal: "SIGKILL",
        windowsHide: true,
      });
    } catch {
      // Never fail the caller.
    }
  }
}

// ── Claude plugin .mcp.json patch ───────────────────────────────────────────
//
// The plugin ships `"command": "kcap"`, which resolves to the node wrapper —
// one resident Node runtime per MCP server per open Claude session (~25 MB
// idle each). Rewriting the command to the resolved native binary makes Claude
// spawn the binary directly. Best-effort: on any failure the shipped value
// stays in place, which still works via the wrapper.

// The servers shipped in the plugin's .mcp.json, keyed by canonical
// `["mcp", <suffix>]` args. Only these exact name/args pairs are patched —
// anything else in the file is preserved verbatim.
const PLUGIN_MCP_SERVERS = {
  "kcap-review":    "review",
  "kcap-sessions":  "sessions",
  "kcap-flows":     "flows",
  "kcap-memory":    "memory",
  "kcap-workitems": "workitems",
  "kcap-analytics": "analytics",
};

// A command this patcher may rewrite: the shipped literal "kcap", or an
// absolute path whose basename is the kcap binary (a previous patch that has
// gone stale after an npm re-layout). Handles both path flavors so a stale
// Windows path is still recognized. Anything else is a customization → keep.
function isPatchableKcapCommand(command) {
  if (command === "kcap") return true;
  if (typeof command !== "string") return false;
  if (!path.posix.isAbsolute(command) && !path.win32.isAbsolute(command)) return false;
  const base = command.split(/[\\/]/).pop().toLowerCase();
  return base === "kcap" || base === "kcap.exe";
}

// Pure: rewrites in place the canonical plugin entries of `cfg` whose command
// is patchable, returning whether anything changed. Throws on a shape that
// isn't the plugin config (caller turns that into the single warning).
function patchPluginMcpServers(cfg, binaryPath) {
  if (!cfg || typeof cfg !== "object" || Array.isArray(cfg)) {
    throw new Error("config is not an object");
  }
  const servers = cfg.mcpServers;
  if (!servers || typeof servers !== "object" || Array.isArray(servers)) {
    throw new Error("mcpServers is not an object");
  }

  let changed = false;
  for (const [name, suffix] of Object.entries(PLUGIN_MCP_SERVERS)) {
    const entry = servers[name];
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) continue;
    if (!Array.isArray(entry.args) || entry.args.length !== 2 ||
        entry.args[0] !== "mcp" || entry.args[1] !== suffix) continue; // customized args → keep
    if (!isPatchableKcapCommand(entry.command)) continue;              // customized command → keep
    if (entry.command === binaryPath) continue;                        // already current
    entry.command = binaryPath;
    changed = true;
  }
  return changed;
}

// Rewrites the plugin's shipped .mcp.json (a sibling `kcap/` dir of the
// launcher's `bin/`) so Claude spawns the native binary directly. Validates
// before writing, writes a sibling temp file, then renames atomically — the
// original survives any pre-rename failure. Never throws; on failure it emits
// ONE concise warning and leaves the shipped `"command": "kcap"` in place.
// `binaryPath` is a test seam; real callers let it resolve.
function patchPluginMcpConfig(launcherPath, binaryPath) {
  let mcpJsonPath;
  try {
    binaryPath = binaryPath || require("./resolve").resolveNativeBinary();
    if (!binaryPath) return; // unsupported platform / package missing — wrapper keeps working

    mcpJsonPath = path.join(path.dirname(launcherPath), "..", "kcap", ".mcp.json");
    if (!fs.existsSync(mcpJsonPath)) return; // dev checkout / plugin not shipped

    const cfg = JSON.parse(fs.readFileSync(mcpJsonPath, "utf8"));
    if (!patchPluginMcpServers(cfg, binaryPath)) return; // already patched / nothing canonical

    const tmp = `${mcpJsonPath}.tmp-${process.pid}-${Date.now()}`;
    try {
      fs.writeFileSync(tmp, JSON.stringify(cfg, null, 2) + "\n");
      fs.renameSync(tmp, mcpJsonPath);
    } catch (e) {
      try { fs.unlinkSync(tmp); } catch {}
      throw e;
    }
  } catch (e) {
    console.warn(
      `kcap: could not point the Claude plugin MCP entries at the native binary` +
      ` (${e && e.message ? e.message : e}); they will keep launching via the node wrapper.`,
    );
  }
}

module.exports = { runRefreshes, patchPluginMcpConfig, patchPluginMcpServers, isPatchableKcapCommand };
