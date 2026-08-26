using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

public static class MachineIdProvider {
    public static string Generate() => "mach-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Returns the persisted machine id, generating + saving one on first use.</summary>
    public static async Task<string> GetOrCreateAsync(ConfigRoot config, CancellationToken ct = default) {
        var stored = await AppConfig.LoadProfileConfig(config, ct);
        if (!string.IsNullOrWhiteSpace(stored.MachineId)) return stored.MachineId;

        var newId  = Generate();
        var result = await ConfigMutator.MutateAsync(config,
            c => string.IsNullOrWhiteSpace(c.MachineId) ? c with { MachineId = newId } : c, ct);
        return result.MachineId!;
    }
}
