# Server-side corroboration for runs 1–2 (F1)

Independent of the probe's own project-level hooks: these rows were written by the
developer's GLOBAL `kcap hook --cursor` installation. `model` is carried ONLY on the
Cursor `sessionStart` payload, so a NULL `model` is evidence that no `sessionStart`
hook was ever received for that session.

Queried 2026-07-30 via the governed analytics view `v_an_sessions` (scope: global):

```sql
SELECT session_id, repo_hash, model, status, event_count, started_at, ended_at
FROM v_an_sessions
WHERE session_id LIKE '17e20135%' OR session_id LIKE '41841599%'
   OR session_id LIKE '30b68e33%' OR session_id LIKE '2170e5d0%'
ORDER BY started_at
```

Result (verbatim, `repo_hash` NULL for all four — the probe workspace has no remote):

| session_id | role | model | status | event_count | started_at | ended_at |
|---|---|---|---|---|---|---|
| `17e20135185149e6a2b8ea81ce4329c8` | run-1 parent | `claude-4.5-sonnet-thinking` | 1 | 9 | 2026-07-30T13:16:27.273292Z | 2026-07-30T13:17:02.502040Z |
| `30b68e33c13f444d8d31bf9539a42a28` | run-1 child  | **NULL** | 1 | 8 | 2026-07-30T13:16:41.542913Z | 2026-07-30T13:16:52.799111Z |
| `4184159907dc42c2bc873acfdff9638d` | run-2 parent | `claude-4.5-sonnet-thinking` | 1 | 9 | 2026-07-30T13:17:01.927641Z | 2026-07-30T13:17:34.483781Z |
| `2170e5d0f48c44e4bc0ce5a38717b8e3` | run-2 child  | **NULL** | 1 | 8 | 2026-07-30T13:17:12.320696Z | 2026-07-30T13:17:23.590749Z |

Both children were nonetheless ingested as TOP-LEVEL sessions (8 events each) — i.e.
the non-`sessionStart` hooks a child does fire are sufficient to create a session
server-side, which is why an unlinked child appears in the session list at all.

Caveat: this is a point-in-time query against a live tenant, not a reproducible
fixture. It corroborates F1; it is not independently re-runnable from this repo once
the rows age out.
