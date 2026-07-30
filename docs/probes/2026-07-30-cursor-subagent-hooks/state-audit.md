# Read-only audit of stale Cursor subagent state

Purpose: evidence for D2a's explicit risk acceptance (whether marker/spool/ack
state exists in the wild). Point-in-time and single-machine — see the caveat.

## Commands (read-only; $CFG = ${KCAP_CONFIG_DIR:-$HOME/.config/kcap})
```bash
CFG="${KCAP_CONFIG_DIR:-$HOME/.config/kcap}"
find "$CFG/cursor-subagent-links"     -type f 2>/dev/null | wc -l   # link markers
find "$CFG/spool"                     -type f -name "*.jsonl" 2>/dev/null | wc -l
grep -rl "subagent-start" "$CFG/spool" 2>/dev/null | wc -l          # spooled starts
find "$CFG/cursor-subagent-start-ack" -type f 2>/dev/null | wc -l   # ack markers
```
Counts are **recursive** (`find -type f`), so a nested layout is still counted.

## Result — developer machine, 2026-07-30, macOS
```
cursor-subagent-links     : 0
spool (*.jsonl)           : 0
spool files w/ subagent-start: 0
cursor-subagent-start-ack : 0
```

## Caveat

One machine, one moment. This is **weak** evidence and explicitly NOT a basis for
assuming the population is clean. It is one of two grounds for deferring the
dual-routing remedy; the section 7 IDE procedure collects a second data point.
