using TUnit.Core.Enums;

// Not Windows: an https probe against the plain-HTTP WireMock server (localhost/::1 resolution +
// TLS-to-HTTP) is environment-flaky there. The cross-platform correctness that matters on Windows —
// path handling and the watcher handle test — lives in the unit suites.
[assembly: RunOn(OS.Linux | OS.MacOs)]

// Every test here spawns the real binary and talks loopback HTTP; at TUnit's default width they
// starve each other into timeout failures.
[assembly: ParallelLimiter<SubprocessLimit>]
