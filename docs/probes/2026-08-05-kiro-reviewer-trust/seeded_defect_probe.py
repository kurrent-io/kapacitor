#!/usr/bin/env python3
"""Seeded-defect differential: does a Kiro reviewer, launched the way we launch it, actually REVIEW?

Every unit test in this feature asserts launch shape, containment or lifecycle. None of them can
tell a working reviewer from an inert one: a reviewer that submits `clean` without reading anything
satisfies "round completed", "zero human interactions", "session reaped" and "result channel
invoked" simultaneously. This is the differential that separates them.

  A (defect present) -> the result must be `findings` AND must name the planted defect
  B (defect removed) -> the same prompt over the fixed text must be `clean`

Neither half means much alone. A alone passes for a reviewer that always finds something; B alone
passes for one that always says clean. Only the pair is an oracle.

Launched through the SHIPPED configuration, not a convenient one:
  * `kiro-cli acp` with the production scoped trust list, including the namespaced result tool
  * an isolated, empty KIRO_HOME (so the operator's global MCP servers do not load)
  * the result channel injected via session/new.mcpServers, exactly as AcpReviewFlowMcp builds it

Cost: TWO billable requests, one per arm, on the cheapest tier. Usage:
  python3 seeded_defect_probe.py [--model deepseek-3.2] [--outdir DIR]
"""

import argparse
import asyncio
import json
import sys
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "2026-08-04-acp-reconnect-c0"))
from acp_c0_probe import AcpClient  # noqa: E402

HERE = Path(__file__).resolve().parent
MCP_SERVER = HERE / "result_channel_server.py"

# ALIASED, exactly as production injects it. Not the canonical id: the shipped admission rule
# compares the frame's title to "@{aliased-wire-name}/{tool}", and a GUID-bearing name is precisely
# the kind of thing a display title might truncate, wrap or reformat. Testing the canonical name
# would prove the easy case and miss the one that ships.
RESULT_CHANNEL = "kcap-flow-result-" + uuid.uuid4().hex
RESULT_TOOL = "submit_review_result"

# The SHIPPED rule, transcribed from UnattendedToolAdmission: strip one measured prefix, then the
# remainder must EQUAL an admitted id.
TITLE_PREFIX = "Running: "


def is_admitted(title, admitted):
    if not title:
        return False
    candidate = title[len(TITLE_PREFIX):] if title.startswith(TITLE_PREFIX) else title
    return candidate in admitted

# The production shape: fs_read + thinking + the namespaced result tools. Never fs_write, never
# execute_bash.
TRUST = f"fs_read,thinking,@{RESULT_CHANNEL}/{RESULT_TOOL},@{RESULT_CHANNEL}/send_flow_message"

# One defect, unambiguous and unique, so "did it find THIS" is decidable without judging prose.
# An off-by-one that drops the last element is the kind of thing any competent reviewer names.
DEFECTIVE = '''\
def take_all_but_nothing(items):
    """Return every item. Callers rely on the full list being returned."""
    result = []
    for i in range(len(items) - 1):
        result.append(items[i])
    return result
'''

FIXED = '''\
def take_all_but_nothing(items):
    """Return every item. Callers rely on the full list being returned."""
    result = []
    for i in range(len(items)):
        result.append(items[i])
    return result
'''

PROMPT = '''You are an unattended code reviewer. Review the function below for correctness.

Deliver your result by calling the submit_review_result tool exactly once:
  - kind "findings" with the specific defect described, if the code is incorrect
  - kind "clean", if the code is correct

Do not ask questions. Do not use any other tool.

```python
{code}
```
'''


def now_iso():
    return datetime.now(timezone.utc).isoformat()


class ResultCapturingClient(AcpClient):
    """Records the reviewer's submitted result and any permission frame it raised."""

    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.permission_requests = []

    async def _handle_server_request(self, obj):
        if obj.get("method") == "session/request_permission":
            self.permission_requests.append(obj.get("params", {}))
        await super()._handle_server_request(obj)


async def run_arm(label, code, model, outdir, trust=TRUST, ct_seconds=300):
    """One arm. Returns the result the reviewer submitted through the injected channel."""
    home = Path(tempfile.mkdtemp(prefix=f"kiro-seeded-home-{label}-"))
    work = Path(tempfile.mkdtemp(prefix=f"kiro-seeded-wt-{label}-"))
    log  = outdir / f"result-channel-{label}.log"

    frames, phase = [], [label]
    client = ResultCapturingClient(
        ["kiro-cli", "acp", "--trust-tools", trust],
        str(work), frames, phase, label, str(outdir / f"stderr-{label}.txt"),
        extra_env={"KIRO_HOME": str(home), "PROBE_RESULT_LOG": str(log)})

    await client.start()
    await client.request("initialize", {
        "protocolVersion": 1,
        "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}},
    })

    new = await client.request("session/new", {
        "cwd": str(work),
        "mcpServers": [{
            "name": RESULT_CHANNEL,
            "command": sys.executable,
            "args": [str(MCP_SERVER)],
            "env": [{"name": "PROBE_RESULT_LOG", "value": str(log)}],
        }],
    })
    session_id = (new.get("result") or {}).get("sessionId")

    await client.request("session/set_model", {"sessionId": session_id, "modelId": model})

    prompt_result = await client.request("session/prompt", {
        "sessionId": session_id,
        "prompt": [{"type": "text", "text": PROMPT.format(code=code)}],
    }, timeout=ct_seconds)

    submitted = None
    if log.exists():
        for line in log.read_text().splitlines():
            entry = json.loads(line)
            if entry.get("event") == "submit":
                submitted = entry["arguments"]

    try:
        client.proc.kill()
    except Exception:  # noqa: BLE001
        pass

    return {
        "stop_reason": (prompt_result.get("result") or {}).get("stopReason"),
        "permission_frames": len(client.permission_requests),
        "permission_details": client.permission_requests,
        "submitted": submitted,
    }


async def main(args):
    outdir = Path(args.outdir).resolve()
    outdir.mkdir(parents=True, exist_ok=True)

    a = ({"stop_reason": "skipped", "permission_frames": 0, "permission_details": [], "submitted": None}
         if args.arm == "B" else
         await run_arm("A-defect-present", DEFECTIVE, args.model, outdir,
                       trust="fs_read,thinking" if args.provoke_frame else TRUST))
    b = ({"stop_reason": "skipped", "permission_frames": 0, "permission_details": [], "submitted": None}
         if args.arm == "A" else
         await run_arm("B-defect-removed", FIXED, args.model, outdir,
                       trust="fs_read,thinking" if args.provoke_frame else TRUST))

    findings_text = ((a["submitted"] or {}).get("findings") or "").lower()

    # What the shipped policy would admit for this launch.
    admitted = {f"@{RESULT_CHANNEL}/{RESULT_TOOL}", f"@{RESULT_CHANNEL}/send_flow_message"}

    frames = [d for arm in (a, b) for d in arm["permission_details"]]
    titles = [(d.get("toolCall") or {}).get("title") for d in frames]

    # The criterion CHANGED with the policy. "Zero frames" was Fail's expectation and is exactly the
    # premise the last run falsified; under AllowlistedAutoApprove a frame for the launch's own tool
    # is expected and admitted. What must hold now is that every frame Kiro actually raises is one
    # the shipped rule ADMITS -- if a real title has a shape the exact match rejects, the reviewer is
    # reaped and the tightening broke the thing it was meant to fix.
    unadmitted = [t for t in titles if not is_admitted(t, admitted)]

    verdict = {
        "started": now_iso(),
        "model": args.model,
        "A_defect_present": a,
        "B_defect_removed": b,
        # The oracle. Both halves, or it proves nothing.
        "A_reported_findings": (a["submitted"] or {}).get("kind") == "findings",
        "A_named_the_defect": any(
            k in findings_text for k in ("len(items) - 1", "len(items)-1", "off-by-one",
                                         "last element", "last item", "final element")),
        "B_reported_clean": (b["submitted"] or {}).get("kind") == "clean",
        "aliased_channel": RESULT_CHANNEL,
        "permission_titles": titles,
        "every_frame_is_admitted": len(unadmitted) == 0,
        "unadmitted_titles": unadmitted,
        # Recorded, not asserted: frames are intermittent, so seeing none means this run could not
        # exercise admissibility -- not that admissibility is fine.
        "frames_observed": len(frames),
    }
    verdict["PASS"] = all([verdict["A_reported_findings"], verdict["A_named_the_defect"],
                           verdict["B_reported_clean"], verdict["every_frame_is_admitted"]])

    (outdir / "seeded-defect-summary.json").write_text(json.dumps(verdict, indent=2))
    print(json.dumps(verdict, indent=2))


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--model", default="deepseek-3.2")
    p.add_argument("--arm", choices=["A", "B"], help="run ONE arm (each costs a request)")
    # Frames are intermittent, so a run that sees none proves nothing about admissibility. Dropping
    # the namespaced trust entry provokes one DETERMINISTICALLY (measured: 1 frame, every time) while
    # leaving the title shape untouched -- and the title shape is the whole question.
    p.add_argument("--provoke-frame", action="store_true",
                   help="omit the namespaced trust entry to force a permission frame")
    p.add_argument("--outdir", default=str(HERE / "out-seeded"))
    asyncio.run(main(p.parse_args()))
