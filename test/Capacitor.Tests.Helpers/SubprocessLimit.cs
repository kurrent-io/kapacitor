using TUnit.Core.Interfaces;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// One pool for the classes whose time goes into real child processes — git, a vendor CLI, a PTY.
/// TUnit keys the semaphore by this type, per test process. Its default width (4x the cores) is
/// sized for IO-bound tests and starves these classes' timing assertions; half the cores is the
/// measured knee — one core for each member and one for its child.
/// </summary>
public sealed class SubprocessLimit : IParallelLimit {
    public int Limit { get; } = Math.Max(2, Environment.ProcessorCount / 2);
}
