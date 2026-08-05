#!/usr/bin/env python3
"""Kiro unattended-reviewer probe: does SCOPED trust let the reviewer report, and does an
isolated KIRO_HOME actually suppress the operator's global MCP servers on this build?

Two questions, both open in the AI-1410 spec, both measured here against the ACP path (the spec's
own standing trap: the earlier trust measurements were taken on `chat --no-interactive`, which
denies for a DIFFERENT reason and does not transfer).

  Q1  Does `--trust-tools=fs_read,thinking,@kcap-flow-result/submit_review_result` cover the
      injected result tool, so a Fail-policy reviewer can deliver its result without a human?
      Discriminator: a session/request_permission frame naming the tool means scoped trust does
      NOT cover it. No frame + a tools/call in the MCP server's own log means it does.

  Q2  Does an isolated (empty) KIRO_HOME suppress the operator's GLOBAL
      ~/.kiro/settings/mcp.json servers -- kcap-flows among them -- on this kiro-cli build?
      Positive control included: the same handshake with the REAL home must show those servers
      initializing, or "zero servers" is unfalsifiable and proves nothing.

Phases:
  free (default; ZERO billable requests -- Kiro bills per prompt, not per control RPC):
    A  isolated KIRO_HOME + injected probe server: stderr warnings (a --trust-tools typo is a
       WARNING, not an error, so an unaccepted namespaced name degrades silently to "nothing
       trusted"), plus every _kiro.dev/mcp/server_initialized notification.
    B  real KIRO_HOME, same handshake: the positive control for A.
  --turn (EXACTLY ONE billable request, on the isolated spawn):
    C  session/set_model deepseek-3.2 (rate_multiplier 0.25, the cheapest tier), then one prompt
       asking for the tool call. Records permission frames and the MCP server's own log.

Usage: python3 probe.py [--turn] [--model deepseek-3.2] [--outdir DIR]
"""

import argparse
import asyncio
import json
import os
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402  (reuse the proven ACP child driver)

HERE = Path(__file__).resolve().parent
MCP_SERVER = HERE / "mcp_probe_server.py"

RESULT_CHANNEL = "kcap-flow-result"
RESULT_TOOL = "submit_review_result"
NONCE = "KIRO-TRUST-PROBE-7F31A"

# The scoped set the spec proposes: read + think + the namespaced result tool. Deliberately no
# execute_bash -- trusting it makes writes and out-of-tree commands run with no frame at all.
SCOPED_TRUST = f"fs_read,thinking,@{RESULT_CHANNEL}/{RESULT_TOOL}"


def now_iso():
    return datetime.now(timezone.utc).isoformat()


class RecordingClient(AcpClient):
    """AcpClient that RECORDS permission requests distinctly instead of quietly approving them.

    The base driver auto-approves, which would erase exactly the signal this probe is after.
    It still approves after recording, so one billable turn yields the full picture (frame
    present or not AND whether the transport works end to end) rather than half of it.
    """

    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.permission_requests = []

    async def _handle_server_request(self, obj):
        if obj.get("method") == "session/request_permission":
            self.permission_requests.append({"t": now_iso(), "params": obj.get("params", {})})
        await super()._handle_server_request(obj)


def server_initialized_names(frames):
    """Every MCP server name Kiro reported starting, from its own notifications."""
    names = []
    for f in frames:
        frame = f.get("frame", {})
        if frame.get("method") != "_kiro.dev/mcp/server_initialized":
            continue
        params = frame.get("params", {}) or {}
        names.append(params.get("server_name") or params.get("name") or params)
    return names


def read_stderr(path):
    try:
        return Path(path).read_text(errors="replace")
    except OSError:
        return ""


def trust_warnings(stderr_text):
    """--trust-tools rejections. A rejected name warns and continues, trusting nothing -- so the
    ABSENCE of a warning for the namespaced entry is what says the syntax was accepted."""
    return [ln for ln in stderr_text.splitlines()
            if "trust-tools" in ln.lower() or "WARNING" in ln]


async def handshake(label, kiro_home, outdir, frames, phase, mcp_log, cwd, trust_argv):
    stderr_path = outdir / f"stderr-{label}.txt"
    env = {"PROBE_MCP_LOG": str(mcp_log), "PROBE_NONCE": NONCE}
    if kiro_home is not None:
        env["KIRO_HOME"] = str(kiro_home)

    argv = ["kiro-cli", "acp", *trust_argv]
    client = RecordingClient(argv, str(cwd), frames, phase, label, str(stderr_path), extra_env=env)
    await client.start()

    await client.request("initialize", {
        "protocolVersion": 1,
        "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}},
    })

    new = await client.request("session/new", {
        "cwd": str(cwd),
        "mcpServers": [{
            "name": RESULT_CHANNEL,
            "command": sys.executable,
            "args": [str(MCP_SERVER)],
            "env": [{"name": "PROBE_MCP_LOG", "value": str(mcp_log)},
                    {"name": "PROBE_NONCE", "value": NONCE}],
        }],
    })

    # Kiro emits server_initialized asynchronously; give it a moment before reading the tally.
    await asyncio.sleep(3)
    return client, new


async def run(args):
    # ABSOLUTE: the MCP server is spawned with cwd set to the review worktree, not ours. A relative
    # log path there fails to open, the server dies before its first write, and Kiro reports the
    # generic "connection closed: initialize response" -- which reads exactly like a vendor refusal.
    outdir = Path(args.outdir).resolve()
    outdir.mkdir(parents=True, exist_ok=True)
    summary = {"started": now_iso(), "kiro_version": None, "phases": {}}

    proc = await asyncio.create_subprocess_exec(
        "kiro-cli", "--version", stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
    out, _ = await proc.communicate()
    summary["kiro_version"] = out.decode(errors="replace").strip()

    work = Path(tempfile.mkdtemp(prefix="kiro-trust-probe-worktree-"))
    (work / "README.md").write_text("# probe worktree\n")

    # ---- Phase A: isolated KIRO_HOME, scoped trust ------------------------------------------
    iso_home = Path(tempfile.mkdtemp(prefix="kiro-trust-probe-home-"))
    frames_a, phase_a = [], ["A-isolated"]
    mcp_log_a = outdir / "mcp-server-A.log"
    client_a, new_a = await handshake(
        "A-isolated", iso_home, outdir, frames_a, phase_a, mcp_log_a, work,
        ["--trust-tools", args.trust])

    stderr_a = read_stderr(outdir / "stderr-A-isolated.txt")
    summary["phases"]["A_isolated_scoped_trust"] = {
        "kiro_home": str(iso_home),
        "trust_tools": args.trust,
        "trust_warnings": trust_warnings(stderr_a),
        "mcp_servers_initialized": server_initialized_names(frames_a),
        "session_new_ok": "result" in new_a,
        "session_id": (new_a.get("result") or {}).get("sessionId"),
        "available_models": [m.get("modelId") for m in
                             ((new_a.get("result") or {}).get("models") or {}).get("availableModels", [])],
    }

    # ---- Phase B: POSITIVE CONTROL, real KIRO_HOME -------------------------------------------
    frames_b, phase_b = [], ["B-control"]
    mcp_log_b = outdir / "mcp-server-B.log"
    client_b, new_b = await handshake(
        "B-control", None, outdir, frames_b, phase_b, mcp_log_b, work,
        ["--trust-tools", SCOPED_TRUST])

    summary["phases"]["B_real_home_control"] = {
        "kiro_home": "(unset - real ~/.kiro)",
        "mcp_servers_initialized": server_initialized_names(frames_b),
        "session_new_ok": "result" in new_b,
    }
    await client_b.close() if hasattr(client_b, "close") else None
    try:
        client_b.proc.kill()
    except Exception:  # noqa: BLE001
        pass

    # ---- Phase C: ONE billable turn on the isolated session ----------------------------------
    if args.turn:
        phase_a[0] = "C-turn"
        session_id = summary["phases"]["A_isolated_scoped_trust"]["session_id"]

        set_model = await client_a.request(
            "session/set_model", {"sessionId": session_id, "modelId": args.model})

        prompt = (
            "Call the submit_review_result tool exactly once, with summary set to the text "
            "'probe ok'. Then reply with the tool's response verbatim and nothing else.")
        res = await client_a.request("session/prompt", {
            "sessionId": session_id,
            "prompt": [{"type": "text", "text": prompt}],
        }, timeout=300)

        mcp_log_lines = []
        if mcp_log_a.exists():
            mcp_log_lines = [json.loads(ln) for ln in
                             mcp_log_a.read_text().splitlines() if ln.strip()]

        summary["phases"]["C_turn"] = {
            "model_requested": args.model,
            "set_model_response": set_model,
            "stop_reason": (res.get("result") or {}).get("stopReason"),
            "permission_requests": client_a.permission_requests,
            "mcp_server_methods": [e["payload"]["method"] for e in mcp_log_lines
                                   if e.get("event") == "request"],
            "tools_call_reached_server": any(
                e.get("event") == "request" and e["payload"].get("method") == "tools/call"
                for e in mcp_log_lines),
            "nonce_in_transcript": NONCE in json.dumps(frames_a),
        }

    try:
        client_a.proc.kill()
    except Exception:  # noqa: BLE001
        pass

    summary["finished"] = now_iso()
    (outdir / "summary.json").write_text(json.dumps(summary, indent=2))
    (outdir / "frames-A.json").write_text(json.dumps(frames_a, indent=2))
    (outdir / "frames-B.json").write_text(json.dumps(frames_b, indent=2))
    print(json.dumps(summary, indent=2))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--turn", action="store_true", help="spend EXACTLY ONE billable request")
    p.add_argument("--model", default="deepseek-3.2", help="cheapest distinguishable tier")
    # The NEGATIVE CONTROL. "No permission frame with the namespaced entry present" does not by
    # itself prove the entry did anything -- MCP tools might need no approval at all, in which case
    # the trust list is decorative and the spec would be claiming a mechanism it does not have.
    # Dropping only that entry discriminates: a frame appearing here is what makes the entry
    # load-bearing.
    p.add_argument("--trust", default=SCOPED_TRUST, help="override the --trust-tools value")
    p.add_argument("--outdir", default=str(HERE / "out"))
    asyncio.run(run(p.parse_args()))


if __name__ == "__main__":
    main()
