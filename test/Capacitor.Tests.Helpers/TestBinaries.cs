using Capacitor.Cli.Core.Setup;

namespace Capacitor.Tests.Helpers;

public static class TestBinaries {
    /// <summary>
    /// A probe over <paramref name="dir"/>, holding a launchable file per name. The extension and the
    /// execute bit are this host's, so what is staged is what the platform will actually run — a
    /// bare name on Windows is unlaunchable however executable it looks.
    /// </summary>
    public static BinaryProbe Searching(TempDir dir, params string[] commands) {
        foreach (var command in commands) Stage(dir.PathTo(Launchable(command)));

        return BinaryProbe.Searching(dir.Path);
    }

    static string Launchable(string command) => OperatingSystem.IsWindows() ? command + ".cmd" : command;

    static void Stage(string path) {
        File.WriteAllText(path, OperatingSystem.IsWindows() ? "@echo off\r\n" : "#!/bin/sh\nexit 0\n");

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
