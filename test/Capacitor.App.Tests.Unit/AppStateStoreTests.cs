using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class AppStateStoreTests {
    [Test]
    public async Task Missing_file_yields_defaults() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var state = await store.LoadAsync();
        await Assert.That(state).IsEqualTo(new AppState());
    }

    [Test]
    public async Task Corrupt_file_yields_defaults_without_throwing() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        File.WriteAllText(path, "{not json");
        var store = new AppStateStore(path);
        var state = await store.LoadAsync();
        await Assert.That(state).IsEqualTo(new AppState());
    }

    [Test]
    public async Task Update_then_fresh_store_load_sees_it() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var ok = await store.UpdateAsync(s => s with { ShimOffered = true, ShimDenied = true });
        await Assert.That(ok).IsTrue();

        var reloaded = new AppStateStore(path);
        var state = await reloaded.LoadAsync();
        await Assert.That(state.ShimOffered).IsTrue();
        await Assert.That(state.ShimDenied).IsTrue();
    }

    [Test]
    public async Task Update_creates_missing_parent_directory() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("nested", "sub", "app-state.json");
        var store = new AppStateStore(path);

        var ok = await store.UpdateAsync(s => s with { ShimOffered = true });

        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task No_tmp_file_left_behind_after_successful_write() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        await store.UpdateAsync(s => s with { ShimOffered = true });

        var leftovers = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp");
        await Assert.That(leftovers).IsEmpty();
    }

    [Test]
    public async Task Fifty_parallel_updates_all_land() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        var tasks = Enumerable.Range(0, 50).Select(i =>
            store.UpdateAsync(s => s with {
                DeclinedTakeoverPairs = [.. s.DeclinedTakeoverPairs ?? [], $"{i}.0.0|{i}.0.1"]
            }));
        var results = await Task.WhenAll(tasks);

        await Assert.That(results.All(r => r)).IsTrue();

        var final = await store.LoadAsync();
        await Assert.That(final.DeclinedTakeoverPairs!.Count).IsEqualTo(50);
        await Assert.That(final.DeclinedTakeoverPairs!.Distinct().Count()).IsEqualTo(50);
    }

    [Test]
    public async Task Write_failure_when_parent_is_a_regular_file_returns_false_without_throwing() {
        using var tmp = new TempDir();
        var blockingFile = tmp.PathTo("blocked");
        File.WriteAllText(blockingFile, "not a directory");
        var path = Path.Combine(blockingFile, "app-state.json"); // parent path component is a file

        var store = new AppStateStore(path);
        var ok = await store.UpdateAsync(s => s with { ShimOffered = true });

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Harness_choice_is_remembered_per_repo() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await store.UpdateAsync(s => s with {
            HarnessByRepo = new Dictionary<string, string> {
                ["/home/a/kcap-cli"] = "codex",
                ["/home/a/kcap-web"] = "kiro",
            }
        });

        var reloaded = await new AppStateStore(path).LoadAsync();

        await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-cli"]).IsEqualTo("codex");
        await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-web"]).IsEqualTo("kiro");
    }

    [Test]
    public async Task Missing_harness_map_is_null_not_empty() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var state = await new AppStateStore(path).LoadAsync();
        await Assert.That(state.HarnessByRepo).IsNull();
    }
}
