using System.Text.Json;

namespace kapacitor.Eval;

/// <summary>
/// Fetches the eval question taxonomy from the server's
/// <c>GET /api/eval/questions</c> endpoint. Returns <c>null</c> on HTTP
/// failure or deserialization error so callers can abort the eval with a
/// specific observer error — the catalog is non-optional for a run, so
/// there is no safe fallback.
/// </summary>
internal static class EvalQuestionCatalogClient {
    public static async Task<EvalQuestionDto[]?> FetchAsync(
            string            baseUrl,
            HttpClient        httpClient,
            CancellationToken ct
        ) {
        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/eval/questions", ct: ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.EvalQuestionDtoArray);
        } catch (HttpRequestException) {
            return null;
        } catch (JsonException) {
            return null;
        }
    }
}
