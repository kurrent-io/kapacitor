#!/usr/bin/env python3
"""AI-1505 probe hook: records every Cursor hook payload plus the on-disk
agent-transcripts state at the instant the hook fired.

Logs to a FILE; stdout carries ONLY the JSON response (never tee-wrap a
stdout-consuming hook -- that fakes 'harness ignores the output' and can hang).
"""
import glob
import json
import os
import sys
import time

LOG = "/private/tmp/kcap-cursor-subagent-probe-1505/probe.log"
WINDOW = 900  # only snapshot transcripts touched in the last 15 min


def scan(path):
    """Return (lines, has_first_user_text, has_task_tool_use) for a transcript."""
    n = 0
    first_user = False
    task = False
    try:
        with open(path) as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                n += 1
                try:
                    obj = json.loads(line)
                except Exception:
                    continue
                content = (obj.get("message") or {}).get("content") or []
                if obj.get("role") == "user":
                    for b in content:
                        if isinstance(b, dict) and b.get("type") == "text":
                            first_user = True
                elif obj.get("role") == "assistant":
                    for b in content:
                        if (isinstance(b, dict) and b.get("type") == "tool_use"
                                and b.get("name") in ("Task", "Agent")):
                            task = True
    except OSError:
        pass
    return n, first_user, task


def main():
    raw = sys.stdin.read()
    ts = time.time()
    try:
        payload = json.loads(raw)
    except Exception:
        payload = {}
    event = payload.get("hook_event_name") or "?"

    out = ["=== t=%.6f event=%s" % (ts, event), "PAYLOAD %s" % raw.strip()]
    pattern = os.path.expanduser("~/.cursor/projects/*/agent-transcripts/*/*.jsonl")
    for p in sorted(glob.glob(pattern)):
        try:
            st = os.stat(p)
        except OSError:
            continue
        if ts - st.st_mtime > WINDOW:
            continue
        n, first_user, task = scan(p)
        out.append(
            "  DIR sid=%s lines=%d bytes=%d mtime=%.3f age=%.3fs firstUser=%s taskToolUse=%s"
            % (os.path.basename(os.path.dirname(p)), n, st.st_size,
               st.st_mtime, ts - st.st_mtime, first_user, task))
    out.append("")

    with open(LOG, "a") as fh:
        fh.write("\n".join(out) + "\n")

    if event == "beforeSubmitPrompt":
        sys.stdout.write(json.dumps({"continue": True}) + "\n")
    elif event in ("subagentStart", "preToolUse", "beforeShellExecution",
                   "beforeReadFile"):
        sys.stdout.write(json.dumps({"permission": "allow"}) + "\n")
    else:
        sys.stdout.write("{}\n")


if __name__ == "__main__":
    main()
