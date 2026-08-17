using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class RemapCommandTests {
    static CwdRemap R(string from, string to) => new() { From = from, To = to };

    [Test]
    public async Task ApplyAdd_appends_new_entry() {
        var (next, replaced) = RemapCommand.ApplyAdd([], "/old", "/new");

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo("/old");
        await Assert.That(next[0].To).IsEqualTo("/new");
    }

    [Test]
    public async Task ApplyAdd_to_null_current_treats_as_empty() {
        var (next, replaced) = RemapCommand.ApplyAdd(null, "/old", "/new");

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyAdd_replaces_when_from_already_exists() {
        var current          = new[] { R("/old", "/v1"), R("/other", "/keep") };
        var (next, replaced) = RemapCommand.ApplyAdd(current, "/old", "/v2");

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(2);
        await Assert.That(next.Single(r => r.From == "/old").To).IsEqualTo("/v2");
        await Assert.That(next.Single(r => r.From == "/other").To).IsEqualTo("/keep");
    }

    [Test]
    public async Task ApplyAdd_treats_trailing_slash_as_same_entry() {
        // Normalize trims trailing separators, so "/old" and "/old/" collide.
        var current          = new[] { R("/old", "/v1") };
        var (next, replaced) = RemapCommand.ApplyAdd(current, "/old/", "/v2");

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(1);
        // Note: ApplyAdd does NOT normalize the args itself (HandleAsync does
        // that before calling). This test exercises the SameFrom comparator.
    }

    [Test]
    public async Task ApplyRemove_drops_matching_entry() {
        var current = new[] { R("/a", "/x"), R("/b", "/y") };
        var next    = RemapCommand.ApplyRemove(current, "/a");

        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo("/b");
    }

    [Test]
    public async Task ApplyRemove_returns_same_count_when_not_found() {
        var current = new[] { R("/a", "/x") };
        var next    = RemapCommand.ApplyRemove(current, "/missing");

        await Assert.That(next).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ApplyRemove_on_null_current_returns_empty() {
        var next = RemapCommand.ApplyRemove(null, "/a");
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_treats_trailing_slash_as_same_entry() {
        var current = new[] { R("/a", "/x") };
        var next    = RemapCommand.ApplyRemove(current, "/a/");
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task Normalize_trims_trailing_separators() {
        await Assert.That(RemapCommand.Normalize("/a/b/")).IsEqualTo("/a/b");
        await Assert.That(RemapCommand.Normalize(@"C:\a\b\")).IsEqualTo(@"C:\a\b");
    }

    [Test]
    public async Task Normalize_preserves_root_separator() {
        // "/" and "\" alone must not collapse to empty.
        await Assert.That(RemapCommand.Normalize("/")).IsEqualTo("/");
        await Assert.That(RemapCommand.Normalize(@"\")).IsEqualTo(@"\");
    }

    [Test]
    public async Task Normalize_returns_empty_for_whitespace() {
        await Assert.That(RemapCommand.Normalize("   ")).IsEqualTo("");
        await Assert.That(RemapCommand.Normalize(null)).IsEqualTo("");
    }

    [Test]
    public async Task Normalize_trims_whitespace_around_path() {
        await Assert.That(RemapCommand.Normalize("  /a/b  ")).IsEqualTo("/a/b");
    }

    [Test]
    public async Task ApplyAdd_with_OrdinalIgnoreCase_replaces_case_variant_entry() {
        // On Windows, "C:\Users\Alice" and "c:\users\alice" refer to the same
        // dir at import time, so kcap remap should replace — not duplicate —
        // when the user re-adds with different casing.
        var current = new[] { R(@"C:\Users\Alice\Dev", @"C:\Users\Alice\Old") };
        var (next, replaced) = RemapCommand.ApplyAdd(
            current, @"c:\users\alice\dev", @"C:\Users\Alice\New", StringComparison.OrdinalIgnoreCase);

        await Assert.That(replaced).IsTrue();
        await Assert.That(next).Count().IsEqualTo(1);
        await Assert.That(next[0].From).IsEqualTo(@"c:\users\alice\dev"); // new casing wins
        await Assert.That(next[0].To).IsEqualTo(@"C:\Users\Alice\New");
    }

    [Test]
    public async Task ApplyAdd_with_Ordinal_keeps_case_variant_as_separate_entry() {
        // On non-Windows hosts, case differences must stay distinct — two
        // separate paths.
        var current = new[] { R("/dev/Foo", "/dev/v1") };
        var (next, replaced) = RemapCommand.ApplyAdd(
            current, "/dev/foo", "/dev/v2", StringComparison.Ordinal);

        await Assert.That(replaced).IsFalse();
        await Assert.That(next).Count().IsEqualTo(2);
    }

    [Test]
    public async Task ApplyRemove_with_OrdinalIgnoreCase_removes_case_variant_entry() {
        var current = new[] { R(@"C:\Users\Alice\Dev", @"C:\Users\Alice\New") };
        var next    = RemapCommand.ApplyRemove(current, @"c:\users\alice\dev", StringComparison.OrdinalIgnoreCase);
        await Assert.That(next).IsEmpty();
    }

    [Test]
    public async Task ApplyRemove_with_Ordinal_keeps_case_variant_entry() {
        var current = new[] { R("/dev/Foo", "/dev/x") };
        var next    = RemapCommand.ApplyRemove(current, "/dev/foo", StringComparison.Ordinal);
        await Assert.That(next).Count().IsEqualTo(1);
    }
}
