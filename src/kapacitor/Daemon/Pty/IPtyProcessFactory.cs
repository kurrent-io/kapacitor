namespace kapacitor.Daemon.Pty;

public interface IPtyProcessFactory
{
    IPtyProcess Spawn(string command, string[] args, string cwd,
        Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40);
}
