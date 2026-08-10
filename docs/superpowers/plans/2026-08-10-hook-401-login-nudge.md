# Hook HTTP 401 re-login nudge — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a hook POST is rejected with HTTP 401, tell the user to run `kcap login` instead of surfacing an opaque hook error or nothing at all.

**Architecture:** A 401 stops being a generic non-2xx failure and becomes a recognized outcome. On the Claude path the two once-per-turn user-facing events (`stop`, `session-start`) write `{"systemMessage": "…"}` to stdout and exit 0 — Claude Code renders that as a user-visible notice, whereas any non-zero exit renders as its opaque "non-blocking status code" banner. Every other event exits 0 silently so a single turn can't stack duplicate notices. Non-Claude vendors can't carry a `systemMessage` (their stdout is a strict handshake contract the vendor parses), so only their stderr text becomes actionable. All three notice strings live in one Core type so they can't drift apart.

**Tech Stack:** .NET 10, NativeAOT, `System.Text.Json.Nodes`, TUnit on Microsoft Testing Platform, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-08-10-hook-401-login-nudge-design.md`
**Issue:** #509 / AI-1835. **Branch:** `alexeyzimarev/ai-1835-hook-401-login-nudge`

> **Historical record.** This plan was executed as written. Two things changed afterwards and are
> NOT reflected below — read the spec for the current design:
> 1. `AuthLapseNotice` was folded into `AuthRejectionNotice` (landed on main as #516 mid-review) and
>    deleted. Every `AuthLapseNotice.X` in the tasks below is now
>    `AuthRejectionNotice.RecordingNotice(StoredCredentialState.Y)` or `.VendorStderrLine(...)`.
> 2. Task 4 assumed all non-Claude vendors share `AgentHookPoster`. Cursor does not — it POSTs
>    directly and needed its own nudge.

## Global Constraints

- **401 only.** Never treat 403 as a credential problem — `kcap login` would not fix it.
- **Never change outcome classification or non-401 exit codes.** `HookPostOutcome.Failed` stays `Failed`; a 500 keeps its bare-status stderr line and exit 1.
- **AOT:** verify no IL3050/IL2026 with `dotnet publish -c Release`. `dotnet build` does NOT surface them.
- **Never use collection expressions for `JsonArray`** (`[a, b]` compiles to `Add<T>()`, which needs dynamic code). Not needed in this plan — every JSON object here is `new JsonObject { … }`, which is fine.
- **Test names are snake_case**, matching the surrounding files.
- **TUnit filtering uses `--treenode-filter`, not `--filter`.** The bare `"*ClassName*"` glob matches zero tests silently; use `/*/*/ClassName/*`.
- **No Linear issue numbers in code comments.** Use the GitHub number (`#509`) if a reference is genuinely needed.
- **README must be updated in the same PR** as any user-facing behaviour change (project rule; has caused doc-only follow-up PRs before).
- Notice strings are **exact**, copied verbatim from the spec — the tests assert on them.

## File Structure

| File | Responsibility |
|---|---|
| `src/Capacitor.Cli.Core/AuthLapseNotice.cs` | **Create.** Vendor-neutral notice text for every auth-lapse kind, plus the vendor stderr line builder. Pure — no I/O, no `Console`, no JSON envelope (mirrors `VersionNudgeEmitter`). |
| `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` | **Modify.** Pre-flight arm reads its strings from `AuthLapseNotice`; the shared-path and `session-start` 401 arms gain the notice + exit 0. |
| `src/Capacitor.Cli/Commands/AgentHookPoster.cs` | **Modify.** Both bare-status stderr sites (`:105`, `:313`) render the actionable line on a 401. |
| `test/Capacitor.Cli.Tests.Unit/AuthLapseNoticeTests.cs` | **Create.** Locks the exact strings and the stderr builder. |
| `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs` | **Modify.** Four new tests over the 401/500 arms. |
| `test/Capacitor.Cli.Tests.Unit/AgentHookPosterTests.cs` | **Modify.** 401 stderr wiring + outcome-unchanged test. |
| `README.md` | **Modify.** One sentence in the `kcap whoami` paragraph under *Getting started*. |

---

### Task 1: `AuthLapseNotice` in Core, with the existing strings moved into it

Pure refactor plus one new string. No behaviour change: the two moved strings are byte-identical to their current inline site, so existing tests must stay green untouched.

**Files:**
- Create: `src/Capacitor.Cli.Core/AuthLapseNotice.cs`
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs:377-387`
- Test: `test/Capacitor.Cli.Tests.Unit/AuthLapseNoticeTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `Capacitor.Cli.Core.AuthLapseNotice` — `const string Expired`, `const string NotAuthenticated`, `const string Rejected`; `static string VendorStderr(string agentTag, string endpoint)`. Tasks 2, 3 use `Rejected`; Task 4 uses `VendorStderr`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/AuthLapseNoticeTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Locks the auth-lapse notice wording. Every string is user-facing and asserted on by the
/// Claude-hook and poster tests, so a reword must be a deliberate edit here rather than a
/// silent drift between the pre-flight nudge and the server-rejection nudge.
/// </summary>
public class AuthLapseNoticeTests {
    [Test]
    public async Task every_notice_names_the_recovery_command() {
        await Assert.That(AuthLapseNotice.Expired).Contains("kcap login");
        await Assert.That(AuthLapseNotice.NotAuthenticated).Contains("kcap login");
        await Assert.That(AuthLapseNotice.Rejected).Contains("kcap login");
    }

    [Test]
    public async Task rejected_names_the_status_and_the_pause() {
        await Assert.That(AuthLapseNotice.Rejected).IsEqualTo(
            "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.");
    }

    [Test]
    public async Task moved_strings_keep_their_existing_wording() {
        await Assert.That(AuthLapseNotice.Expired).IsEqualTo(
            "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.");
        await Assert.That(AuthLapseNotice.NotAuthenticated).IsEqualTo(
            "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.");
    }

    [Test]
    public async Task vendor_stderr_carries_the_tag_route_status_and_command() {
        var line = AuthLapseNotice.VendorStderr("codex-hook", "stop");

        await Assert.That(line).IsEqualTo(
            "[kcap] codex-hook stop: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AuthLapseNoticeTests/*"
```

Expected: build failure — `AuthLapseNotice` does not exist (`CS0246`).

- [ ] **Step 3: Create the Core type**

Create `src/Capacitor.Cli.Core/AuthLapseNotice.cs`:

```csharp
namespace Capacitor.Cli.Core;

/// <summary>
/// The user-facing "your credential needs attention" wording, in one place so the pre-flight
/// nudge (the token store already knows the credential is dead) and the server-rejection nudge
/// (the store thought it was fine; the server said 401) cannot drift apart.
///
/// <para>Pure text, like <see cref="VersionNudgeEmitter"/>: no I/O, no <c>Console</c>, and no
/// vendor envelope. Claude wraps <see cref="Rejected"/> in a <c>systemMessage</c> JSON object;
/// vendors whose stdout is a handshake contract use <see cref="VendorStderr"/> instead.</para>
/// </summary>
public static class AuthLapseNotice {
    /// <summary>A token is stored but expired and could not be refreshed.</summary>
    public const string Expired =
        "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.";

    /// <summary>No usable token: never logged in, or the token belongs to another server.</summary>
    public const string NotAuthenticated =
        "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.";

    /// <summary>
    /// The store handed over a locally-valid token and the server rejected it anyway — a revoked
    /// session or an org mismatch. Names the status because the raw <c>HTTP 401</c> is what a user
    /// searching their transcript or an issue tracker will have seen.
    /// </summary>
    public const string Rejected =
        "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.";

    /// <summary>
    /// The stderr form for vendors with no user-facing stdout channel. Keeps the existing
    /// <c>[kcap] {tag} {endpoint}: HTTP 401</c> prefix — it is what the vendors' debug logs and
    /// existing issue reports show — and appends the recovery step.
    /// </summary>
    public static string VendorStderr(string agentTag, string endpoint) =>
        $"[kcap] {agentTag} {endpoint}: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording";
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AuthLapseNoticeTests/*"
```

Expected: PASS, 4 tests.

- [ ] **Step 5: Point the pre-flight arm at the shared strings**

In `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`, replace the inline strings in the pre-flight lapse arm (around line 377). Before:

```csharp
        if (authStatus is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
            if (command == "session-start") {
                var notice = new JsonObject {
                    ["systemMessage"] = authStatus == AuthStatus.Expired
                        ? "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume."
                        : "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording."
                };
                writer.WriteLine(notice.ToJsonString());
            }
            return 0;
        }
```

After — same behaviour, same wording, `WrongServer` still maps to `NotAuthenticated`:

```csharp
        if (authStatus is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
            if (command == "session-start") {
                var notice = new JsonObject {
                    ["systemMessage"] = authStatus == AuthStatus.Expired
                        ? AuthLapseNotice.Expired
                        : AuthLapseNotice.NotAuthenticated
                };
                writer.WriteLine(notice.ToJsonString());
            }
            return 0;
        }
```

- [ ] **Step 6: Run the whole Claude-hook suite to prove the move changed nothing**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"
```

Expected: PASS, same count as before the change. If any test fails on string content, the move was not verbatim — diff the strings rather than editing the test.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/AuthLapseNotice.cs src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/AuthLapseNoticeTests.cs
git commit -m "Collect the auth-lapse notice wording into one Core type"
```

---

### Task 2: Claude shared POST path — nudge on `stop`, exit 0 on any 401

The path the reported bug actually hit. `stop`, `notification`, `subagent-start` and `pre-compact` all land here.

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs:786-790`
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: `AuthLapseNotice.Rejected` from Task 1.
- Produces: nothing new. `HandleCore` keeps its `Task<int>` signature; the `stdout` parameter already exists and defaults to `Console.Out`.

- [ ] **Step 1: Write the failing tests**

Add to `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`, inside the existing class. `Fixture` already accepts a status code, and `SendWithRetryAsync` retries only transport faults — never a status — so both the 401 and 500 calls return on the first attempt.

```csharp
    // ── Server-rejected credential (HTTP 401) ───────────────────────────────────────────────
    // A 401 is not a transient failure the user can wait out. Exiting non-zero makes Claude
    // render its opaque "non-blocking status code" banner, which says nothing about recording
    // being paused; exit 0 plus a systemMessage says exactly what to do. Only `stop` nudges on
    // this path — `notification` fires on every permission prompt, so nudging there would stack
    // duplicate notices within one turn.

    [Test]
    public async Task stop_on_401_exits_zero_and_nudges_the_user_to_log_in() {
        using var fx = new Fixture(HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await ClaudeHookCommand.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
            "http://localhost", new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);

        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthLapseNotice.Rejected);
    }

    [Test]
    public async Task notification_on_401_exits_zero_without_a_notice() {
        using var fx = new Fixture(HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await ClaudeHookCommand.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
            "http://localhost", new StringReader(
                $$"""{"hook_event_name":"Notification","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    /// <summary>Regression guard on the arm this change must NOT touch: a real server fault keeps
    /// its bare-status stderr line and its non-zero exit, so a 500 still reads as a failure.</summary>
    [Test]
    public async Task stop_on_500_still_exits_non_zero_without_a_notice() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await ClaudeHookCommand.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
            "http://localhost", new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stdout.ToString()).IsEmpty();
    }
```

Add `using Capacitor.Cli.Core;` to the file's usings if it is not already present (it is — line 4 — so no edit needed; verify rather than assume).

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"
```

Expected: `stop_on_401_exits_zero_and_nudges_the_user_to_log_in` FAILS (exit is 1, stdout empty → `JsonNode.Parse("")` throws or returns null). `notification_on_401_exits_zero_without_a_notice` FAILS (exit is 1). `stop_on_500_still_exits_non_zero_without_a_notice` PASSES already — it characterizes existing behaviour.

- [ ] **Step 3: Implement the 401 arm**

In `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs`, replace the shared non-success arm (around line 786). Before:

```csharp
        if (!response.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");

            return 1;
        }
```

After:

```csharp
        if (!response.IsSuccessStatusCode) {
            var code = (int)response.StatusCode;
            response.Dispose();

            // A rejected credential is not a transient fault: exit 0 so Claude renders a clean
            // notice instead of its opaque hook-error banner, and nudge from `stop` only — the
            // one once-per-turn event on this path. `notification` fires per permission prompt,
            // so nudging there would stack duplicates within a single turn.
            if (code == 401) {
                if (command == "stop") {
                    writer.WriteLine(new JsonObject { ["systemMessage"] = AuthLapseNotice.Rejected }.ToJsonString());
                }

                return 0;
            }

            Console.Error.WriteLine($"HTTP {code}");

            return 1;
        }
```

Note: `response` is declared without `using` at this call site, so the explicit `Dispose()` above is added deliberately — the previous code leaked it on every failure.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"
```

Expected: PASS, including every pre-existing test.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "Nudge the user to re-login when a stop hook is rejected with 401"
```

---

### Task 3: Claude `session-start` 401 arm — notice instead of silence

`session-start` has its own bounded POST, and today a 401 there drops the payload and returns 0 without a word.

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ClaudeHookCommand.cs` — the `session-start` non-success arm (search for `if (resp is null || !resp.IsSuccessStatusCode) {` inside the `command == "session-start"` block, around line 608)
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: `AuthLapseNotice.Rejected` from Task 1.
- Produces: nothing new.

- [ ] **Step 1: Write the failing test**

Add to `ClaudeHookCommandTests`. `session-start` needs `memoryStoreFactory` (the block builds a lease store), matching the existing session-start tests:

```csharp
    [Test, NotInParallel]
    public async Task session_start_on_401_exits_zero_and_nudges_the_user_to_log_in() {
        using var fx = new Fixture(HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await ClaudeHookCommand.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, System.Diagnostics.Stopwatch.GetTimestamp(),
            "http://localhost", new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            memoryStoreFactory: () => new SessionStartMemoryLeaseStore(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);

        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthLapseNotice.Rejected);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/session_start_on_401_exits_zero_and_nudges_the_user_to_log_in"
```

Expected: FAIL — exit is already 0, but stdout is empty, so `JsonNode.Parse("")` throws / the assertion sees no `systemMessage`.

- [ ] **Step 3: Implement**

Replace the `session-start` non-success arm. Before:

```csharp
            if (resp is null || !resp.IsSuccessStatusCode) {
                var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent && sessionId is not null) spool.Append(sessionId, "session-start", body);
                return 0;
            }
```

After:

```csharp
            if (resp is null || !resp.IsSuccessStatusCode) {
                var code      = resp is null ? 0 : (int)resp.StatusCode;
                var permanent = resp is not null && code is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent && sessionId is not null) spool.Append(sessionId, "session-start", body);

                // Without this the session's start event is dropped in silence — the user learns
                // nothing, and recording stays off for the rest of the session. The envelope below
                // is only built from a 2xx body, so this is the only stdout write on this arm.
                if (code == 401) {
                    writer.WriteLine(new JsonObject { ["systemMessage"] = AuthLapseNotice.Rejected }.ToJsonString());
                }

                return 0;
            }
```

- [ ] **Step 4: Run the whole Claude-hook suite to verify**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeHookCommandTests/*"
```

Expected: PASS, including the pre-flight-lapse and envelope tests (a 2xx session-start must still emit its `hookSpecificOutput` envelope and nothing else).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ClaudeHookCommand.cs test/Capacitor.Cli.Tests.Unit/ClaudeHookCommandTests.cs
git commit -m "Say why session-start was dropped when the server rejects the credential"
```

---

### Task 4: Vendor stderr line — Codex, Cursor, Gemini, Copilot, Pi, Kiro, OpenCode

All of them share `AgentHookPoster`, so both bare-status sites are one edit each. Exit codes and outcome classification stay exactly as they are.

**Files:**
- Modify: `src/Capacitor.Cli/Commands/AgentHookPoster.cs:104-107` and `:306-315`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentHookPosterTests.cs`

**Interfaces:**
- Consumes: `AuthLapseNotice.VendorStderr(agentTag, endpoint)` from Task 1.
- Produces: nothing new. `PostAsync` and `PostOrSpoolAsync` keep their signatures and their `HookPostOutcome` results.

- [ ] **Step 1: Write the failing test**

Add to `test/Capacitor.Cli.Tests.Unit/AgentHookPosterTests.cs`. `Console.SetError` is process-global, so this test is serialized — TUnit's `[NotInParallel]` with a shared key keeps it away from other console-capturing tests:

```csharp
    /// <summary>
    /// A 401 must stay <see cref="HookPostOutcome.Failed"/> — the outcome drives the caller's exit
    /// code and is not part of this change — while the line the user actually reads names the fix.
    /// These vendors have no user-facing stdout channel (their stdout is a handshake contract the
    /// vendor parses), so stderr is the only place a nudge can go.
    /// </summary>
    [Test, NotInParallel("ConsoleErrorRedirect")]
    public async Task Unauthorized_reports_Failed_and_names_kcap_login_on_stderr() {
        _server.Given(Request.Create().WithPath("/hooks/stop/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        var originalError = Console.Error;
        var captured = new StringWriter { NewLine = "\n" };
        HookPostOutcome outcome;

        try {
            Console.SetError(captured);
            outcome = await AgentHookPoster.PostAsync(
                Factory(AuthStatus.Ok), _server.Url!, "stop/codex", "{}", "codex-hook");
        } finally {
            Console.SetError(originalError);
        }

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
        await Assert.That(captured.ToString().Trim()).IsEqualTo(
            AuthLapseNotice.VendorStderr("codex-hook", "stop/codex"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentHookPosterTests/*"
```

Expected: FAIL — stderr reads `[kcap] codex-hook stop/codex: HTTP 401`, without the recovery clause.

- [ ] **Step 3: Implement both sites**

In `PostAsync` (around line 104). Before:

```csharp
                if (!resp.IsSuccessStatusCode) {
                    Console.Error.WriteLine($"[kcap] {agentTag} {endpoint}: HTTP {(int)resp.StatusCode}");
                    return HookPostOutcome.Failed;
                }
```

After:

```csharp
                if (!resp.IsSuccessStatusCode) {
                    var code = (int)resp.StatusCode;
                    // These vendors have no systemMessage channel, so the stderr line is the only
                    // place a rejected credential can name its own fix.
                    Console.Error.WriteLine(code == 401
                        ? AuthLapseNotice.VendorStderr(agentTag, endpoint)
                        : $"[kcap] {agentTag} {endpoint}: HTTP {code}");
                    return HookPostOutcome.Failed;
                }
```

In `PostOrSpoolAsync` (around line 313), where `code` is already in scope from the transient check above it. Before:

```csharp
                Console.Error.WriteLine($"[kcap] {agentTag} {endpoint}: HTTP {code}");

                return HookPostOutcome.Failed;
```

After:

```csharp
                Console.Error.WriteLine(code == 401
                    ? AuthLapseNotice.VendorStderr(agentTag, endpoint)
                    : $"[kcap] {agentTag} {endpoint}: HTTP {code}");

                return HookPostOutcome.Failed;
```

Add `using Capacitor.Cli.Core;` to `AgentHookPoster.cs` only if absent — it is already there (line 2), so verify rather than assume.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentHookPosterTests/*"
```

Expected: PASS, including `Server_error_reports_Failed` (the 500 keeps the bare-status line).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/AgentHookPoster.cs test/Capacitor.Cli.Tests.Unit/AgentHookPosterTests.cs
git commit -m "Name the fix on the vendor hooks' 401 stderr line"
```

---

### Task 5: README, full suite, AOT verification

**Files:**
- Modify: `README.md` (the `kcap whoami` paragraph under `## Getting started`, around line 111)

**Interfaces:**
- Consumes: the behaviour from Tasks 2–4.
- Produces: nothing.

- [ ] **Step 1: Add the README sentence**

Find the paragraph that begins `Verify with \`kcap whoami\` and \`kcap status\`.` Insert this sentence immediately after `If the server can't be reached it says so and still exits 0, so it stays usable offline.` and immediately before `\`kcap status\` prints its own **Version** line` — the whoami material and the status material stay unmixed:

```markdown
If the server rejects your token while a session is running, the hook now says so in the agent —
`[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap
login' to resume.` — instead of surfacing an opaque hook error, so you no longer have to run
`kcap whoami` to work out why recording stopped.
```

Keep the existing line wrapping style of the surrounding paragraph.

- [ ] **Step 2: Run the full unit suite**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: PASS. Report the actual pass/fail counts; do not claim success without reading the summary line.

- [ ] **Step 3: Run the integration suite**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

Expected: PASS. If something fails, confirm whether it also fails on `main` before treating it as caused by this change.

- [ ] **Step 4: Verify no AOT warnings**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output. `dotnet build` does not surface these, so this step is not optional.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "Document the in-agent notice for a server-rejected credential"
```

---

## Manual verification (before opening the PR)

The unit tests prove the CLI emits the right bytes; they cannot prove Claude Code *renders* them. The bundle's embedded docs give `Stop` + `systemMessage` as a worked example, and headless `-p` does not fire `Stop` hooks at all, so confirm this interactively once:

- [ ] In a real interactive Claude Code session, point kcap at a server that returns 401 for `/hooks/stop` (or temporarily invalidate the stored token), send any prompt, and confirm the transcript shows the `Run 'kcap login' to resume.` notice — **not** a `Stop hook error` banner.

## PR

Reference both trackers in the PR **description** (not the title), per the project rule:

```
Closes #509

AI-1835
```

The spec commit already on this branch rides this PR — do not open a spec-only PR.
