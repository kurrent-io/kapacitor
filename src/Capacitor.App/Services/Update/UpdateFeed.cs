namespace Capacitor.App.Services.Update;

public static class UpdateFeed {
    public const string BaseUrl = "https://www.kurrent.io/download/desktop/osx-arm64/";
    public const string OverrideVariable = "KCAP_APP_UPDATE_URL";

    public static string Resolve(Func<string, string?> getEnv) {
        var overrideUrl = getEnv(OverrideVariable);
        return string.IsNullOrWhiteSpace(overrideUrl) ? BaseUrl : overrideUrl.Trim();
    }
}
