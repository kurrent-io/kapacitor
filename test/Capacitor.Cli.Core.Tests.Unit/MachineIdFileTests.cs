namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="MachineId"/> — the stable per-machine id the daemon reports at
/// registration (distinct from <see cref="MachineIdProvider"/>'s memory-tagging id; see
/// MachineIdTests.cs for that one). Each test gets its own root, so a machine.json written here is
/// nobody else's.
/// </summary>
public class MachineIdFileTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Lazy: injection happens after construction, so Config is not readable from an initializer.
    MachineId Machine => field ??= new MachineId(Config.Root);

    string MachinePath => Config.PathTo("machine.json");

    [Test]
    public async Task Get_ReturnsNonEmptyId() {
        var id = Machine.Get();

        await Assert.That(id).IsNotNull();
        await Assert.That(id).IsNotEmpty();
    }

    [Test]
    public async Task Get_IsStableAcrossCalls() {
        var first  = Machine.Get();
        var second = Machine.Get();

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Get_PersistsSoAFreshReadReturnsTheSameId() {
        var id = Machine.Get();

        // Simulates a new process: reads machine.json straight off disk rather than relying on
        // whatever Get() might keep in memory.
        var persisted = Machine.ReadPersisted();

        await Assert.That(persisted).IsEqualTo(id);
    }

    [Test]
    public async Task Get_WhenMachineJsonAlreadyExists_ReturnsThePersistedValueRatherThanRegenerating() {
        // Simulate a peer process having already won the first-write race before we ever call Get().
        var seeded = Machine.Get();
        var before = File.ReadAllText(MachinePath);

        var again = Machine.Get();

        await Assert.That(again).IsEqualTo(seeded);
        await Assert.That(File.ReadAllText(MachinePath)).IsEqualTo(before);
    }

    [Test]
    public async Task Get_WhenMachineJsonIsCorrupt_HealsTheFileAndReturnsAStableId() {
        // A corrupt machine.json (partial/garbled write) makes ReadPersisted() return null, so Get()
        // falls to Create(). Without the heal, Create()'s exclusive FileMode.CreateNew can't overwrite
        // the existing corrupt file, so every Get() churns a fresh, UNPERSISTED GUID (Qodo #290 #2).
        File.WriteAllText(MachinePath, "{ this is not valid json");

        var first = Machine.Get();
        await Assert.That(first).IsNotNull();
        await Assert.That(first).IsNotEmpty();

        // Stable across calls — the corrupt file was healed, not re-generated each time.
        var second = Machine.Get();
        await Assert.That(second).IsEqualTo(first);

        // The heal actually persisted: a fresh read off disk returns the same id.
        await Assert.That(Machine.ReadPersisted()).IsEqualTo(first);
    }
}
