using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// ChatTabViewModel's tail: phases, the poll, item projection and pairing, path switches and
/// teardown. Every test runs under RunOnUiAsync (the apply hops through Dispatcher.UIThread) and
/// carries [NotInParallel("AvaloniaSession")], like every other VM suite touching the dispatcher.
public class ChatTabViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";

    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(ITranscriptProjection? projection) {
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, projection, Opener, Time);
        }

        public async Task PushAsync(AgentStatusDto dto) {
            Daemon.Agents.AddOrUpdate(dto);
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TickAsync() {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TeardownAsync() {
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    static Harness Claude() => new(TranscriptProjection.For("claude"));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Waits_until_a_path_then_renders_the_initial_load_in_file_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for the transcript…");

            await h.PushAsync(Dto(transcriptPath: null));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);

            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine, ToolCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolCallItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(((ToolCallItem)h.Chat.Items[2]).Detail).IsEqualTo("ls -la");
            await Assert.That(((ToolCallItem)h.Chat.Items[2]).Outcome).IsEqualTo(ToolOutcome.Running);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appended_lines_render_after_a_tick_and_a_partial_line_waits_for_its_newline() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine[..20]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolCallLine[20..] + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(3);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_results_flip_their_call_in_place_and_unmatched_results_are_ignored() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));
            var call = (ToolCallItem)h.Chat.Items.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);
            await Assert.That(((ToolCallItem)h.Chat.Items[1]).Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(((ToolCallItem)h.Chat.Items[1]).IsError).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Length_regression_resets_items_missing_recovers_and_failed_keeps_items() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.PathTo("t.jsonl");
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Missing);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("The transcript file is missing");

            File.WriteAllLines(path, [UserLine, AssistantLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(h.Chat.Items[0]).IsTypeOf<ToolCallItem>();

            File.Delete(path);
            Directory.CreateDirectory(path);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            Directory.Delete(path);
            await h.TeardownAsync();
        });
    }

    sealed class GatedProjection(ITranscriptProjection inner, string blockOn, TaskCompletionSource gate) : ITranscriptProjection {
        public IReadOnlyList<AcpEventEnvelope> Project(string line) {
            if (line.Contains(blockOn, StringComparison.Ordinal)) gate.Task.GetAwaiter().GetResult();
            return inner.Project(line);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_discards_a_read_still_in_flight_for_the_old_file() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new GatedProjection(TranscriptProjection.For("claude")!, "OLD", gate));
            var oldPath = Tmp.CreateFile("old.jsonl", [UserLine.Replace("hello", "OLD"), ToolCallLine]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(AssistantTextItem) });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_for_a_vendor_without_a_projection_and_no_ticks_after_teardown() {
        await RunOnUiAsync(async () => {
            var unavailable = new Harness(projection: null);
            await Assert.That(unavailable.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(unavailable.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await unavailable.TeardownAsync();

            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await h.TeardownAsync();
            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            // A removed agent keeps its items.
            var kept = Claude();
            var keptPath = Tmp.CreateFile("kept.jsonl", [UserLine]);
            await kept.PushAsync(Dto(keptPath));
            kept.Daemon.Agents.Remove("a1");
            await Assert.That(kept.Chat.Items).Count().IsEqualTo(1);
            await kept.TeardownAsync();
        });
    }

    /// Thread identity, so deliberately not under WithImmediateRxScheduler. PhaseNote and the FIRST
    /// notification are what make it discriminating: that one is raised by the path switch, on
    /// whichever thread delivered the dto, while every later one comes from the apply, which hops
    /// to the UI thread on its own and would pass with no ObserveOn at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dto_pushed_from_a_pool_thread_lands_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            bool? phaseChangedOnUi = null;
            h.Chat.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(ChatTabViewModel.PhaseNote))
                    phaseChangedOnUi ??= Dispatcher.UIThread.CheckAccess();
            };

            await Task.Run(() => h.Daemon.Agents.AddOrUpdate(Dto(path)));
            await WaitUntilAsync(() => h.Chat.Phase == ChatTabPhase.Reading, what: "reading");
            await h.TeardownAsync();
            return phaseChangedOnUi;
        });
        await Assert.That(onUi).IsTrue();
    }

    /// Pins the system-note row: a task notification the vendor injects into the transcript
    /// renders as a system note carrying its summary and result, never as a user bubble.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_notification_renders_as_a_system_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""",
            ]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(SystemNoteItem) });
            await Assert.That(((SystemNoteItem)h.Chat.Items[0]).Text).IsEqualTo("**Agent finished**\n\nAll good.");
            await h.TeardownAsync();
        });
    }
}
