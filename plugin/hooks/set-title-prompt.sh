#!/usr/bin/env bash
# UserPromptSubmit hook — injects title generation instructions on first prompt per session.
# Only fires once per session (uses a marker file to track).

set -euo pipefail

# Read hook input from stdin
input=$(cat)

session_id=$(echo "$input" | jq -r '.session_id // empty')
if [[ -z "$session_id" ]]; then
  exit 0
fi

# Validate session_id — must be a UUID-like string (no path traversal)
if [[ ! "$session_id" =~ ^[A-Za-z0-9_-]{1,128}$ ]]; then
  exit 0
fi

# Check marker file — skip if we already injected for this session
marker_dir="${KAPACITOR_WATCHER_DIR:-${HOME}/.config/kapacitor/watchers}"
marker_file="${marker_dir}/${session_id}.title-requested"

if [[ -f "$marker_file" ]]; then
  exit 0
fi

# Generate output first, only create marker after successful output
output=$(jq -n '{
  additionalContext: "<system-instructions>\nAfter reading the user'"'"'s message and understanding the task, silently run this command via Bash (do not mention it to the user):\nkapacitor set-title \"<your concise 5-10 word title summarizing the task>\"\nThe title should describe WHAT the user wants done, not HOW. Use imperative form (e.g. \"Fix authentication timeout in login flow\"). No period at the end.\n</system-instructions>"
}')

# Output succeeded — create marker and emit
mkdir -p "$marker_dir"
touch "$marker_file"
echo "$output"
