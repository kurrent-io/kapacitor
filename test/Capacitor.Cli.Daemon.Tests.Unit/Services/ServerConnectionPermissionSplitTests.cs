using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;
using static Capacitor.Cli.Daemon.Services.ServerConnection;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

internal sealed class ServerConnectionPermissionSplitTests {
    [Test]
    public async Task Abandonment_is_neither_cancellation_nor_invalid_operation() {
        Exception ex = new PermissionRequestAbandonedException();
        await Assert.That(ex is OperationCanceledException).IsFalse();
        await Assert.That(ex is InvalidOperationException).IsFalse();
    }

    [Test]
    [Arguments("Permission request is no longer pending.", RespondOutcomeKind.NotPending)]
    [Arguments("Caller is not the daemon owning session", RespondOutcomeKind.Failed)]
    public async Task Respond_classifies_hub_exceptions_by_message(string message, RespondOutcomeKind expected) {
        await Assert.That(ServerConnection.ClassifyRespondFailure(new HubException(message)).Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task Respond_classifies_a_dropped_connection_as_failed() {
        var outcome = ServerConnection.ClassifyRespondFailure(new InvalidOperationException("The 'InvokeCoreAsync' method cannot be called if the connection is not active"));
        await Assert.That(outcome.Kind).IsEqualTo(RespondOutcomeKind.Failed);
        await Assert.That(outcome.Reason).IsNotNull();
    }
}
