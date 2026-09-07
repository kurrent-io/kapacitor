using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Core;

public static class CapacitorContainer {
    /// <summary>
    /// Builds a container, and in debug builds first checks that every descriptor it holds can
    /// actually be constructed. A dependency nobody registered is otherwise found by the one code
    /// path that resolves it — a command's dispatch, a pane's first open — so it survives a green
    /// suite and fails on a user's run instead. Release skips the walk, which every start pays for.
    /// </summary>
    public static ServiceProvider BuildValidated(this IServiceCollection services) =>
#if DEBUG
        services.BuildServiceProvider(new ServiceProviderOptions {
            ValidateOnBuild = true,
            ValidateScopes  = true,
        });
#else
        services.BuildServiceProvider();
#endif
}
