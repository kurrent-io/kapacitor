using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap machine` — machine credentials for headless recording.
///
/// <para><b>Two hops, and the split is the security property.</b> Provisioning goes to the auth proxy
/// with the operator's own WorkOS token; registration goes to the tenant with only the public
/// <c>client_id</c>. So the secret travels from WorkOS to this terminal and nowhere else — it never
/// reaches the Capacitor server, which is what makes "no secret is stored by Capacitor" structural
/// rather than a promise. Do not "simplify" this by routing provisioning through the tenant.</para>
///
/// <para><b>The secret is printed once and never persisted.</b> Not written to the config, not to the
/// token store, not logged. WorkOS will not disclose it again either, so a lost secret means
/// provisioning a new machine — which is why the output says so at the point of printing rather than
/// burying it in documentation.</para>
/// </summary>
public static class MachineCommand {
    /// <summary>
    /// Visibility values a machine may record with — the same set a human's profile accepts, because a
    /// machine is just another principal running this CLI. Kept in sync with the server's own list by
    /// being validated here rather than silently passed through.
    /// </summary>
    static readonly string[] Visibilities = ["private", "org_public", "public"];

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2 || IsHelp(args[1])) return await PrintUsage();

        return args[1] switch {
            "create" => await CreateAsync(args),
            "list"   => await ListAsync(),
            "revoke" => await RevokeAsync(args),
            _        => await PrintUsage()
        };
    }

    static bool IsHelp(string arg) => arg is "--help" or "-h" or "help";

    // ── create ──────────────────────────────────────────────────────────────────────────────────

    static async Task<int> CreateAsync(string[] args) {
        if (args.Length < 3 || IsHelp(args[2])) return await PrintCreateUsage();

        var name       = args[2].Trim();
        var visibility = GetArg(args, "--visibility") ?? "private";
        var role       = GetArg(args, "--role");

        if (string.IsNullOrWhiteSpace(name)) {
            await Console.Error.WriteLineAsync("A machine name is required.");

            return 1;
        }

        if (!Visibilities.Contains(visibility, StringComparer.Ordinal)) {
            await Console.Error.WriteLineAsync(
                $"--visibility must be one of: {string.Join(", ", Visibilities)}");

            return 1;
        }

        // The operator's own WorkOS access token is what the proxy scopes on: it reads org_id and role
        // from the token's signed claims, so this CLI cannot ask for another organization even if it
        // wanted to. Nothing about the request names an org.
        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken)) {
            await Console.Error.WriteLineAsync("Not authenticated. Run `kcap login` first.");

            return 1;
        }

        using var http = new HttpClient();

        CreateMachineApplicationResponse? provisioned;

        try {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{AuthProxyEndpoint.Url}/connect/m2m-applications") {
                Content = JsonContent.Create(new CreateMachineApplicationRequest(name),
                    CapacitorJsonContext.Default.CreateMachineApplicationRequest)
            };
            request.Headers.Authorization = new("Bearer", tokens.AccessToken);

            using var response = await http.SendAsync(request);

            if (response.StatusCode is HttpStatusCode.Unauthorized) {
                await Console.Error.WriteLineAsync(
                    "The Kurrent auth service rejected your sign-in. Run `kcap login` and try again.");

                return 1;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden) {
                await Console.Error.WriteLineAsync(
                    "You need the owner or admin role in this organization to create a machine.");

                return 1;
            }

            if (!response.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync(
                    $"Provisioning failed ({(int)response.StatusCode}). {await response.Content.ReadAsStringAsync()}");

                return 1;
            }

            provisioned = await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.CreateMachineApplicationResponse);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) {
            await Console.Error.WriteLineAsync($"The Kurrent auth service is unreachable: {e.Message}");

            return 1;
        }

        if (provisioned is null || string.IsNullOrEmpty(provisioned.ClientId)) {
            await Console.Error.WriteLineAsync("The auth service returned no credential.");

            return 1;
        }

        // An idempotent hit means the machine already existed. Say so and stop, rather than
        // re-registering: WorkOS cannot re-disclose the secret, so there is nothing useful to print and
        // implying otherwise would send someone looking for a value that no longer exists anywhere.
        if (!provisioned.Created) {
            await Console.Error.WriteLineAsync(
                $"A machine named '{name}' already exists in this organization "
              + $"(client id {provisioned.ClientId}).");
            await Console.Error.WriteLineAsync(
                "Its secret was shown only when it was created and cannot be retrieved. "
              + "To replace it, revoke that machine and create one with a new name.");

            return 1;
        }

        // Register the PUBLIC client id with the tenant. The secret is deliberately not in this call —
        // there is no field for it, and there is no code path on the server that could store one.
        var registered = await RegisterAsync(provisioned.ClientId, name, role);

        if (registered is null) return 1;

        await PrintCredentialAsync(name, provisioned, registered, visibility);

        return 0;
    }

    static async Task<RegisterMachineResponse?> RegisterAsync(string clientId, string name, string? role) {
        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            using var response = await client.PostAsJsonAsync(
                "/api/admin/machines",
                new RegisterMachineRequest(clientId, name, role),
                CapacitorJsonContext.Default.RegisterMachineRequest);

            if (response.StatusCode is HttpStatusCode.NotFound) {
                await Console.Error.WriteLineAsync(
                    "This server does not have machine credentials enabled. "
                  + "The WorkOS application was created but nothing here recognises it — "
                  + "ask an administrator to enable the feature, then run `kcap machine create` again "
                  + "with a new name.");

                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) {
                await Console.Error.WriteLineAsync("You need to be a Capacitor administrator to register a machine.");

                return null;
            }

            if (!response.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync(
                    $"Registering the machine failed ({(int)response.StatusCode}). "
                  + await response.Content.ReadAsStringAsync());

                return null;
            }

            return await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.RegisterMachineResponse);
        }
        catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"Could not reach the Capacitor server: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Prints the credential and how to use it. The secret goes to STDOUT (so it can be piped into a
    /// secret store) and everything else to STDERR (so a pipe captures only the value).
    /// </summary>
    static async Task PrintCredentialAsync(
            string name, CreateMachineApplicationResponse provisioned,
            RegisterMachineResponse registered, string visibility) {
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"Machine '{name}' created.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"  Client ID     {provisioned.ClientId}");
        await Console.Error.WriteLineAsync($"  Principal     {registered.UserId}");
        await Console.Error.WriteLineAsync($"  Organization  {provisioned.OrganizationId}");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  ── The secret below is shown ONCE. It is not stored anywhere ──");
        await Console.Error.WriteLineAsync("     Not by this CLI, not by Capacitor. WorkOS will not show it");
        await Console.Error.WriteLineAsync("     again. If you lose it, revoke this machine and create a new one.");
        await Console.Error.WriteLineAsync();

        // STDOUT, alone, so `kcap machine create ci-runner 2>/dev/null` yields exactly the secret.
        await Console.Out.WriteLineAsync(provisioned.ClientSecret);

        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  Give the runner these environment variables:");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"    KCAP_CLIENT_ID={provisioned.ClientId}");
        await Console.Error.WriteLineAsync("    KCAP_CLIENT_SECRET=<the secret above>");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"  And set what its sessions are visible to (default '{visibility}'):");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"    kcap config set default_visibility {visibility}");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync(
            "  Run that on the machine itself, in the profile it records with — visibility is the");
        await Console.Error.WriteLineAsync(
            "  machine's own setting, exactly as it is for a person.");
    }

    // ── list ────────────────────────────────────────────────────────────────────────────────────

    static async Task<int> ListAsync() {
        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            using var response = await client.GetAsync("/api/admin/machines");

            if (response.StatusCode is HttpStatusCode.NotFound) {
                await Console.Error.WriteLineAsync("This server does not have machine credentials enabled.");

                return 1;
            }

            if (!response.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync(
                    $"Could not list machines ({(int)response.StatusCode}).");

                return 1;
            }

            var machines = await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.MachineSummaryArray) ?? [];

            if (machines.Length == 0) {
                await Console.Out.WriteLineAsync("No machines registered.");

                return 0;
            }

            var width = machines.Max(m => m.DisplayName.Length);

            foreach (var m in machines) {
                // A revoked machine is listed, not hidden: an operator needs to see that it WAS
                // revoked, and hiding it would make revocation indistinguishable from never existing.
                var status = m.Usable ? "active " : "revoked";

                await Console.Out.WriteLineAsync(
                    $"  {m.DisplayName.PadRight(width)}  {status}  {m.WorkOsClientId}  {m.ServiceId}");
            }

            return 0;
        }
        catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"Could not reach the Capacitor server: {ex.Message}");

            return 1;
        }
    }

    // ── revoke ──────────────────────────────────────────────────────────────────────────────────

    static async Task<int> RevokeAsync(string[] args) {
        if (args.Length < 3 || IsHelp(args[2])) {
            await Console.Error.WriteLineAsync("Usage: kcap machine revoke <service-id>");
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("Run `kcap machine list` to see service ids.");

            return 1;
        }

        var serviceId = args[2];

        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            using var response = await client.PostAsync($"/api/admin/machines/{Uri.EscapeDataString(serviceId)}/revoke", null);

            if (response.StatusCode is HttpStatusCode.NotFound) {
                await Console.Error.WriteLineAsync($"No machine '{serviceId}'. Run `kcap machine list`.");

                return 1;
            }

            if (!response.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync($"Revoking failed ({(int)response.StatusCode}).");

                return 1;
            }

            await Console.Out.WriteLineAsync($"Machine {serviceId} revoked.");
            await Console.Error.WriteLineAsync();

            // Says exactly what revocation does and does not do. An operator responding to a leak needs
            // to know the old token keeps working until it expires, so they can decide whether to also
            // delete the application in WorkOS — which cuts it off immediately.
            await Console.Error.WriteLineAsync(
                "It stops authenticating from its next request. A token it already holds stays valid");
            await Console.Error.WriteLineAsync(
                "until it expires (up to an hour) but is no longer honoured here. To cut it off at the");
            await Console.Error.WriteLineAsync(
                "source as well, delete the application in the WorkOS dashboard.");

            return 0;
        }
        catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"Could not reach the Capacitor server: {ex.Message}");

            return 1;
        }
    }

    // ── help ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Help comes from the embedded <c>help-machine.txt</c>, the same way every other command's does,
    /// so `kcap machine --help` and `kcap --help machine` render one text rather than two that drift.
    /// </summary>
    static async Task<int> PrintUsage() {
        await Console.Out.WriteAsync(EmbeddedResources.Load("help-machine.txt"));

        return 1;
    }

    /// <summary>
    /// `create --help` shows the same page: the flags, the once-only secret and the four-step setup
    /// all live there, and a second shorter copy here would be the one that goes stale.
    /// </summary>
    static Task<int> PrintCreateUsage() => PrintUsage();

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
