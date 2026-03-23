using System.Text.Json;

namespace kapacitor;

static class ValidatePlanCommand {
    public static async Task<int> Handle(string baseUrl, string sessionId) {
        using var httpClient = new HttpClient();

        // Fetch chain recap to find plans from previous sessions (e.g. ExitPlanMode in parent)
        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap?chain=true");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json    = await resp.Content.ReadAsStringAsync();
        var entries = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListRecapEntry);

        if (entries is null || entries.Count == 0) {
            Console.WriteLine("No plan found for this session.");

            return 0;
        }

        // Plans can come from any session in the chain (continuation planContent or ExitPlanMode write)
        var plans = entries.Where(e => e.Type == "plan").ToList();
        // Work done: only from the current session being validated
        var work = entries.Where(e => e.Type is "write" or "edit" && e.SessionId == sessionId).ToList();
        // AI-generated summaries
        var summaries = entries.Where(e => e.Type == "whats_done").ToList();

        if (plans.Count == 0) {
            Console.WriteLine("No plan found for this session.");

            return 0;
        }

        // Output plan(s)
        Console.WriteLine("## Plan");
        Console.WriteLine();

        foreach (var plan in plans)
            Console.WriteLine(plan.Content);
        Console.WriteLine();

        // Output what's done
        Console.WriteLine("## What's Done");
        Console.WriteLine();

        if (summaries.Count > 0) {
            Console.WriteLine("### Summary");
            Console.WriteLine();

            foreach (var summary in summaries)
                Console.WriteLine(summary.Content);
            Console.WriteLine();
        }

        Console.WriteLine("### Details");
        Console.WriteLine();

        if (work.Count == 0) {
            Console.WriteLine("No file writes or edits recorded.");
        }
        else {
            foreach (var entry in work) {
                var label = entry.Type == "write" ? "Write" : "Edit";
                var path  = entry.FilePath ?? "unknown";
                Console.WriteLine($"- {label}: {path}");
            }
        }

        Console.WriteLine();

        // Verification instruction
        Console.WriteLine("## Instructions");
        Console.WriteLine();

        Console.WriteLine(
            "Compare the plan above against the summary and file list under \"What's Done\". Identify any planned items that were NOT completed. If everything is done, confirm that. If there are gaps, list them and complete the remaining work now."
        );

        return 0;
    }
}
