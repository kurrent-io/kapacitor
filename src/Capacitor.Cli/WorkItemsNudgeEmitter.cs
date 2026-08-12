namespace Capacitor.Cli;

/// <summary>
/// Builds the SessionStart "work items" nudge fragment: short standing guidance telling the agent it
/// is in a recorded Kurrent Capacitor session and that it should register the session with its work
/// item and declare the work's structure (breakdown + dependencies) as it discovers them.
///
/// <para>Unlike the team-memory index and judge-fact guidelines, this nudge is a pure function of the
/// current session id — it needs no server round-trip and no lease. It is therefore composed at the
/// output layer (<see cref="SessionStartMemory.SessionStartMemoryOutputAdapters"/>), AFTER the
/// lease-gated memory/guidelines fragment is decided, so its presence can never change the
/// acquire/complete/retry state of those lanes.</para>
///
/// <para>The current session id is rendered verbatim so a harness without an ambient
/// <c>KCAP_SESSION_ID</c> can pass it to <c>declare_work_item</c> explicitly. Returns <c>null</c> when
/// there is no usable session id, so the caller emits nothing.</para>
/// </summary>
static class WorkItemsNudgeEmitter {
    /// <summary>Upper bound on the rendered session id — a defensive guard mirroring the memory
    /// emitter's scalar cap, so a malformed hook payload can't inject an unbounded string.</summary>
    const int MaxSessionIdLength = 256;

    /// <summary>
    /// Resolves the nudge fragment for a harness: <c>null</c> (emit nothing) when the user opted out
    /// (<c>disable_workitems_nudge</c>), when <c>kcap-workitems</c> is not materialized in that
    /// harness's config (fail-closed availability gate), or when there is no usable session id;
    /// otherwise the built nudge. The optional <paramref name="home"/> / <paramref name="codexConfigPath"/>
    /// are the availability gate's test seams and are null in production.
    /// </summary>
    public static string? Resolve(SessionStartMemory.SessionStartHarness harness, string? sessionId, bool optedOut,
                                  string? home = null, string? codexConfigPath = null) {
        if (optedOut) return null;
        if (!WorkItemsNudgeAvailability.IsRegisteredFor(harness, home, codexConfigPath)) return null;
        return Build(sessionId);
    }

    public static string? Build(string? sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var id = sessionId.Trim();
        if (id.Length == 0 || id.Length > MaxSessionIdLength) return null;

        return
            "## Work items\n" +
            $"You are in a recorded Kurrent Capacitor session (id: `{id}`). When you start work on a " +
            "tracked item, register this session with it using the kcap-workitems MCP tool " +
            "`declare_work_item` — attach by the issue key, PR number, or existing work-item id you are " +
            "working on; create a new item by title ONLY when there is genuinely no tracker item, and " +
            "never invent an id for an item that already exists. As you discover structure, declare it: " +
            "`declare_work_breakdown` when the work splits into a parent and parts, and " +
            "`declare_work_relation` when you find a dependency (`blocks` = this item blocks the other; " +
            "`blocked_by` = this item is blocked by the other). If a tool cannot resolve the session " +
            "automatically, pass `session_id` explicitly.";
    }
}
