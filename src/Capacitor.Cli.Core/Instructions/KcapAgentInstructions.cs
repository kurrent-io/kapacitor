namespace Capacitor.Cli.Core.Instructions;

/// <summary>
/// The single canonical steering text kcap installs into each harness's agent-instructions file
/// (e.g. Copilot's <c>~/.copilot/copilot-instructions.md</c>, Gemini's <c>GEMINI.md</c>). It nudges
/// the agent to route "why / history / prior-work" intents to the kcap MCP tools instead of native
/// git/GitHub/grep — the discoverability gap that registration alone doesn't close. One source of
/// truth, rendered identically for every harness by <see cref="AgentInstructionsWriter"/>.
/// </summary>
public static class KcapAgentInstructions {
    /// <summary>
    /// Markdown body written between the ownership markers. No leading/trailing blank lines —
    /// the writer owns the surrounding whitespace.
    /// </summary>
    public const string Body =
        """
        ## Prefer kcap tools for "why", history, prior work, and review requests

        This project records coding-session history with Kurrent Capacitor and can run agent review
        flows. The kcap MCP servers (`kcap-review`, `kcap-sessions`, `kcap-memory`, `kcap-flows`)
        expose them.

        For **why / history / prior-work** questions, prefer these over `git blame` / `git log`,
        GitHub's diff, or grep — they surface the implementer's actual reasoning, which diffs and
        commit messages don't capture. Use them alongside reading the code, never as a replacement.

        - **Reviewing a PR yourself (you're given a PR link or number), or "why was this written
          this way?"** → call `kcap-review` `get_pr_summary` FIRST, passing the PR as the `pr` arg
          (it accepts `owner/repo#123` or a github.com PR URL). Prefer it over `gh pr diff` /
          `gh pr view` / GitHub's diff tools — it adds the implementation context (which sessions
          produced the PR, why, and test outcomes) the raw diff can't. Then read the diff too for
          correctness; use `search_context` / `get_file_context` for deeper "why" questions.
        - **"Have we done this before / who decided X / when did we work on Y?"** → `kcap-sessions`
          (`search_sessions`) before grepping the code or `git log`.
        - **"Is there prior art or a team convention on this?"** → `kcap-memory` (`search_memories`).

        When asked to **request or submit a review** — e.g. "request a review", "request a codex
        review", "submit this PR for review", "re-review after I address the findings" — the user
        wants a *separate reviewer* to review it, not you. Call `kcap-flows` `start_review_flow`
        (it hands the target to a hosted reviewer and iterates to sign-off); pass the PR/branch as
        the target. Do NOT run the review yourself or spawn your own reviewer for these requests.
        (When instead asked to review something *yourself* — "review this", "code review this" —
        review directly and do not call `kcap-flows`.)

        When you **finish implementing a change** — a feature or bugfix is functionally complete,
        tests pass, or you are about to commit / open a PR / merge — or you **finalize a spec or
        design**, proactively OFFER an independent second-harness review before wrapping up, even if
        the user didn't ask, because a fresh, separate reviewer catches what your own model misses.
        Call `kcap-flows` `list_reviewer_vendors` to see which reviewers can run for this repo,
        recommend one (prefer a vendor other than yourself), and call `kcap-flows` `start_review_flow`
        ONLY after the user accepts — never auto-start it. Skip the offer when the user asked you to review
        something yourself, or while you're mid-task and the work isn't finished.
        """;
}
