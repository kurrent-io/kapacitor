# Desktop Shell: Chrome, Home, and Repository + Harness Selection — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Avalonia app's window into a Home surface that lists hosted sessions and starts new ones, with the harness chosen per session and remembered per repository.

**Architecture:** The app already observes the daemon over the local control socket (`IDaemonClientService` → `SourceCache<AgentStatusDto>`); Home renders off that cache. Starting a session goes the other way — through the *server*, because the local `Spawn` frame is PTY-only (claude/codex) while the server's `RequestLaunchAgentV2` reaches every vendor via the daemon's runtime factories. The harness catalogue comes from the daemon itself: `DaemonInfoDto` gains an additive `SupportedVendors` member carrying the same set the daemon already advertises to the server, so availability is the runtime-factory probe and never a hardcoded list.

**Tech Stack:** .NET 10, Avalonia 11 + FluentTheme, ReactiveUI.Avalonia, DynamicData, `Microsoft.AspNetCore.SignalR.Client` (new to `Capacitor.App`, already in `Directory.Packages.props` at 10.0.11), TUnit.

**Spec:** The design record is the AI-2171 Linear comment (decisions, with the reasoning); the visual reference is the design canvas linked from it. AI-2194 is this slice.

## Global Constraints

- **Nothing here is safety-bearing.** `AppState` is app-owned UX state only; the CLI's fixed-namespace marker remains the source of truth for anything else (`AppStateStore.cs:6`).
- **AOT.** The app publishes NativeAOT. Every new serialized type joins a `JsonSerializerContext`; no reflection-based `JsonSerializer` overloads. Verify with `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must be empty.
- **`JsonArray` collection expressions are banned** — `[a, b]` compiles to `Add<T>()` which needs dynamic code. Use `new JsonArray(a, b)`.
- **Additive DTO members only.** `DaemonInfoDto`/`AgentStatusDto` members are appended with defaults; never reorder or retype. No new `FrameType` values in this slice.
- **Harness availability is never version-gated.** It comes from the runtime factory's own `IsAvailable()`, mirroring the borrowed-review invariant: a vendor auto-update must not silently withdraw a capability.
- **Model sentinel:** `LaunchAgentRequestV2.Model` is the empty string for "vendor default", never null (`Capacitor.Server.Core/Models.cs:471`).
- **Vendor must be explicit:** a null `Vendor` normalizes to Claude server-side. Always send the selected token.
- **Hub launches always create a daemon-owned worktree.** `CapacitorHub` passes `borrowed: false` (`CapacitorHub.cs:3007`). Sessions started from Home land in a fresh worktree — do not promise in-place launches in copy.
- **Tests:** TUnit on Microsoft Testing Platform. Throwaway dirs come from Helpers' `TempDir`. Console capture uses `ConsoleOutput.StartCapture()` with bare `[NotInParallel]`. Environment variables go through `EnvScope`.

---

## File Structure

**New:**
- `src/Capacitor.App/Services/HarnessCatalog.cs` — merges the daemon's advertised vendor tokens with display metadata (label, transport family, default model). Pure; no I/O.
- `src/Capacitor.App/Services/ILaunchClient.cs` — the launch seam, so `HomeViewModel` tests never open a socket.
- `src/Capacitor.App/Services/ServerLaunchClient.cs` — SignalR client invoking `RequestLaunchAgentV2`.
- `src/Capacitor.App/ViewModels/HomeViewModel.cs` — repository + harness selection, remembered preference, start command, session cards.
- `src/Capacitor.App/ViewModels/SessionCardViewModel.cs` — one active-session card.
- `src/Capacitor.App/Views/HomeView.axaml` (+ `.axaml.cs`) — the Home surface.

**Modified:**
- `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:21-23` — `DaemonInfoDto` gains `SupportedVendors`.
- `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs` — populate it.
- `src/Capacitor.App/Services/AppStateStore.cs:9-13` — `AppState` gains `HarnessByRepo`.
- `src/Capacitor.App/Capacitor.App.csproj:14-19` — add the SignalR client package reference.
- `src/Capacitor.App/Views/MainWindow.axaml:47` — Home joins as the first tab.

**Tests:** one file per unit, mirroring the above, in `test/Capacitor.App.Tests.Unit/` (and `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/` for the DTO).

---

## Task 1: Advertise supported vendors on the local status snapshot

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:21-23`
- Modify: `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/DaemonStatusDtoTests.cs`

**Interfaces:**
- Produces: `DaemonInfoDto.SupportedVendors` — `string[]?`, trailing, default `null`. Vendor tokens exactly as the runtime factories key them (`claude`, `codex`, `cursor`, `copilot`, `gemini`, `kiro`, `opencode`, `antigravity`, `pi`). `null` means "an older daemon that never set it" — consumers must treat that as *unknown*, not *none*.

- [ ] **Step 1: Write the failing test**

Append to `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/DaemonStatusDtoTests.cs`:

```csharp
[Test]
public async Task Supported_vendors_round_trips() {
    var dto = new DaemonStatusDto(
        new DaemonInfoDto("kcap-dev", "1.2.3", "https://example.test", "connected", 4, 1,
            Pid: 42, InstanceId: "abc", SupportedVendors: ["claude", "cursor"]),
        []);

    var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
    var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

    await Assert.That(back.Daemon.SupportedVendors).IsEquivalentTo(new[] { "claude", "cursor" });
}

[Test]
public async Task Snapshot_without_supported_vendors_deserializes_as_null() {
    const string json = """
        {"daemon":{"name":"kcap-dev","version":"1","server_url":"u","connection":"connected",
        "max_agents":4,"active_agents":0,"pid":1,"instance_id":"i"},"agents":[]}
        """;

    var back = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto)!;

    await Assert.That(back.Daemon.SupportedVendors).IsNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/DaemonStatusDtoTests/*"`
Expected: FAIL — `DaemonInfoDto` has no `SupportedVendors` member (compile error).

- [ ] **Step 3: Add the member**

`src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`:

```csharp
public sealed record DaemonInfoDto(
    string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents,
    int? Pid = null, string? InstanceId = null,
    // Vendor tokens this daemon can host, from the runtime factories' own availability probe —
    // the same set advertised to the server on DaemonConnect. Trailing/additive: null from a
    // daemon that predates it, which a client must read as UNKNOWN, never as "hosts nothing".
    string[]? SupportedVendors = null);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj --treenode-filter "/*/*/DaemonStatusDtoTests/*"`
Expected: PASS

- [ ] **Step 5: Populate it in the daemon**

In `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs`, where `DaemonInfoDto` is constructed, pass the same vendor set the daemon computes for `DaemonConnect.SupportedVendors`. Find that computation first:

```bash
grep -n 'SupportedVendors' src/Capacitor.Cli.Daemon/DaemonRunner.cs
```

Thread the resulting `string[]` into `DaemonStatusIpc` the way the other daemon-identity values reach it (constructor injection or the existing snapshot factory — follow whatever `Name`/`Version` already do; do not introduce a second source of truth for the vendor set).

- [ ] **Step 6: Extend the app-test snapshot helper**

`test/Capacitor.App.Tests.Unit/FakeDaemonClientService.cs:33` has a `Snap(...)` factory that builds `DaemonInfoDto` positionally. The new member is trailing and defaulted, so it still compiles — but Task 5's view-model needs to script a vendor set. Add a matching optional parameter:

```csharp
public static DaemonStatusDto Snap(
        string daemon = "daemon-a", string version = "1.2.3", string serverUrl = "http://localhost:9999",
        string connection = "connected", int active = 0, int max = 5, int? pid = null, string? instanceId = null,
        string[]? supportedVendors = null) {
```

and pass `supportedVendors` as the last argument to the `DaemonInfoDto` constructor.

- [ ] **Step 7: Run the daemon suite**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/DaemonStatusDtoTests.cs
git commit -m "feat(ipc): advertise supported vendors on the daemon status snapshot"
```

---

## Task 2: Remember the harness per repository

**Files:**
- Modify: `src/Capacitor.App/Services/AppStateStore.cs:9-13`
- Test: `test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs`

**Interfaces:**
- Produces: `AppState.HarnessByRepo` — `IReadOnlyDictionary<string, string>?`, absolute repository path → vendor token. `null` or a missing key means "never chosen here"; the caller falls back to its own default. The reserved key `""` (empty string) holds the choice for the "No repository" scratch target.

- [ ] **Step 1: Write the failing test**

Append to `test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs`:

```csharp
[Test]
public async Task Harness_choice_is_remembered_per_repo() {
    using var tmp = TempDir.WithPathTo("app-state.json", out var path);
    var store = new AppStateStore(path);

    await store.UpdateAsync(s => s with {
        HarnessByRepo = new Dictionary<string, string> {
            ["/home/a/kcap-cli"] = "codex",
            ["/home/a/kcap-web"] = "kiro",
        }
    });

    var reloaded = await new AppStateStore(path).LoadAsync();

    await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-cli"]).IsEqualTo("codex");
    await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-web"]).IsEqualTo("kiro");
}

[Test]
public async Task Missing_harness_map_is_null_not_empty() {
    using var tmp = TempDir.WithPathTo("app-state.json", out var path);
    var state = await new AppStateStore(path).LoadAsync();
    await Assert.That(state.HarnessByRepo).IsNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/AppStateStoreTests/*"`
Expected: FAIL — `AppState` has no `HarnessByRepo` member.

- [ ] **Step 3: Add the member**

`src/Capacitor.App/Services/AppStateStore.cs`:

```csharp
public sealed record AppState(
    bool ShimOffered = false,
    bool ShimDenied = false,
    IReadOnlyList<string>? DeclinedTakeoverPairs = null,
    bool ConsentQuarantineAcked = false,
    // Absolute repo path -> vendor token. "" is the scratch ("No repository") target.
    // Absent key = never chosen here; the caller picks its own default rather than
    // inheriting another repository's choice.
    IReadOnlyDictionary<string, string>? HarnessByRepo = null);
```

`AppStateJsonContext` already covers `AppState`; the source generator picks the dictionary up from the record. No context change is needed — confirm by running the test, and if the generator complains, add `[JsonSerializable(typeof(Dictionary<string, string>))]` to the context.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/AppStateStoreTests/*"`
Expected: PASS (all pre-existing tests in the class too — the record's `IsEqualTo(new AppState())` assertions still hold because the new member defaults to null)

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/AppStateStore.cs test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs
git commit -m "feat(app): remember the chosen harness per repository"
```

---

## Task 3: Harness catalogue

**Files:**
- Create: `src/Capacitor.App/Services/HarnessCatalog.cs`
- Test: `test/Capacitor.App.Tests.Unit/HarnessCatalogTests.cs`

**Interfaces:**
- Produces:
  - `sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available)` — `TransportFamily` ∈ `"pty" | "acp" | "rpc"`, used only for the picker's grouping colour and its one-line description.
  - `static IReadOnlyList<HarnessOption> HarnessCatalog.Build(string[]? supportedVendors)` — every known vendor, in a stable display order, with `Available` set from the advertised set. A `null` argument (older daemon: *unknown*, per Task 1) marks everything `Available = true` rather than greying the whole picker out.
  - `static string HarnessCatalog.DescriptionFor(HarnessOption option)` — e.g. `"PTY · terminal + chat"`, `"ACP · chat"`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.App.Tests.Unit/HarnessCatalogTests.cs`:

```csharp
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class HarnessCatalogTests {
    [Test]
    public async Task Advertised_vendors_are_available_and_others_are_not() {
        var options = HarnessCatalog.Build(["claude", "cursor"]);

        await Assert.That(options.Single(o => o.Vendor == "claude").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "cursor").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "pi").Available).IsFalse();
    }

    [Test]
    public async Task Unknown_vendor_set_leaves_everything_available() {
        var options = HarnessCatalog.Build(null);
        await Assert.That(options.All(o => o.Available)).IsTrue();
    }

    [Test]
    public async Task Transport_family_matches_how_the_daemon_hosts_each_vendor() {
        var options = HarnessCatalog.Build(null).ToDictionary(o => o.Vendor);

        await Assert.That(options["claude"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["codex"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["cursor"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["opencode"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["antigravity"].TransportFamily).IsEqualTo("rpc");
        await Assert.That(options["pi"].TransportFamily).IsEqualTo("rpc");
    }

    [Test]
    public async Task An_unknown_advertised_vendor_is_listed_rather_than_dropped() {
        var options = HarnessCatalog.Build(["claude", "brandnew"]);

        var added = options.Single(o => o.Vendor == "brandnew");
        await Assert.That(added.Available).IsTrue();
        await Assert.That(added.Label).IsEqualTo("brandnew");
    }

    [Test]
    public async Task Description_names_the_transport_and_the_surface() {
        var pty = HarnessCatalog.Build(null).Single(o => o.Vendor == "claude");
        var acp = HarnessCatalog.Build(null).Single(o => o.Vendor == "gemini");

        await Assert.That(HarnessCatalog.DescriptionFor(pty)).IsEqualTo("PTY · terminal + chat");
        await Assert.That(HarnessCatalog.DescriptionFor(acp)).IsEqualTo("ACP · chat");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HarnessCatalogTests/*"`
Expected: FAIL — `HarnessCatalog` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Capacitor.App/Services/HarnessCatalog.cs`:

```csharp
namespace Capacitor.App.Services;

public sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available);

/// The picker's vendor list. Availability comes from what the daemon advertises
/// (DaemonInfoDto.SupportedVendors), never from a version check — a vendor auto-update must not
/// silently withdraw a harness. A vendor the daemon advertises but this build has never heard of
/// is still offered, listed under its raw token: the daemon is the authority on what it can host.
public static class HarnessCatalog {
    static readonly (string Vendor, string Label, string Family)[] Known = [
        ("claude",      "Claude",      "pty"),
        ("codex",       "Codex",       "pty"),
        ("cursor",      "Cursor",      "acp"),
        ("copilot",     "Copilot",     "acp"),
        ("gemini",      "Gemini",      "acp"),
        ("kiro",        "Kiro",        "acp"),
        ("opencode",    "OpenCode",    "acp"),
        ("antigravity", "Antigravity", "rpc"),
        ("pi",          "Pi",          "rpc"),
    ];

    public static IReadOnlyList<HarnessOption> Build(string[]? supportedVendors) {
        // null = an older daemon that never sent the field: unknown, not empty.
        var advertised = supportedVendors is null
            ? null
            : new HashSet<string>(supportedVendors, StringComparer.OrdinalIgnoreCase);

        var options = Known
            .Select(k => new HarnessOption(k.Vendor, k.Label, k.Family, advertised?.Contains(k.Vendor) ?? true))
            .ToList();

        if (advertised is null) return options;

        var known = new HashSet<string>(Known.Select(k => k.Vendor), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in supportedVendors!.Where(v => !known.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            options.Add(new HarnessOption(extra, extra, "rpc", true));

        return options;
    }

    public static string DescriptionFor(HarnessOption option) => option.TransportFamily switch {
        "pty" => "PTY · terminal + chat",
        "acp" => "ACP · chat",
        _     => "chat",
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HarnessCatalogTests/*"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/HarnessCatalog.cs test/Capacitor.App.Tests.Unit/HarnessCatalogTests.cs
git commit -m "feat(app): harness catalogue driven by the daemon's advertised vendors"
```

---

## Task 4: Launch client

**Files:**
- Create: `src/Capacitor.App/Services/ILaunchClient.cs`
- Create: `src/Capacitor.App/Services/ServerLaunchClient.cs`
- Modify: `src/Capacitor.App/Capacitor.App.csproj:14-19`
- Test: `test/Capacitor.App.Tests.Unit/LaunchRequestTests.cs`

**Interfaces:**
- Produces:
  - `sealed record LaunchRequest(string DaemonName, string RepoPath, string Vendor, string? Prompt)`
  - `sealed record LaunchOutcome(bool Started, string? AgentId, string? Error)`
  - `interface ILaunchClient { Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct); }`
  - `sealed record LaunchAgentRequestV2Payload` — the hub argument as a concrete, source-generated type. **Not** an anonymous type: this assembly publishes NativeAOT, so the payload must round-trip through a `JsonSerializerContext`, and SignalR must be told to use it.
  - `static LaunchAgentRequestV2Payload LaunchPayload.For(LaunchRequest r)` — builds the `RequestLaunchAgentV2` argument. Split out from the transport so its shape is testable without a hub.
  - `partial class LaunchJsonContext : JsonSerializerContext` — registers the payload.

The hub contract this targets (`Capacitor.Server.Core/Models.cs:447`):

```
RequestLaunchAgentV2(LaunchAgentRequestV2) -> Task<string>   // returns the agent id
LaunchAgentRequestV2(DaemonName, Prompt?, Model, Effort?, RepoPath, Tools?,
                     AttachmentIds?, Visibility?, Grants?, Vendor?,
                     CodexPosture?, AcpPermissionPreset? = null)
```

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.App.Tests.Unit/LaunchRequestTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class LaunchRequestTests {
    // Source-generated context, not the reflection overload: this assembly is AOT-published.
    static JsonElement Payload(LaunchRequest request) =>
        JsonSerializer.SerializeToElement(
            LaunchPayload.For(request), LaunchJsonContext.Default.LaunchAgentRequestV2Payload);

    [Test]
    public async Task Model_is_the_empty_string_sentinel_not_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "cursor", "go"));

        // Server contract: "" means "use the vendor default"; null is not a legal value.
        await Assert.That(json.GetProperty("model").GetString()).IsEqualTo("");
    }

    [Test]
    public async Task Vendor_is_always_sent_explicitly() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "gemini", null));

        // A null vendor normalizes to Claude server-side — never rely on that.
        await Assert.That(json.GetProperty("vendor").GetString()).IsEqualTo("gemini");
    }

    [Test]
    public async Task Prompt_and_repo_path_are_carried_verbatim() {
        var json = Payload(new LaunchRequest("kcap-dev", "/home/a/kcap-cli", "claude", "Fix the flaky test"));

        await Assert.That(json.GetProperty("prompt").GetString()).IsEqualTo("Fix the flaky test");
        await Assert.That(json.GetProperty("repoPath").GetString()).IsEqualTo("/home/a/kcap-cli");
        await Assert.That(json.GetProperty("daemonName").GetString()).IsEqualTo("kcap-dev");
    }

    [Test]
    public async Task Blank_prompt_is_sent_as_null() {
        var json = Payload(new LaunchRequest("kcap-dev", "/repo", "claude", "   "));
        await Assert.That(json.GetProperty("prompt").ValueKind).IsEqualTo(JsonValueKind.Null);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/LaunchRequestTests/*"`
Expected: FAIL — `LaunchRequest` / `LaunchPayload` do not exist.

- [ ] **Step 3: Add the package reference**

`src/Capacitor.App/Capacitor.App.csproj`, in the existing `PackageReference` group (central package management supplies the version):

```xml
<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" />
```

- [ ] **Step 4: Write the seam and the payload**

Create `src/Capacitor.App/Services/ILaunchClient.cs`:

```csharp
namespace Capacitor.App.Services;

public sealed record LaunchRequest(string DaemonName, string RepoPath, string Vendor, string? Prompt);

public sealed record LaunchOutcome(bool Started, string? AgentId, string? Error);

/// Starting a session goes through the SERVER, not the local socket: the local Spawn frame
/// resolves against the daemon's PTY launchers (claude and codex only), while the server's
/// RequestLaunchAgentV2 reaches every vendor through the runtime factories.
public interface ILaunchClient {
    Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct);
}

/// The RequestLaunchAgentV2 hub argument. A concrete record, not an anonymous type: this
/// assembly publishes NativeAOT, so the payload rides a source-generated context (below) and
/// the HubConnection is told to use it. Member names must match LaunchAgentRequestV2's
/// properties — the hub binds by name.
public sealed record LaunchAgentRequestV2Payload {
    [JsonPropertyName("daemonName")]          public required string   DaemonName          { get; init; }
    [JsonPropertyName("prompt")]              public          string?  Prompt              { get; init; }
    [JsonPropertyName("model")]               public required string   Model               { get; init; }
    [JsonPropertyName("effort")]              public          string?  Effort              { get; init; }
    [JsonPropertyName("repoPath")]            public required string   RepoPath            { get; init; }
    [JsonPropertyName("tools")]               public          string[]? Tools              { get; init; }
    [JsonPropertyName("attachmentIds")]       public          string[]? AttachmentIds      { get; init; }
    [JsonPropertyName("visibility")]          public          string?  Visibility          { get; init; }
    [JsonPropertyName("grants")]              public          object[]? Grants             { get; init; }
    [JsonPropertyName("vendor")]              public required string   Vendor              { get; init; }
    [JsonPropertyName("codexPosture")]        public          object?  CodexPosture        { get; init; }
    [JsonPropertyName("acpPermissionPreset")] public          string?  AcpPermissionPreset { get; init; }
}

[JsonSerializable(typeof(LaunchAgentRequestV2Payload))]
public partial class LaunchJsonContext : JsonSerializerContext;

/// The RequestLaunchAgentV2 argument, split from the transport so its shape is testable.
public static class LaunchPayload {
    public static LaunchAgentRequestV2Payload For(LaunchRequest r) => new() {
        DaemonName = r.DaemonName,
        Prompt     = string.IsNullOrWhiteSpace(r.Prompt) ? null : r.Prompt,
        Model      = "",   // vendor default; the server rejects null
        RepoPath   = r.RepoPath,
        Vendor     = r.Vendor,
    };
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/LaunchRequestTests/*"`
Expected: PASS

- [ ] **Step 6: Write the transport**

Create `src/Capacitor.App/Services/ServerLaunchClient.cs`. It resolves the server URL and bearer token from `Capacitor.Cli.Core` (the app already references it — use the same `AppConfig`/`TokenStore` path the CLI uses; read `AppConfig.ResolvedServerUrl` and the active profile's token rather than re-implementing profile resolution), opens a `HubConnection` to `{serverUrl}/hub` lazily, and invokes the hub method.

**AOT:** point SignalR's JSON protocol at the generated context when building the connection, or the payload serializes reflectively at runtime and the publish warns:

```csharp
.AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolver = LaunchJsonContext.Default)
```

Then:

```csharp
var agentId = await connection.InvokeAsync<string>("RequestLaunchAgentV2", LaunchPayload.For(request), ct);
return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
```

Wrap the invoke in `try/catch (Exception ex)` returning `new LaunchOutcome(false, null, ex.Message)` — a `HubException` carries the server's own rejection text (capacity, unknown vendor, consent denial), which is exactly what Home should show. Confirm the hub path against the CLI's own connection setup:

```bash
grep -rn 'HubConnectionBuilder\|WithUrl' src/Capacitor.Cli.Daemon --include='*.cs' | head
```

Match whatever URL suffix and access-token provider the daemon uses; do not invent a second convention.

- [ ] **Step 7: Verify it builds and the suite is green**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj && dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: build succeeds, all tests PASS

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.App/Services/ILaunchClient.cs src/Capacitor.App/Services/ServerLaunchClient.cs src/Capacitor.App/Capacitor.App.csproj test/Capacitor.App.Tests.Unit/LaunchRequestTests.cs
git commit -m "feat(app): launch sessions through the server hub"
```

---

## Task 5: HomeViewModel

**Files:**
- Create: `src/Capacitor.App/ViewModels/HomeViewModel.cs`
- Create: `src/Capacitor.App/ViewModels/SessionCardViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs`

**Interfaces:**
- Consumes: `IDaemonClientService` (Task 0, pre-existing), `IAppStateStore.HarnessByRepo` (Task 2), `HarnessCatalog.Build` (Task 3), `ILaunchClient.StartAsync` (Task 4).
- Produces:
  - `HomeViewModel(IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch)`
  - `string SelectedRepoPath { get; set; }` — `""` for the scratch target.
  - `string SelectedVendor { get; }` — follows the repository; setting it goes through `ChooseHarnessAsync`.
  - `bool RememberHarness { get; set; }` — default `true`.
  - `Task ChooseHarnessAsync(string vendor)` — sets the selection and, when `RememberHarness`, persists it for `SelectedRepoPath`.
  - `Task SelectRepositoryAsync(string repoPath)` — sets the repository and restores that repository's remembered harness, or `DefaultVendor` when none.
  - `const string DefaultVendor = "claude"` — the fallback for a repository with no remembered choice.
  - `const string ScratchRepoPath = ""` — the "No repository" target; a session started against it runs in a daemon-owned worktree with no upstream checkout.
  - `IReadOnlyList<HarnessOption> Harnesses { get; }`
  - `string Goal { get; set; }` — the launcher's free-text goal; cleared on a successful start.
  - `string? StartError { get; }`
  - `ReactiveCommand<Unit, Unit> StartCommand`
  - `ReadOnlyObservableCollection<SessionCardViewModel> Sessions { get; }`

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class HomeViewModelTests {
    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchRequest? Last;
        public LaunchOutcome Next = new(true, "agent-1", null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Last = request;
            return Task.FromResult(Next);
        }
    }

    static HomeViewModel Build(out RecordingLaunchClient launch, out AppStateStore store, string statePath) {
        launch = new RecordingLaunchClient();
        store = new AppStateStore(statePath);
        return new HomeViewModel(new FakeDaemonClientService(), store, launch);
    }

    [Test]
    public async Task Choosing_a_harness_remembers_it_for_that_repository() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");

        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
    }

    [Test]
    public async Task Switching_repository_restores_that_repositorys_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        await vm.SelectRepositoryAsync("/repo/b");
        await vm.ChooseHarnessAsync("kiro");
        await vm.SelectRepositoryAsync("/repo/a");

        await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_repository_with_no_choice_falls_back_to_the_default_not_the_previous_repo() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("kiro");
        await vm.SelectRepositoryAsync("/repo/never-seen");

        await Assert.That(vm.SelectedVendor).IsEqualTo(HomeViewModel.DefaultVendor);
    }

    [Test]
    public async Task Not_remembering_leaves_the_stored_choice_untouched() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        vm.RememberHarness = false;
        await vm.ChooseHarnessAsync("pi");

        await Assert.That(vm.SelectedVendor).IsEqualTo("pi");
        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");
    }

    [Test]
    public async Task Start_sends_the_selected_repository_and_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("gemini");
        vm.Goal = "Fix the flaky test";
        await vm.StartCommand.Execute();

        await Assert.That(launch.Last!.RepoPath).IsEqualTo("/repo/a");
        await Assert.That(launch.Last!.Vendor).IsEqualTo("gemini");
        await Assert.That(launch.Last!.Prompt).IsEqualTo("Fix the flaky test");
    }

    [Test]
    public async Task A_failed_start_surfaces_the_servers_reason() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);
        launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.StartCommand.Execute();

        await Assert.That(vm.StartError).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
    }

    [Test]
    public async Task The_scratch_target_keeps_its_own_remembered_harness() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out _, out var store, path);

        await vm.SelectRepositoryAsync("/repo/a");
        await vm.ChooseHarnessAsync("codex");
        await vm.SelectRepositoryAsync(HomeViewModel.ScratchRepoPath);
        await vm.ChooseHarnessAsync("claude");

        var saved = await store.LoadAsync();
        await Assert.That(saved.HarnessByRepo![HomeViewModel.ScratchRepoPath]).IsEqualTo("claude");
        await Assert.That(saved.HarnessByRepo!["/repo/a"]).IsEqualTo("codex");

        await vm.SelectRepositoryAsync("/repo/a");
        await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
    }

    [Test]
    public async Task A_successful_start_clears_the_goal_and_any_previous_error() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var vm = Build(out var launch, out _, path);
        launch.Next = new LaunchOutcome(false, null, "boom");
        await vm.SelectRepositoryAsync("/repo/a");
        await vm.StartCommand.Execute();

        launch.Next = new LaunchOutcome(true, "agent-2", null);
        vm.Goal = "next thing";
        await vm.StartCommand.Execute();

        await Assert.That(vm.StartError).IsNull();
        await Assert.That(vm.Goal).IsEqualTo("");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HomeViewModelTests/*"`
Expected: FAIL — `HomeViewModel` does not exist.

- [ ] **Step 3: Read the existing view-model conventions before writing**

Read `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` and `src/Capacitor.App/ViewModels/AgentRowViewModel.cs`, and `test/Capacitor.App.Tests.Unit/FakeDaemonClientService.cs`. Match their `ReactiveObject` / `RaiseAndSetIfChanged` / `ToProperty` idiom and their DynamicData binding of `IDaemonClientService.Agents` — do not introduce a second style.

- [ ] **Step 4: Write the implementation**

Create `src/Capacitor.App/ViewModels/SessionCardViewModel.cs` (title from the agent's repo leaf + vendor, the state dot from `AgentStatusDto.Status`, age from `CreatedAt` — reuse `UptimeFormat` and `StatusColors`, which already exist at `src/Capacitor.App/UptimeFormat.cs` and `StatusColors.cs`).

Create `src/Capacitor.App/ViewModels/HomeViewModel.cs` implementing the interface listed above. The two rules the tests pin:

- A repository with no remembered harness falls back to `DefaultVendor`, never to the previously selected vendor — otherwise the preference leaks across repositories.
- `RememberHarness = false` must not erase an existing stored choice; it only skips the write.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HomeViewModelTests/*"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/HomeViewModel.cs src/Capacitor.App/ViewModels/SessionCardViewModel.cs test/Capacitor.App.Tests.Unit/HomeViewModelTests.cs
git commit -m "feat(app): Home view-model with per-repository harness memory"
```

---

## Task 6: Home view and window wiring

**Files:**
- Create: `src/Capacitor.App/Views/HomeView.axaml`, `src/Capacitor.App/Views/HomeView.axaml.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml:47`
- Test: `test/Capacitor.App.Tests.Unit/HomeViewSmokeTests.cs`

**Interfaces:**
- Consumes: `HomeViewModel` (Task 5).
- Produces: named controls the smoke test finds in the window's name scope — `GoalInput`, `RepositoryChip`, `HarnessChip`, `StartButton`, `StartErrorText`, `SessionCards`.

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.App.Tests.Unit/HomeViewSmokeTests.cs`, following the existing `MainWindowSmokeTests.cs` exactly for session setup and control lookup (it already solves headless Avalonia startup via `AvaloniaSession`). Assert that:

- `HomeView` resolves all six named controls.
- `StartButton.IsEnabled` is false while `SelectedRepoPath` is unset and true once a repository is selected.
- `StartErrorText.IsVisible` follows `HomeViewModel.StartError` being non-null.
- `SessionCards` item count tracks the `FakeDaemonClientService` agent cache.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HomeViewSmokeTests/*"`
Expected: FAIL — `HomeView` does not exist.

- [ ] **Step 3: Write the view**

Create `HomeView.axaml` following the design canvas: greeting row with Search and New session, the launcher card (goal input, repository chip, harness chip, remember toggle, Start), then the Active sessions grid. `MainWindow.axaml` currently uses FluentTheme defaults with no explicit palette — introduce the design's colours as a `ResourceDictionary` in `App.axaml` (canvas `#0B0D12`, surface `#12151D`, raised `#191D27`, border `#2A3040`, text `#F1F3F7`, muted `#9299AA`, accent `#5BE0B3`, warning `#F4B860`, purple `#A994FF`, danger `#FF7272`) rather than hardcoding hexes per control. These are the values `prototypes/AI-2171-desktop-sessions/PrototypeUi.cs` already established.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj --treenode-filter "/*/*/HomeViewSmokeTests/*"`
Expected: PASS

- [ ] **Step 5: Add Home as the first tab**

In `MainWindow.axaml:47`, add a `TabItem Header="Home"` before `Agents`, hosting `HomeView`. Leave the existing Agents and Activity tabs and every control name in them untouched — `MainWindowSmokeTests` finds those by name.

- [ ] **Step 6: Run the whole app suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: PASS, including the pre-existing `MainWindowSmokeTests`.

- [ ] **Step 7: Verify AOT is clean**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 8: Update the README**

Any user-facing CLI surface change needs `README.md` in the same PR. This slice changes the *app*, not the CLI — but if `kcap agent start`'s behaviour or flags were touched anywhere in the work, update both the quick-start and the per-command section. Verify with `git diff --stat src/Capacitor.Cli/` before deciding.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/Views/HomeView.axaml src/Capacitor.App/Views/HomeView.axaml.cs src/Capacitor.App/Views/MainWindow.axaml src/Capacitor.App/App.axaml test/Capacitor.App.Tests.Unit/HomeViewSmokeTests.cs
git commit -m "feat(app): Home surface with repository and harness selection"
```

---

## Deliberate deviations from the design

- **The left nav rail is not in this slice.** The design replaces `MainWindow`'s `TabControl` with a 250px nav rail (Home / Sessions / Needs you / Repositories). Task 6 adds Home as a *tab* instead, because the rail lands properly in AI-2195 alongside the session rail, and because every control name in the existing Agents and Activity tabs is load-bearing for `MainWindowSmokeTests`. Swapping the chrome twice is worse than swapping it once, in the slice that needs it.
- **Every session started here runs in a daemon-owned worktree.** `CapacitorHub` hardcodes `borrowed: false`, so there is no in-place launch through this path. The design's `main`-checkout row therefore only ever holds sessions started outside the app. Do not write copy promising otherwise.

## Softer steps — read before executing

Three steps deliberately direct rather than dictate, because the surrounding code has an established shape that must be matched rather than guessed:

- **Task 1 Step 5** (populating `SupportedVendors` in the daemon) — the vendor set already exists for `DaemonConnect`; find it and reuse it, never compute a second one.
- **Task 4 Step 6** (`ServerLaunchClient` transport) — match the daemon's own `HubConnectionBuilder` URL suffix and access-token provider.
- **Task 6 Step 1** (the smoke test) — `MainWindowSmokeTests` already solves headless Avalonia startup; copy its session setup rather than inventing one.

If you are executing this with a subagent per task, give each of those tasks the named reference file in its brief.

## Open questions for the implementer

1. **Which daemon does Home target?** `LaunchRequest.DaemonName` needs a value. `IDaemonClientService.DaemonName` is the locally attached daemon — correct for the single-daemon case, which is all this slice supports. Multi-daemon selection is out of scope; if `DaemonName` is empty, disable Start with "No daemon connected".
2. **Where does the repository list come from?** This slice has no repository registry. Derive the list from the distinct `AgentStatusDto.RepoPath` values in the agent cache plus anything already in `AppState.HarnessByRepo`, and let the user add one with a folder picker. A proper registry is worth its own issue if this proves thin.
3. **`ServerLaunchClient` has no test here.** Its payload is covered (Task 4) but the transport is not — it needs a live hub. Either accept that gap for this slice or add an integration test under `test/Capacitor.Cli.Tests.Integration/` if a server fixture already exists there.
