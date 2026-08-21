#!/usr/bin/env node

// Resolves and exec's the native kcap binary for the current platform.

const { execFileSync, spawnSync } = require("child_process");
const path = require("path");
const fs = require("fs");
// Platform detection + native-binary resolution live in resolve.js — a
// side-effect-free module shared with refresh.js (which must never require
// THIS file: during `kcap update`, runUpdate executes before the final
// module.exports below, so it would see partial exports).
const { PLATFORM_PACKAGES, platformKey } = require("./resolve");

// Resolves the npm dist-tag to install for `kcap update`, based on the
// resolved channel reported by `kcap update --check`'s `install_tag` field.
// Falls back to "latest" when the probe is missing, failed, or has no tag,
// preserving today's default behavior.
function resolveInstallSpec(info) {
  const tag = info && typeof info.install_tag === "string" && info.install_tag
    ? info.install_tag
    : "latest";
  return `@kurrent/kcap@${tag}`;
}

// Builds the arg list for the `kcap update --check` probe, forwarding only
// the channel-switch flags (`--beta`/`--stable`) from the user's `kcap update`
// invocation. Nothing else is forwarded — the probe already supplies `--check`
// and `--no-update-check` itself, so this is a controlled call.
function probeArgs(updArgs) {
  const channelFlags = (updArgs || []).filter((a) => a === "--beta" || a === "--stable");
  return ["update", "--check", "--no-update-check", ...channelFlags];
}

// ── Windows binary-lock handling ────────────────────────────────────────────
//
// Windows locks an executing image against overwrite/delete for the process
// lifetime, and kcap has long-lived native processes: MCP servers (four per
// open Claude Code session) and the daemon. A plain `npm install -g` therefore
// dies with EBUSY whenever any agent session is open. But Windows DOES allow
// renaming/moving a running image within a volume — so `kcap update` moves the
// exes into a trash directory first (rename-aside), lets npm lay down fresh
// files at the real path, and sweeps the trash on later launcher runs once the
// old processes have exited. Running processes keep executing the moved image
// untouched and pick up the new binary on their next start.

const TRASH_DIR_NAME = ".kcap-trash";

// Trash lives next to node_modules (e.g. %APPDATA%\npm\.kcap-trash): renaming
// a running image requires the destination to be on the same volume, and a
// sibling of the install tree guarantees that.
function trashDirFor(globalRoot) {
  return path.join(globalRoot, "..", TRASH_DIR_NAME);
}

// Trash dir as seen from this launcher file, without shelling out to
// `npm root -g` (too slow for the hook hot path). Returns null when the
// launcher isn't laid out as a node_modules install.
function trashDirFromLauncher(launcherDir) {
  // <root>\node_modules\@kurrent\kcap\bin → <root>\.kcap-trash
  const nodeModules = path.resolve(launcherDir, "..", "..", "..");
  if (path.basename(nodeModules) !== "node_modules") return null;
  return path.join(path.dirname(nodeModules), TRASH_DIR_NAME);
}

// Best-effort reclaim of exes parked by earlier updates. Deleting a file whose
// process is still running fails; it just stays for a later sweep.
function sweepTrash(trashDir) {
  if (!trashDir) return;
  let entries;
  try { entries = fs.readdirSync(trashDir); } catch { return; }
  for (const f of entries) {
    try { fs.unlinkSync(path.join(trashDir, f)); } catch {}
  }
}

// Move the native exes out of the install tree so npm can replace them.
// Returns the performed moves so a failed install can restore them. Throws if
// even a rename is refused (something holds the file exclusively) — after
// first undoing any move it already made, so a partial failure never leaves
// the install half-emptied.
function renameAsideBinaries(binDir, trashDir) {
  const moved = [];
  for (const name of ["kcap.exe", "kcap-daemon.exe"]) {
    const src = path.join(binDir, name);
    if (!fs.existsSync(src)) continue;
    try {
      fs.mkdirSync(trashDir, { recursive: true });
      const dest = path.join(trashDir, `${name}.${process.pid}.${Date.now()}`);
      fs.renameSync(src, dest);
      moved.push({ from: src, to: dest });
    } catch (e) {
      restoreMoved(moved);
      throw e;
    }
  }
  return moved;
}

// Undo rename-aside after a failed install, so the user isn't left without a
// binary. Skips any path npm already managed to replace.
function restoreMoved(moved) {
  const failures = [];
  for (const m of moved) {
    try {
      if (!fs.existsSync(m.from)) fs.renameSync(m.to, m.from);
    } catch (e) {
      failures.push({ ...m, error: e });
    }
  }

  if (failures.length) {
    console.error("Could not restore one or more kcap binaries after a failed update:");
    for (const f of failures) {
      console.error(`  ${f.to} -> ${f.from}: ${f.error.message}`);
    }
    console.error("You may need to move the files back manually from the .kcap-trash directory.");
  }

  return failures;
}

// Pure helper (unit-tested): shape a Win32_Process JSON payload into the kcap
// processes running from the given install root. ConvertTo-Json unwraps
// single-element arrays, so `parsed` may be a bare object.
function filterKcapProcesses(parsed, installRoot) {
  const list = Array.isArray(parsed) ? parsed : parsed ? [parsed] : [];
  const root = installRoot.toLowerCase();
  return list
    .filter((p) => p && typeof p.ExecutablePath === "string"
      && p.ExecutablePath.toLowerCase().startsWith(root))
    .map((p) => ({
      pid: p.ProcessId,
      name: path.basename(p.ExecutablePath),
      role: describeRole(p.CommandLine || p.ExecutablePath || ""),
    }));
}

// Pure helper (unit-tested): human label for what a kcap process is, from its
// command line. MCP servers are the common locker — one per MCP entry in the
// kcap Claude Code plugin, so four per open session.
function describeRole(commandLine) {
  if (/kcap-daemon/i.test(commandLine)) return "daemon";
  if (/\bmcp\b/i.test(commandLine)) return "MCP server — open Claude Code/agent session";
  if (/\bdaemon\b/i.test(commandLine)) return "daemon";
  return "kcap process";
}

// Enumerate running kcap processes under the install tree (Windows only).
// Returns null when enumeration itself fails, so callers can degrade to a
// generic message.
function listKcapProcesses(installRoot) {
  try {
    const script =
      "Get-CimInstance Win32_Process -Filter \"Name='kcap.exe' OR Name='kcap-daemon.exe'\" | " +
      "Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress";
    const out = execFileSync("powershell.exe",
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { encoding: "utf8", windowsHide: true });
    return filterKcapProcesses(out.trim() ? JSON.parse(out) : [], installRoot);
  } catch {
    return null;
  }
}

// Actionable diagnosis for a locked binary: name the processes holding it and
// say what to do, instead of leaving the user with npm's raw EBUSY. With
// `onlyIfFound` (the npm-failure path, where the cause may be unrelated —
// network, registry) it stays silent unless lockers are actually found.
function printLockedBinaryHelp(globalRoot, onlyIfFound = false) {
  const procs = listKcapProcesses(path.join(globalRoot, "@kurrent"));
  if (procs && procs.length) {
    console.error("The kcap binary is locked by running kcap processes:");
    for (const p of procs) {
      console.error(`  PID ${String(p.pid).padEnd(6)} ${p.name}  (${p.role})`);
    }
    console.error("MCP servers belong to open Claude Code / agent sessions. Close those");
    console.error("sessions (and stop the daemon, if running), then re-run `kcap update`.");
  } else if (!onlyIfFound) {
    console.error("The kcap binary appears to be locked by a running process (open");
    console.error("Claude Code sessions run kcap MCP servers; the daemon also counts).");
    console.error("Close agent sessions and stop the daemon, then re-run `kcap update`.");
  }
}

// Everything below actually DOES something (resolves the binary, execs it,
// runs the update flow), so it's guarded to only run when this file is
// executed directly — not when `require()`d (e.g. by the test).
if (require.main === module) {
  const key = platformKey();
  const packageName = PLATFORM_PACKAGES[key];

  if (!packageName) {
    console.error(`Unsupported platform: ${key}`);
    console.error(`Supported: ${Object.keys(PLATFORM_PACKAGES).join(", ")}`);
    process.exit(1);
  }

  // Resolve the platform package
  let binaryDir;
  try {
    binaryDir = path.dirname(require.resolve(`${packageName}/package.json`));
  } catch {
    console.error(`Platform package ${packageName} is not installed.`);
    console.error(`Try: npm install -g @kurrent/kcap`);
    process.exit(1);
  }

  const ext = process.platform === "win32" ? ".exe" : "";
  const binaryPath = path.join(binaryDir, "bin", `kcap${ext}`);

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

  // Reclaim exes parked by a previous rename-aside update, now that their
  // processes may have exited. Cheap when there's nothing to do (one stat).
  if (process.platform === "win32") {
    sweepTrash(trashDirFromLauncher(__dirname));
  }

  // `kcap update` for npm-global installs is driven HERE, in the Node launcher,
  // not by the native binary. The OS locks an executable image for the whole
  // process lifetime but a script only during load — so with the short-lived
  // probe exited, npm can overwrite the binary in place on macOS/Linux; on
  // Windows the long-lived MCP/daemon processes are handled by rename-aside
  // (see the binary-lock section above).
  // `--check` (JSON probe) and `--help` fall through to the native binary.
  {
    const updArgs   = process.argv.slice(3);
    const checkOnly = updArgs.includes("--check");
    const wantsHelp = updArgs.some((a) => a === "--help" || a === "-h");
    if (process.argv[2] === "update" && !checkOnly && !wantsHelp) {
      runUpdate(binaryPath, updArgs); // never returns
    }
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
}

// Performs `kcap update`: upgrade the global npm package, then refresh
// user-scope skills/hooks. Always exits the process; never returns.
function runUpdate(binaryPath, updArgs) {
  // On Windows, `npm` is a .cmd shim; Node refuses to spawn .cmd/.bat directly
  // (2024 command-injection fix), so route npm through a shell there. Our npm
  // args are static, so shell use is safe.
  const npmOpts = process.platform === "win32" ? { shell: true } : {};

  // Resolve npm's global root so we can (a) confirm this is a global install we
  // own and (b) check we can write to it before starting.
  let globalRoot = null;
  try {
    globalRoot = execFileSync("npm", ["root", "-g"], { encoding: "utf8", ...npmOpts }).trim();
  } catch {}

  let realGlobalRoot = null;
  let isGlobalNpm = false;
  try {
    if (globalRoot) {
      realGlobalRoot = fs.realpathSync(globalRoot);
      const root = realGlobalRoot + path.sep;
      isGlobalNpm = fs.realpathSync(__filename).startsWith(root);
    }
  } catch {}

  if (!isGlobalNpm) {
    console.error("kcap was not installed via a global npm install, so it can't");
    console.error("self-update. Update it the way you installed it, e.g.:");
    console.error("  npm install -g @kurrent/kcap@latest   (npm)");
    console.error("  brew upgrade kcap                      (Homebrew)");
    process.exit(1);
  }

  // Ask the native binary whether a newer version exists. This short-lived
  // child fully EXITS before we run npm, so the binary file is unlocked when
  // npm overwrites it (matters on Windows). `--no-update-check` keeps the
  // background nudge from racing onto stderr.
  let info = null;
  try {
    const out  = execFileSync(binaryPath, probeArgs(updArgs), { encoding: "utf8" });
    // The probe prints one JSON line; take the last {...} line in case anything
    // else (e.g. a git-remote warning) landed on stdout first.
    const line = out.split(/\r?\n/).reverse().find((l) => l.trim().startsWith("{"));
    info = JSON.parse(line);
    // Only short-circuit when the probe is CONFIDENT we're up to date: `newer`
    // explicitly false AND a known current version. `newer: null` (version
    // unknown / check failed) falls through to the upgrade rather than stranding
    // the user on a stale CLI.
    if (info && info.newer === false && info.current) {
      console.log(`Already up to date: ${info.current}`);
      process.exit(0);
    }
    if (info && info.newer) {
      console.log(`Updating kcap: ${info.current} → ${info.latest}`);
    }
  } catch {
    // Couldn't determine — fall through and let npm decide (it's idempotent).
  }

  // Detect a running daemon up front (short-lived child; it exits before npm
  // runs). Used only for the post-update notice — the update itself no longer
  // needs the daemon stopped on any platform. `--no-update-check` keeps this
  // child's "update available" nudge from landing mid-update.
  let daemonRunning = false;
  try {
    const status = execFileSync(binaryPath, ["daemon", "status", "--no-update-check"], { encoding: "utf8" });
    daemonRunning = /running \(PID/i.test(status);
  } catch {
    // status probe failed (no daemon / old binary) — treat as not running.
  }

  // Fail clearly instead of half-installing under a root-owned prefix.
  try {
    fs.accessSync(realGlobalRoot, fs.constants.W_OK);
  } catch {
    console.error(`Cannot write to the global npm directory (${globalRoot}).`);
    console.error("Re-run with the permissions you installed kcap with, or reinstall");
    console.error("to a user-owned prefix to avoid needing elevated rights on updates.");
    process.exit(1);
  }

  // Windows: move the (possibly executing) exes out of npm's way. See the
  // binary-lock section above for why rename works when overwrite doesn't.
  let moved = [];
  if (process.platform === "win32") {
    try {
      moved = renameAsideBinaries(path.dirname(binaryPath), trashDirFor(realGlobalRoot));
    } catch (e) {
      // Even a rename was refused — something holds the file exclusively
      // (AV scan, exclusive open). renameAsideBinaries already rolled back its
      // partial moves; diagnose instead of letting npm EBUSY.
      console.error(`Could not move the current kcap binary aside: ${e.message}`);
      printLockedBinaryHelp(realGlobalRoot);
      process.exit(1);
    }
  }

  const res = spawnSync("npm", ["install", "-g", resolveInstallSpec(info)], {
    stdio: "inherit",
    windowsHide: true,
    ...npmOpts,
  });
  if (res.status !== 0) {
    restoreMoved(moved);
    console.error("npm install failed; kcap was not updated.");
    if (process.platform === "win32") printLockedBinaryHelp(realGlobalRoot, /* onlyIfFound */ true);
    process.exit(res.status == null ? 1 : res.status);
  }

  // npm has now overwritten kcap.js, refresh.js, and the binary. require()
  // reads the NEW refresh.js, which spawns the NEW launcher -> NEW binary.
  console.log("Refreshing hooks and skills…");
  require("./refresh").runRefreshes(fs.realpathSync(__filename));
  console.log("kcap updated.");

  // Tell the server about the new version now, so the "CLI out of date"
  // banner clears immediately instead of waiting for the next request.
  try {
    execFileSync(binaryPath, ["report-version", "--no-update-check"], { stdio: "ignore", timeout: 8000 });
  } catch {
    // fire-and-forget: the server will observe on the next request anyway
  }

  // A running daemon keeps executing the old image until restarted. On
  // macOS/Linux it self-detects the new binary and restarts when idle; on
  // Windows self-detection is off (the running image was moved, not replaced),
  // so the user applies it explicitly.
  if (daemonRunning) {
    if (process.platform === "win32") {
      console.log("A kcap daemon is running and still uses the old version. Apply the");
      console.log("update with `kcap daemon restart` (add --force if agents are running).");
    } else {
      console.log("A kcap daemon is running; it will restart automatically when idle to");
      console.log("pick up the new version. Check with `kcap daemon status`, or apply now");
      console.log("with `kcap daemon restart --force`.");
    }
  }

  process.exit(0);
}

module.exports = {
  resolveInstallSpec,
  probeArgs,
  trashDirFor,
  trashDirFromLauncher,
  filterKcapProcesses,
  describeRole,
  restoreMoved,
};
