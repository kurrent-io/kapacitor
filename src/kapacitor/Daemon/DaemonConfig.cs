namespace kapacitor.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

    public string WorktreeRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".capacitor",
        "worktrees"
    );

    public string ClaudePath { get; set; } = "claude";

    public List<string> Validate() {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl)) {
            errors.Add("ServerUrl is required");
        } else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri)
                   || uri.Scheme is not ("http" or "https")) {
            errors.Add($"ServerUrl must be a valid http/https URL, got: {ServerUrl}");
        }

        if (MaxConcurrentAgents < 1) {
            errors.Add("MaxConcurrentAgents must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(WorktreeRoot)) {
            errors.Add("WorktreeRoot is required");
        }

        return errors;
    }

    public bool IsRepoAllowed(string repoPath) {
        if (AllowedRepoPaths.Length == 0) {
            return true;
        }

        return AllowedRepoPaths.Any(pattern => {
                if (pattern.EndsWith("/*")) {
                    var prefix = pattern[..^1]; // keep trailing slash: "/allowed/"

                    return repoPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(repoPath, pattern[..^2], StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(repoPath, pattern, StringComparison.OrdinalIgnoreCase);
            }
        );
    }
}
