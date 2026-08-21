using Capacitor.Cli.Core.Auth;

namespace Capacitor.Tests.Helpers;

/// <summary>Records every call instead of writing to Console — the test seam for asserting call shape.</summary>
public sealed class RecordingAuthProgress : IAuthProgress {
    public List<string>                                    Notices         { get; } = [];
    public List<string>                                    Errors          { get; } = [];
    public List<string>                                    BrowserOpenings { get; } = [];
    public List<(string Code, string Uri, bool Prefilled)> DeviceCodes     { get; } = [];
    public int                                             PollTicks       { get; private set; }

    public void Notice(string message) => Notices.Add(message);
    public void Error(string message) => Errors.Add(message);
    public void BrowserOpening(string url) => BrowserOpenings.Add(url);
    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) =>
        DeviceCodes.Add((code, verificationUri, prefilled));
    public void PollTick() => PollTicks++;
}
