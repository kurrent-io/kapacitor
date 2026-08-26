using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(ServicePlatform platform, ConfigRoot config) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(),
        ServicePlatform.Systemd              => new SystemdServiceManager(),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(config),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    public static IServiceManager ForCurrentOs(ConfigRoot config) {
        if (OperatingSystem.IsMacOS())   return ForPlatform(ServicePlatform.Launchd, config);
        if (OperatingSystem.IsLinux())   return ForPlatform(ServicePlatform.Systemd, config);
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask, config);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }
}
