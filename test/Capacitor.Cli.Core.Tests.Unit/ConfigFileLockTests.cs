using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Exercises the REAL kernel objects ConfigFileLock creates, on every platform the suite
/// runs on. The Windows branch of ConfigFileLock is the only caller of MutexAcl/MutexSecurity in
/// the product, and nothing else in the suite reaches it: the types resolve from the framework
/// reference assemblies, so a build compiles whether or not they are actually loadable at run time
/// on Windows. Only running this code on the Windows CI leg proves it.</summary>
public class ConfigFileLockTests {
    /// A Mutex is thread-affine: WaitOne and ReleaseMutex must run on the same thread, and an await
    /// between them can resume on a different pool thread (ReleaseMutex then throws). Production has
    /// the same constraint — see ConfigMutator. So every lock body here runs on its own dedicated
    /// thread, and the assertions happen once it has finished.
    ///
    /// <para>Awaited rather than joined: Join would park a POOL thread for the whole lock body, and
    /// the pool grows about one thread a second — under the full suite's parallelism that starves
    /// the timing-sensitive tests running alongside. Only the dedicated thread ever blocks.</para>
    static Task<Exception?> OnItsOwnThreadAsync(Action body) {
        var done   = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { body(); done.SetResult(null); } catch (Exception ex) { done.SetResult(ex); }
        });
        thread.Start();
        return done.Task;
    }

    /// Blocking variant, for use only from INSIDE a body already running on a dedicated thread —
    /// there the Join costs nothing the pool needs back.
    static Exception? OnItsOwnThread(Action body) {
        Exception? failure = null;
        var thread = new Thread(() => {
            try { body(); } catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        thread.Join();
        return failure;
    }

    [Test]
    public async Task The_lock_is_acquirable_released_and_reacquirable() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);

        // On Windows this is the MutexAcl.Create path: a DACL'd Global\ mutex. A missing or
        // unloadable System.Threading.AccessControl surfaces here and nowhere earlier.
        var failure = await OnItsOwnThreadAsync(() => {
            using (ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5))) { }
            using (ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5))) { }
        });

        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task A_second_acquirer_of_the_same_path_times_out_while_the_lock_is_held() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);
        Exception? contender = null;

        var failure = await OnItsOwnThreadAsync(() => {
            using var _ = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));
            // A second Acquire on the HOLDING thread would just re-enter the mutex, so the
            // contender has to be a different thread to prove anything.
            contender = OnItsOwnThread(() => {
                using var __ = ConfigFileLock.Acquire(path, TimeSpan.FromMilliseconds(250));
            });
        });

        await Assert.That(failure).IsNull();
        await Assert.That(contender).IsTypeOf<TimeoutException>();
    }

    [Test]
    public async Task Distinct_config_paths_do_not_share_a_lock() {
        using var tmp = new TempDir();
        var held  = tmp.PathTo("held");
        var other = tmp.PathTo("other");
        Exception? contender = null;

        var failure = await OnItsOwnThreadAsync(() => {
            using var _ = ConfigFileLock.Acquire(held, TimeSpan.FromSeconds(5));
            contender = OnItsOwnThread(() => {
                using var __ = ConfigFileLock.Acquire(other, TimeSpan.FromMilliseconds(250));
            });
        });

        await Assert.That(failure).IsNull();
        await Assert.That(contender).IsNull();
    }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task On_Windows_the_mutex_is_Global_and_its_DACL_grants_the_current_user_only() {
        await AssertGlobalMutexDaclAsync();
    }

    /// Attributed rather than guarded inline: CA1416's flow analysis does not follow an
    /// OperatingSystem.IsWindows() guard across the lambda boundary into the thread body below.
    [SupportedOSPlatform("windows")]
    static async Task AssertGlobalMutexDaclAsync() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);
        // Deliberately recomputed rather than shared with ConfigFileLock: the name IS the
        // cross-process, cross-VERSION contract (the class doc records a past rename that silently
        // lost mutual exclusion), and a shared helper would make this test agree with whatever the
        // product does. Opening this exact Global\ name from outside the lock is itself the proof
        // that the mutex is cross-session rather than Local\.
        var name = @"Global\kcap-cfg-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).ToLowerInvariant();

        MutexAccessRule[] rules = [];
        var failure = await OnItsOwnThreadAsync(() => {
            using var _ = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));

            using var opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions);
            rules = opened.GetAccessControl()
                          .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                          .Cast<MutexAccessRule>()
                          .ToArray();
        });

        await Assert.That(failure).IsNull();
        // Exactly one rule: anything else means another identity was granted access to a lock whose
        // whole point is that a different local user cannot squat or hold it.
        await Assert.That(rules.Length).IsEqualTo(1);
        await Assert.That(rules[0].IdentityReference).IsEqualTo(WindowsIdentity.GetCurrent().User!);
        await Assert.That(rules[0].MutexRights).IsEqualTo(MutexRights.FullControl);
        await Assert.That(rules[0].AccessControlType).IsEqualTo(AccessControlType.Allow);
    }
}
