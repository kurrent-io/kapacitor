using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

public static class MachineIdProvider {
    public static string Generate() => "mach-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Returns the persisted machine id, generating + saving one on first use.</summary>
    public static async Task<string> GetOrCreateAsync(CancellationToken ct = default) {
        var config = await AppConfig.LoadProfileConfig(ct);
        if (!string.IsNullOrWhiteSpace(config.MachineId)) return config.MachineId;

        var id = Generate();
        await AppConfig.SaveProfileConfig(config with { MachineId = id }, ct);
        return id;
    }
}
