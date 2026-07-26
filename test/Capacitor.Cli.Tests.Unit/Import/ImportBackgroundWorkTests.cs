using System.Collections.Concurrent;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Import;

/// <summary>Pins the background-await decision so a re-inversion can't skip Task.WhenAll(backgroundTasks).</summary>
public class ImportBackgroundWorkTests {
    [Test]
    public async Task HadBackgroundWork_is_false_for_an_empty_bag() {
        await Assert.That(ImportCommand.HadBackgroundWork(new ConcurrentBag<Task>())).IsFalse();
    }

    [Test]
    public async Task HadBackgroundWork_is_true_when_tasks_were_scheduled() {
        var bag = new ConcurrentBag<Task> { Task.CompletedTask };

        await Assert.That(ImportCommand.HadBackgroundWork(bag)).IsTrue();
    }
}
