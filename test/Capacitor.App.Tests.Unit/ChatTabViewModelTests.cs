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
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string NoteLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""";
    const string ThinkingLine = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"weighing it"}]}}""";

    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    static ToolGroupItem Group(ChatTabViewModel chat, int index) => (ToolGroupItem)chat.Items[index];

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(ITranscriptProjection? projection, Action<FakePermissionService>? seed = null) {
            seed?.Invoke(Permissions);
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel("a1", Daemon, Terminal, projection, Opener, Time, Permissions);
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

    static Harness Claude(Action<FakePermissionService>? seed = null) => new(TranscriptProjection.For("claude"), seed);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_already_cached_when_the_tab_opens_lights_the_row_at_once() {
        await RunOnUiAsync(async () => {
            var h = Claude(seed: p => p.Add(PermissionEntries.Entry("r1", "a1")));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the replayed card");
            await Assert.That(h.Chat.HasPendingCards).IsTrue();
            await h.TeardownAsync();
        });
    }

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
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(Group(h.Chat, 2).Calls[0].Detail).IsEqualTo("ls -la");
            await Assert.That(Group(h.Chat, 2).Calls[0].Outcome).IsEqualTo(ToolOutcome.Running);
            await Assert.That(Group(h.Chat, 2).Calls[0].Category).IsEqualTo(ToolCategory.Search);
            await h.TeardownAsync();
        });
    }

    /// Tool paths read relative to the checkout the agent runs in, which the wire names as
    /// worktree_path; the repository alone would leave a checkout outside its own directory, or
    /// one under `.claude/worktrees`, rendering absolute paths.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_paths_relativize_against_the_worktree_the_agent_runs_in() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string worktree = "/repo/x/.claude/worktrees/slug";
            const string readLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/.claude/worktrees/slug/src/Foo.cs"}}]}}""";
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/.claude/worktrees/slug/src/Bar.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");

            var path = Tmp.CreateFile("t.jsonl", [readLine]);
            await h.PushAsync(Dto(path) with { WorktreePath = worktree, WorkLocation = "owned" });

            await Assert.That(((ToolCallItem)h.Chat.Items.Single()).Detail).IsEqualTo("src/Foo.cs");
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/Bar.cs", what: "relative to the worktree once the root lands");
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
            var call = Group(h.Chat, 0).Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await Assert.That(Group(h.Chat, 0).Calls[1].Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(Group(h.Chat, 0).Calls[1].IsError).IsTrue();
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
            await Assert.That(h.Chat.Items[0]).IsTypeOf<ToolGroupItem>();

            File.Delete(path);
            Directory.CreateDirectory(path);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            Directory.Delete(path);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Consecutive_calls_across_reads_share_a_group_and_any_prose_closes_it() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, ThinkingLine + "\n" + ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash", "Read" }, CollectionOrdering.Matching);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine.Replace("t1", "t3") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 2).Calls).Count().IsEqualTo(1);

            File.AppendAllText(path, NoteLine + "\n" + ToolCallLine.Replace("t1", "t4") + "\n" + UserLine + "\n" + ToolCallLine.Replace("t1", "t5") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] {
                nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem),
                nameof(SystemNoteItem), nameof(ToolGroupItem), nameof(UserTurnItem), nameof(ToolGroupItem),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_folds_its_call_and_the_summary_follows_the_settled_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var group = Group(h.Chat, 0);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" });
            await Assert.That(group.Summary).IsEqualTo("Searched files");
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.HasFailure).IsFalse();

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(1);

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Searched files, read a file");
            await Assert.That(group.HasFailure).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_and_a_path_switch_start_a_fresh_group_for_later_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(1);
            File.AppendAllText(other, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cards_are_filtered_to_the_agent_ordered_by_request_time_and_removed_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", requestedAt: "2026-08-28T10:00:02.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("rX", "other", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "two cards");
            await Assert.That(h.Chat.PendingCards.Select(c => c.RequestId).ToArray()).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.HasPendingCards).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r2");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_permission_replayed_before_the_agent_dto_ends_up_with_a_relative_detail() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");
            await Assert.That(((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail).IsEqualTo("/repo/x/src/a.cs");
            await h.PushAsync(Dto(transcriptPath: null));
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/a.cs", what: "relative once the root lands");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_entries_become_question_cards_beside_permission_cards() {
        await RunOnUiAsync(async () => {
            var h = Claude(p => {
                p.Add(PermissionEntries.Entry("r1", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
                p.Add(PermissionEntries.Question("q1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            });
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "both cards");
            await Assert.That(h.Chat.PendingCards[0]).IsTypeOf<PermissionCardViewModel>();
            await Assert.That(h.Chat.PendingCards[1]).IsTypeOf<QuestionCardViewModel>();

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "question card removed");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r1");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_with_an_id_marks_its_row_in_either_order_and_clears_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            var read = Group(h.Chat, 0).Calls[1];
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "the card-first mark");
            await Assert.That(bash.OutcomeGlyph).IsEqualTo("?");
            await Assert.That(read.IsAwaitingPermission).IsFalse();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !bash.IsAwaitingPermission, what: "cleared on resolve");

            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            await WaitUntilAsync(() => read.IsAwaitingPermission, what: "the row-first mark");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();

            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", toolUseId: "nope"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the unmatched card");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(read.IsAwaitingPermission).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Two_requests_on_one_row_keep_the_mark_until_both_go_and_a_settled_row_is_cleared() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "marked");

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(bash.IsAwaitingPermission).IsTrue();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(bash.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(h.Chat.PendingCards).Count().IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_without_an_id_marks_the_sole_running_call_and_abstains_on_two() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", vendor: "codex"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var first = Group(h.Chat, 0).Calls[0];
            await WaitUntilAsync(() => first.IsAwaitingPermission, what: "the sole running call, row after card");

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            var second = Group(h.Chat, 0).Calls[1];
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsFalse();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !second.IsAwaitingPermission, what: "cleared on resolve");

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "a card with nothing running");
            await Assert.That(second.IsAwaitingPermission).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_request_marks_the_rebuilt_row_after_a_reset_and_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => Group(h.Chat, 1).Calls[0].IsAwaitingPermission, what: "marked before the reset");

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine.Replace("t1", "t9")]);
            await h.PushAsync(Dto(other));
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r2");
            await WaitUntilAsync(() => !Group(h.Chat, 0).Calls[0].IsAwaitingPermission, what: "only the id-less request fitted t9");
            await h.TeardownAsync();
        });
    }
}
