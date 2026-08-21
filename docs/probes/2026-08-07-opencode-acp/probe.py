#!/usr/bin/env python3
"""OpenCode ACP hosting probe: what does `opencode acp` actually support?

Answers the questions the hosted-agent and unattended-reviewer descriptors cannot be written
without. Every one of them is measured at EFFECT level, because the weaker signals available here
are all known to lie: an advertised `loadSession`/`mcpCapabilities` shape predicts nothing (Kiro and
Gemini advertise `{http,sse}` and both honour stdio; Copilot advertises the same and does not), and
a server that merely STARTS proves nothing about whether its tools are discoverable or callable.

Phases:
  free (default; ZERO model requests):
    A1 initialize                -> protocolVersion, agentCapabilities, authMethods
    A2 session/new {cwd, mcpServers: []} -> sessionId + any models/modes payload
    A3 model write half          -> session/set_model, then session/set_config_option
    A4 isolation levers          -> re-handshake under OPENCODE_CONFIG_DIR (empty, daemon-owned),
                                    OPENCODE_PURE=1 and OPENCODE_DISABLE_PROJECT_CONFIG=1, and
                                    report whether session/new still succeeds (i.e. whether
                                    credentials survive config isolation -- if they do not, a
                                    reviewer cannot be isolated this way at all)
    A5 stdio mcpServers ADMISSION -> session/new with a purpose-built stdio server; report whether
                                    the server process was spawned at all (necessary, NOT
                                    sufficient -- --turn is what proves callability)

  --turn (ONE model request per arm; requires credentials):
    B1 mcp call-level            -> ask the model to call the injected tool and report its nonce
    B2 permission posture        -> ask for a write, with and without OPENCODE_PERMISSION trust,
                                    and record every session/request_permission frame

Usage: python3 probe.py [--turn] [--arm ARM] [--outdir DIR]
"""

import argparse
import asyncio
import json
import os
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402  (reuse the proven ACP child driver)

OPENCODE = os.environ.get("KCAP_OPENCODE_PATH", "opencode")

# A stdio MCP server that exists only to be reached. It reports a nonce, so a passing arm proves
# the model saw a real tool RESULT -- not that a process started, and not that a tool was listed.
MCP_SERVER_JS = r"""
const fs = require("fs");
const LOG = process.env.PROBE_MCP_LOG;
const NONCE = process.env.PROBE_MCP_NONCE || "no-nonce";
function log(o) { try { fs.appendFileSync(LOG, JSON.stringify(o) + "\n"); } catch {} }
log({ t: new Date().toISOString(), event: "spawned", argv: process.argv, cwd: process.cwd() });
let buf = "";
process.stdin.on("data", (d) => {
  buf += d.toString();
  let i;
  while ((i = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    log({ t: new Date().toISOString(), event: "in", msg });
    const reply = (result) => {
      const out = JSON.stringify({ jsonrpc: "2.0", id: msg.id, result }) + "\n";
      process.stdout.write(out);
      log({ t: new Date().toISOString(), event: "out", result });
    };
    if (msg.method === "initialize") {
      reply({ protocolVersion: "2024-11-05", capabilities: { tools: {} },
              serverInfo: { name: "kcap-probe-mcp", version: "1.0.0" } });
    } else if (msg.method === "tools/list") {
      reply({ tools: [{ name: "probe_nonce",
                        description: "Returns the probe nonce. Call this when asked for the nonce.",
                        inputSchema: { type: "object", properties: {} } }] });
    } else if (msg.method === "tools/call") {
      reply({ content: [{ type: "text", text: "NONCE=" + NONCE }] });
    } else if (msg.id !== undefined) {
      reply({});
    }
  }
});
"""


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def write_mcp_server(outdir):
    p = outdir / "probe-mcp-server.cjs"
    p.write_text(MCP_SERVER_JS)
    return p


def model_options(session_new_frame):
    """(currentValue, a distinct target) from session/new's `configOptions`.

    Prefers a `-free` target so the model-effect turn arm costs nothing."""
    res = ((session_new_frame or {}).get("result") or {})
    for co in res.get("configOptions") or []:
        if co.get("id") != "model":
            continue
        cur = co.get("currentValue")
        values = [o.get("value") for o in (co.get("options") or []) if o.get("value")]
        free = [v for v in values if v != cur and v.endswith("-free")]
        other = [v for v in values if v != cur]
        return cur, (free[0] if free else (other[0] if other else None))
    return None, None


CAPTURE_DIR = Path.home() / ".cache" / "kcap" / "opencode"


def agent_text(frames):
    """Only what the MODEL said, concatenated.

    Searching the whole frame log instead is how the first revision of the model-effect arm scored
    BOTH the target and the previous model as 'named': session/set_config_option echoes the entire
    option list back, so every model id in the account appears in the transcript regardless of which
    one ran. An attribution claim has to read the answer, not the log."""
    out = []
    for f in frames:
        fr = f.get("frame") or {}
        if fr.get("method") != "session/update":
            continue
        upd = ((fr.get("params") or {}).get("update") or {})
        if upd.get("sessionUpdate") not in ("agent_message_chunk", "agent_message"):
            continue
        content = upd.get("content")
        if isinstance(content, dict) and isinstance(content.get("text"), str):
            out.append(content["text"])
        elif isinstance(content, list):
            for c in content:
                if isinstance(c, dict) and isinstance(c.get("text"), str):
                    out.append(c["text"])
    return "".join(out)


class Arm:
    """One `opencode acp` child plus the artifacts it produced."""

    def __init__(self, name, outdir, extra_env=None, argv_extra=None, cwd=None):
        self.name = name
        self.outdir = outdir
        self.frames = []
        self.phase = [name]
        self.extra_env = dict(extra_env or {})
        self.argv = [OPENCODE, "acp"] + list(argv_extra or [])
        self.cwd = str(cwd) if cwd else os.getcwd()
        self.result = {"arm": name, "argv": self.argv, "env": self.extra_env, "cwd": self.cwd}
        self.client = None

    async def start(self):
        self.client = AcpClient(
            argv=self.argv, cwd=self.cwd, frames=self.frames, phase_ref=self.phase,
            label=self.name, stderr_path=str(self.outdir / f"{self.name}.stderr.log"),
            extra_env=self.extra_env)
        await self.client.start()

    async def initialize(self):
        try:
            r = await self.client.request("initialize", {
                "protocolVersion": 1,
                "clientCapabilities": {"fs": {"readTextFile": True, "writeTextFile": True}},
            }, timeout=90)
            self.result["initialize"] = r
        except Exception as ex:  # noqa: BLE001
            self.result["initialize_error"] = repr(ex)

    async def session_new(self, mcp_servers=None):
        try:
            r = await self.client.request("session/new", {
                "cwd": self.cwd,
                "mcpServers": mcp_servers or [],
            }, timeout=120)
            self.result["session_new"] = r
            return (r.get("result") or {}).get("sessionId")
        except Exception as ex:  # noqa: BLE001
            self.result["session_new_error"] = repr(ex)
            return None

    async def dump(self):
        if self.client:
            await self.client.shutdown()
        (self.outdir / f"{self.name}.frames.json").write_text(
            json.dumps(self.frames, indent=2, default=str))
        return self.result


async def free_phase(outdir):
    """Control RPCs only. No model request is issued, so this arm costs nothing."""
    out = {"t": now_iso(), "opencode_version": None, "arms": []}
    try:
        out["opencode_version"] = subprocess.run(
            [OPENCODE, "--version"], capture_output=True, text=True, timeout=60).stdout.strip()
    except Exception as ex:  # noqa: BLE001
        out["opencode_version"] = f"error: {ex!r}"

    # ---- A1..A3: baseline handshake + the model-selection WRITE half -------------------------
    base = Arm("baseline", outdir)
    await base.start()
    await base.initialize()
    sid = await base.session_new()

    if sid:
        # Both wire selectors, in the order the daemon would try them. Recorded raw: a
        # "method not found" is a real answer, and so is a success that does not take effect.
        #
        # The model read half lives in session/new's `configOptions` -- NOT in a `models` object as
        # Kiro and Gemini return. An earlier revision of this probe looked for `models`, found
        # nothing, and fell back to a hard-coded id the account cannot reach: both selectors then
        # answered -32602 "model not found", which reads exactly like an unsupported method and
        # would have argued for NoOpModelSelector on a measurement that never named a real model.
        cur, target = model_options(base.result.get("session_new"))
        base.result["model_current"] = cur
        base.result["model_target"] = target
        for method, params in (
            ("session/set_config_option", {"sessionId": sid, "configId": "model", "value": target}),
            ("session/set_model", {"sessionId": sid, "modelId": target}),
        ):
            if target is None:
                base.result[method + "_error"] = "no distinct target model available"
                continue
            try:
                base.result[method] = await base.client.request(method, params, timeout=60)
            except Exception as ex:  # noqa: BLE001
                base.result[method + "_error"] = repr(ex)

    out["arms"].append(await base.dump())

    # ---- A4: isolation levers ----------------------------------------------------------------
    # The reviewer questions these answer: can the daemon give OpenCode an EMPTY config dir (so the
    # operator's global MCP servers -- kcap-flows among them -- are not inherited), and does the
    # session still start (i.e. do credentials live outside the config dir)?
    for name, env, argv_extra in (
        ("pure", {"OPENCODE_PURE": "1"}, []),
        ("no_project_config", {"OPENCODE_DISABLE_PROJECT_CONFIG": "1"}, []),
        ("isolated_config_dir", {}, []),          # env filled in below (needs a temp dir)
    ):
        arm_out = outdir / name
        arm_out.mkdir(exist_ok=True)
        if name == "isolated_config_dir":
            cfg = arm_out / "config"
            cfg.mkdir(exist_ok=True)
            env = {"OPENCODE_CONFIG_DIR": str(cfg), "OPENCODE_PURE": "1",
                   "OPENCODE_DISABLE_PROJECT_CONFIG": "1"}
        arm = Arm(name, outdir, extra_env=env, argv_extra=argv_extra)
        await arm.start()
        await arm.initialize()
        await arm.session_new()
        out["arms"].append(await arm.dump())

    # ---- A5: stdio mcpServers ADMISSION ------------------------------------------------------
    server = write_mcp_server(outdir)
    mcp_log = outdir / "mcp-admission.log"
    nonce = "kcapadm" + os.urandom(4).hex()
    arm = Arm("mcp_admission", outdir)
    await arm.start()
    await arm.initialize()
    await arm.session_new(mcp_servers=[{
        "name": "kcap-probe",
        "command": shutil.which("node") or "node",
        "args": [str(server)],
        "env": [{"name": "PROBE_MCP_LOG", "value": str(mcp_log)},
                {"name": "PROBE_MCP_NONCE", "value": nonce}],
    }])
    await asyncio.sleep(6)  # give the agent time to spawn + handshake the server
    arm.result["mcp_nonce"] = nonce
    arm.result["mcp_server_spawned"] = mcp_log.exists()
    arm.result["mcp_server_log"] = (
        mcp_log.read_text().splitlines() if mcp_log.exists() else None)
    out["arms"].append(await arm.dump())

    # ---- A6: DUAL CAPTURE control ------------------------------------------------------------
    # The one question that decides capture precedence for a daemon-hosted OpenCode agent: does the
    # operator's installed kcap plugin load INSIDE the `opencode acp` process and start capturing
    # the same session the ACP mapper is already capturing? Two arms with identical dwell, differing
    # ONLY in OPENCODE_PURE, so a difference is attributable to the lever and not to timing --
    # an earlier uncontrolled pass had unequal dwell and would have "shown" prevention that was
    # really just a shutdown race.
    #
    # Evidence is the plugin's OWN side effect: ~/.cache/kcap/opencode/<sessionId>.jsonl, which only
    # its session.created -> start() path creates.
    dual = {"arm": "dual_capture_control", "dwell_seconds": 10, "arms": {}}
    for name, env in (("plugin_default", {}), ("plugin_pure", {"OPENCODE_PURE": "1"})):
        arm = Arm(name, outdir, extra_env=env)
        await arm.start()
        await arm.initialize()
        sid = await arm.session_new()
        await asyncio.sleep(dual["dwell_seconds"])
        dual["arms"][name] = {
            "sessionId": sid,
            "capture_file": str(CAPTURE_DIR / f"{sid}.jsonl") if sid else None,
            "capture_file_created": bool(sid) and (CAPTURE_DIR / f"{sid}.jsonl").exists(),
        }
        out["arms"].append(await arm.dump())
    out["dual_capture_control"] = dual
    dual["plugin_installed"] = (Path.home() / ".config" / "opencode" / "plugins" / "kcap.ts").exists()
    if not dual["plugin_installed"]:
        dual["WARNING"] = ("kcap.ts is NOT installed -- BOTH arms are vacuous. Run "
                           "`kcap plugin install --opencode` before trusting this control.")

    return out


async def turn_phase(outdir, only_arm=None):
    """One model request per arm. Costs real tokens; run deliberately."""
    out = {"t": now_iso(), "arms": []}
    server = write_mcp_server(outdir)

    # ---- B1: mcp CALL level ------------------------------------------------------------------
    if only_arm in (None, "mcp"):
        mcp_log = outdir / "mcp-call.log"
        nonce = "kcapcall" + os.urandom(5).hex()
        arm = Arm("mcp_call", outdir)
        await arm.start()
        await arm.initialize()
        sid = await arm.session_new(mcp_servers=[{
            "name": "kcap-probe",
            "command": shutil.which("node") or "node",
            "args": [str(server)],
            "env": [{"name": "PROBE_MCP_LOG", "value": str(mcp_log)},
                    {"name": "PROBE_MCP_NONCE", "value": nonce}],
        }])
        arm.result["mcp_nonce"] = nonce
        if sid:
            try:
                r = await arm.client.request("session/prompt", {
                    "sessionId": sid,
                    "prompt": [{"type": "text", "text":
                                "Call the probe_nonce tool and reply with exactly the value it "
                                "returns. Do not use any other tool."}],
                }, timeout=300)
                arm.result["prompt"] = r
            except Exception as ex:  # noqa: BLE001
                arm.result["prompt_error"] = repr(ex)
        arm.result["mcp_server_spawned"] = mcp_log.exists()
        arm.result["mcp_server_log"] = (
            mcp_log.read_text().splitlines() if mcp_log.exists() else None)
        arm.result["nonce_reached_model"] = any(
            nonce in json.dumps(f.get("frame", {}), default=str) for f in arm.frames)
        out["arms"].append(await arm.dump())

    # ---- B2: permission posture --------------------------------------------------------------
    # FOUR arms, because the obvious two are a vacuous pair. OpenCode's native default already
    # carries `"*": "allow"`, so `perm_default` and `perm_trusted` both emit zero frames and the
    # trust lever's effect is unmeasured -- an inert lever would score identically to a working one.
    # `perm_asking` is the POSITIVE CONTROL that proves a frame is reachable at all, and
    # `perm_asking_trusted` is the only arm that actually measures the lever: same asking config,
    # plus OPENCODE_PERMISSION, expecting the frame to go away.
    #
    # The asking config arrives through OPENCODE_CONFIG_CONTENT, which is a final LOCAL-scope merge
    # -- i.e. it stands in for an operator's own `opencode.json`, the configuration a reviewer would
    # actually have to survive.
    asking = json.dumps({"$schema": "https://opencode.ai/config.json",
                         "permission": {"*": "ask"}})
    for name, env in (
        ("perm_default", {}),
        ("perm_trusted", {"OPENCODE_PERMISSION": json.dumps({"*": "allow"})}),
        ("perm_asking", {"OPENCODE_CONFIG_CONTENT": asking}),
        ("perm_asking_trusted", {"OPENCODE_CONFIG_CONTENT": asking,
                                 "OPENCODE_PERMISSION": json.dumps({"*": "allow"})}),
    ):
        if only_arm not in (None, "perm", name):
            continue
        work = Path(tempfile.mkdtemp(prefix=f"kcap-oc-{name}-"))
        (work / "README.md").write_text("probe\n")
        subprocess.run(["git", "init", "-q"], cwd=work, check=False)
        arm = Arm(name, outdir, extra_env=env, cwd=work)
        await arm.start()
        await arm.initialize()
        sid = await arm.session_new()
        if sid:
            try:
                r = await arm.client.request("session/prompt", {
                    "sessionId": sid,
                    "prompt": [{"type": "text", "text":
                                "Create a file named probe.txt in the working directory containing "
                                "the single word OK, then tell me you are done."}],
                }, timeout=300)
                arm.result["prompt"] = r
            except Exception as ex:  # noqa: BLE001
                arm.result["prompt_error"] = repr(ex)
        arm.result["permission_frames"] = [
            f for f in arm.frames
            if f.get("frame", {}).get("method") == "session/request_permission"
        ]
        arm.result["permission_frame_count"] = len(arm.result["permission_frames"])
        arm.result["file_written"] = (work / "probe.txt").exists()
        arm.result["workdir"] = str(work)
        out["arms"].append(await arm.dump())

    # ---- B4: the SCOPED reviewer posture -----------------------------------------------------
    # `{"*":"allow"}` makes a reviewer able to run bash and write files, which is broader than the
    # Kiro reviewer's read-only trust list. This measures whether OpenCode's permission config can
    # express the narrow posture instead: deny everything, then allow the read family plus the
    # injected result channel (which OpenCode presents to the model as `{server}_{tool}` -- measured
    # in the mcp_call arm as `kcap-probe_probe_nonce`, so an allowlist can name it).
    #
    # TWO arms, because "the model did not run bash" is indistinguishable from "bash was denied" --
    # a model that simply chose not to shell out would score as containment. The arms differ ONLY in
    # whether bash is allowed, so the difference is attributable to the rule.
    # `reviewer_mcp_unlisted` rules out the alternative explanation for the scoped arm: that MCP tools
    # bypass the permission system altogether, which would make the `{server}_*` allowlist entry
    # decorative while looking load-bearing. Same posture, entry REMOVED — if the tool is still
    # callable, the entry proves nothing and the write-up must not claim it does.
    for name, bash_rule, list_server in (("reviewer_scoped", "deny", True),
                                         ("reviewer_bash_allowed", "allow", True),
                                         ("reviewer_mcp_unlisted", "deny", False)):
        if only_arm not in (None, "reviewer", name):
            continue

        work = Path(tempfile.mkdtemp(prefix=f"kcap-oc-{name}-"))
        (work / "README.md").write_text("The secret word is PLATYPUS.\n")
        subprocess.run(["git", "init", "-q"], cwd=work, check=False)

        mcp_log = work / "mcp.log"
        nonce = "kcaprev" + os.urandom(5).hex()
        server_name = "kcap-flow-result-probe"

        permission = {
            "*": "deny",
            "read": "allow", "grep": "allow", "glob": "allow", "list": "allow",
            "bash": bash_rule,
        }
        if list_server:
            permission[f"{server_name}_*"] = "allow"

        arm = Arm(name, outdir,
                  extra_env={"OPENCODE_PERMISSION": json.dumps(permission)}, cwd=work)
        await arm.start()
        await arm.initialize()
        sid = await arm.session_new(mcp_servers=[{
            "name": server_name,
            "command": shutil.which("node") or "node",
            "args": [str(server)],
            "env": [{"name": "PROBE_MCP_LOG", "value": str(mcp_log)},
                    {"name": "PROBE_MCP_NONCE", "value": nonce}],
        }])
        arm.result["permission"] = permission
        arm.result["mcp_nonce"] = nonce

        if sid:
            try:
                arm.result["prompt"] = await arm.client.request("session/prompt", {
                    "sessionId": sid,
                    "prompt": [{"type": "text", "text":
                                "Do all three, in order, and report each outcome: "
                                "(1) read README.md and tell me the secret word; "
                                "(2) call the probe_nonce tool and tell me what it returned; "
                                "(3) run the shell command `echo ok > shell-ran.txt` and tell me "
                                "whether it worked or what error you got."}],
                }, timeout=300)
            except Exception as ex:  # noqa: BLE001
                arm.result["prompt_error"] = repr(ex)

        said = agent_text(arm.frames)
        blob = json.dumps(arm.frames, default=str)
        arm.result["answer_text"] = said
        arm.result["read_worked"] = "PLATYPUS" in blob
        arm.result["mcp_called"] = mcp_log.exists() and "tools/call" in mcp_log.read_text()
        # A FILESYSTEM side effect, not a string in the answer. An earlier revision searched the
        # model's own text for the echoed sentinel and scored the denied arm as having run the shell,
        # because the model quoted the command it had been asked to run while explaining that it
        # could not. Prose cannot create this file.
        arm.result["shell_ran"] = (work / "shell-ran.txt").exists()
        arm.result["permission_frame_count"] = sum(
            1 for f in arm.frames if f.get("frame", {}).get("method") == "session/request_permission")
        arm.result["workdir"] = str(work)
        out["arms"].append(await arm.dump())

    # ---- B3: model override at EFFECT level --------------------------------------------------
    # session/set_config_option echoes the new currentValue back, but that is the agent's own
    # self-report -- exactly the failure mode that kept Gemini on NoOpModelSelector, where a session
    # reports the requested model while running another. This arm asks the MODEL, which OpenCode's
    # own system prompt tells its exact id ("You are powered by the model named ..."), so the answer
    # is the running model rather than the configured one. Targets a `-free` model: zero cost.
    if only_arm in (None, "model"):
        arm = Arm("model_effect", outdir)
        await arm.start()
        await arm.initialize()
        sid = await arm.session_new()
        cur, target = model_options(arm.result.get("session_new"))
        arm.result["model_current"] = cur
        arm.result["model_target"] = target
        if sid and target:
            try:
                arm.result["set"] = await arm.client.request(
                    "session/set_config_option",
                    {"sessionId": sid, "configId": "model", "value": target}, timeout=60)
                r = await arm.client.request("session/prompt", {
                    "sessionId": sid,
                    "prompt": [{"type": "text", "text":
                                "Reply with ONLY the exact model ID you are running as. "
                                "No other words."}],
                }, timeout=300)
                arm.result["prompt"] = r
            except Exception as ex:  # noqa: BLE001
                arm.result["prompt_error"] = repr(ex)
        said = agent_text(arm.frames)
        arm.result["answer_text"] = said
        # Attribute by the DISTINGUISHING half of each id: both live under the same provider, so
        # matching the provider prefix would score every answer as both.
        arm.result["answer_names_target"] = bool(target) and target.split("/")[-1] in said
        arm.result["answer_names_previous"] = bool(cur) and cur.split("/")[-1] in said
        out["arms"].append(await arm.dump())

    return out


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--turn", action="store_true", help="run the billable model-request phase")
    ap.add_argument("--arm", default=None, help="restrict --turn to one arm (mcp|perm|<arm name>)")
    ap.add_argument("--outdir", default=None)
    args = ap.parse_args()

    outdir = Path(args.outdir) if args.outdir else Path(__file__).resolve().parent / "out"
    outdir.mkdir(parents=True, exist_ok=True)

    if args.turn:
        res = await turn_phase(outdir, args.arm)
        name = "turn-summary.json"
    else:
        res = await free_phase(outdir)
        name = "free-phase-summary.json"

    (Path(__file__).resolve().parent / name).write_text(json.dumps(res, indent=2, default=str))
    print(json.dumps(res, indent=2, default=str)[:6000])


if __name__ == "__main__":
    asyncio.run(main())
