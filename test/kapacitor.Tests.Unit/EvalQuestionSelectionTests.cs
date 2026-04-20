using kapacitor;
using kapacitor.Eval;

namespace kapacitor.Tests.Unit;

public class EvalQuestionSelectionTests {
    static readonly IReadOnlyList<EvalQuestionDto> Catalog = [
        new() { Category = "safety",     Id = "sensitive_files",         Text = "t", Prompt = "p" },
        new() { Category = "safety",     Id = "destructive_commands",    Text = "t", Prompt = "p" },
        new() { Category = "safety",     Id = "security_vulnerabilities",Text = "t", Prompt = "p" },
        new() { Category = "safety",     Id = "permission_bypass",       Text = "t", Prompt = "p" },
        new() { Category = "quality",    Id = "tests_written",           Text = "t", Prompt = "p" },
        new() { Category = "efficiency", Id = "redundant_calls",         Text = "t", Prompt = "p" }
    ];

    [Test]
    public async Task Null_include_null_skip_returns_full_catalog() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog, include: null, skip: null);
        await Assert.That(error).IsNull();
        await Assert.That(resolved!.Count).IsEqualTo(Catalog.Count);
    }

    [Test]
    public async Task Include_category_expands() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog, include: ["safety"], skip: null);
        await Assert.That(error).IsNull();
        await Assert.That(resolved!.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Include_mixed_category_and_id_deduplicates() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog,
            include: ["safety", "tests_written"], skip: null);
        await Assert.That(error).IsNull();
        await Assert.That(resolved!.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Skip_category_removes_from_full_catalog() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog, include: null, skip: ["safety"]);
        await Assert.That(error).IsNull();
        await Assert.That(resolved!.All(q => q.Category != "safety")).IsTrue();
        await Assert.That(resolved!.Count).IsEqualTo(Catalog.Count - 4);
    }

    [Test]
    public async Task Include_and_skip_both_set_is_mutually_exclusive() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog,
            include: ["safety"], skip: ["tests_written"]);
        await Assert.That(resolved).IsNull();
        await Assert.That(error!).Contains("mutually exclusive");
    }

    [Test]
    public async Task Unknown_token_returns_error() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog,
            include: ["notarealthing"], skip: null);
        await Assert.That(resolved).IsNull();
        await Assert.That(error!).Contains("notarealthing");
    }

    // Qodo PR #23 finding #1 — an explicitly-empty --questions must not
    // silently fall back to the full catalog; Resolve sees an empty token
    // list and returns an empty selection, which HandleEval treats as a
    // zero-question user error.
    [Test]
    public async Task Empty_include_returns_empty_selection_not_full_catalog() {
        var (resolved, error) = EvalQuestionSelection.Resolve(Catalog, include: [], skip: null);
        await Assert.That(error).IsNull();
        await Assert.That(resolved!.Count).IsEqualTo(0);
    }

    // Qodo PR #23 finding #3 — a misbehaving server returning a catalog
    // with duplicate IDs must produce a controlled error, not an
    // uncaught ToDictionary ArgumentException.
    [Test]
    public async Task Duplicate_catalog_id_returns_error_instead_of_throwing() {
        IReadOnlyList<EvalQuestionDto> dupCatalog = [
            new() { Category = "safety", Id = "same_id", Text = "t", Prompt = "p" },
            new() { Category = "safety", Id = "same_id", Text = "t", Prompt = "p" }
        ];

        var (resolved, error) = EvalQuestionSelection.Resolve(dupCatalog, include: ["safety"], skip: null);

        await Assert.That(resolved).IsNull();
        await Assert.That(error!).Contains("duplicate");
        await Assert.That(error!).Contains("same_id");
    }
}
