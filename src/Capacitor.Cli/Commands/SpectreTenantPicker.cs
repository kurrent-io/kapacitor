using Capacitor.Cli.Core.Auth;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

public class SpectreTenantPicker : ITenantPicker {
    public DiscoveredTenant Pick(DiscoveredTenant[] tenants) {
        var prompt = new SelectionPrompt<DiscoveredTenant>()
            .Title("Which Capacitor tenant would you like to use as default?")
            .UseConverter(t => $"{t.Label} · {t.Origin}")
            .AddChoices(tenants);

        return AnsiConsole.Prompt(prompt);
    }

    // Spectre prompts are not cancellable and the CLI never cancels this path.
    public Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct) =>
        Task.FromResult<DiscoveredTenant?>(Pick(tenants));
}
