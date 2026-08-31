using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap projects</c> / <c>kcap project &lt;slug&gt;</c> — read-only views over the server's
/// <c>/api/projects</c> endpoints. Follows <see cref="ErrorsCommand"/> for client/auth/error handling.
/// Every route 403s with <c>projects_not_in_plan</c> on the Free plan (see <see cref="CliProjectError"/>).
/// </summary>
class ProjectsCommand(ConfigRoot config, ProfileContext profiles) {
    public async Task<int> HandleList() {
        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync(config, profiles, baseUrl);

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/projects");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden) {
            return await ReportForbidden(resp);
        }

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json     = await resp.Content.ReadAsStringAsync();
        var projects = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ListCliProjectSummary);

        if (projects is null || projects.Count == 0) {
            await Console.Out.WriteLineAsync("No projects found.");

            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Slug");
        table.AddColumn("Name");
        table.AddColumn("Repos");
        table.AddColumn("Members");
        table.AddColumn("Your role");

        foreach (var project in projects) {
            table.AddRow(
                Markup.Escape(project.Slug),
                Markup.Escape(project.Name),
                project.RepoCount.ToString(),
                project.MemberCount.ToString(),
                Markup.Escape(FormatRole(project.ViewerMembership, project.ViewerPending))
            );
        }

        AnsiConsole.Write(table);

        return 0;
    }

    public async Task<int> HandleDetail(string slug) {
        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await HttpClientExtensions.CreateAuthenticatedClientAsync(config, profiles, baseUrl);

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/projects/{Uri.EscapeDataString(slug)}");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
            return 1;
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden) {
            return await ReportForbidden(resp);
        }

        if (resp.StatusCode == HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync("Project not found.");

            return 1;
        }

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}");

            return 1;
        }

        var json    = await resp.Content.ReadAsStringAsync();
        var project = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.CliProjectDetail);

        if (project is null) {
            await Console.Error.WriteLineAsync("Project not found.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"{project.Name} ({project.Slug})");

        if (!string.IsNullOrWhiteSpace(project.Description)) {
            await Console.Out.WriteLineAsync(project.Description);
        }

        await Console.Out.WriteLineAsync($"  Owner:     {project.OwnerUserId}");
        await Console.Out.WriteLineAsync($"  Your role: {FormatRole(project.ViewerMembership, project.ViewerPending)}");

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"Repos ({project.Repos.Count}):");

        if (project.Repos.Count == 0) {
            await Console.Out.WriteLineAsync("  (none)");
        } else {
            foreach (var repo in project.Repos) {
                await Console.Out.WriteLineAsync($"  {repo.RepoSlug}");
            }
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"Members ({project.Members.Count}):");

        if (project.Members.Count == 0) {
            await Console.Out.WriteLineAsync("  (none)");
        } else {
            foreach (var member in project.Members) {
                await Console.Out.WriteLineAsync($"  {member.DisplayName} ({member.MemberKind})");
            }
        }

        if (project.JoinRequests.Count > 0) {
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"Join requests ({project.JoinRequests.Count}):");

            foreach (var request in project.JoinRequests) {
                await Console.Out.WriteLineAsync($"  {request.UserId} — {request.Direction} ({request.RequestedAt:u})");
            }
        }

        return 0;
    }

    /// <summary>
    /// Every <c>/api/projects*</c> route 403s identically when the tenant plan doesn't include
    /// projects (Free). Falls back to a generic message for any other 403 shape.
    /// </summary>
    static async Task<int> ReportForbidden(HttpResponseMessage resp) {
        var body = await resp.Content.ReadAsStringAsync();

        try {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.Str("error") == "projects_not_in_plan") {
                await Console.Error.WriteLineAsync("Projects require the Team or Enterprise plan.");

                return 1;
            }
        } catch (JsonException) {
            /* fall through to generic message */
        }

        await Console.Error.WriteLineAsync("Forbidden.");

        return 1;
    }

    static string FormatRole(string viewerMembership, string? viewerPending) => viewerMembership switch {
        "owner"  => "owner",
        "member" => "member",
        _ => viewerPending switch {
            "invite"  => "pending invite",
            "request" => "pending request",
            _         => "—"
        }
    };
}
