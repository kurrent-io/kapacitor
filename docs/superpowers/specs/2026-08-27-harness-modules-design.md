# Harness modules: one type per vendor, one list, no type that names them all

Follow-up to part 4 of `2026-08-21-ai2147-config-dir-explicit-context-design.md`, which replaced the
flattened bag of vendor override strings with a `HarnessPaths` bundle. The bundle removed the
duplicate reads but kept the shape underneath: one type in shared Core naming all nine vendors as
members. This design retires that shape.

## Problem

Adding a harness edits shared code in five places — a `HarnessPaths` property and its factory line,
an `AgentDetectionResult` field, an arm of `AgentDetection.Detect`, and a `HarnessCatalog` row —
before anything vendor-specific is written. The harness layout rule asks for the opposite: a new
`Harness/<Vendor>/` directory plus one registration site per assembly.

Two symptoms show it is the wrong shape rather than merely verbose.

**`KnownHarness.IsWired` takes a `HarnessPaths`.** A vendor's wiring check needs one vendor's layout
and receives all nine, because the delegate cannot name the type it wants.

**`KnownHarness.Select` exists only to index a positional record.** Every consumer already loops the
catalog — `HarnessInventory`, `HarnessNudge`, `FirstRunMachineReport`, the desktop app's Agents and
Import steps — so the selector is a workaround for the result not being keyed. The only site that
names vendors positionally is `SkillsCommand.ConsumerPresent`.

Meanwhile the codebase already has the shape this wants, three times: `IImportSource`,
`IHostedAgentLauncher` and the reviewer runtime factories are each an interface with one
implementation per vendor, registered in a list. The path and detection side is the outlier.

## The shape

One interface per question a caller asks, one type per vendor implementing only the questions it can
answer, one list where they are registered. A vendor's paths become a private field of its module —
nothing outside asks for `ClaudePaths`.

```csharp
public interface IHarnessModule {                 // identity; every vendor has it
    string VendorId { get; }
    string Label { get; }
    string? InstallFlag { get; }
}

public interface IPathProbed    { IReadOnlyList<string> Binaries { get; } }   // not Cursor
public interface IMarkerProbed  { bool IsInstalled { get; } }                 // not Claude, not Codex
public interface IWirable       { bool IsWired { get; } }
public interface ISkillsHost    { string SkillsDir { get; } bool ReadsSharedTree { get; } }
public interface IImportable    { IImportSource ImportSource(ConfigRoot config); }
```

```csharp
sealed class GeminiHarness(UserHome home)
        : IHarnessModule, IPathProbed, IMarkerProbed, IWirable, ISkillsHost {
    readonly GeminiPaths _paths = GeminiPaths.FromEnvironment(home);   // private

    public string VendorId => "gemini";
    public IReadOnlyList<string> Binaries => ["gemini"];
    public bool IsInstalled => _paths.IsInstalled;
    public bool IsWired => GeminiHooksInstaller.IsInstalled(_paths.SettingsJson);
    public string SkillsDir => _paths.SkillsDir;
}
```

Registration is one list, and that list is the only place shared code names a vendor. It stays a
list rather than a map: setup and display order is meaningful, and a dictionary would not carry it.

Consumers become queries — `OfType<IPathProbed>()` for the PATH pass, `OfType<IWirable>()` for the
status line and the nudge, `OfType<IImportable>()` for import sources, `OfType<ISkillsHost>()` for
skills sync.

## Why instances rather than static-abstract descriptors

A descriptor struct keyed by type parameter (`Detect<ClaudeDescriptor>()`) cannot recover that
vendor's paths type: C# has no associated types, so the paths type must be a second type parameter,
and every call site names both. An instance carries its resolved paths instead, so there is no
type-to-instance lookup and no two-parameter generic anywhere.

`OfType<T>` is also a stronger statement than the sentinels it replaces. Cursor has no PATH probe by
design and Claude and Codex have no on-disk marker; today those are `[]` and `false` with a comment,
and under this shape they are the absence of an interface. It costs less under NativeAOT too:
interface dispatch rather than nine monomorphised instantiations, and no reflection either way.

## What "plugins" can mean here

NativeAOT rules out loading assemblies at runtime, so a plugin cannot be a third-party DLL. Two
things it can be:

- **Compile-time modules in one list** — the shape above. Adding a vendor becomes a directory plus a
  registration line, with nothing in shared code enumerating vendors.
- **A declarative manifest**, vendor described as data and interpreted by generic code. It fits the
  vendors whose integration is a hooks file, a marker and a binary name. It does not fit
  Antigravity's two product roots, Codex's `config.toml` sandbox allowlist, Cursor's SQLite
  workspace storage, or OpenCode's TypeScript plugin. A manifest is this interface set serialised,
  so it is reachable only after the interfaces exist and some vendors prove uniform under them.

## Work order

Each step leaves the tree shippable.

1. `IHarnessModule` plus the probe capabilities; nine modules wrapping today's paths and catalog
   rows. Detection loops modules and its result becomes vendor-keyed, which deletes
   `KnownHarness.Select`; `HarnessPaths` and `KnownHarness` retreat to registry internals.
2. `IWirable` — the status Hooks line and the nudge go through it, and `HarnessWiring`'s hop through
   the catalog goes away.
3. `IImportable` — replaces `SetupCommand.BuildImportSources`, which is the last site still
   resolving eight vendors itself.
4. `ISkillsHost` with `ReadsSharedTree`, deleting `SkillsCommand.ConsumerPresent`'s switch.
5. `PluginCommand`'s nine near-parallel install branches behind a wire/unwire capability. The
   largest payoff and the largest risk; it wants its own design pass.

## Constraints

- **Detection stays separable from wiring.** Wiring reads a file per vendor while detection stats a
  directory, and `HarnessNudgeEmitter` runs on the latency-budgeted SessionStart hook path, so
  folding `IsWired` into the detection pass would buy nine file reads per hook.
- **Modules are per-process values built at the entry point** from `UserHome`, exactly as the bundle
  is, so the test fixture becomes a set of modules over a temp home rather than a paths bundle.
- **Expect two or three vendors to keep an escape hatch.** Antigravity is one vendor over two
  product roots, and Codex's sandbox allowlist has no counterpart elsewhere. A capability interface
  they cannot implement is a signal to add a capability, not to widen an existing one.
