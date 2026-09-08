namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

/// <summary>Answers by argument prefix; an unmatched call fails with exit 1 so a test cannot pass on an unscripted spawn.</summary>
internal sealed class FakeGhProcessRunner : IProcessRunner {
    public readonly List<(string FileName, string[] Args, RunOptions Options)> Calls = [];
    readonly List<(string[] Prefix, Func<Task<ProcessResult>> Reply)> _replies = [];
    public Exception? StartFailure;

    public void When(string[] prefix, string stdout, int exitCode = 0, string stderr = "", bool timedOut = false)
        => _replies.Add((prefix, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, timedOut))));
    public void WhenPending(string[] prefix, TaskCompletionSource<ProcessResult> source) => _replies.Add((prefix, () => source.Task));
    /// <summary>Matches when every needle appears somewhere in the argument list; register the more specific rule first.</summary>
    public void WhenAll(string[] needles, string stdout, int exitCode = 0, string stderr = "")
        => _replies.Add((needles, () => Task.FromResult(new ProcessResult(exitCode, stdout, stderr, false))));

    public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
        Calls.Add((fileName, args, options));
        if (StartFailure is not null) throw StartFailure;
        foreach (var (prefix, reply) in _replies)
            if (args.Length >= prefix.Length && prefix.SequenceEqual(args.Take(prefix.Length)) || prefix.All(args.Contains)) return reply();
        return Task.FromResult(new ProcessResult(1, "", "unscripted: " + string.Join(' ', args), false));
    }

    public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct)
        => throw new NotSupportedException();
}
