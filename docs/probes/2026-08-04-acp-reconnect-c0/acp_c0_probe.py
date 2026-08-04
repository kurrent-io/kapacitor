#!/usr/bin/env python3
"""AI-1325 C0 re-probe: ACP reconnect/resume identity facts, per vendor.

Measures, against each installed ACP vendor CLI:
  1. loadSession advertisement (initialize).
  2. messageId presence on LIVE session/update chunks (the AI-1325 revisit trigger).
  3. session/load replay: update kinds, chunk granularity, messageId presence,
     live-vs-replay messageId stability, identical-turn (occurrence) identity,
     tool-call replay + toolCallId stability.
  4. Interrupted-turn shape after a mid-turn SIGKILL (absent / user-only / partial).
  5. The closed-world barrier: conversation updates must not arrive after the
     session/load response (ACP spec MUST).
  6. Post-load liveness: a fresh prompt on the loaded session completes.

Usage: python3 acp_c0_probe.py --vendor cursor|copilot|kiro|gemini [--outdir DIR]
Writes <outdir>/<vendor>/frames.jsonl, stderr.log, summary.json.
"""

import argparse
import asyncio
import json
import os
import signal
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

CONVERSATION_KINDS = {
    "user_message_chunk", "agent_message_chunk", "agent_thought_chunk",
    "tool_call", "tool_call_update", "plan",
}

TURN_A_TEXT = "Reply with exactly: alpha"
TURN_B_TEXT = "Reply with exactly: alpha"  # deliberately identical to A
TURN_C_TEXT = ("Run the shell command `echo kcap-probe-c0` and then reply with "
               "exactly: tool-done")
TURN_D_TEXT = ("Count from 1 to 40, one number per line. Do not use any tools "
               "for the counting. After you finish counting, run the shell "
               "command `echo kcap-probe-d-done`.")
TURN_E_TEXT = "Reply with exactly: resumed"

VENDORS = {
    "cursor":  {"argv": ["cursor-agent", "acp"], "model": "claude-sonnet-4-5"},
    "copilot": {"argv": ["copilot", "--acp", "--stdio"], "model": None},
    "kiro":    {"argv": ["kiro-cli", "acp"], "model": None},
    "gemini":  {"argv": ["gemini", "--experimental-acp", "--skip-trust",
                          "--allowed-mcp-server-names", "__kcap_probe_unmatchable__"],
                 "model": None,
                 "env": {"GOOGLE_CLOUD_PROJECT": "capacitortesting"}},
}


def now_iso():
    return datetime.now(timezone.utc).isoformat()


class AcpClient:
    """One ACP child process: spawn, framed JSON-RPC, frame capture."""

    def __init__(self, argv, cwd, frames, phase_ref, label, stderr_path, extra_env=None):
        self.argv = argv
        self.cwd = cwd
        self.frames = frames          # shared list of frame dicts
        self.phase_ref = phase_ref    # mutable [str] so phase changes tag frames
        self.label = label
        self.stderr_path = stderr_path
        self.extra_env = extra_env or {}
        self.proc = None
        self.reader_task = None
        self.next_id = 0
        self.pending = {}             # id -> Future
        self.update_waiters = []      # list of (predicate, Future)
        self.write_lock = asyncio.Lock()
        self.eof = asyncio.Event()

    def record(self, direction, obj, note=None):
        self.frames.append({
            "t": now_iso(), "dir": direction, "phase": self.phase_ref[0],
            "child": self.label, **({"note": note} if note else {}), "frame": obj,
        })

    async def start(self):
        env = dict(os.environ)
        env.update(self.extra_env)
        stderr_f = open(self.stderr_path, "ab")
        self.proc = await asyncio.create_subprocess_exec(
            *self.argv, cwd=self.cwd, env=env,
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            stderr=stderr_f)
        self.reader_task = asyncio.create_task(self._read_loop())
        self.record("mark", {"event": "spawned", "pid": self.proc.pid, "argv": self.argv})

    async def _read_loop(self):
        try:
            while True:
                line = await self.proc.stdout.readline()
                if not line:
                    break
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    self.record("in", {"unparseable": line.decode("utf-8", "replace")[:2000]})
                    continue
                self.record("in", obj)
                await self._dispatch(obj)
        except Exception as ex:  # noqa: BLE001
            self.record("mark", {"event": "read_loop_error", "error": repr(ex)})
        finally:
            self.eof.set()
            for fut in self.pending.values():
                if not fut.done():
                    fut.set_exception(ConnectionError("child EOF with request pending"))
            self.record("mark", {"event": "read_loop_ended"})

    async def _dispatch(self, obj):
        if "id" in obj and ("result" in obj or "error" in obj) and "method" not in obj:
            fut = self.pending.pop(obj["id"], None)
            if fut and not fut.done():
                fut.set_result(obj)
            return
        if "method" in obj and "id" in obj:
            await self._handle_server_request(obj)
            return
        if "method" in obj:
            # notification
            if obj.get("method") == "session/update":
                for pred, fut in list(self.update_waiters):
                    try:
                        if not fut.done() and pred(obj):
                            fut.set_result(obj)
                    except Exception:  # noqa: BLE001
                        pass

    async def _handle_server_request(self, obj):
        method = obj.get("method", "")
        rid = obj["id"]
        params = obj.get("params", {})
        if method == "session/request_permission":
            options = params.get("options", []) or []
            chosen = None
            for kind in ("allow_once", "allow_always"):
                for opt in options:
                    if opt.get("kind") == kind:
                        chosen = opt
                        break
                if chosen:
                    break
            if chosen is None and options:
                chosen = options[0]
            if chosen is not None:
                result = {"outcome": {"outcome": "selected", "optionId": chosen.get("optionId")}}
            else:
                result = {"outcome": {"outcome": "cancelled"}}
            await self._respond(rid, result=result)
            self.record("mark", {"event": "auto_approved_permission",
                                  "optionId": (chosen or {}).get("optionId"),
                                  "options": options})
            return
        if method.startswith("elicitation/"):
            await self._respond(rid, result={"action": "decline"})
            self.record("mark", {"event": "declined_elicitation"})
            return
        await self._respond(rid, error={"code": -32601, "message": f"Method not found: {method}"})

    async def _respond(self, rid, result=None, error=None):
        frame = {"jsonrpc": "2.0", "id": rid}
        if error is not None:
            frame["error"] = error
        else:
            frame["result"] = result
        await self._write(frame)

    async def _write(self, frame):
        data = (json.dumps(frame) + "\n").encode()
        async with self.write_lock:
            self.proc.stdin.write(data)
            await self.proc.stdin.drain()
        self.record("out", frame)

    async def request(self, method, params, timeout=180):
        self.next_id += 1
        rid = self.next_id
        fut = asyncio.get_event_loop().create_future()
        self.pending[rid] = fut
        await self._write({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
        try:
            return await asyncio.wait_for(fut, timeout)
        finally:
            self.pending.pop(rid, None)

    async def notify(self, method, params):
        await self._write({"jsonrpc": "2.0", "method": method, "params": params})

    def wait_for_update(self, predicate):
        fut = asyncio.get_event_loop().create_future()
        self.update_waiters.append((predicate, fut))
        return fut

    def sigkill(self):
        self.record("mark", {"event": "sigkill", "pid": self.proc.pid})
        try:
            self.proc.send_signal(signal.SIGKILL)
        except ProcessLookupError:
            pass

    async def shutdown(self, hard_after=5):
        if self.proc.returncode is None:
            try:
                self.proc.terminate()
            except ProcessLookupError:
                pass
            try:
                await asyncio.wait_for(self.proc.wait(), hard_after)
            except asyncio.TimeoutError:
                self.sigkill()
                await self.proc.wait()
        if self.reader_task:
            await asyncio.wait([self.reader_task], timeout=5)


def update_kind(frame_obj):
    try:
        return frame_obj["params"]["update"].get("sessionUpdate")
    except Exception:  # noqa: BLE001
        return None


def update_body(frame_obj):
    try:
        return frame_obj["params"]["update"]
    except Exception:  # noqa: BLE001
        return None


async def run_turn(client, text, phase, phase_ref, timeout=240):
    phase_ref[0] = phase
    try:
        resp = await client.request(
            "session/prompt",
            {"sessionId": client.session_id,
             "prompt": [{"type": "text", "text": text}]},
            timeout=timeout)
        return resp
    except asyncio.TimeoutError:
        client.record("mark", {"event": "turn_timeout", "phase": phase})
        try:
            await client.notify("session/cancel", {"sessionId": client.session_id})
        except Exception:  # noqa: BLE001
            pass
        return None
    except (ConnectionError, BrokenPipeError, ConnectionResetError) as ex:
        client.record("mark", {"event": "turn_connection_lost", "phase": phase,
                                "error": repr(ex)})
        return None


async def probe_vendor(vendor, outdir):
    spec = VENDORS[vendor]
    vdir = Path(outdir) / vendor
    vdir.mkdir(parents=True, exist_ok=True)
    cwd = vdir / "cwd"
    cwd.mkdir(exist_ok=True)
    frames = []
    phase_ref = ["init1"]
    summary = {"vendor": vendor, "argv": spec["argv"], "started": now_iso(), "notes": []}

    def note(msg):
        summary["notes"].append(msg)
        print(f"  [{vendor}] {msg}", flush=True)

    c1 = AcpClient(spec["argv"], str(cwd), frames, phase_ref, "child1",
                   vdir / "stderr-child1.log", spec.get("env"))
    try:
        await c1.start()
        init = await c1.request("initialize", {
            "protocolVersion": 1,
            "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False},
                                     "terminal": False}}, timeout=60)
        caps = (init.get("result") or {}).get("agentCapabilities") or {}
        summary["load_session_advertised"] = bool(caps.get("loadSession"))
        summary["agent_capabilities"] = caps
        note(f"initialize ok; loadSession={summary['load_session_advertised']}")

        phase_ref[0] = "session_new"
        sn = await c1.request("session/new", {"cwd": str(cwd), "mcpServers": []}, timeout=90)
        snr = sn.get("result") or {}
        c1.session_id = snr.get("sessionId")
        summary["session_id"] = c1.session_id
        if not c1.session_id:
            note(f"session/new gave no sessionId: {json.dumps(sn)[:500]}")
            return summary
        note(f"session/new ok; sessionId={c1.session_id}")

        # model selection (cursor only), mirroring ConfigOptionModelSelector
        if spec.get("model"):
            models = ((snr.get("models") or {}).get("availableModels")) or []
            resolved = None
            want = spec["model"].lower()
            for m in models:
                if m.get("modelId", "").lower() == want:
                    resolved = m["modelId"]
                    break
            if resolved is None:
                for m in models:
                    if m.get("modelId", "").lower().startswith(want):
                        resolved = m["modelId"]
                        break
            if resolved:
                phase_ref[0] = "set_model"
                await c1.request("session/set_config_option",
                                 {"sessionId": c1.session_id, "configId": "model",
                                  "value": resolved}, timeout=60)
                summary["model_selected"] = resolved
                note(f"model selected: {resolved}")

        ra = await run_turn(c1, TURN_A_TEXT, "turn_a", phase_ref)
        rb = await run_turn(c1, TURN_B_TEXT, "turn_b", phase_ref)
        rc = await run_turn(c1, TURN_C_TEXT, "turn_c", phase_ref)
        summary["turn_a_stop"] = (ra or {}).get("result", {}).get("stopReason") if ra else None
        summary["turn_b_stop"] = (rb or {}).get("result", {}).get("stopReason") if rb else None
        summary["turn_c_stop"] = (rc or {}).get("result", {}).get("stopReason") if rc else None
        note(f"turns A/B/C stopReasons: {summary['turn_a_stop']}/{summary['turn_b_stop']}/{summary['turn_c_stop']}")

        if not summary["load_session_advertised"]:
            note("loadSession=false — skipping kill/replay phases")
            summary["replay"] = {"loaded_ok": False, "skipped": "loadSession not advertised"}
            return summary

        # Turn D: kill mid-turn once chunks are flowing.
        phase_ref[0] = "turn_d_kill"
        d_chunks_seen = 0
        kill_fired = asyncio.get_event_loop().create_future()

        def d_pred(obj):
            nonlocal d_chunks_seen
            k = update_kind(obj)
            if k in ("agent_message_chunk", "agent_thought_chunk"):
                d_chunks_seen += 1
                if d_chunks_seen >= 3 and not kill_fired.done():
                    kill_fired.set_result(True)
            return False  # never resolve the waiter; we use kill_fired

        c1.update_waiters.append((d_pred, asyncio.get_event_loop().create_future()))
        d_task = asyncio.create_task(run_turn(c1, TURN_D_TEXT, "turn_d_kill", phase_ref))
        try:
            await asyncio.wait_for(kill_fired, timeout=120)
            c1.sigkill()
            summary["kill_fired_after_chunks"] = d_chunks_seen
            note(f"SIGKILL fired after {d_chunks_seen} live chunks of turn D")
        except asyncio.TimeoutError:
            note("turn D produced <3 chunks in 120s — killing anyway")
            c1.sigkill()
        d_resp = None
        try:
            d_resp = await asyncio.wait_for(d_task, timeout=15)
        except Exception as ex:  # noqa: BLE001 — EOF/broken pipe expected after SIGKILL
            summary["turn_d_await_error"] = repr(ex)
        summary["turn_d_response_after_kill"] = d_resp is not None and d_resp is not False
        if d_resp:
            note(f"NOTE: turn D response arrived before/despite kill: {json.dumps(d_resp)[:200]}")
        await c1.shutdown()

        # Child 2: reload.
        phase_ref[0] = "init2"
        c2 = AcpClient(spec["argv"], str(cwd), frames, phase_ref, "child2",
                       vdir / "stderr-child2.log", spec.get("env"))
        c2.session_id = c1.session_id
        await c2.start()
        try:
            init2 = await c2.request("initialize", {
                "protocolVersion": 1,
                "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False},
                                         "terminal": False}}, timeout=60)
            phase_ref[0] = "load_replay"
            load_ok = False
            load_err = None
            try:
                load_resp = await c2.request(
                    "session/load",
                    {"sessionId": c2.session_id, "cwd": str(cwd), "mcpServers": []},
                    timeout=240)
                load_ok = "error" not in load_resp
                load_err = load_resp.get("error")
                summary["load_response"] = load_resp.get("result") if load_ok else load_resp
            except (asyncio.TimeoutError, ConnectionError) as ex:
                load_err = repr(ex)
            c2.record("mark", {"event": "load_response_received", "ok": load_ok})
            note(f"session/load ok={load_ok} err={json.dumps(load_err)[:300] if load_err else None}")
            phase_ref[0] = "post_load_trailer"
            await asyncio.sleep(3)

            if load_ok:
                re_resp = await run_turn(c2, TURN_E_TEXT, "turn_e", phase_ref)
                summary["post_load_prompt_ok"] = bool(
                    re_resp and (re_resp.get("result") or {}).get("stopReason"))
                note(f"post-load prompt ok={summary.get('post_load_prompt_ok')}")
            summary["replay_load_ok"] = load_ok
            summary["replay_load_error"] = load_err
        finally:
            phase_ref[0] = "teardown"
            await c2.shutdown()
    finally:
        summary["ended"] = now_iso()
        with open(vdir / "frames.jsonl", "w") as f:
            for fr in frames:
                f.write(json.dumps(fr) + "\n")
        analyze(frames, summary)
        with open(vdir / "summary.json", "w") as f:
            json.dump(summary, f, indent=2)
    return summary


def analyze(frames, summary):
    live_phases = {"turn_a", "turn_b", "turn_c", "turn_d_kill"}
    live = {"kinds": {}, "keys_by_kind": {}, "msgid_values": {}, "tool_call_ids": []}
    replay = {"kinds": {}, "keys_by_kind": {}, "msgid_values": {}, "tool_call_ids": [],
               "user_chunks": [], "after_response_conversation": 0,
               "trailer_kinds_after_response": []}
    saw_response_mark = False
    replay_updates = []

    for fr in frames:
        if fr["dir"] == "mark" and fr["frame"].get("event") == "load_response_received":
            saw_response_mark = True
            continue
        if fr["dir"] != "in":
            continue
        obj = fr["frame"]
        if obj.get("method") != "session/update":
            continue
        k = update_kind(obj)
        body = update_body(obj) or {}
        bucket = None
        if fr["phase"] in live_phases:
            bucket = live
        elif fr["phase"] in ("load_replay", "post_load_trailer"):
            bucket = replay
            replay_updates.append((fr["phase"], k, body, saw_response_mark))
            if saw_response_mark:
                replay["trailer_kinds_after_response"].append(k)
                if k in CONVERSATION_KINDS:
                    replay["after_response_conversation"] += 1
        if bucket is None:
            continue
        bucket["kinds"][k] = bucket["kinds"].get(k, 0) + 1
        keys = sorted(body.keys())
        bucket["keys_by_kind"].setdefault(k, set()).update(keys)
        for id_field in ("messageId", "message_id", "id", "turnId", "turn_id"):
            if id_field in body:
                bucket["msgid_values"].setdefault(f"{k}.{id_field}", []).append(body[id_field])
        if k == "tool_call" and body.get("toolCallId"):
            bucket["tool_call_ids"].append(body["toolCallId"])
        if k == "user_message_chunk" and bucket is replay:
            txt = (body.get("content") or {}).get("text", "")
            replay["user_chunks"].append(txt[:80])

    for b in (live, replay):
        b["keys_by_kind"] = {k: sorted(v) for k, v in b["keys_by_kind"].items()}

    # Interrupted-turn (D) shape in replay.
    d_marker = "Count from 1 to 40"
    d_user_idx = None
    for i, (_, k, body, _) in enumerate(replay_updates):
        if k == "user_message_chunk" and d_marker in ((body.get("content") or {}).get("text") or ""):
            d_user_idx = i
            break
    if d_user_idx is None:
        shape = "absent"
    else:
        after = [k for (_, k, _, _) in replay_updates[d_user_idx + 1:]
                 if k in ("agent_message_chunk", "agent_thought_chunk", "tool_call",
                           "tool_call_update")]
        shape = "user_only" if not after else f"user_plus_{len(after)}_agent_updates"
    summary["interrupted_turn_replay_shape"] = shape

    summary["live_analysis"] = live
    summary["replay_analysis"] = replay
    summary["live_vs_replay_msgid_overlap"] = {
        key: len(set(map(str, live["msgid_values"].get(key, []))) &
                  set(map(str, replay["msgid_values"].get(key, []))))
        for key in set(live["msgid_values"]) | set(replay["msgid_values"])
    }
    summary["tool_call_id_overlap"] = len(set(live["tool_call_ids"]) & set(replay["tool_call_ids"]))


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--vendor", required=True, choices=sorted(VENDORS))
    ap.add_argument("--outdir", default=str(Path(__file__).parent / "out"))
    args = ap.parse_args()
    print(f"== probing {args.vendor} ==", flush=True)
    t0 = time.time()
    summary = await probe_vendor(args.vendor, args.outdir)
    print(f"== {args.vendor} done in {time.time()-t0:.0f}s ==", flush=True)
    print(json.dumps({k: v for k, v in summary.items()
                       if k in ("load_session_advertised", "replay_load_ok",
                                 "interrupted_turn_replay_shape",
                                 "live_vs_replay_msgid_overlap", "post_load_prompt_ok")},
                      indent=2))


if __name__ == "__main__":
    asyncio.run(main())
