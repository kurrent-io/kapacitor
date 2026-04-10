namespace kapacitor.Commands;

public static class UseCommand {
    public static Task<int> HandleAsync(string[] args) {
        Console.Error.WriteLine("Use command not yet implemented.");
        return Task.FromResult(1);
    }
}
