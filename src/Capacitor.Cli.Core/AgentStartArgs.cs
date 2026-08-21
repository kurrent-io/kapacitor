namespace Capacitor.Cli.Core;

/// Parses `agent start &lt;vendor&gt; [kcap flags] -- [agent args]`: kcap's own flags come
/// before <c>--</c>; everything after <c>--</c> is forwarded to the agent CLI verbatim.
public sealed class AgentStartArgs {
    public string   Vendor      { get; private set; } = "";
    public bool     Worktree    { get; private set; }
    public string?  DaemonName  { get; private set; }
    public bool     Detached    { get; private set; }
    public bool     Private     { get; private set; }
    public string[] Passthrough { get; private set; } = [];
    public string?  Error       { get; private set; }

    public static AgentStartArgs Parse(string[] args) {
        var r = new AgentStartArgs();

        if (args.Length == 0) {
            r.Error = "usage: kcap agent start <vendor> [--worktree] [--private] [--daemon <name>] [-d|--detach] [-- <agent args>]";

            return r;
        }

        var dash = Array.IndexOf(args, "--");
        var kcap = dash < 0 ? args : args[..dash];
        r.Passthrough = dash < 0 ? [] : args[(dash + 1)..];

        if (kcap.Length == 0) {
            r.Error = "missing <vendor>";

            return r;
        }

        r.Vendor = kcap[0];

        for (var i = 1; i < kcap.Length; i++) {
            switch (kcap[i]) {
                case "--worktree":        r.Worktree = true; break;
                case "-d" or "--detach":  r.Detached = true; break;
                case "--private":         r.Private  = true; break;
                case "--daemon":
                    if (i + 1 >= kcap.Length || string.IsNullOrEmpty(kcap[i + 1]) || kcap[i + 1].StartsWith('-')) {
                        r.Error = "--daemon requires a value";

                        return r;
                    }

                    r.DaemonName = kcap[++i];

                    break;
                default:
                    r.Error = $"unknown flag {kcap[i]} (agent args go after `--`)";

                    return r;
            }
        }

        return r;
    }
}
