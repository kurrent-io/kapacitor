# Session Rail and Tabless Shell (AI-2199) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A 310px session rail (repository → worktree → session) in a restructured, tabless desktop shell: two top-level views (Home, Sessions), an additive `title` wire field, and the Agents tab deleted.

**Architecture:** `SessionRailViewModel` projects the daemon's `Agents` SourceCache through nested DynamicData groups (repo root → checkout path → session rows). `MainWindowViewModel` gains a `CurrentView` (Home/Sessions) alongside `CurrentWorkspace`; `MainWindow.axaml` swaps two surfaces instead of hosting a TabControl. The daemon stamps a truncated launch-prompt line as `title` on `AgentStatusDto`.

**Tech Stack:** .NET 10 / NativeAOT, Avalonia + ReactiveUI, DynamicData, TUnit (Microsoft Testing Platform), headless Avalonia tests via `AvaloniaSession`.

**Spec:** `docs/superpowers/specs/2026-08-25-ai2199-session-rail-design.md` — read it first; every decision below argues from it.

## Global Constraints

- Branch: `alexeyzimarev/ai-2199-desktop-shell-session-rail` (already created; spec committed on it).
- Namespaces follow directories (compiler-enforced): `Capacitor.App.ViewModels`, `Capacitor.App.Views`, etc.
- All UI-touching tests: `[NotInParallel("AvaloniaSession")]` and go through `AvaloniaSession.WithImmediateRxScheduler` / `DispatchAsync` (see `test/Capacitor.App.Tests.Unit/AvaloniaSession.cs`).
- Every `daemon.Agents`/`Snapshots` consumer must `ObserveOn(RxSchedulers.MainThreadScheduler)` BEFORE any operator that touches bound state.
- Wire rule (`StatusIpc.cs` doc): every field ALWAYS emitted, absent = JSON null, never omitted; additive fields are trailing with defaults; never add a `DefaultIgnoreCondition`.
- `dotnet build` does NOT surface AOT warnings — the final task runs `dotnet publish -c Release` and greps `IL[23][01][0-9]{2}`.
- Comments only where they add value; never narrate the change.
- Test suites run directly, e.g. `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` — filter with `--treenode-filter '/*/*/ClassName/*'` (NOT `--filter`).
- Commit after every task (small, green commits).

---

### Task 1: Wire field `AgentStatusDto.Title`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:38-47`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `AgentStatusDto(..., bool? HasTerminal = null, string? Title = null)` — trailing member, serialized `title`. Tasks 2, 3 rely on `Title`.

- [ ] **Step 1: Write the failing tests**

In `StatusIpcJsonTests.cs`:

1. In `DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order`, give the first agent a title by appending `Title: "Fix the flaky test"` to its constructor call (after `"Ada Lovelace"` — use the named argument), leave the second agent without one, and extend the pinned JSON string: first agent's tail becomes `"requester_display":"Ada Lovelace","has_terminal":null,"title":"Fix the flaky test"}` and the second's `"requester_display":null,"has_terminal":null,"title":null}`.
2. Add a new test mirroring `Old_agent_json_without_has_terminal_deserializes_to_null`:

```csharp
[Test]
public async Task Old_agent_json_without_title_deserializes_to_null() {
    var dto = new AgentStatusDto(
        "a1", "agent", "claude", "/repo", "Running",
        null, null, null, DateTime.UtcNow, null, null);
    var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
    var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"title\":[^,}]+", "");

    var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

    await Assert.That(back!.Title).IsNull();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/StatusIpcJsonTests/*'`
Expected: compile error (`Title` not defined) — that counts as the failing state.

- [ ] **Step 3: Add the field**

In `StatusIpc.cs`, extend the `AgentStatusDto` record with a trailing member after `HasTerminal`:

```csharp
    bool? HasTerminal = null,
    // The launch prompt's first non-blank line, truncated by the daemon (TitleFromPrompt) —
    // display text for session rows. Trailing + nullable: null = older daemon or no goal text.
    string? Title = null);
```

- [ ] **Step 4: Run to verify pass**

Same command as Step 2. Expected: PASS (all StatusIpcJsonTests).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs
git commit -m "AI-2199: additive title field on AgentStatusDto"
```

---

### Task 2: Daemon stamps `title` from the launch prompt

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (SnapshotAgentsForStatus, ~line 44; add `TitleFromPrompt`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:1513` (`SeedAgentForTest` gains `string? prompt = null`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs`

**Interfaces:**
- Consumes: `AgentStatusDto.Title` (Task 1); `AgentInstance.Prompt` (exists, `AgentOrchestrator.cs:24`).
- Produces: `internal static string? AgentOrchestrator.TitleFromPrompt(string? prompt)`; snapshots carry `Title`.

- [ ] **Step 1: Write the failing tests**

In `AgentStatusSnapshotTests.cs` (reuse the file's existing `Build()` fixture and `SeedAgentForTest` pattern — see its existing tests around line 190 for the seed/snapshot/cleanup shape). Serialize the snapshot the way the file's existing serialization assertions do (via `StatusIpcJsonContext.Default`):

```csharp
[Test]
public async Task Snapshot_title_is_first_line_truncated_or_null() {
    var fx = Build();
    try {
        fx.Orchestrator.SeedAgentForTest("t-short", prompt: "Fix the flaky test");
        fx.Orchestrator.SeedAgentForTest("t-multi", prompt: "\n  First real line  \nsecond line");
        fx.Orchestrator.SeedAgentForTest("t-long",  prompt: new string('x', 200));
        fx.Orchestrator.SeedAgentForTest("t-blank", prompt: "   \n  ");
        fx.Orchestrator.SeedAgentForTest("t-none");

        var byId = fx.Orchestrator.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

        await Assert.That(byId["t-short"].Title).IsEqualTo("Fix the flaky test");
        await Assert.That(byId["t-multi"].Title).IsEqualTo("First real line");
        await Assert.That(byId["t-long"].Title!.Length).IsEqualTo(80);
        await Assert.That(byId["t-long"].Title).EndsWith("…");
        await Assert.That(byId["t-blank"].Title).IsNull();
        await Assert.That(byId["t-none"].Title).IsNull();

        // The wire boundary, not just the in-memory DTO (spec §1).
        var json = JsonSerializer.Serialize(byId["t-short"], StatusIpcJsonContext.Default.AgentStatusDto);
        await Assert.That(json).Contains("\"title\":\"Fix the flaky test\"");
        var jsonNone = JsonSerializer.Serialize(byId["t-none"], StatusIpcJsonContext.Default.AgentStatusDto);
        await Assert.That(jsonNone).Contains("\"title\":null");
    } finally { await fx.CleanupAsync(); }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/AgentStatusSnapshotTests/*'`
Expected: compile error — `SeedAgentForTest` has no `prompt` parameter.

- [ ] **Step 3: Implement**

1. `SeedAgentForTest` (`AgentOrchestrator.cs:1513`): add `string? prompt = null` to the parameter list and pass it as the second positional argument of `new AgentInstance(id, prompt, model, ...)` (currently hardcoded `null`).
2. In `AgentOrchestrator.LocalIpc.cs`, add below `KindText`:

```csharp
    /// First non-blank line of the launch prompt, trimmed, capped at 80 chars (ellipsis when
    /// cut) — the status payload is re-sent on every revision, so the full prompt never rides it.
    internal static string? TitleFromPrompt(string? prompt) {
        if (prompt is null) return null;
        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length <= 80 ? line : line[..79] + "…";
        }
        return null;
    }
```

3. In `SnapshotAgentsForStatus`, add `Title: TitleFromPrompt(a.Prompt)` after `HasTerminal: a.Runtime.EmitsTerminalOutput`.

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS (whole class — the exact-payload tests in this file may need their pinned strings extended with `"title":null`; if any fail on the pinned JSON, extend those strings, that is the contract updating, not a regression).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs
git commit -m "AI-2199: daemon stamps title from the launch prompt"
```

---

### Task 3: `RailSessionViewModel` + shared status dots

**Files:**
- Create: `src/Capacitor.App/ViewModels/SessionStatusDots.cs`
- Create: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/SessionCardViewModel.cs` (use the shared dots)
- Test: `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`

**Interfaces:**
- Consumes: `AgentStatusDto` (incl. `Title` from Task 1), `UptimeFormat.Format(TimeSpan)`, `StatusColors`.
- Produces:
  - `static class SessionStatusDots { public static IBrush For(string status); }`
  - `sealed class RailSessionViewModel : ReactiveObject, IDisposable` with ctor `(AgentStatusDto dto, IObservable<string?> selectedAgentId, Action<string> open)` and members: `string Id`, `string Primary`, `string Sub`, `IBrush StatusDot`, `bool NeedsYou`, `string Tooltip`, `bool IsSelected` (OAPH), `ReactiveCommand<Unit, Unit> OpenCommand`, `DateTime CreatedAt` (internal sort key).

- [ ] **Step 1: Extract the shared dots (no behavior change)**

Create `SessionStatusDots.cs` by moving `SessionCardViewModel`'s four `ImmutableSolidColorBrush` statics and its `StatusDotFor` switch (keep its comments — the thread-affinity reasoning is the point of the type):

```csharp
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Capacitor.App.ViewModels;

/// Status → dot brush for session surfaces (Home cards, the rail). ImmutableSolidColorBrush,
/// not SolidColorBrush: these are built on the daemon client's pump thread and an immutable
/// brush has no thread affinity, which is also what makes the four instances shareable.
public static class SessionStatusDots {
    static readonly ImmutableSolidColorBrush RunningDot  = new(Color.Parse(StatusColors.Connected));
    static readonly ImmutableSolidColorBrush StartingDot = new(Color.Parse(StatusColors.InProgress));
    static readonly ImmutableSolidColorBrush FailedDot   = new(Color.Parse(StatusColors.Disrupted));
    static readonly ImmutableSolidColorBrush NeutralDot  = new(Color.Parse(StatusColors.Unavailable));

    // Running/Starting/Failed are the daemon's own open vocabulary (AgentOrchestrator); anything
    // else (Completed, or a value this build has never heard of) reads as neutral.
    public static IBrush For(string status) => status switch {
        "Running"  => RunningDot,
        "Starting" => StartingDot,
        "Failed"   => FailedDot,
        _          => NeutralDot,
    };
}
```

In `SessionCardViewModel`, delete the four statics + `StatusDotFor` and call `SessionStatusDots.For(dto.Status)`.

- [ ] **Step 2: Write the failing tests**

`test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`. Helper DTO builder mirrors `FakeDaemonClientService.Snap`'s agent shape:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

public class RailSessionViewModelTests {
    static AgentStatusDto Dto(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test") =>
        new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null, Title: title);

    [Test]
    public async Task Title_is_primary_with_vendor_model_age_sub() {
        using var row = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), _ => { });
        await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
        await Assert.That(row.Sub).StartsWith("claude · Opus 5 · ");
    }

    [Test]
    public async Task Null_title_promotes_vendor_and_drops_it_from_sub() {
        using var row = new RailSessionViewModel(Dto(title: null), new BehaviorSubject<string?>(null), _ => { });
        await Assert.That(row.Primary).IsEqualTo("claude");
        await Assert.That(row.Sub).StartsWith("Opus 5 · ");
    }

    [Test]
    public async Task Review_kind_is_appended_to_the_vendor() {
        using var row = new RailSessionViewModel(Dto(kind: "review", title: null), new BehaviorSubject<string?>(null), _ => { });
        await Assert.That(row.Primary).IsEqualTo("claude · review");
    }

    [Test]
    public async Task Null_model_is_omitted_from_sub() {
        using var row = new RailSessionViewModel(Dto(model: null), new BehaviorSubject<string?>(null), _ => { });
        await Assert.That(row.Sub).DoesNotContain("· ·");
        await Assert.That(row.Sub).DoesNotStartWith("·");
    }

    [Test]
    public async Task Failed_status_sets_the_pip() {
        using var ok = new RailSessionViewModel(Dto(), new BehaviorSubject<string?>(null), _ => { });
        using var bad = new RailSessionViewModel(Dto(status: "Failed"), new BehaviorSubject<string?>(null), _ => { });
        await Assert.That(ok.NeedsYou).IsFalse();
        await Assert.That(bad.NeedsYou).IsTrue();
    }

    [Test]
    public async Task IsSelected_tracks_the_selection_observable() {
        var selected = new BehaviorSubject<string?>(null);
        using var row = new RailSessionViewModel(Dto(id: "a1"), selected, _ => { });
        await Assert.That(row.IsSelected).IsFalse();
        selected.OnNext("a1");
        await Assert.That(row.IsSelected).IsTrue();
        selected.OnNext("other");
        await Assert.That(row.IsSelected).IsFalse();
    }

    [Test]
    public async Task OpenCommand_invokes_the_callback_with_the_id() {
        string? opened = null;
        using var row = new RailSessionViewModel(Dto(id: "a9"), new BehaviorSubject<string?>(null), id => opened = id);
        row.OpenCommand.Execute().Subscribe();
        await Assert.That(opened).IsEqualTo("a9");
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/RailSessionViewModelTests/*'`
Expected: compile error — `RailSessionViewModel` does not exist.

- [ ] **Step 4: Implement**

`RailSessionViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One session row of the rail. Recreated per dto revision (DynamicData Transform), so every
/// static field is computed once from the ctor dto; only IsSelected is live — selection changes
/// are not dto revisions. Age is a point-in-time snapshot (SessionCardViewModel precedent).
public sealed class RailSessionViewModel : ReactiveObject, IDisposable {
    public string Id { get; }
    public string Primary { get; }
    public string Sub { get; }
    public IBrush StatusDot { get; }
    public bool NeedsYou { get; }
    public string Tooltip { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    internal DateTime CreatedAt { get; }

    readonly ObservableAsPropertyHelper<bool> _isSelected;
    public bool IsSelected => _isSelected.Value;

    readonly CompositeDisposable _disposables = new();

    public RailSessionViewModel(AgentStatusDto dto, IObservable<string?> selectedAgentId, Action<string> open) {
        Id = dto.Id;
        CreatedAt = dto.CreatedAt;
        var vendorLine = dto.Kind == "agent" ? dto.Vendor : $"{dto.Vendor} · {dto.Kind}";
        var age = UptimeFormat.Format(DateTime.UtcNow - DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc));

        Primary = dto.Title ?? vendorLine;
        Sub = dto.Title is null
            ? Join(dto.Model, age)
            : Join(vendorLine, dto.Model, age);
        StatusDot = SessionStatusDots.For(dto.Status);
        NeedsYou = dto.Status == "Failed";
        Tooltip = Join(dto.Id, dto.Status, dto.RequesterDisplay);

        _isSelected = selectedAgentId.Select(sel => sel == dto.Id)
            .ToProperty(this, x => x.IsSelected, initialValue: false)
            .DisposeWith(_disposables);
        OpenCommand = ReactiveCommand.Create(() => open(dto.Id));
        _disposables.Add(OpenCommand);
    }

    static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    public void Dispose() => _disposables.Dispose();
}
```

- [ ] **Step 5: Run to verify pass**

Same command. Also run the Home card tests to prove the extraction changed nothing: `--treenode-filter '/*/*/SessionCard*/*'` (if a card test class exists; otherwise run the whole App suite).
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/SessionStatusDots.cs src/Capacitor.App/ViewModels/RailSessionViewModel.cs src/Capacitor.App/ViewModels/SessionCardViewModel.cs test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs
git commit -m "AI-2199: rail session row VM over shared status dots"
```

---

### Task 4: `RailCollapseState` + `RailWorktreeViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/RailCollapseState.cs`
- Create: `src/Capacitor.App/ViewModels/RailWorktreeViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/RailWorktreeViewModelTests.cs`

**Interfaces:**
- Consumes: `RailSessionViewModel` (Task 3); DynamicData `IObservableCache<AgentStatusDto, string>`.
- Produces:
  - `sealed class RailCollapseState` — `bool IsCollapsed(string path, bool isMainCheckout)`, `void Set(string path, bool collapsed)`, `IObservable<string> Changes`. Default rule: collapsed iff main checkout, until an explicit `Set`.
  - `sealed class RailWorktreeViewModel : ReactiveObject, IDisposable` with ctor `(string path, string repoRoot, bool showHeader, IObservableCache<AgentStatusDto, string> sessionsCache, RailCollapseState collapse, IObservable<string?> selectedAgentId, Action<string> open)` and members: `string Path`, `string Label`, `bool IsMainCheckout`, `bool ShowHeader`, `bool IsExpanded` (OAPH), `int SessionCount` (OAPH), `string CountText` (OAPH), `bool NeedsYou` (OAPH), `bool HoldsSelected` (OAPH), `bool SessionsVisible` (OAPH: `IsExpanded || !ShowHeader`), `ReadOnlyObservableCollection<RailSessionViewModel> Sessions`, `ReactiveCommand<Unit, Unit> ToggleCommand`.
  - `internal static string RailWorktreeViewModel.LabelFor(string path, bool isMainCheckout)` → `"main checkout"` or the path leaf.

- [ ] **Step 1: Write the failing tests**

`RailWorktreeViewModelTests.cs`. Build a private `SourceCache<AgentStatusDto, string>(a => a.Id)` per test and hand `cache.AsObservableCache()` to the VM. Tests are pure VM tests but OAPHs subscribe immediately — no scheduler involved (no ObserveOn inside this VM; marshaling is the outer pipeline's job, see Task 5), so no Avalonia session is needed:

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using System.Reactive.Subjects;

namespace Capacitor.App.Tests.Unit;

public class RailWorktreeViewModelTests {
    static AgentStatusDto Dto(string id, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", "/repo/.claude/worktrees/wt-a", status,
            null, null, null, created ?? DateTime.UtcNow, null, null);

    static RailWorktreeViewModel Build(
            SourceCache<AgentStatusDto, string> cache, RailCollapseState? collapse = null,
            string path = "/repo/.claude/worktrees/wt-a", string root = "/repo", bool showHeader = true,
            IObservable<string?>? selected = null) =>
        new(path, root, showHeader, cache.AsObservableCache(),
            collapse ?? new RailCollapseState(), selected ?? new BehaviorSubject<string?>(null), _ => { });

    [Test]
    public async Task Label_is_the_checkout_leaf_and_main_checkout_for_the_root() {
        await Assert.That(RailWorktreeViewModel.LabelFor("/repo/.claude/worktrees/wt-a", false)).IsEqualTo("wt-a");
        await Assert.That(RailWorktreeViewModel.LabelFor("/repo/", false)).IsEqualTo("repo");
        await Assert.That(RailWorktreeViewModel.LabelFor("/repo", true)).IsEqualTo("main checkout");
    }

    [Test]
    public async Task Count_and_pip_follow_the_cache() {
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        using var wt = Build(cache);
        cache.AddOrUpdate(Dto("a1"));
        cache.AddOrUpdate(Dto("a2", status: "Failed"));
        await Assert.That(wt.SessionCount).IsEqualTo(2);
        await Assert.That(wt.CountText).IsEqualTo("2");
        await Assert.That(wt.NeedsYou).IsTrue();

        cache.AddOrUpdate(Dto("a2", status: "Running")); // recovery clears the pip
        await Assert.That(wt.NeedsYou).IsFalse();
    }

    [Test]
    public async Task Main_checkout_defaults_collapsed_and_others_expanded() {
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        using var main = Build(cache, path: "/repo", root: "/repo");
        using var wt = Build(cache);
        await Assert.That(main.IsExpanded).IsFalse();
        await Assert.That(wt.IsExpanded).IsTrue();
    }

    [Test]
    public async Task Toggle_persists_in_the_shared_state_across_VM_recreation() {
        var collapse = new RailCollapseState();
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        using (var first = Build(cache, collapse, path: "/repo", root: "/repo")) {
            first.ToggleCommand.Execute().Subscribe();
            await Assert.That(first.IsExpanded).IsTrue();
        }
        using var recreated = Build(cache, collapse, path: "/repo", root: "/repo");
        await Assert.That(recreated.IsExpanded).IsTrue(); // survived the group's death
    }

    [Test]
    public async Task Sessions_sort_by_created_then_id() {
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        var t = DateTime.UtcNow;
        using var wt = Build(cache);
        cache.AddOrUpdate(Dto("b", created: t));
        cache.AddOrUpdate(Dto("a", created: t));
        cache.AddOrUpdate(Dto("c", created: t.AddMinutes(-1)));
        await Assert.That(wt.Sessions.Select(s => s.Id)).IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Headerless_group_always_shows_sessions() {
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        using var wt = Build(cache, showHeader: false, path: "", root: "");
        await Assert.That(wt.SessionsVisible).IsTrue();
    }

    [Test]
    public async Task Dispose_stops_tracking_the_cache() {
        var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
        var wt = Build(cache);
        cache.AddOrUpdate(Dto("a1"));
        wt.Dispose();
        cache.AddOrUpdate(Dto("a2"));
        await Assert.That(wt.SessionCount).IsEqualTo(1);
        await Assert.That(wt.Sessions.Count).IsEqualTo(1);
    }
}
```

(`CollectionOrdering` comes from `TUnit.Assertions.Enums`, already used by daemon tests.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/RailWorktreeViewModelTests/*'`
Expected: compile errors — types do not exist.

- [ ] **Step 3: Implement**

`RailCollapseState.cs`:

```csharp
using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Collapse choices for worktree rows, held OUTSIDE the group VMs: DynamicData drops and
/// re-forms a group whenever it empties or the cache resets, so state on the VM itself would
/// silently reset (spec §3). Default rule: collapsed iff main checkout. UI-thread only.
public sealed class RailCollapseState {
    readonly Dictionary<string, bool> _explicit = new(StringComparer.Ordinal);
    readonly Subject<string> _changes = new();

    /// Fires the path whose state changed — worktree VMs re-read IsCollapsed on it.
    public IObservable<string> Changes => _changes;

    public bool IsCollapsed(string path, bool isMainCheckout) =>
        _explicit.TryGetValue(path, out var collapsed) ? collapsed : isMainCheckout;

    public void Set(string path, bool collapsed) {
        _explicit[path] = collapsed;
        _changes.OnNext(path);
    }
}
```

`RailWorktreeViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One worktree level of the rail: the collapsible row plus its session rows. ShowHeader=false
/// is the No-repository group's single nested group — rendered headerless, sessions always
/// visible (spec §3). No ObserveOn here: the OUTER pipeline (SessionRailViewModel) marshals to
/// the UI thread before any group cache is mutated, so everything below already runs there.
public sealed class RailWorktreeViewModel : ReactiveObject, IDisposable {
    public string Path { get; }
    public string Label { get; }
    public bool IsMainCheckout { get; }
    public bool ShowHeader { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    readonly ObservableAsPropertyHelper<bool> _isExpanded;
    public bool IsExpanded => _isExpanded.Value;

    readonly ObservableAsPropertyHelper<bool> _sessionsVisible;
    public bool SessionsVisible => _sessionsVisible.Value;

    readonly ObservableAsPropertyHelper<int> _sessionCount;
    public int SessionCount => _sessionCount.Value;

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly ObservableAsPropertyHelper<bool> _holdsSelected;
    public bool HoldsSelected => _holdsSelected.Value;

    readonly ObservableCollectionExtended<RailSessionViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<RailSessionViewModel> Sessions { get; }

    static readonly IComparer<RailSessionViewModel> SessionComparer =
        Comparer<RailSessionViewModel>.Create((a, b) => {
            var byCreated = a.CreatedAt.CompareTo(b.CreatedAt);
            return byCreated != 0 ? byCreated : string.CompareOrdinal(a.Id, b.Id);
        });

    readonly CompositeDisposable _disposables = new();

    public RailWorktreeViewModel(
            string path, string repoRoot, bool showHeader,
            IObservableCache<AgentStatusDto, string> sessionsCache, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, Action<string> open) {
        Path = path;
        IsMainCheckout = PathEquals(path, repoRoot);
        Label = LabelFor(path, IsMainCheckout);
        ShowHeader = showHeader;

        _isExpanded = collapse.Changes.Where(p => p == path).Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .Select(_ => !collapse.IsCollapsed(path, IsMainCheckout))
            .ToProperty(this, x => x.IsExpanded)
            .DisposeWith(_disposables);
        _sessionsVisible = this.WhenAnyValue(x => x.IsExpanded, expanded => expanded || !showHeader)
            .ToProperty(this, x => x.SessionsVisible)
            .DisposeWith(_disposables);
        ToggleCommand = ReactiveCommand.Create(() => collapse.Set(path, IsExpanded));
        _disposables.Add(ToggleCommand);

        _sessionCount = sessionsCache.CountChanged
            .ToProperty(this, x => x.SessionCount, initialValue: sessionsCache.Count)
            .DisposeWith(_disposables);
        _countText = this.WhenAnyValue(x => x.SessionCount, c => c.ToString())
            .ToProperty(this, x => x.CountText)
            .DisposeWith(_disposables);

        _needsYou = sessionsCache.Connect()
            .QueryWhenChanged(q => q.Items.Any(d => d.Status == "Failed"))
            .ToProperty(this, x => x.NeedsYou, initialValue: false)
            .DisposeWith(_disposables);

        _holdsSelected = sessionsCache.Connect().QueryWhenChanged(q => q.Keys.ToHashSet())
            .CombineLatest(selectedAgentId, (ids, sel) => sel is not null && ids.Contains(sel))
            .ToProperty(this, x => x.HoldsSelected, initialValue: false)
            .DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        sessionsCache.Connect()
            .Transform(dto => new RailSessionViewModel(dto, selectedAgentId, open))
            .DisposeMany()
            .SortAndBind(_sessionsSource, SessionComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    internal static string LabelFor(string path, bool isMainCheckout) =>
        isMainCheckout ? "main checkout"
        : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));

    // Same platform rule HomeViewModel.PathComparer documents: case-insensitive except Linux.
    internal static bool PathEquals(string a, string b) =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _disposables.Dispose();
}
```

Note `LabelFor("/repo/", false)` must yield `repo` — `TrimEndingDirectorySeparator` before `GetFileName` handles it.

- [ ] **Step 4: Run to verify pass**

Same command. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/RailCollapseState.cs src/Capacitor.App/ViewModels/RailWorktreeViewModel.cs test/Capacitor.App.Tests.Unit/RailWorktreeViewModelTests.cs
git commit -m "AI-2199: worktree level VM with shared collapse state"
```

---

### Task 5: `RailRepoViewModel` + `SessionRailViewModel`

**Files:**
- Create: `src/Capacitor.App/ViewModels/RailRepoViewModel.cs`
- Create: `src/Capacitor.App/ViewModels/SessionRailViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionRailViewModelTests.cs`

**Interfaces:**
- Consumes: `RailWorktreeViewModel`, `RailCollapseState` (Task 4); `IDaemonClientService.Agents`; `GitRepository.ResolveMainRepoRoot(string)` (Cli.Core).
- Produces:
  - `sealed class RailRepoViewModel : ReactiveObject, IDisposable` — `string RootPath`, `string Label`, `bool IsNoRepository`, `string CountText` (OAPH, "N session(s)"), `ReadOnlyObservableCollection<RailWorktreeViewModel> Worktrees`.
  - `sealed class SessionRailViewModel : ReactiveObject, IDisposable` — ctor `(IDaemonClientService daemon, Action<string> openSession, Func<string, string>? resolveRepoRoot = null)`; members `ReadOnlyObservableCollection<RailRepoViewModel> Repos`, `bool IsEmpty` (OAPH), `string HostedText` (OAPH, "N hosted"), `string? SelectedAgentId` (reactive property), `void NotifySessionOpened(string agentId)`.
  - `resolveRepoRoot` defaults to `GitRepository.ResolveMainRepoRoot`; injectable so tests never do real `.git` I/O.

- [ ] **Step 1: Write the failing tests**

`SessionRailViewModelTests.cs`. The pipeline `ObserveOn`s the main-thread scheduler, so every test wraps in `AvaloniaSession.WithImmediateRxScheduler` and carries `[NotInParallel("AvaloniaSession")]` (MainWindowViewModelTests conventions). Use a fake resolver: worktree paths map under their repo, plain paths map to themselves.

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class SessionRailViewModelTests {
    // "/x/wt/<leaf>" resolves to "/x" — a stand-in for GitRepository.ResolveMainRepoRoot so no
    // test touches real .git files.
    static string Resolve(string path) {
        var marker = path.IndexOf("/wt/", StringComparison.Ordinal);
        return marker < 0 ? path : path[..marker];
    }

    static AgentStatusDto Dto(string id, string? repoPath, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", repoPath, status, null, null, null, created ?? DateTime.UtcNow, null, null);

    static (FakeDaemonClientService Service, SessionRailViewModel Rail) Build(Action<string>? open = null) {
        var service = new FakeDaemonClientService();
        return (service, new SessionRailViewModel(service, open ?? (_ => { }), Resolve));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Groups_repo_worktree_session_with_no_repository_last() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/zeta"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a4", null));

                await Assert.That(rail.Repos.Select(r => r.Label))
                    .IsEquivalentTo(["alpha", "zeta", "No repository"], CollectionOrdering.Matching);

                var alpha = rail.Repos[0];
                await Assert.That(alpha.Worktrees.Select(w => w.Label))
                    .IsEquivalentTo(["main checkout", "feature-x"], CollectionOrdering.Matching);

                var noRepo = rail.Repos[2];
                await Assert.That(noRepo.IsNoRepository).IsTrue();
                await Assert.That(noRepo.Worktrees).HasCount().EqualTo(1);
                await Assert.That(noRepo.Worktrees[0].ShowHeader).IsFalse();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Counts_and_hosted_text_track_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                await Assert.That(rail.IsEmpty).IsTrue();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.IsEmpty).IsFalse();
                await Assert.That(rail.HostedText).IsEqualTo("2 hosted");
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("2 sessions");

                service.Agents.RemoveKey("a2");
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("1 session");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_repo_group_disappears_and_collapse_survives_recreation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                rail.Repos[0].Worktrees[0].ToggleCommand.Execute().Subscribe(); // expand main checkout

                service.Agents.RemoveKey("a1"); // the whole repo group dies
                await Assert.That(rail.Repos).HasCount().EqualTo(0);

                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha")); // and re-forms
                await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task NotifySessionOpened_expands_the_collapsed_worktree_and_selects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha")); // main checkout: collapsed
                await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsFalse();

                rail.NotifySessionOpened("a1");
                rail.SelectedAgentId = "a1";

                var wt = rail.Repos[0].Worktrees[0];
                await Assert.That(wt.IsExpanded).IsTrue();
                await Assert.That(wt.HoldsSelected).IsTrue();
                await Assert.That(wt.Sessions[0].IsSelected).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_tracking() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
            rail.Dispose();
            service.Agents.AddOrUpdate(Dto("a2", "/dev/zeta"));
            await Assert.That(rail.Repos).HasCount().EqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repo_pip_reaches_the_worktree_row_when_a_session_fails() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.Repos[0].Worktrees[0].NeedsYou).IsFalse();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha/wt/feature-x", status: "Failed"));
                await Assert.That(rail.Repos[0].Worktrees[0].NeedsYou).IsTrue();
            }
        });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/SessionRailViewModelTests/*'`
Expected: compile errors.

- [ ] **Step 3: Implement**

`RailRepoViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One repository level of the rail. IsNoRepository is the "" sentinel group — its single
/// nested worktree group renders headerless (spec §3). No ObserveOn: the outer pipeline
/// already marshaled (RailWorktreeViewModel's identical note).
public sealed class RailRepoViewModel : ReactiveObject, IDisposable {
    public string RootPath { get; }
    public string Label { get; }
    public bool IsNoRepository { get; }

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;

    readonly ObservableCollectionExtended<RailWorktreeViewModel> _worktreesSource = new();
    public ReadOnlyObservableCollection<RailWorktreeViewModel> Worktrees { get; }

    // Main checkout first, then leaf label; path tiebreak keeps the order total.
    static readonly IComparer<RailWorktreeViewModel> WorktreeComparer =
        Comparer<RailWorktreeViewModel>.Create((a, b) => {
            var byMain = b.IsMainCheckout.CompareTo(a.IsMainCheckout);
            if (byMain != 0) return byMain;
            var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Path, b.Path);
        });

    readonly CompositeDisposable _disposables = new();

    public RailRepoViewModel(
            IGroup<AgentStatusDto, string, string> group, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, Action<string> open) {
        RootPath = group.Key;
        IsNoRepository = group.Key.Length == 0;
        Label = IsNoRepository ? "No repository"
            : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(group.Key));

        _countText = group.Cache.CountChanged
            .Select(c => c == 1 ? "1 session" : $"{c} sessions")
            .ToProperty(this, x => x.CountText, initialValue: "")
            .DisposeWith(_disposables);

        Worktrees = new ReadOnlyObservableCollection<RailWorktreeViewModel>(_worktreesSource);
        group.Cache.Connect()
            .Group(dto => dto.RepoPath ?? "")
            .Transform(wt => new RailWorktreeViewModel(
                wt.Key, RootPath, showHeader: !IsNoRepository, wt.Cache, collapse, selectedAgentId, open))
            .DisposeMany()
            .SortAndBind(_worktreesSource, WorktreeComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    public void Dispose() => _disposables.Dispose();
}
```

`SessionRailViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// The session rail's root: repository → worktree → session over daemon.Agents (spec §3).
/// Ctor-scoped and disposable like HomeViewModel — the tree must be live from construction.
/// ONE ObserveOn at the top of the pipeline: nested group caches are mutated by this outer
/// pipeline, so every inner Connect() below already fires on the UI thread.
public sealed class SessionRailViewModel : ReactiveObject, IDisposable {
    readonly IDaemonClientService _daemon;
    readonly RailCollapseState _collapse = new();
    readonly Dictionary<string, string> _rootByPath = new(StringComparer.Ordinal);
    readonly Func<string, string> _resolveRepoRoot;
    readonly CompositeDisposable _disposables = new();

    string? _selectedAgentId;
    public string? SelectedAgentId {
        get => _selectedAgentId;
        set => this.RaiseAndSetIfChanged(ref _selectedAgentId, value);
    }

    readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;

    readonly ObservableAsPropertyHelper<string> _hostedText;
    public string HostedText => _hostedText.Value;

    readonly ObservableCollectionExtended<RailRepoViewModel> _reposSource = new();
    public ReadOnlyObservableCollection<RailRepoViewModel> Repos { get; }

    // No-repository last, then leaf label; root path tiebreak.
    static readonly IComparer<RailRepoViewModel> RepoComparer =
        Comparer<RailRepoViewModel>.Create((a, b) => {
            var byNoRepo = a.IsNoRepository.CompareTo(b.IsNoRepository);
            if (byNoRepo != 0) return byNoRepo;
            var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(a.RootPath, b.RootPath);
        });

    /// resolveRepoRoot defaults to the real .git-reading heuristic; tests inject a pure one.
    public SessionRailViewModel(
            IDaemonClientService daemon, Action<string> openSession,
            Func<string, string>? resolveRepoRoot = null) {
        _daemon = daemon;
        _resolveRepoRoot = resolveRepoRoot ?? GitRepository.ResolveMainRepoRoot;
        var selected = this.WhenAnyValue(x => x.SelectedAgentId);

        _isEmpty = daemon.Agents.CountChanged
            .Select(c => c == 0)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.IsEmpty, initialValue: daemon.Agents.Count == 0)
            .DisposeWith(_disposables);
        _hostedText = daemon.Agents.CountChanged
            .Select(c => $"{c} hosted")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.HostedText, initialValue: $"{daemon.Agents.Count} hosted")
            .DisposeWith(_disposables);

        Repos = new ReadOnlyObservableCollection<RailRepoViewModel>(_reposSource);
        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Group(dto => RepoRootFor(dto))
            .Transform(g => new RailRepoViewModel(g, _collapse, selected, openSession))
            .DisposeMany()
            .SortAndBind(_reposSource, RepoComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    /// The launch auto-open's counterpart: a session opened into a collapsed worktree (main
    /// checkout by default) must never highlight an invisible row (spec §3).
    public void NotifySessionOpened(string agentId) {
        var dto = _daemon.Agents.Lookup(agentId);
        if (!dto.HasValue || dto.Value.RepoPath is not { Length: > 0 } path) return;
        _collapse.Set(path, collapsed: false);
    }

    // Memoized: ResolveMainRepoRoot reads .git files — cheap once, not per-changeset cheap; a
    // path's resolution never changes within a daemon's lifetime.
    string RepoRootFor(AgentStatusDto dto) {
        if (dto.RepoPath is not { Length: > 0 } path) return "";
        if (_rootByPath.TryGetValue(path, out var root)) return root;
        root = _resolveRepoRoot(path);
        _rootByPath[path] = root;
        return root;
    }

    public void Dispose() => _disposables.Dispose();
}
```

Note: `_rootByPath` is only touched from the pipeline's `Group` selector, which runs post-`ObserveOn` on the UI thread — no lock needed.

- [ ] **Step 4: Run to verify pass**

Same command; then the two prior rail test classes too. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/RailRepoViewModel.cs src/Capacitor.App/ViewModels/SessionRailViewModel.cs test/Capacitor.App.Tests.Unit/SessionRailViewModelTests.cs
git commit -m "AI-2199: session rail projection over nested DynamicData groups"
```

---

### Task 6: `MainWindowViewModel` view state + rail wiring

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs` (append a new region/section)

**Interfaces:**
- Consumes: `SessionRailViewModel` (Task 5).
- Produces on `MainWindowViewModel`:
  - `public enum ShellView { Home, Sessions }` (top of the file, same namespace).
  - `ShellView CurrentView` (private set, default `Home`), `bool IsHomeView`, `bool IsSessionsView` (both raised when CurrentView changes).
  - `ReactiveCommand<Unit, Unit> ShowHomeCommand`, `ShowSessionsCommand`.
  - `SessionRailViewModel? Rail { get; }` — new optional ctor param `SessionRailViewModel? rail = null` (after `workspaceFactory`).
  - `OpenSession`: same-session no-op; sets `CurrentView = Sessions`; calls `Rail?.NotifySessionOpened(agentId)`.
  - `SwapTo`/`LatchShutdown`: push `CurrentWorkspace?.AgentId` into `Rail.SelectedAgentId`.

- [ ] **Step 1: Write the failing tests**

Append to `MainWindowViewModelTests.cs`, following that file's existing construction helpers (find its `NewVm`-style helper and mirror it; pass `rail:` where needed). New tests:

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task OpenSession_switches_to_sessions_view_and_reopening_the_same_id_is_a_noop() {
    await AvaloniaSession.WithImmediateRxScheduler(async () => {
        var service = new FakeDaemonClientService();
        var built = 0;
        var vm = /* file's standard construction */ NewVm(service,
            workspaceFactory: id => { built++; return NewWorkspace(service, id); });

        await Assert.That(vm.IsHomeView).IsTrue();
        vm.OpenSession("a1");
        await Assert.That(vm.IsSessionsView).IsTrue();
        await Assert.That(built).IsEqualTo(1);

        vm.OpenSession("a1"); // same id: no teardown/rebuild of a live attach
        await Assert.That(built).IsEqualTo(1);

        vm.OpenSession("a2"); // different id still swaps
        await Assert.That(built).IsEqualTo(2);
    });
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task View_commands_swap_surfaces_and_close_keeps_sessions_view() {
    await AvaloniaSession.WithImmediateRxScheduler(async () => {
        var service = new FakeDaemonClientService();
        var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id));
        vm.OpenSession("a1");
        vm.CloseWorkspace();
        await Assert.That(vm.CurrentWorkspace).IsNull();
        await Assert.That(vm.IsSessionsView).IsTrue(); // placeholder pane, not Home

        vm.ShowHomeCommand.Execute().Subscribe();
        await Assert.That(vm.IsHomeView).IsTrue();
        vm.ShowSessionsCommand.Execute().Subscribe();
        await Assert.That(vm.IsSessionsView).IsTrue();
    });
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task Rail_selection_follows_the_workspace() {
    await AvaloniaSession.WithImmediateRxScheduler(async () => {
        var service = new FakeDaemonClientService();
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", "/dev/alpha", "Running", null, null, null, DateTime.UtcNow, null, null));
        var rail = new SessionRailViewModel(service, _ => { }, p => p);
        var vm = NewVm(service, workspaceFactory: id => NewWorkspace(service, id), rail: rail);

        vm.OpenSession("a1");
        await Assert.That(rail.SelectedAgentId).IsEqualTo("a1");
        await Assert.That(rail.Repos[0].Worktrees[0].IsExpanded).IsTrue(); // NotifySessionOpened ran

        vm.CloseWorkspace();
        await Assert.That(rail.SelectedAgentId).IsNull();
    });
}
```

(`NewVm`/`NewWorkspace` stand for whatever helpers the file already uses to build a `MainWindowViewModel`/`WorkspaceViewModel` against the fake service — reuse them verbatim; only add the optional `rail` pass-through to the helper.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/MainWindowViewModelTests/*'`
Expected: compile errors (`IsHomeView` etc. missing).

- [ ] **Step 3: Implement**

In `MainWindowViewModel.cs`:

```csharp
/// Which surface owns the window: Home (status block + launcher + cards + Activity) or
/// Sessions (rail | workspace). Orthogonal to CurrentWorkspace, which only means anything in
/// Sessions view.
public enum ShellView { Home, Sessions }
```

Members (ctor-scoped, near CurrentWorkspace):

```csharp
    ShellView _currentView = ShellView.Home;
    public ShellView CurrentView {
        get => _currentView;
        private set {
            this.RaiseAndSetIfChanged(ref _currentView, value);
            this.RaisePropertyChanged(nameof(IsHomeView));
            this.RaisePropertyChanged(nameof(IsSessionsView));
        }
    }
    public bool IsHomeView => CurrentView == ShellView.Home;
    public bool IsSessionsView => CurrentView == ShellView.Sessions;

    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSessionsCommand { get; }

    public SessionRailViewModel? Rail { get; }
```

Ctor: add `SessionRailViewModel? rail = null` parameter (last), `Rail = rail;`, and:

```csharp
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Home; });
        ShowSessionsCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Sessions; });
```

`OpenSession` becomes:

```csharp
    public void OpenSession(string agentId) {
        if (_navigation.ShutdownLatched || _workspaceFactory is null) return;
        CurrentView = ShellView.Sessions;
        // Re-clicking the open session must not tear down and rebuild a live attach.
        if (CurrentWorkspace?.AgentId == agentId) return;

        var workspace = _workspaceFactory(agentId);
        SwapTo(workspace);
        Rail?.NotifySessionOpened(agentId);
    }
```

(The `workspace.BackCommand = CloseWorkspaceCommand;` line is removed in Task 7 together with the property — leave it in place for now so the build stays green.)

`SwapTo` gains one line after `CurrentWorkspace = next;`:

```csharp
        if (Rail is not null) Rail.SelectedAgentId = next?.AgentId;
```

`LatchShutdown` similarly sets `if (Rail is not null) Rail.SelectedAgentId = null;` after clearing `CurrentWorkspace`.

- [ ] **Step 4: Run to verify pass**

Same command; the whole MainWindowViewModelTests class must stay green (pre-existing tests untouched).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/MainWindowViewModel.cs test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs
git commit -m "AI-2199: shell view state and rail wiring on the window VM"
```

---

### Task 7: XAML restructure — tabless window, rail view, Back removal

**Files:**
- Create: `src/Capacitor.App/Views/SessionRailView.axaml` + `src/Capacitor.App/Views/SessionRailView.axaml.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml` (full restructure), `src/Capacitor.App/Views/MainWindow.axaml.cs` (Activity gating), `src/Capacitor.App/Views/WorkspaceView.axaml` (delete BackButton), `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (delete `BackCommand`), `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (drop the `workspace.BackCommand` assignment)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs` (rewrite tab-dependent tests), plus `grep`-found BackCommand tests

**Interfaces:**
- Consumes: `IsHomeView`/`IsSessionsView`/`Rail`/`ShowHomeCommand`/`ShowSessionsCommand` (Task 6), `SessionRailViewModel` tree (Task 5).
- Produces: named controls the smoke tests use — `HomeSurface`, `SessionsSurface`, `SessionRail`, `RailNewSessionButton`, `RailHomeButton`, `RailSessionsButton`, `HomeSessionsButton`, `ActivityExpander`, `WorkspacePlaceholder`. Existing names preserved: `DaemonIdentityText`, `DaemonVersionText`, `ServerUrlText`, `ConnectionText`, `AgentCountText`, `StartButton`, `RetryButton`, `StartMessageText`, `ReasonText`, `ActivityEmptyText`, `ActivityHeader`, `ActivityItems`, `WorkspaceHost`.

- [ ] **Step 1: Inventory the blast radius**

Run: `grep -rn 'BackCommand\|BackButton\|MainTabs\|HomeTabItem\|ActivityTabItem\|AgentsGrid\|AgentsItems\|EmptyStateText\|AgentsGridHeader' src/Capacitor.App test/Capacitor.App.Tests.Unit --include='*.cs' --include='*.axaml'`
Every hit must be accounted for by the end of this task or Task 8 (Agents grid names die in Task 8's file deletions; tab and Back names die here).

- [ ] **Step 2: Rewrite `MainWindow.axaml`**

Keep the `rxui:ReactiveWindow` root attributes; change `Width="1200" Height="760"`. New body (the status block, Home content, and Activity block are the EXISTING XAML moved, not rewritten — control names preserved):

```xml
    <Panel>
        <!-- Home surface: daemon status block + launcher/cards (HomeView) + Activity. -->
        <Grid x:Name="HomeSurface" Margin="20" RowDefinitions="Auto,*"
              IsVisible="{Binding IsHomeView}">
            <Grid Grid.Row="0" ColumnDefinitions="*,Auto">
                <!-- the ENTIRE existing status StackPanel (DaemonIdentityText … ReasonText) goes here, unchanged -->
                <Button Grid.Column="1" x:Name="HomeSessionsButton" Content="Sessions ›"
                        Command="{Binding ShowSessionsCommand}" VerticalAlignment="Top" />
            </Grid>
            <ScrollViewer Grid.Row="1" Margin="0,8,0,0" VerticalScrollBarVisibility="Auto">
                <StackPanel Spacing="12">
                    <views:HomeView DataContext="{Binding Home}" />
                    <Expander x:Name="ActivityExpander" Header="Activity" IsExpanded="False"
                              Expanded="OnActivityExpandChanged" Collapsed="OnActivityExpandChanged">
                        <!-- the ENTIRE existing Activity tab Grid (ActivityEmptyText/ActivityHeader/ActivityItems) goes here, unchanged -->
                    </Expander>
                </StackPanel>
            </ScrollViewer>
        </Grid>

        <!-- Sessions surface: rail | workspace-or-placeholder. -->
        <Grid x:Name="SessionsSurface" ColumnDefinitions="310,*"
              IsVisible="{Binding IsSessionsView}">
            <views:SessionRailView x:Name="SessionRail" Grid.Column="0" />
            <Panel Grid.Column="1">
                <TextBlock x:Name="WorkspacePlaceholder" Text="Select a session"
                           Opacity="0.6" HorizontalAlignment="Center" VerticalAlignment="Center"
                           IsVisible="{Binding CurrentWorkspace, Converter={x:Static ObjectConverters.IsNull}}" />
                <ContentControl x:Name="WorkspaceHost" Content="{Binding CurrentWorkspace}"
                                IsVisible="{Binding CurrentWorkspace, Converter={x:Static ObjectConverters.IsNotNull}}">
                    <ContentControl.ContentTemplate>
                        <DataTemplate x:DataType="vm:WorkspaceViewModel">
                            <views:WorkspaceView />
                        </DataTemplate>
                    </ContentControl.ContentTemplate>
                </ContentControl>
            </Panel>
        </Grid>
    </Panel>
```

The `TabControl`, the Agents `TabItem` content, and `OnTabSelectionChanged`'s XAML hook are gone. The Agents grid XAML is deleted here (its VM projection dies in Task 8 — deleting XAML first keeps compiled bindings valid throughout).

- [ ] **Step 3: Write `SessionRailView.axaml`**

`UserControl`, `x:DataType="vm:MainWindowViewModel"` (DataContext inherited from the window; `Rail` may be null in bare tests — bindings tolerate it):

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             x:Class="Capacitor.App.Views.SessionRailView"
             x:DataType="vm:MainWindowViewModel">
    <Border BorderThickness="0,0,1,0" BorderBrush="{DynamicResource SystemControlForegroundBaseLowBrush}">
        <DockPanel Margin="0">
            <StackPanel DockPanel.Dock="Top" Margin="10,10,10,4" Spacing="8">
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Spacing="4">
                    <Button x:Name="RailHomeButton" Content="⌂" Command="{Binding ShowHomeCommand}" Padding="8,2" />
                    <Button x:Name="RailSessionsButton" Content="‹›" Command="{Binding ShowSessionsCommand}" Padding="8,2" />
                </StackPanel>
                <Button x:Name="RailNewSessionButton" HorizontalAlignment="Stretch"
                        Command="{Binding ShowHomeCommand}">
                    <DockPanel>
                        <TextBlock DockPanel.Dock="Right" Text="⌘N" Opacity="0.5" FontSize="11" />
                        <TextBlock Text="+  New session" />
                    </DockPanel>
                </Button>
            </StackPanel>

            <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Spacing="6" Margin="14,8,14,12">
                <Ellipse Width="8" Height="8" Fill="{Binding StatusDotBrush}" VerticalAlignment="Center" />
                <TextBlock Text="{Binding ConnectionDisplay}" FontSize="11" VerticalAlignment="Center" />
                <TextBlock Text="{Binding Rail.HostedText, FallbackValue=''}" FontSize="11" Opacity="0.6" VerticalAlignment="Center" />
            </StackPanel>

            <ScrollViewer VerticalScrollBarVisibility="Auto" Margin="10,4">
                <Panel>
                    <TextBlock Text="No sessions" Opacity="0.5" Margin="8,16"
                               IsVisible="{Binding Rail.IsEmpty, FallbackValue=False}" />
                    <ItemsControl x:Name="RailRepos" ItemsSource="{Binding Rail.Repos, FallbackValue={x:Null}}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:RailRepoViewModel">
                                <StackPanel Margin="0,8,0,0">
                                    <DockPanel Margin="4,0,4,4" ToolTip.Tip="{Binding RootPath}">
                                        <TextBlock DockPanel.Dock="Right" Text="{Binding CountText}" FontSize="10" Opacity="0.6" />
                                        <TextBlock Text="{Binding Label}" FontSize="10" FontWeight="Bold" Opacity="0.6" />
                                    </DockPanel>
                                    <TextBlock Text="Work with no checkout yet. It keeps a title and a session; parts and blockers arrive once it lands in a repo."
                                               FontSize="10" Opacity="0.5" TextWrapping="Wrap" Margin="4,0,4,4"
                                               IsVisible="{Binding IsNoRepository}" />
                                    <ItemsControl ItemsSource="{Binding Worktrees}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate x:DataType="vm:RailWorktreeViewModel">
                                                <StackPanel>
                                                    <Button Command="{Binding ToggleCommand}" IsVisible="{Binding ShowHeader}"
                                                            HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch"
                                                            Background="Transparent" BorderThickness="0" Padding="4,3"
                                                            ToolTip.Tip="{Binding Path}"
                                                            Classes.holdsSelected="{Binding HoldsSelected}">
                                                        <DockPanel>
                                                            <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="6">
                                                                <TextBlock Text="{Binding CountText}" FontSize="10" Opacity="0.6" VerticalAlignment="Center" />
                                                                <TextBlock Text="!" FontSize="10" FontWeight="Bold" Foreground="Orange"
                                                                           IsVisible="{Binding NeedsYou}" VerticalAlignment="Center" />
                                                            </StackPanel>
                                                            <StackPanel Orientation="Horizontal" Spacing="6">
                                                                <TextBlock Text="{Binding IsExpanded, Converter={x:Static views:ExpanderChevronConverter.Instance}}" FontSize="9" Opacity="0.6" VerticalAlignment="Center" />
                                                                <TextBlock Text="{Binding Label}" FontSize="12" TextTrimming="CharacterEllipsis" />
                                                            </StackPanel>
                                                        </DockPanel>
                                                    </Button>
                                                    <ItemsControl ItemsSource="{Binding Sessions}" Margin="14,0,0,0"
                                                                  IsVisible="{Binding SessionsVisible}">
                                                        <ItemsControl.ItemTemplate>
                                                            <DataTemplate x:DataType="vm:RailSessionViewModel">
                                                                <Button Command="{Binding OpenCommand}" HorizontalAlignment="Stretch"
                                                                        HorizontalContentAlignment="Stretch" Background="Transparent"
                                                                        BorderThickness="0" Padding="6,4"
                                                                        ToolTip.Tip="{Binding Tooltip}"
                                                                        Classes.selected="{Binding IsSelected}">
                                                                    <DockPanel>
                                                                        <TextBlock DockPanel.Dock="Right" Text="!" FontSize="10" FontWeight="Bold"
                                                                                   Foreground="Orange" IsVisible="{Binding NeedsYou}" />
                                                                        <StackPanel Orientation="Horizontal" Spacing="7">
                                                                            <Ellipse Width="7" Height="7" Fill="{Binding StatusDot}" VerticalAlignment="Center" />
                                                                            <StackPanel>
                                                                                <TextBlock Text="{Binding Primary}" FontSize="12" TextTrimming="CharacterEllipsis" />
                                                                                <TextBlock Text="{Binding Sub}" FontSize="10" Opacity="0.6" TextTrimming="CharacterEllipsis" />
                                                                            </StackPanel>
                                                                        </StackPanel>
                                                                    </DockPanel>
                                                                </Button>
                                                            </DataTemplate>
                                                        </ItemsControl.ItemTemplate>
                                                    </ItemsControl>
                                                </StackPanel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </Panel>
            </ScrollViewer>
        </DockPanel>
    </Border>
</UserControl>
```

Add the `xmlns:views` namespace, a `Styles` block giving `Button.selected` / `Button.holdsSelected` a raised background (`{DynamicResource SystemControlBackgroundListLowBrush}`), and a tiny `ExpanderChevronConverter` in `Views/Converters.cs` (pattern-match the existing converters there): `true → "▾"`, `false → "▸"`. Code-behind `SessionRailView.axaml.cs` is the standard empty `InitializeComponent` partial (copy `HomeView.axaml.cs`'s shape).

- [ ] **Step 4: Back button removal**

- `WorkspaceView.axaml`: delete the `BackButton` element; the title block takes its grid column.
- `WorkspaceViewModel.cs`: delete the `BackCommand` property (`_backCommand` field included).
- `MainWindowViewModel.OpenSession`: delete the `workspace.BackCommand = CloseWorkspaceCommand;` line.
- Fix every test the Step-1 grep found referencing `BackCommand`/`BackButton` — delete the assertions (the behavior they pinned no longer exists; `CloseWorkspaceCommand` tests stay).

- [ ] **Step 5: Activity gating in `MainWindow.axaml.cs`**

Replace `OnTabSelectionChanged` and `_activityTabSelected` with:

```csharp
    // Activity polls only when it is ACTUALLY on screen: window visible AND Home view AND the
    // section expanded — the same contract the Activity tab's selection used to carry.
    bool _activityExpanded;

    void OnActivityExpandChanged(object? sender, RoutedEventArgs e) {
        _activityExpanded = ActivityExpander.IsExpanded;
        UpdateActivityVisibility();
    }

    void UpdateActivityVisibility() {
        if (DataContext is MainWindowViewModel vm)
            vm.Activity.OnTabVisibleChanged(_activityExpanded && IsVisible && vm.IsHomeView);
    }
```

Keep the existing `OnPropertyChanged` override (IsVisible/DataContext triggers). Add view-change tracking in the constructor:

```csharp
        this.WhenActivated(disposables => {
            ViewModel?.WhenAnyValue(x => x.IsHomeView)
                .Subscribe(_ => UpdateActivityVisibility())
                .DisposeWith(disposables);
        });
```

(`using System.Reactive.Disposables.Fluent;` and `ReactiveUI` are already the codebase's idiom.)

- [ ] **Step 6: Update the smoke tests**

In `MainWindowSmokeTests.cs`:
- Delete `SelectAgentsTab` and every test that clicks through the Agents tab (their grid subject dies in Task 8; if a test asserts VM-level stop/open-in-web behavior rather than grid rendering, move the assertion to `AgentActionServiceTests` style or drop it if already covered there).
- The identity test stays as-is (the status block still renders on the default Home view).
- Add two new tests in the same file's style:

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task Rail_click_opens_the_workspace_in_sessions_view() {
    await AvaloniaSession.WithImmediateRxScheduler(async () => {
        var opened = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap());
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a1", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
                null, null, null, DateTime.UtcNow, null, null, Title: "Fix the flaky test"));

            var (actions, _) = NewActions(service);
            MainWindowViewModel? vm = null;
            var rail = new SessionRailViewModel(service, id => vm!.OpenSession(id), p => p.Contains("/wt/") ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)] : p);
            vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(),
                workspaceFactory: id => WorkspaceFixtures.NewWorkspace(service, actions, id), rail: rail);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.ShowSessionsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Fix the flaky test"));
            row.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var result = (vm.IsSessionsView, vm.CurrentWorkspace?.AgentId);
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });
        await Assert.That(opened.Item1).IsTrue();
        await Assert.That(opened.Item2).IsEqualTo("a1");
    });
}

[Test]
[NotInParallel("AvaloniaSession")]
public async Task Window_boots_tabless_on_home_with_a_sessions_entry() {
    await AvaloniaSession.WithImmediateRxScheduler(async () => {
        var ok = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap());
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var noTabs = !window.GetVisualDescendants().OfType<TabControl>().Any();
            var sessionsButton = window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "HomeSessionsButton");
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return noTabs && sessionsButton;
        });
        await Assert.That(ok).IsTrue();
    });
}
```

(`WorkspaceFixtures.NewWorkspace` — reuse the existing workspace construction helper from `WorkspaceFixtures.cs`; check its actual signature and adapt the call.)

- [ ] **Step 7: Build, run the App suite, fix fallout**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` then `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: everything green except tests that only exist to pin the Agents grid (they go in Task 8 — if they fail to COMPILE now because a control name vanished, move their deletion forward into this task; compile breakage cannot wait).
The Activity cadence tests (existing `ActivityViewModelTests` and any window-level cadence tests): the gate is now expander+view+visible — update the arrange steps that used to select the Activity tab to instead set `ActivityExpander.IsExpanded = true` and `vm.ShowHomeCommand`. Then add one explicit gate test (spec §4: each of the three flips it off) in `MainWindowSmokeTests` style: boot the window, expand `ActivityExpander`, assert `vm.Activity` received visible=true (the existing cadence tests show how visibility is observed — reuse their recording seam); then (a) execute `ShowSessionsCommand`, assert visible=false, `ShowHomeCommand` back to true; (b) collapse the expander, assert false, expand back to true; (c) `window.Hide()`, assert false. One test, three sub-assertions, using `Dispatcher.UIThread.RunJobs()` between steps.

- [ ] **Step 8: Commit**

```bash
git add -A src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "AI-2199: tabless shell — Home/Sessions surfaces, session rail view, Back removed"
```

---

### Task 8: Delete the Agents grid remnants

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (delete `Agents`, `_agentsSource`, `RowComparer`, the WhenActivated agents pipeline)
- Delete (if unreferenced): `src/Capacitor.App/ViewModels/AgentRowViewModel.cs`
- Modify: `src/Capacitor.App/Views/Converters.cs` (delete now-unused converters)
- Delete: `test/Capacitor.App.Tests.Unit/AgentGridTests.cs` (retarget any still-valuable VM assertions first)

**Interfaces:**
- Consumes: Task 7 (XAML no longer binds `Agents`).
- Produces: nothing — pure deletion.

- [ ] **Step 1: Verify the consumers are gone**

Run: `grep -rn 'AgentRowViewModel\|\.Agents\b' src/Capacitor.App --include='*.cs' --include='*.axaml' | grep -v 'daemon.Agents\|service.Agents\|_daemon.Agents'`
Expected: hits only inside `MainWindowViewModel` (the projection being deleted) and `AgentRowViewModel.cs` itself. If the tray or another surface consumes `AgentRowViewModel`, keep that type and delete only the window projection — record which in the commit message.

- [ ] **Step 2: Delete**

- `MainWindowViewModel`: remove `_agentsSource`, `Agents`, `RowComparer`, the `service.Agents.Connect()...SortAndBind` block, the `_agentsSource.Clear()` line, and the now-unused `stopsInFlight` local if nothing else in WhenActivated reads it. The `actions` and `ticker` ctor params STAY (commands/other consumers) — if `ticker` becomes fully unused, keep the parameter (callers pass it everywhere) and note it feeds nothing until a later surface needs it; do NOT change the ctor signature.
- Delete `AgentRowViewModel.cs` (per Step 1).
- `Views/Converters.cs`: `grep -rn 'EmptyStateVisibleConverter\|HeaderRowVisibleConverter\|GridEnabledOpacityConverter' src test` — delete each converter with zero remaining references.
- Review `AgentGridTests.cs`: any test asserting `AgentActionService` behavior (not grid rendering) moves to `AgentActionServiceTests.cs`; the rest is deleted with the file.
- `GridEnabled` on the VM: `grep -rn 'GridEnabled' src test` — if the rail/Home no longer bind it and only tests reference it, delete the projection and its tests; if HomeView binds it, leave it.

- [ ] **Step 3: Run the whole App suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add -A src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "AI-2199: delete the Agents grid — the rail supersedes it"
```

---

### Task 9: Composition root — build, wire, dispose the rail

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildAndShowMainWindow` ~line 611; `_home` read-back site ~line 323; both disposal lists, lines ~187 and ~1092)
- Test: `test/Capacitor.App.Tests.Unit/AppStartupTests.cs` (only if it asserts the disposal list contents — extend, don't restructure)

**Interfaces:**
- Consumes: `SessionRailViewModel` (Task 5), `MainWindowViewModel.Rail` (Task 6).
- Produces: the production window carries a live rail; the rail is disposed on both teardown paths.

- [ ] **Step 1: Wire the rail in `BuildAndShowMainWindow`**

Next to the `home` construction (same `vm`-closure knot):

```csharp
        SessionRailViewModel? rail = null;
        rail = new SessionRailViewModel(service, openSession: agentId => vm?.OpenSession(agentId));
```

Pass `rail: rail` to the `MainWindowViewModel` construction. (`resolveRepoRoot` stays defaulted — production uses the real `GitRepository.ResolveMainRepoRoot`.)

- [ ] **Step 2: Dispose it on both teardown paths**

Mirror `_home` exactly:
- Add a `SessionRailViewModel? _rail;` field beside `_home` (line ~83).
- At the read-back site (~line 323): `_rail = (_coordinator.Window?.DataContext as MainWindowViewModel)?.Rail;`
- Add `_rail` to BOTH UI-disposables lists (lines ~187 and ~1092), right after `_home`, and null it where `_home` is nulled (~line 202).

- [ ] **Step 3: Build and run the full App suite**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj && dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: PASS (AppStartupTests construct through `BuildAndShowMainWindow` with unchanged signature).

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit
git commit -m "AI-2199: compose and dispose the session rail with the window"
```

---

### Task 10: README, full suite, AOT gate

**Files:**
- Modify: `README.md` (only if it documents the app's tabbed layout)
- No new tests.

- [ ] **Step 1: README check**

Run: `grep -n -i 'tab\|agents tab\|activity' README.md`
If the desktop-app section describes the Home/Agents/Activity tabs or the Agents table, rewrite that passage to the two-view structure (Home ⇄ Sessions, rail navigation). If the README does not cover the app's internal layout, no change — state that in the commit message of the final commit instead.

- [ ] **Step 2: Full solution test run**

Run: `dotnet test --solution Capacitor.slnx`
Expected: green, modulo the pre-existing local failures recorded in memory (7 unit + 1 integration session-start nudge tests fail on main on this machine — verify any failure against that list before treating it as a regression).

- [ ] **Step 3: AOT warning gate**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. (The App project is not the AOT target, but the Core wire change rides into the CLI publish.)

- [ ] **Step 4: Commit anything outstanding**

```bash
git add README.md
git commit -m "AI-2199: README for the tabless shell"   # only if README changed
```

---

## Post-plan notes for the executor

- PR references: `Closes` the GitHub issue if one exists for this work, plus `AI-2199` in the description (never the title). The spec file rides this PR (`docs/superpowers/specs/2026-08-25-ai2199-session-rail-design.md`, already committed on the branch).
- Deferred by spec (do NOT implement): per-repo "+" quick-launch, branch names on worktree rows, Needs-you nav counts, Home's 250px nav column, work-item lanes, ⌘N as a real KeyBinding (the hint text is enough for this slice).
