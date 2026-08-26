namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin for <c>KCAP_URL</c> and <c>KCAP_PROFILE</c>. Both are read by profile
/// resolution (<c>AppConfig</c>) and by the app's startup path, but production never sets either
/// into its own process — so the only way a value reaches a test is the developer's own shell or
/// <c>.envrc</c>, where it would silently redirect resolution at a server or profile the test never
/// named.
/// </summary>
public class ProfileOverridesGlobalSetup {
    static string? _savedKcapUrl;
    static string? _savedKcapProfile;

    [BeforeEvery(Assembly)]
    public static void PinProfileOverrides() {
        _savedKcapUrl     = Environment.GetEnvironmentVariable("KCAP_URL");
        _savedKcapProfile = Environment.GetEnvironmentVariable("KCAP_PROFILE");
        Environment.SetEnvironmentVariable("KCAP_URL", null);
        Environment.SetEnvironmentVariable("KCAP_PROFILE", null);
    }

    [AfterEvery(Assembly)]
    public static void RestoreProfileOverrides() {
        Environment.SetEnvironmentVariable("KCAP_URL", _savedKcapUrl);
        Environment.SetEnvironmentVariable("KCAP_PROFILE", _savedKcapProfile);
    }
}
