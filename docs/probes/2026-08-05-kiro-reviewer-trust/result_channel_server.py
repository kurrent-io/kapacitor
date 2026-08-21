#!/usr/bin/env python3
"""Stands in for the kcap-flow-result MCP channel, with the production tool names and shapes.

Records every tools/call to $PROBE_RESULT_LOG. That log is the evidence: a reviewer's result only
counts if it arrived HERE, because a turn ending end_turn proves the model stopped talking, not that
it delivered anything.
"""

import json
import os
import sys

LOG = os.environ.get("PROBE_RESULT_LOG", "/tmp/probe-result.log")

TOOLS = [
    {
        "name": "submit_review_result",
        "description": "Submit the review result. Call exactly once to deliver findings.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "kind":     {"type": "string", "enum": ["findings", "clean"]},
                "findings": {"type": "string", "description": "The findings text when kind is findings"},
            },
            "required": ["kind"],
        },
    },
    {
        "name": "send_flow_message",
        "description": "Send a short out-of-band note to the flow driver.",
        "inputSchema": {
            "type": "object",
            "properties": {"text": {"type": "string"}},
            "required": ["text"],
        },
    },
]


def log(event, **fields):
    with open(LOG, "a") as f:
        f.write(json.dumps({"event": event, **fields}) + "\n")
        f.flush()


def respond(rid, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": rid, "result": result}) + "\n")
    sys.stdout.flush()


def main():
    log("started")

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue

        method, rid, params = req.get("method", ""), req.get("id"), req.get("params") or {}

        if method == "initialize":
            respond(rid, {
                "protocolVersion": params.get("protocolVersion", "2025-06-18"),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "kcap-flow-result", "version": "probe"},
            })
        elif method == "tools/list":
            respond(rid, {"tools": TOOLS})
        elif method == "tools/call":
            name = params.get("name")
            args = params.get("arguments") or {}

            if name == "submit_review_result":
                log("submit", arguments=args)
            else:
                log("other_tool", name=name, arguments=args)

            respond(rid, {"content": [{"type": "text", "text": "accepted"}]})
        elif rid is not None:
            respond(rid, {})


if __name__ == "__main__":
    main()
