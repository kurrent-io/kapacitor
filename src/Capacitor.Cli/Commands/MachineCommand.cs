using System.Net;
using System.Text;
using System.Net.Http.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;

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

    /// <summary>
    /// Takes <paramref name="baseUrl"/> because <c>CreateAuthenticatedClientAsync</c> does NOT set a
    /// BaseAddress — every other command in this CLI builds absolute URLs from the resolved server URL,
    /// and a relative URI here throws <see cref="InvalidOperationException"/> before a request is even
    /// sent. An earlier revision used relative paths and would have failed on every tenant call.
    /// Raised by Qodo. `machine` is not in Program.cs's offlineCommands, so baseUrl is non-null here.
    /// </summary>
    public static async Task<int> HandleAsync(string baseUrl, string[] args) {
        if (args.Length < 2 || IsHelp(args[1])) return await PrintUsage();

        return args[1] switch {
            "create" => await CreateAsync(baseUrl, args),
            "list"   => await ListAsync(baseUrl),
            "revoke" => await RevokeAsync(baseUrl, args),
            _        => await PrintUsage()
        };
    }

    static bool IsHelp(string arg) => arg is "--help" or "-h" or "help";

    // ── create ──────────────────────────────────────────────────────────────────────────────────

    static async Task<int> CreateAsync(string baseUrl, string[] args) {
        if (args.Length < 3 || IsHelp(args[2])) return await PrintCreateUsage();

        var name = args[2].Trim();
        // The visibility PRINTED in the setup instructions is resolved from the operator's own
        // configuration, not steered to private: an explicit flag wins, else this machine records
        // with whatever default_visibility the active profile carries (what `kcap setup` wrote),
        // else the product default a profile-less runner would use anyway.
        var profile = await AppConfig.GetActiveProfileAsync();
        var (visibility, visibilityProvenance) =
            ResolveCreateVisibility(GetArg(args, "--visibility"), profile?.DefaultVisibility);
        var role = GetArg(args, "--role");

        if (string.IsNullOrWhiteSpace(name)) {
            await Console.Error.WriteLineAsync("A machine name is required.");

            return 1;
        }

        if (!Visibilities.Contains(visibility, StringComparer.Ordinal)) {
            // Validated even though the value is never SENT anywhere: its whole job is to produce a
            // correct `kcap config set default_visibility ...` line in the output, and printing an
            // instruction that will not work is worse than refusing. The message says so, because
            // erroring on a flag with no server effect is otherwise surprising. Raised in review.
            await Console.Error.WriteLineAsync(
                $"--visibility must be one of: {string.Join(", ", Visibilities)}. "
              + "It does not configure anything here — it selects the value printed in the setup "
              + "instructions, which you then set on the machine itself.");

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
            // Deliberately does NOT say "shown when you created it" — whoever runs this may not be
            // the person who did, and telling them to go and look for a secret they never had sends
            // them somewhere that does not exist. Raised in review round 1.
            await Console.Error.WriteLineAsync(
                "Its secret cannot be retrieved. To replace it, revoke that machine and create one "
              + "with a different name.");

            return 1;
        }

        // A create that carries no secret must NEVER reach the printer.
        //
        // Console.Out.WriteLineAsync(null) writes a bare newline, and the documented idiom for this
        // command is `... 2>/dev/null | gh secret set KCAP_CLIENT_SECRET` — so a malformed response
        // would store an EMPTY secret and report success. The runner would then fail to authenticate
        // with nothing anywhere explaining why. The server already 502s this case; this is the second
        // half of the same guard, on the side that would do the damage. Raised in review round 1.
        if (string.IsNullOrEmpty(provisioned.ClientSecret)) {
            await Console.Error.WriteLineAsync(
                "The auth service reported a new machine but returned no secret. "
              + $"The WorkOS application '{name}' ({provisioned.ClientId}) exists and is unusable — "
              + "delete it in the WorkOS dashboard, then try again.");

            return 1;
        }

        // ORDER MATTERS: disclose the secret BEFORE registering.
        //
        // WorkOS discloses it exactly once. An earlier revision registered first and printed after, so
        // any registration failure — server unreachable, feature disabled, caller not an admin —
        // destroyed the secret permanently, and a retry could not recover it: the second provisioning
        // call is an idempotent hit that returns no secret at all. The operator was left with an
        // unusable application occupying the name, and no way back. Raised by Qodo.
        //
        // Printing first cannot lose anything. The worst case becomes an unregistered machine whose
        // credential the operator holds, which the failure path below tells them how to resolve.
        await PrintSecretAsync(name, provisioned.ClientSecret, provisioned);

        // The PUBLIC client id, and only that. There is no field on this request for a secret and no
        // code path on the server that could store one.
        var registered = await RegisterAsync(baseUrl, provisioned.ClientId, name, role);

        if (registered is null) {
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(
                "The credential above is valid, but this machine is NOT registered on the server, so it "
              + "cannot record yet. Once the problem above is fixed, delete the application in the "
              + "WorkOS dashboard and run `kcap machine create` again — the secret above belongs to an "
              + "application you are about to remove, so there is nothing to keep.");

            return 1;
        }

        await PrintSetupAsync(registered, provisioned, visibility, visibilityProvenance);

        return 0;
    }

    static async Task<RegisterMachineResponse?> RegisterAsync(
            string baseUrl, string clientId, string name, string? role) {
        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            // DELIBERATELY NOT PostWithRetryAsync — this is the one call here that must not auto-retry.
            //
            // Registration is not idempotent from the client's side: the server 409s a duplicate. So if
            // the server succeeds and the RESPONSE is lost, a retry returns 409 and the CLI reports
            // failure for something that worked. That is bad on its own, and worse in combination with
            // the disclosure ordering above: the operator is holding a freshly printed secret and being
            // told to go and destroy the credential. Raised in review.
            //
            // Retry buys little here anyway — it only helps for failures BEFORE the request lands, which
            // the operator can retry themselves knowing what happened. list and revoke keep the retry:
            // one is a read, the other is idempotent server-side.
            var url = $"{baseUrl}/api/admin/machines";

            // The absolute-URL guard the retry helpers would have given us, kept explicitly rather than
            // lost with them. A relative URL here is what made this whole command dead until review.
            if (!HttpClientExtensions.IsAcceptableUrl(url)) {
                await Console.Error.WriteLineAsync($"Server URL is not usable: '{baseUrl}'.");

                return null;
            }

            using var response = await client.PostAsJsonAsync(
                url, new RegisterMachineRequest(clientId, name, role),
                CapacitorJsonContext.Default.RegisterMachineRequest);

            if (response.StatusCode is HttpStatusCode.NotFound) {
                // The application EXISTS in WorkOS and is unregistered here. Saying "try again" alone
                // would send the user into the idempotent-hit wall, because the name is now taken by
                // the orphan. Both ways out are stated. Raised in review round 1.
                await Console.Error.WriteLineAsync(
                    $"This server does not have machine credentials enabled, so '{name}' "
                  + $"({clientId}) was created in WorkOS but is not registered here.");
                await Console.Error.WriteLineAsync(
                    "That name is now taken. Ask an administrator to enable the feature, then either "
                  + "delete that application in the WorkOS dashboard and reuse the name, or create a "
                  + "machine with a different one.");

                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) {
                await Console.Error.WriteLineAsync("You need to be a Capacitor administrator to register a machine.");

                return null;
            }

            // A 409 means the server already has this client id — which may be a genuine duplicate, or
            // a previous attempt of THIS command that landed and lost its response. Saying "failed"
            // flatly would send someone to delete a machine that is in fact registered and working.
            if (response.StatusCode is HttpStatusCode.Conflict) {
                await Console.Error.WriteLineAsync(
                    "The server reports this machine is already registered. That may be a genuine "
                  + "duplicate, or an earlier attempt that succeeded without the response reaching us. "
                  + "Run `kcap machine list` to check before deleting anything.");

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
    /// The irrecoverable half: the client id and the secret. Called BEFORE registration so nothing
    /// downstream can prevent disclosure.
    ///
    /// <para>The secret goes to STDOUT alone and everything else to STDERR, so
    /// <c>... 2>/dev/null | gh secret set X</c> yields exactly the value.</para>
    /// </summary>
    static async Task PrintSecretAsync(string name, string secret, CreateMachineApplicationResponse provisioned) {
        // Says only what is TRUE AT THIS POINT. Registration has not run yet and may fail, so claiming
        // "machine created" here would be a lie in exactly the case the operator most needs to trust
        // the output. `PrintSetupAsync` announces the completed machine once registration succeeds.
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"Credential issued for '{name}'.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"  Client ID     {provisioned.ClientId}");
        await Console.Error.WriteLineAsync($"  Organization  {provisioned.OrganizationId}");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  ── The secret below is shown ONCE. It is not stored anywhere ──");
        await Console.Error.WriteLineAsync("     Not by this CLI, not by Capacitor. WorkOS will not show it");
        await Console.Error.WriteLineAsync("     again. If you lose it, revoke this machine and create a new one.");
        await Console.Error.WriteLineAsync();

        await Console.Out.WriteLineAsync(secret);
    }

    /// <summary>
    /// The recoverable half: what to do with the credential. Needs the registration result, so it runs
    /// after — and a failure here costs instructions the help can repeat, not a secret.
    /// </summary>
    static async Task PrintSetupAsync(
            RegisterMachineResponse registered, CreateMachineApplicationResponse provisioned,
            string visibility, string visibilityProvenance) {
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"Machine registered as {registered.UserId}. It can now record.");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  Give the runner these environment variables:");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"    KCAP_CLIENT_ID={provisioned.ClientId}");
        await Console.Error.WriteLineAsync("    KCAP_CLIENT_SECRET=<the secret above>");
        await Console.Error.WriteLineAsync();
        // Describe, don't prescribe: the machine records with the default_visibility of the profile
        // on the machine it runs on (a machine with no profile records org_public). The value below
        // is labeled with where it came from — the flag, the operator's profile, or the product
        // default — and never introduces 'private' on this command's own authority.
        await Console.Error.WriteLineAsync(
            $"  Its sessions are visible per the profile it records with ({visibility} — {visibilityProvenance}).");
        await Console.Error.WriteLineAsync("  To confirm or change that on the machine itself:");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync($"    kcap config set default_visibility {visibility}");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync(
            "  Run that on the machine itself, in the profile it records with — visibility is the");
        await Console.Error.WriteLineAsync(
            "  machine's own setting, exactly as it is for a person.");
    }

    /// <summary>
    /// The visibility PRINTED in the setup instructions, with a label for where it came from. An
    /// explicit <c>--visibility</c> flag wins; otherwise the active profile's
    /// <c>default_visibility</c> (what <c>kcap setup</c> wrote — non-null once a profile exists);
    /// otherwise the product default <c>org_public</c>, which is what a profile-less runner records
    /// with anyway (the server treats an absent <c>default_visibility</c> as <c>org_public</c>).
    ///
    /// <para>Deliberately never invents <c>private</c>: it appears only because the flag or the
    /// operator's own profile chose it, and the provenance label then says which. Pure so it is
    /// unit-tested directly, without the profile/HTTP machinery around it.</para>
    /// </summary>
    internal static (string Value, string Provenance) ResolveCreateVisibility(
            string? flagValue, string? profileDefault) =>
        flagValue      is not null ? (flagValue,      "from --visibility")
      : profileDefault is not null ? (profileDefault, "your profile default")
      :                              ("org_public",   "product default");

    // ── list ────────────────────────────────────────────────────────────────────────────────────

    static async Task<int> ListAsync(string baseUrl) {
        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            using var response = await client.GetWithRetryAsync($"{baseUrl}/api/admin/machines");

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

    static async Task<int> RevokeAsync(string baseUrl, string[] args) {
        if (args.Length < 3 || IsHelp(args[2])) {
            await Console.Error.WriteLineAsync("Usage: kcap machine revoke <service-id>");
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync("Run `kcap machine list` to see service ids.");

            return 1;
        }

        var serviceId = args[2];

        try {
            using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync();

            // An empty StringContent would send `Content-Type: text/plain`, which a strict endpoint can
            // reject with a 415. The endpoint binds no body at all, so the content is inert either way —
            // but an inert body with the WRONG media type is a needless way to fail. Raised in review.
            //
            // Retry is safe HERE, unlike registration: revocation is idempotent server-side, so a lost
            // response followed by a retry converges on the intended state rather than a false conflict.
            using var response = await client.PostWithRetryAsync(
                $"{baseUrl}/api/admin/machines/{Uri.EscapeDataString(serviceId)}/revoke",
                new StringContent("{}", Encoding.UTF8, "application/json"));

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
