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
            IEvalObserver     observer,
            CancellationToken ct
        ) {
        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/eval/questions", ct: ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) {
                observer.OnFailed("authentication failed — run 'kapacitor login' to re-authenticate");
                return null;
            }
            if (!resp.IsSuccessStatusCode) {
                observer.OnFailed($"failed to load eval question catalog: HTTP {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.EvalQuestionDtoArray);
            if (parsed is null || parsed.Length == 0) {
                observer.OnFailed("eval question catalog is empty");
                return null;
            }
            foreach (var q in parsed) {
                if (string.IsNullOrWhiteSpace(q.Category)
                    || string.IsNullOrWhiteSpace(q.Id)
                    || string.IsNullOrWhiteSpace(q.Prompt)) {
                    observer.OnFailed("eval question catalog contains a malformed entry (missing category, id, or prompt)");
                    return null;
                }
            }
            return parsed;
        } catch (HttpRequestException ex) {
            observer.OnFailed($"failed to load eval question catalog: {ex.Message}");
            return null;
        } catch (JsonException ex) {
            observer.OnFailed($"eval question catalog response was not valid JSON: {ex.Message}");
            return null;
        }
    }
}
