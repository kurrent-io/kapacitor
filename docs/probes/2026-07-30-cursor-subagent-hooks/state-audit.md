# Read-only audit of stale Cursor subagent state

Purpose: evidence for D2a's explicit risk acceptance (whether link-marker, spool or
ack state exists in the wild). Point-in-time and single-machine — see the caveat.

## Commands (read-only; $CFG = ${KCAP_CONFIG_DIR:-$HOME/.config/kcap})

`HookSpool` treats a session as having backlog if ANY of a live `<sid>.jsonl`, a
rotated `<sid>.<pid-seq>.draining` temp, or an ordered-drain `<sid>.ordered-*` temp
exists (`HookSpool.cs:170-172`). Counting only `*.jsonl` would under-report durable
backlog after a crash, so the total below counts **all regular files**.
```bash
CFG="${KCAP_CONFIG_DIR:-$HOME/.config/kcap}"
find "$CFG/cursor-subagent-links"     -type f 2>/dev/null | wc -l  # link markers
find "$CFG/cursor-subagent-start-ack" -type f 2>/dev/null | wc -l  # ack markers
find "$CFG/spool" -type f 2>/dev/null | wc -l                      # ALL spool files
find "$CFG/spool" -type f -name "*.jsonl"     2>/dev/null | wc -l  #   live
find "$CFG/spool" -type f -name "*.draining"  2>/dev/null | wc -l  #   rotated temps
find "$CFG/spool" -type f -name "*.ordered-*" 2>/dev/null | wc -l  #   ordered-drain temps
grep -rl "subagent-start" "$CFG/spool" 2>/dev/null | wc -l         # FILES containing >=1 match
```
All counts are **recursive** (`find -type f`).

## Result — developer machine, 2026-07-30, macOS
```
cursor-subagent-links (markers)      : 0
cursor-subagent-start-ack (markers)  : 0
spool, ALL regular files             : 1
  .jsonl (live)                      : 0
  .draining (rotated temps)          : 0
  .ordered-* (ordered-drain temps)   : 0
spool FILES containing subagent-start: 0
```

Note the last line counts **files containing at least one match**, not entries.

**On the one non-zero count.** The single regular file under `spool/` is `.last-drain`
— a zero-byte drain-bookkeeping marker, not a spool entry. All three backlog patterns
(`*.jsonl`, `*.draining`, `*.ordered-*`) are zero, so actual backlog is nil. This is
worth recording rather than smoothing over: the broader "all regular files" count exists
precisely because the narrower `*.jsonl`-only count would have under-reported real
backlog, and here it surfaced a file the narrow count missed — benign in this instance,
but the distinction is the point.

## Caveat

One machine, one moment. This is **weak** evidence and explicitly NOT a basis for
assuming the population is clean. It is one of two grounds for deferring the
dual-routing remedy; the section 7 IDE procedure collects a second data point.
