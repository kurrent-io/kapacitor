namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

public sealed record TrayAgentEntry(string Id, string Label, bool StopEnabled); // StopEnabled: always true in this task; Task 4 wires in-flight gating
public sealed record TrayPauseItem(bool Enabled, bool Checked);
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause);
