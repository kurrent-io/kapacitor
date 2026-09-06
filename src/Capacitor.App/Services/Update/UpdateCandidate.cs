namespace Capacitor.App.Services.Update;

/// One release the feed offers, identified by version; the Velopack asset behind it stays inside
/// the adapter so the coordinator and its tests never see Velopack types.
public sealed record UpdateCandidate(string Version, bool IsPrerelease);
