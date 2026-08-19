---
name: work-items
description: >-
  This skill should be used when you are planning or discovering the SHAPE of a
  work item — that it breaks into sub-tasks (a parent and its parts), or that
  one piece must land before another (a blocks / blocked-by dependency) — and
  you want that structure recorded so it shows up in Kurrent Capacitor's Home
  "Blockers & dependencies" view and progress figures. Use the `kcap mcp
  workitems` MCP tools to DECLARE the breakdown and relations. Do NOT use this
  skill for ordinary "attach this session to issue X" correlation alone (a
  single `declare_work_item` call, no structure) or when the work is a single
  indivisible task with no parts and no dependencies.
---

# Work items — declaring breakdown and dependencies

A work item's **breakdown** (a parent broken into parts) and its **dependencies**
(one item blocks / is blocked by another) are things you **declare** through the
`kcap mcp workitems` tools. The server never infers them — if you don't declare
the structure, the work item has an empty topology and Home renders no blockers,
no dependency graph, and no `n/m parts` progress figure for it.

Only declare structure that is **real and you are confident of**. Fabricated or
speculative parts/relations are worse than none. A single indivisible task needs
no breakdown.

## When to use these tools

- You've planned a work item as several sub-tasks → create the part items and
  declare the parent→parts breakdown.
- You know one item must be finished before another can start → declare the
  dependency.
- The structure changed (a part was dropped, a dependency no longer holds) →
  retract it.
- You want to see the current structure → read the topology.

## The flow

1. **Attach the session to its work item** (if it isn't already). `declare_work_item`
   with exactly one of `issue_key`, `pr_number`, `work_item_id`, or `new_title`.
   Check `get_session_work_items` first if unsure what the session is attached to.
2. **Create the part items.** Each part is itself a work item — create one per
   sub-task with `declare_work_item` (`new_title`), keeping the id each returns.
3. **Declare the breakdown.** `declare_work_breakdown` with `parent_id` and the
   `part_ids`. It is idempotent — re-declaring an existing part is accepted, not
   an error.
4. **Declare dependencies** where they exist. `declare_work_relation` with
   `from_id`, `to_id`, and `relation_kind` `"blocks"` (from_id blocks to_id) or
   `"blocked_by"` (from_id is blocked by to_id).
5. **Verify** with `get_work_item_topology` (pass the parent's `work_item_id`) —
   it returns the parent, parts, and dependencies you can see.

## Rules the server enforces

- **Same repository.** A parent and its parts, and both ends of a relation, must
  live in the same repository. Cross-repo structure is rejected.
- **One parent per part.** A part can belong to at most one parent breakdown.
- **No self-relation.** An item cannot block or be blocked by itself.
- **Idempotent declares.** Re-declaring an existing part or relation is fine.
- **Server-assigned attribution.** These tools take no `source`/`declared_by` —
  the server resolves the caller. Don't look for such arguments.
- **Retract, don't delete.** Use `retract_work_breakdown` / `retract_work_relation`
  when the structure changes.

## Tool reference

| Tool | Required args | Purpose |
|---|---|---|
| `declare_work_item` | exactly one of `issue_key` \| `pr_number` \| `work_item_id` \| `new_title` | Attach the session to a work item (or create one). `session_id` defaults to `KCAP_SESSION_ID`. |
| `get_session_work_items` | — | List what the current session is attached to. |
| `declare_work_breakdown` | `parent_id`, `part_ids` | Declare parent → parts. |
| `retract_work_breakdown` | `parent_id`, `part_ids` | Detach parts from the parent. |
| `declare_work_relation` | `from_id`, `to_id`, `relation_kind` (`blocks`\|`blocked_by`) | Declare a dependency. |
| `retract_work_relation` | `from_id`, `to_id`, `relation_kind` | Retract a dependency. |
| `get_work_item_topology` | `work_item_id` | Read parent, parts, and dependencies (visibility-scoped). |

## Requirements

Requires `kcap login`. The `kcap-workitems` MCP server is auto-registered for
every supported harness by `kcap setup` and by `kcap plugin install`.
