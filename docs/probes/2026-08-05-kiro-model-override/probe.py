#!/usr/bin/env python3
"""Kiro model-override probe: does session/set_config_option {configId:"model"} TAKE EFFECT?

The hosting work deliberately shipped Kiro with NoOpModelSelector because
ConfigOptionModelSelector's write half (session/set_config_option) was unverified on Kiro and
that selector fails silently. This probe measures the write half at the EFFECT level, mirroring
the daemon's exact wire behavior (initialize protocolVersion 1 -> session/new {cwd, mcpServers:[]}
-> session/set_config_option {sessionId, configId:"model", value:<exact modelId from
availableModels>} -> first session/prompt).

Phases:
  free (default; ZERO billable requests -- Kiro bills per prompt request, not per control RPC):
    1. initialize, session/new -> record result.models (currentModelId + availableModels).
    2. pick target: first availableModels id containing --prefer (default "haiku", the cheapest
       distinguishable tier), else first id != currentModelId.
    3. session/set_config_option -> record the raw response.
       On "method not found" ALSO probe session/set_model {sessionId, modelId} for the record.
    4. read the session sidecar ~/.kiro/sessions/cli/{sessionId}.json ->
       session_state.rts_model_state.model_info (the persisted model setting).
  --turn (EXACTLY ONE billable request):
    5. one session/prompt asking the model to self-identify (no tools), record stopReason +
       every session/update frame.
    6. re-read the sidecar; capture new kiro-cli log lines (KIRO_LOG_LEVEL=trace) that name a
       model id around the request, from $TMPDIR/kiro-log/ and ~/.kiro/logs/.

Usage: python3 probe.py [--turn] [--prefer haiku] [--model EXACT_ID] [--outdir DIR]
"""

import argparse
import asyncio
import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402  (reuse the proven ACP child driver)

KIRO_SESSIONS = Path.home() / ".kiro" / "sessions" / "cli"
KIRO_LOG_ROOT = Path.home() / ".kiro" / "logs"


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def tmp_log_dir():
    return Path(os.environ.get("TMPDIR", "/tmp")) / "kiro-log"


def snapshot_log_offsets():
    offs = {}
    d = tmp_log_dir()
    if d.is_dir():
        for f in d.glob("*.log"):
            offs[str(f)] = f.stat().st_size
    return offs


def new_log_lines(offsets):
    """Lines appended to $TMPDIR/kiro-log/*.log since snapshot, plus any ~/.kiro/logs dir
    created after the snapshot was taken (kiro-cli picks one of the two at startup)."""
    out = {}
    d = tmp_log_dir()
    if d.is_dir():
        for f in d.glob("*.log"):
            prev = offsets.get(str(f), 0)
            size = f.stat().st_size
            if size > prev:
                with open(f, "rb") as fh:
                    fh.seek(prev)
                    out[str(f)] = fh.read().decode("utf-8", "replace").splitlines()
    return out


def newest_kiro_log_dirs(since_epoch):
    out = {}
    if KIRO_LOG_ROOT.is_dir():
        for d in sorted(KIRO_LOG_ROOT.iterdir()):
            if d.is_dir() and d.stat().st_mtime >= since_epoch - 2:
                for f in d.glob("*.log"):
                    try:
                        out[str(f)] = f.read_text("utf-8", errors="replace").splitlines()
                    except OSError:
                        pass
    return out


def read_sidecar(session_id):
    p = KIRO_SESSIONS / f"{session_id}.json"
    for _ in range(10):
        if p.exists():
            try:
                d = json.loads(p.read_text())
                state = (d.get("session_state") or {}).get("rts_model_state") or {}
                return {"path": str(p), "rts_model_state": state}
            except (json.JSONDecodeError, OSError):
                pass
        time.sleep(0.3)
    return {"path": str(p), "rts_model_state": None, "note": "sidecar absent/unreadable after 3s"}


def model_lines(lines_by_file, needles):
    hits = {}
    for f, lines in lines_by_file.items():
        keep = [ln for ln in lines if any(n.lower() in ln.lower() for n in needles)]
        if keep:
            hits[f] = keep[:400]
    return hits


async def run(args):
    outdir = Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)
    cwd = outdir / "cwd"
    cwd.mkdir(exist_ok=True)
    frames = []
    phase_ref = ["init"]
    summary = {"started": now_iso(), "argv": ["kiro-cli", "acp"], "turn_requested": args.turn,
               "notes": []}

    def note(msg):
        summary["notes"].append(msg)
        print(f"  [kiro-model] {msg}", flush=True)

    t0 = time.time()
    offsets = snapshot_log_offsets()
    client = AcpClient(["kiro-cli", "acp"], str(cwd), frames, phase_ref, "child1",
                       outdir / "stderr.log", {"KIRO_LOG_LEVEL": "trace"})
    try:
        await client.start()
        init = await client.request("initialize", {
            "protocolVersion": 1,
            "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False},
                                     "terminal": False}}, timeout=60)
        summary["agent_capabilities"] = (init.get("result") or {}).get("agentCapabilities")
        note("initialize ok")

        phase_ref[0] = "session_new"
        sn = await client.request("session/new", {"cwd": str(cwd), "mcpServers": []}, timeout=90)
        snr = sn.get("result") or {}
        client.session_id = snr.get("sessionId")
        summary["session_id"] = client.session_id
        summary["session_new_models"] = snr.get("models")
        summary["session_new_config_options"] = snr.get("configOptions")
        if not client.session_id:
            note(f"session/new gave no sessionId: {json.dumps(sn)[:500]}")
            return summary
        models = (snr.get("models") or {}).get("availableModels") or []
        current = (snr.get("models") or {}).get("currentModelId")
        note(f"session/new ok; sessionId={client.session_id} currentModelId={current} "
             f"availableModels={[m.get('modelId') for m in models]}")

        target = args.model
        if not target:
            for m in models:
                if args.prefer.lower() in (m.get("modelId") or "").lower():
                    target = m["modelId"]
                    break
        if not target:
            target = next((m["modelId"] for m in models
                           if m.get("modelId") and m["modelId"] != current), None)
        summary["target_model"] = target
        if not target:
            note("no selectable non-default model in availableModels -- nothing to probe")
            return summary
        note(f"target model: {target}")

        phase_ref[0] = "set_config_option"
        try:
            resp = await client.request(
                "session/set_config_option",
                {"sessionId": client.session_id, "configId": "model", "value": target},
                timeout=60)
        except (asyncio.TimeoutError, ConnectionError) as ex:
            resp = {"transport_error": repr(ex)}
        summary["set_config_option_response"] = resp
        err = resp.get("error") if isinstance(resp, dict) else None
        note(f"session/set_config_option -> {json.dumps(resp)[:300]}")

        applied_via = None if err else "set_config_option"
        if err and err.get("code") == -32601:
            phase_ref[0] = "set_model_fallback"
            try:
                sm = await client.request(
                    "session/set_model",
                    {"sessionId": client.session_id, "modelId": target}, timeout=60)
            except (asyncio.TimeoutError, ConnectionError) as ex:
                sm = {"transport_error": repr(ex)}
            summary["set_model_response"] = sm
            if isinstance(sm, dict) and "result" in sm and "error" not in sm:
                applied_via = "set_model"
            note(f"session/set_model -> {json.dumps(sm)[:300]}")
        summary["applied_via"] = applied_via

        summary["sidecar_after_set"] = read_sidecar(client.session_id)
        note(f"sidecar after set: {json.dumps(summary['sidecar_after_set'].get('rts_model_state'))[:300]}")

        if args.turn and applied_via:
            phase_ref[0] = "turn"
            turn_offsets = snapshot_log_offsets()
            try:
                tr = await client.request(
                    "session/prompt",
                    {"sessionId": client.session_id,
                     "prompt": [{"type": "text",
                                  "text": "Without using any tools, reply in one short line: "
                                          "exactly which AI model are you (family and version)?"}]},
                    timeout=240)
            except (asyncio.TimeoutError, ConnectionError) as ex:
                tr = {"transport_error": repr(ex)}
            summary["turn_response"] = tr
            summary["turn_stop"] = ((tr or {}).get("result") or {}).get("stopReason")
            note(f"turn stopReason={summary['turn_stop']}")
            agent_text = []
            for fr in frames:
                if fr["dir"] == "in" and fr["frame"].get("method") == "session/update":
                    upd = (fr["frame"].get("params") or {}).get("update") or {}
                    if upd.get("sessionUpdate") == "agent_message_chunk":
                        agent_text.append(((upd.get("content") or {}).get("text")) or "")
            summary["agent_reply_text"] = "".join(agent_text)
            note(f"agent reply: {summary['agent_reply_text'][:200]!r}")
            summary["sidecar_after_turn"] = read_sidecar(client.session_id)
            note(f"sidecar after turn: {json.dumps(summary['sidecar_after_turn'].get('rts_model_state'))[:300]}")
            needles = ["model", target]
            summary["turn_log_model_lines"] = model_lines(new_log_lines(turn_offsets), needles)
    finally:
        phase_ref[0] = "teardown"
        await client.shutdown()
        summary["ended"] = now_iso()
        needles = ["model_id", "modelid", "set_config", "set_model"]
        if summary.get("target_model"):
            needles.append(summary["target_model"])
        summary["session_log_model_lines"] = model_lines(new_log_lines(offsets), needles)
        summary["kiro_home_log_model_lines"] = model_lines(newest_kiro_log_dirs(t0), needles)
        with open(outdir / "frames.jsonl", "w") as f:
            for fr in frames:
                f.write(json.dumps(fr) + "\n")
        with open(outdir / "summary.json", "w") as f:
            json.dump(summary, f, indent=2)
        print(f"== done in {time.time()-t0:.0f}s; outputs in {outdir} ==", flush=True)
    return summary


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--turn", action="store_true",
                    help="spend EXACTLY ONE billable prompt request to verify at effect level")
    ap.add_argument("--prefer", default="haiku",
                    help="substring picking the target from availableModels (default: haiku)")
    ap.add_argument("--model", default=None, help="exact target modelId (overrides --prefer)")
    ap.add_argument("--outdir", default=str(Path(__file__).parent / "out"))
    args = ap.parse_args()
    asyncio.run(run(args))


if __name__ == "__main__":
    main()
