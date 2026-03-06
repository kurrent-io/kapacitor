using KurrentDB.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace kapacitor.Tests.Integration;

public class CapacitorFactory : WebApplicationFactory<Kurrent.Capacitor.Program> {
    readonly string _kurrentConnectionString;

    public CapacitorFactory(string kurrentConnectionString) {
        _kurrentConnectionString = kurrentConnectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) {
        builder.UseEnvironment("Development");

        // Program.cs creates KurrentDBClient inline before ConfigureWebHost runs,
        // so ConfigureAppConfiguration can't override it. Replace the singleton directly.
        builder.ConfigureServices(services => {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(KurrentDBClient));
            if (descriptor is not null) services.Remove(descriptor);
            services.AddSingleton(new KurrentDBClient(KurrentDBClientSettings.Create(_kurrentConnectionString)));
        });
    }
}
