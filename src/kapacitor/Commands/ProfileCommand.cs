namespace kapacitor.Commands;

public static class ProfileCommand {
    public static Task<int> HandleAsync(string[] args) {
        Console.Error.WriteLine("Profile commands not yet implemented.");
        return Task.FromResult(1);
    }
}
