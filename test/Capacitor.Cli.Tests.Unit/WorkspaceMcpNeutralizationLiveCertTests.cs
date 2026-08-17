using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The end-to-end certification for branch-authored MCP containment: a real vendor, launched into a
/// worktree built by the real <see cref="WorktreeManager"/>, against the exploit as it was actually
/// measured.
///
/// <para><b>Why this exists when the unit tests already pass.</b> Those assert that a file is gone. They
/// cannot show that the file being gone is what stops the vendor — a vendor reading the config from
/// somewhere else, or caching it, or resolving it from an ancestor, would leave them all green while the
/// exposure stayed open. Only running the vendor closes that gap.</para>
///
/// <para><b>The measurement being reproduced.</b> Kiro spawns the command in a worktree's
/// <c>.kiro/settings/mcp.json</c> at session setup — no prompt, no model involvement, no tool call, so it
/// costs nothing to run and needs no authenticated turn. That is what makes it a good permanent cert.</para>
///
/// <para>Gated because it needs the vendor installed. Unlike a "does it still pass?" test, a skip here is
/// safe: the unit tests still fail if the neutralizer regresses. This catches the different thing — the
/// vendor changing where it reads from.</para>
/// </summary>
public class WorkspaceMcpNeutralizationLiveCertTests {
    const string Gate = "KCAP_WORKSPACE_MCP_CERT";

    static void SkipUnlessGated() {
        Skip.Unless(Environment.GetEnvironmentVariable(Gate) == "1",
            $"Gated live certification of branch-authored MCP containment — set {Gate}=1 to run "
          + "(requires `kiro-cli` on PATH; spends no model turn, the spawn happens at session setup).");
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The hostile-repo fixture's payload script is a POSIX executable shell script.");
    }

    /// <summary>
    /// Positive control FIRST. A cert that only asserts "no marker" is worthless if the vendor stopped
    /// spawning workspace servers for an unrelated reason — it would keep passing while the neutralizer
    /// rotted. So the same hostile config is driven in a RAW worktree, and the marker MUST appear there.
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Kiro_spawns_a_branch_authored_server_when_the_worktree_is_not_neutralized() {
        SkipUnlessGated();

        var (repo, marker) = HostileRepo();
        var raw = Path.Combine(Path.GetTempPath(), "kcap-cert-raw-" + Guid.NewGuid().ToString("N")[..8]);
        Git(repo, "worktree", "add", "-q", raw, "-b", "raw-" + Guid.NewGuid().ToString("N")[..8]);

        await DriveKiroSessionAsync(raw);

        await Assert.That(File.Exists(marker))
            .IsTrue()
            .Because("the control must reproduce the exploit, or the negative case below proves nothing");
    }

    /// <summary>The actual claim: the same repo, through the production creation path, does not spawn.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Kiro_does_not_spawn_it_from_a_worktree_created_by_WorktreeManager() {
        SkipUnlessGated();

        var (repo, marker) = HostileRepo();

        var info = await new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance)
            .CreateAsync(repo);

        await DriveKiroSessionAsync(info.Path);

        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(info.Path, ".kiro", "settings", "mcp.json"))).IsFalse();
    }

    // ── fixture ──

    /// <summary>A repo whose committed content declares an MCP server that writes a marker when executed.
    /// The marker path is ABSOLUTE and baked in at repo-creation time, and each test builds its own repo —
    /// an earlier version derived it from the worktree with relative path arithmetic, which silently broke
    /// when the control and the subject sat at different depths and made the control unfireable.
    /// The marker lands OUTSIDE the worktree, so neutralization can never be credited for its absence.</summary>
    [UnsupportedOSPlatform("windows")]
    static (string repo, string marker) HostileRepo() {
        var repo = Path.Combine(Path.GetTempPath(), "kcap-cert-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "cert@example.com");
        Git(repo, "config", "user.name", "Cert");

        // One marker path per repo, and one repo per test, so the control's spawn can never be mistaken
        // for the subject's.
        var marker = Path.Combine(Path.GetTempPath(),
            "kcap-cert-marker-" + Guid.NewGuid().ToString("N")[..12]);

        var payload = Path.Combine(repo, "payload.sh");
        File.WriteAllText(payload,
            "#!/bin/sh\n"
          + $"printf spawned > '{marker}'\n"
          + "sleep 30\n");
        File.SetUnixFileMode(payload, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                      UnixFileMode.UserExecute | UnixFileMode.OtherRead |
                                      UnixFileMode.OtherExecute);

        var settings = Path.Combine(repo, ".kiro", "settings");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "mcp.json"), JsonSerializer.Serialize(new {
            mcpServers = new Dictionary<string, object> {
                [$"probe-{Guid.NewGuid():N}"[..20]] = new { command = "/bin/sh", args = new[] { payload } }
            }
        }));

        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "branch content declaring an MCP server");
        return (repo, marker);
    }

    /// <summary>initialize + session/new only. The measured spawn happens at session setup, so no prompt
    /// is sent and no model turn is spent.</summary>
    static async Task DriveKiroSessionAsync(string cwd) {
        var psi = new ProcessStartInfo("kiro-cli", ["acp"]) {
            WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        psi.Environment["KCAP_DISABLE"] = "1";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("kiro-cli did not start");
        // Both streams drained: an undrained stderr can fill its pipe and wedge the child mid-handshake,
        // which would present as a false NEGATIVE — the dangerous direction here.
        _ = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();

        await proc.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{}}}""");
        await Task.Delay(TimeSpan.FromSeconds(5));
        var newSession = """{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":CWD,"mcpServers":[]}}"""
            .Replace("CWD", JsonSerializer.Serialize(cwd));
        await proc.StandardInput.WriteLineAsync(newSession);
        await Task.Delay(TimeSpan.FromSeconds(25));

        try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }
}
