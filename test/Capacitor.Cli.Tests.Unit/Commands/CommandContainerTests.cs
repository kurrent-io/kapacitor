using Capacitor.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Every command the dispatch switch can ask for is constructible from the container it is asked
/// from. Nothing else proves this: a command is resolved on the one dispatch that runs it, so a
/// dependency nobody registered surfaces as an unhandled resolve failure on a user's first run of
/// that verb, in a build whose suites were green.
/// </summary>
public class CommandContainerTests {
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    ServiceProvider Build(string? baseUrl) =>
        new ServiceCollection()
            .AddCapacitorCli(
                Config.Root, Home, Daemons.Store,
                baseUrl is null ? Resolutions.None(Config.Root) : Resolutions.At(baseUrl, Config.Root),
                new HookClock(TimeProvider.System), baseUrl)
            // What Program.cs builds with, so a registration this rejects is one a run would too.
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    /// <summary>The URL is the one thing a run may not have, and it reaches the container as a null.</summary>
    [Test]
    [Arguments("http://server.example")]
    [Arguments(null)]
    public async Task Every_registered_command_resolves(string? baseUrl) {
        var commands = new ServiceCollection().AddCapacitorCommands()
            .Select(descriptor => descriptor.ServiceType)
            .ToArray();

        // Guards the loop below: an empty set would assert nothing at all.
        await Assert.That(commands).IsNotEmpty();

        using var sp = Build(baseUrl);

        foreach (var command in commands) {
            // GetRequiredService, not GetService: a missing registration must fail here rather than
            // hand back a null the assertion would have to interpret.
            await Assert.That(sp.GetRequiredService(command)).IsNotNull()
                .Because($"{command.Name} is dispatchable, so it must be constructible");
        }
    }

    /// <summary>
    /// The registration list is not its own witness: enumerating it to check itself would pass on a
    /// command dropped from it. The assembly is the independent source — a command type it defines
    /// that nothing registers is a resolve failure only the run that dispatches it discovers.
    /// </summary>
    [Test]
    public async Task Every_command_type_the_assembly_defines_is_registered() {
        var registered = new ServiceCollection().AddCapacitorCommands()
            .Select(descriptor => descriptor.ServiceType)
            .ToHashSet();

        var defined = typeof(CommandServices).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsNested: false })
            .Where(type => type.Namespace?.StartsWith("Capacitor.Cli.Commands", StringComparison.Ordinal) == true)
            .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal)
                        || type.Name.EndsWith("Commands", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(defined).IsNotEmpty();

        // Built by their callers rather than the dispatch switch, so a registration would be dead:
        // ClaudeHookCommand constructs a permission request from its own dependencies, and
        // DaemonServiceCommands takes a daemon name argv supplies, which no container can.
        Type[] builtByHand = [typeof(PermissionRequestCommand), typeof(DaemonServiceCommands)];

        var missing = defined.Except(builtByHand).Where(type => !registered.Contains(type))
            .Select(type => type.Name)
            .Order()
            .ToArray();

        await Assert.That(missing).IsEmpty().Because($"unregistered: {string.Join(", ", missing)}");
    }
}
