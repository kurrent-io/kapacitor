# Guided-tour skill in the kcap plugin — design

Date: 2026-07-27
Status: proposed

## Motivation

A user who has just run `kcap setup` has hooks installed, sessions streaming, and no idea what
any of it buys them. Nothing in the product tells them. The skills we ship today (`recap`,
`errors`, `hide`, `disable`, `validate-plan`, `review-flows`, `agent-flows`) all assume the user
already knows what Capacitor does and which surface they want — they are tools for an
established user, not an onboarding path.

A guided-tour skill was authored and iterated against live trials outside this repo
(`kcap-quickstart-eval`, revision `c6717f1`). Its content — the menu template, the five tour
rules, the analytics queries, the completion-marker scheme, and the honesty rules — is the
product of those trials and is treated as **final** here. This spec covers only what it takes to
ship that content inside the kcap plugin, plus the discovery path that points users at it.

## Goal

`/kcap:guided-tour` is an invocable skill wherever the kcap plugin is installed, and a user who
finishes `kcap setup` is told to run it.

## Decisions

1. **Skill lands at `kcap/skills/guided-tour/SKILL.md`.** `kcap/skills/` is the single source of
   skills for every vendor: Claude Code reads it in place via the plugin marketplace entry, and
   every other vendor gets a `kcap-`-prefixed copy written to `~/.agents/skills/` (or the
   Kiro/Antigravity skills dir) by `AgentsSkillsInstaller`. There is no second copy to maintain
   for `.codex-plugin` — that manifest only points at `./.codex-mcp.json`.

2. **Frontmatter `name: guided-tour`.** Matches the existing short-name convention (`recap`,
   `errors`, …). `AgentsSkillsInstaller.RewriteNameFrontmatter` rewrites it to
   `kcap-guided-tour` on copy for non-Claude vendors, so the short name is the correct source
   form.

3. **Register in `AgentsSkillsInstaller.SourceNames`.** Without this the skill ships to Claude
   Code only. The list is also what `IsCurrent`/`Remove` iterate, so adding the name gives
   upgrade-refresh and clean uninstall for free.

4. **Content adaptations, and nothing else.** Only these edits to the source file:
   - `name: kcap-guided-tour` → `name: guided-tour` (decision 2).
   - Nothing else. The source at `c6717f1` already uses the plugin-namespaced `/kcap:guided-tour`
     invocation and already references MCP tools the way `recap/SKILL.md` does (bare tool name +
     server name, e.g. "`query_analytics` on the `kcap-analytics` MCP server") rather than
     Claude-Code-specific `mcp__plugin_kcap_*` ids — verified by grep, zero hits for both
     `/kcap-guided-tour` and `mcp__`.
   - One scoping fix: the `Never` bullet reading "`curate apply` (writes files), `kcap-memory`,
     `kcap-flows` — empty or inert on day one" asserts something about every org. Reword to
     scope it to a fresh workspace. This is a factual-accuracy fix, not a rewording of tour
     mechanics.

5. **Setup call-to-action.** `SetupCommand.HandleAsync` prints, as the last thing on a successful
   run, exactly:

   ```
   New here? Run /kcap:guided-tour in your agent for a guided tour of what got recorded.
   ```

   The string lives in an `internal const` so a unit test can pin the wording; rendering escapes
   it into a Spectre panel for prominence rather than re-wording it inline. It prints
   unconditionally on success — even a setup that installed no hooks leaves a user who may
   already have sessions from a prior install, and the tour degrades gracefully when there is
   nothing to show (it offers install/import instead).

6. **Version bump `kcap/.claude-plugin/plugin.json` 1.7.2 → 1.8.0.** Repo convention is a minor
   bump per feature (1.6.0 → 1.7.0 added the flows MCP server; patch bumps were skill-behavior
   fixes). `kcap/.codex-plugin/plugin.json` is left at 1.6.0: it has never been bumped in
   lockstep since the rename commit, it declares only the Codex MCP server map, and nothing in
   this change touches that map. Bumping it would imply a versioning contract that does not
   exist.

7. **No server changes.** The skill's queries read `v_an_*` analytics views, which are part of
   the server's governed schema. This PR does not touch them.

## Non-goals

- Rewording, reordering, or restructuring any of the tour content. It is final.
- Fixing the pre-existing staleness in the skill lists in `kcap/README.md` and
  `help-plugin.txt` (both predate `review-flows` and `agent-flows`). This PR adds
  `guided-tour` to those lists and leaves the older gap alone.
- Changing the other six skills.
- Any `.codex-plugin` restructuring.

## Public-repo constraint

kcap-cli is public. The skill file must carry no usernames, session ids, repo names, or
org-specific numbers. Verified by grep before commit: zero hits for `stktung` and zero hits for
any 16+-hex-character run.

## Surfaces to update

| Surface | Change |
|---|---|
| `kcap/skills/guided-tour/SKILL.md` | new file |
| `src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs` | `SourceNames` += `guided-tour` |
| `src/Capacitor.Cli/Commands/SetupCommand.cs` | closing call-to-action |
| `kcap/.claude-plugin/plugin.json` | 1.7.2 → 1.8.0 |
| `kcap/README.md` | skill list + plugin-structure tree |
| `README.md` | skills nav line + Codex skill table |
| `src/Capacitor.Cli.Core/Resources/help-plugin.txt` | `~/.agents/skills/kcap-{…}` list |

## Acceptance

- `/kcap:guided-tour` is invocable in Claude Code with the plugin installed.
- `kcap plugin install --skills` writes `~/.agents/skills/kcap-guided-tour/SKILL.md` with
  `name: kcap-guided-tour`.
- A successful `kcap setup` ends with the exact call-to-action line.
- `grep -c 'stktung\|[0-9a-f]\{16,\}' kcap/skills/guided-tour/SKILL.md` → 0.
- No occurrence of `/kcap-guided-tour` anywhere in the repo.
- Unit + integration suites pass; `dotnet publish -c Release` shows no IL3050/IL2026.

## Test strategy

- `AgentsSkillsInstallerTests` already iterates a local `SourceNames` mirror — extend it with
  `guided-tour` so install/remove/refresh coverage picks the new skill up.
- New `SetupCommandTests` case pinning the call-to-action constant's exact wording (mirrors the
  existing `LiveRecordingRestartTip` tests, which assert on the returned string rather than
  driving Spectre output).
- Live manual verification: `kcap plugin install --skills` into a scratch HOME, then invoke the
  skill in a real Claude Code session.

## Untested-by-live-run paths (to be listed in the PR)

The tour's own runtime behaviour cannot be covered by this repo's test suite — it is a prompt.
The PR description must name which paths a live trial exercised and which did not: the
install-offer path (kcap absent), the import path (no sessions), the marker round-trip across
sessions, and the long-operation progress notes during `kcap eval`.
