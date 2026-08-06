#!/usr/bin/env python3
"""Minimal stdio MCP server standing in for the kcap-flow-result channel.

Exposes ONE tool under the production name (submit_review_result) and logs every JSON-RPC
request it receives to $PROBE_MCP_LOG. The log is the evidence: a tools/call line there is the
only proof the reviewer could actually deliver a result, as opposed to a server that merely
started (the server_initialized trap this probe family already sprang once).
"""

import json
import os
import sys

LOG = os.environ.get("PROBE_MCP_LOG", "/tmp/probe-mcp.log")
NONCE = os.environ.get("PROBE_NONCE", "NONCE-UNSET")

TOOL = {
    "name": "submit_review_result",
    "description": "Submit the review result. Call this exactly once to deliver findings.",
    "inputSchema": {
        "type": "object",
        "properties": {"summary": {"type": "string", "description": "The review summary"}},
        "required": ["summary"],
    },
}


def log(event, payload):
    with open(LOG, "a") as f:
        f.write(json.dumps({"event": event, "payload": payload}) + "\n")
        f.flush()


def respond(rid, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": rid, "result": result}) + "\n")
    sys.stdout.flush()


def main():
    log("started", {"argv": sys.argv, "nonce": NONCE})
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            log("unparseable", line[:500])
            continue

        method = req.get("method", "")
        rid = req.get("id")
        log("request", {"method": method, "id": rid, "params": req.get("params")})

        if method == "initialize":
            respond(rid, {
                "protocolVersion": req.get("params", {}).get("protocolVersion", "2025-06-18"),
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "kcap-flow-result", "version": "probe"},
            })
        elif method == "tools/list":
            respond(rid, {"tools": [TOOL]})
        elif method == "tools/call":
            # The nonce is what proves the model saw THIS server's output, not its own invention.
            respond(rid, {"content": [{"type": "text", "text": f"ACCEPTED {NONCE}"}]})
        elif rid is not None:
            respond(rid, {})
        # notifications (no id) need no reply


if __name__ == "__main__":
    main()
