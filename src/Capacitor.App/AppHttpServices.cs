using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App;

public static class AppHttpServices {
    /// <summary>
    /// Only the foreign-host clients: a workspace is signed up for before there is a server to
    /// authenticate against, so the lanes that need a resolved one cannot be registered here.
    ///
    /// <para>The token store rides along for its pooled clients. Its refresh against our own server
    /// names a lane only <see cref="CapacitorHttpServices.AddCapacitorHttp"/> registers, and an
    /// unregistered name yields a plain pooled client rather than an error — which is what this
    /// process wants, since that lane's handlers need a server it does not have.</para>
    /// </summary>
    public static IServiceCollection AddAppForeignHttp(this IServiceCollection services, ConfigRoot config) {
        services.AddSingleton(config);
        services.AddCapacitorForeignClients();
        services.AddSingleton<TokenStore>();

        return services;
    }
}
