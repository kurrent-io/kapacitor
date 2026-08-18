using TUnit.Core.Enums;

// Linux only: the integration suite does real loopback HTTP (WireMock). On the Windows runner an
// https probe against the plain-HTTP test server (localhost/::1 resolution + TLS-to-HTTP) is
// environment-flaky. The cross-platform correctness that matters on Windows — path handling and the
// watcher handle test — lives in the unit suites.
[assembly: RunOn(OS.Linux)]
