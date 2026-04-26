using kapacitor.Auth;
using Spectre.Console;

namespace kapacitor.Commands;

public class SpectreTenantPicker : ITenantPicker {
    public DiscoveredTenant Pick(DiscoveredTenant[] tenants) {
        var prompt = new SelectionPrompt<DiscoveredTenant>()
            .Title("Which Capacitor tenant would you like to use as default?")
            .UseConverter(t => $"{t.OrgLogin} · {t.Origin}")
            .AddChoices(tenants);

        return AnsiConsole.Prompt(prompt);
    }
}
