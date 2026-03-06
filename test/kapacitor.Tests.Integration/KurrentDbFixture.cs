using Testcontainers.KurrentDb;
using TUnit.Core.Interfaces;

namespace kapacitor.Tests.Integration;

public class KurrentDbFixture : IAsyncInitializer, IAsyncDisposable {
    KurrentDbContainer _container = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync() {
        _container = new KurrentDbBuilder("docker.kurrent.io/kurrent-latest/kurrentdb:latest")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() {
        await _container.DisposeAsync();
    }
}
