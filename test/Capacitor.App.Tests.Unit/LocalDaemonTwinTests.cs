using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class LocalDaemonTwinTests {
    static DaemonInfo D(string name, string? machineId, string owner = "u1") =>
        new() { Name = name, MachineId = machineId, OwnerUserId = owner, Connected = true };

    const string Server = "https://cap.example.com";

    [Test]
    public async Task ExactlyOneMatchWins() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m2", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsEqualTo(("u1", "work-mac"));
    }

    [Test]
    public async Task TwoCandidatesFailOpen() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1"), D("work-mac", "m1", "u2")], "m1", "work-mac", Server, Server);
        await Assert.That(twin).IsNull();
    }

    [Test]
    public async Task MissingMachineIdFailsOpen() {
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", null)], "m1", "work-mac", Server, Server)).IsNull();
        await Assert.That(LocalDaemonTwin.Find([D("work-mac", "m1")], null, "work-mac", Server, Server)).IsNull();
    }

    [Test]
    public async Task DifferentServerNeverMatches() {
        var twin = LocalDaemonTwin.Find([D("work-mac", "m1")], "m1", "work-mac", "https://other.example.com", Server);
        await Assert.That(twin).IsNull();
    }
}
