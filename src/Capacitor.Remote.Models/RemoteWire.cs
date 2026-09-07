namespace Capacitor.Remote.Models;

/// Hub methods a UI client invokes. SignalR JSON does not backfill missing trailing arguments,
/// so every method's arity is frozen — new capability means a NEW method taking one record.
public static class HubMethods {
    public const string GetConnectedDaemons    = "GetConnectedDaemons";
    public const string RequestLaunchAgentV2   = "RequestLaunchAgentV2";
    public const string RequestStopAgent       = "RequestStopAgent";
    public const string SendUserInput          = "SendUserInput";
    public const string SendSpecialKey         = "SendSpecialKey";
    public const string SubscribeToTerminal    = "SubscribeToTerminal";
    public const string UnsubscribeFromTerminal = "UnsubscribeFromTerminal";
    public const string RequestResizeTerminal  = "RequestResizeTerminal";
    public const string ReleaseResizeTerminal  = "ReleaseResizeTerminal";
    public const string SubscribeToChat        = "SubscribeToChat";
    public const string UnsubscribeFromChat    = "UnsubscribeFromChat";
    public const string SubscribeToAcpEphemeral = "SubscribeToAcpEphemeral";
    public const string SubscribeToStream      = "SubscribeToStream";
    public const string RegisterSessionAccessWatch = "RegisterSessionAccessWatch";
    public const string ResolveAttribution     = "ResolveAttribution";
}

/// Server → UI-client pushes. Org-wide ones arrive with no join call; the rest are group-scoped.
public static class HubBroadcasts {
    public const string AgentInstancesChanged  = "AgentInstancesChanged";
    public const string DaemonsChanged         = "DaemonsChanged";
    public const string LaunchFailed           = "LaunchFailed";
    public const string PermissionPending      = "PermissionPending";
    public const string PermissionResponded    = "PermissionResponded";
    public const string PermissionRequested    = "PermissionRequested";
    public const string AcpElicitationRequested = "AcpElicitationRequested";
    public const string PendingInputChanged    = "PendingInputChanged";
    public const string TerminalOutput         = "TerminalOutput";
    public const string TerminalDimensions     = "TerminalDimensions";
    public const string SessionTitleChanged    = "SessionTitleChanged";
    public const string ActiveSessionAdded     = "ActiveSessionAdded";
    public const string ActiveSessionChanged   = "ActiveSessionChanged";
    public const string ActiveSessionRemoved   = "ActiveSessionRemoved";
    public const string SessionAccessChanged   = "SessionAccessChanged";
}

public static class ApiRoutes {
    public const string AgentInstances = "api/agent-instances";
    public const string Daemons        = "api/daemons";
    public static string SessionDetail(string sessionId) =>
        $"api/sessions/{Uri.EscapeDataString(sessionId)}/detail";
    public static string PermissionResponse(string sessionId, string requestId) =>
        $"api/sessions/{Uri.EscapeDataString(sessionId)}/permission-response/{Uri.EscapeDataString(requestId)}";
}

/// The daemon's fixed special-key vocabulary — anything else is a server-side no-op.
public static class SpecialKeys {
    public const string Escape = "Escape";
    public const string Tab = "Tab";
    public const string Enter = "Enter";
    public const string CtrlC = "CtrlC";
    public const string ArrowUp = "ArrowUp";
    public const string ArrowDown = "ArrowDown";
    public const string ShiftTab = "ShiftTab";
    public static readonly string[] All = [Escape, Tab, Enter, CtrlC, ArrowUp, ArrowDown, ShiftTab];
}

/// Literal tokens compared against wire values.
public static class WireTokens {
    /// LaunchFailed reason prefix for a consent-gate denial on the target machine.
    public const string LaunchDeniedByOwnerPrefix = "launch_denied_by_owner";
}
