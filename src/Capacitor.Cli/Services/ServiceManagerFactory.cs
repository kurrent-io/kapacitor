using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(ServicePlatform platform, ConfigRoot config, UserHome home) => platform switch {
        ServicePlatform.Launchd              => new LaunchdServiceManager(home),
        ServicePlatform.Systemd              => new SystemdServiceManager(home),
        ServicePlatform.WindowsScheduledTask => new WindowsScheduledTaskServiceManager(config),
        _ => throw new PlatformNotSupportedException($"No service manager for {platform}"),
    };

    public static IServiceManager ForCurrentOs(ConfigRoot config, UserHome home) {
        if (OperatingSystem.IsMacOS())   return ForPlatform(ServicePlatform.Launchd, config, home);
        if (OperatingSystem.IsLinux())   return ForPlatform(ServicePlatform.Systemd, config, home);
        if (OperatingSystem.IsWindows()) return ForPlatform(ServicePlatform.WindowsScheduledTask, config, home);
        throw new PlatformNotSupportedException("kcap daemon service is not supported on this OS.");
    }
}
