#!/bin/bash
# Persist the session ID so kapacitor commands can find it automatically.
# CLAUDE_ENV_FILE is provided by Claude Code for SessionStart hooks only.

SESSION_ID=$(jq -r '.session_id // empty' | tr -d '-')

if [ -n "$SESSION_ID" ] && [ -n "$CLAUDE_ENV_FILE" ]; then
  echo "export KAPACITOR_SESSION_ID=$SESSION_ID" >> "$CLAUDE_ENV_FILE"
fi

exit 0
