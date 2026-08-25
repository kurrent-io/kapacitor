using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <param name="isInteractive">Test seam; the ambient value belongs to whatever host the suite runs under.</param>
public class SpectreTenantPicker(Func<bool>? isInteractive = null) : ITenantPicker {
    public DiscoveredTenant Pick(DiscoveredTenant[] tenants) {
        PromptHygiene.DiscardTypeAhead();

        var prompt = new SelectionPrompt<DiscoveredTenant>()
            .Title("Which Capacitor tenant would you like to use as default?")
            .UseConverter(t => $"{t.Label} · {t.Origin}")
            .AddChoices(tenants);

        return AnsiConsole.Prompt(prompt);
    }

    // Spectre prompts are not cancellable and the CLI never cancels this path.
    public Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct) {
        // Spectre throws from inside a prompt rather than returning, so a session with no terminal
        // has to be turned away before one opens. Naming each tenant is the whole point: the reader
        // is a log, and the way out is to pass the one they wanted.
        if (!(isInteractive ?? (() => AnsiConsole.Profile.Capabilities.Interactive))()) {
            // Console rather than AnsiConsole: Spectre hard-wraps, breaking the commands below across
            // a line and handing the reader something that will not copy.
            Console.Error.WriteLine();
            Console.Error.WriteLine("Choosing between workspaces needs an interactive terminal, and this session is non-interactive.");
            Console.Error.WriteLine("Name the one you want instead:");

            // Canonicalized, not printed raw: these origins are proxy-supplied, and a control character
            // in one would forge lines in the log this is written for.
            foreach (var tenant in tenants)
                Console.Error.WriteLine($"  • kcap setup --server-url {ServerIdentity.Canonicalize(tenant.Origin) ?? tenant.Slug} --no-prompt");

            return Task.FromResult<DiscoveredTenant?>(null);
        }

        return Task.FromResult<DiscoveredTenant?>(Pick(tenants));
    }
}
