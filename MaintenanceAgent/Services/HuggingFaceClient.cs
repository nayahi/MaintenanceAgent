using System.Net.Http.Headers;
using System.Net.Http.Json;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public class HuggingFaceClient
{
    // Confirmed live on 6 Inference Providers (Novita, Together, Fireworks, Featherless, DeepInfra, zai-org).
    public const string DefaultModel = "zai-org/GLM-5.2";

    // Ordered by number of live Inference Providers (widest coverage first), so that if the
    // preferred model or provider is unavailable on this account, the next one is likely to work.
    // Verify current coverage for any model with:
    //   curl "https://huggingface.co/api/models/{model}?expand[]=inferenceProviderMapping"
    public static readonly IReadOnlyList<string> FallbackModels =
    [
        "zai-org/GLM-5.2",                          // 6 providers
        "openai/gpt-oss-120b",                      // 10 providers
        "meta-llama/Llama-3.1-8B-Instruct",         // 5 providers
        "Qwen/Qwen2.5-Coder-32B-Instruct",          // 3 providers
        "Qwen/Qwen3-Coder-480B-A35B-Instruct",      // 2 providers
        "Qwen/Qwen2.5-7B-Instruct-1M",              // 1 provider (featherless-ai)
    ];

    // HF's unified Inference Providers router — OpenAI-compatible chat completions endpoint.
    // The old per-model api-inference.huggingface.co/models/{model}/... URL was retired; the
    // model is now specified in the request body instead of the URL path.
    private const string BaseUrl = "https://router.huggingface.co/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string     _preferredModel;

    // Set after a successful call so the caller can log which model actually answered.
    public string? LastUsedModel { get; private set; }

    public HuggingFaceClient(HttpClient httpClient, string? model = null)
    {
        _http           = httpClient;
        _preferredModel = model ?? DefaultModel;
    }

    public static HuggingFaceClient Create(string apiKey, string? model = null)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return new HuggingFaceClient(http, model);
    }

    public async Task<string> GetMaintenanceAdviceAsync(
        string scanOutput, string? historicalInsights = null, CancellationToken ct = default)
    {
        var messages = BuildMessages(scanOutput, historicalInsights);

        ModelNotSupportedException? lastNotSupported = null;
        foreach (var model in CandidateModels())
        {
            try
            {
                var advice = await RequestChatCompletionAsync(model, messages, ct);
                LastUsedModel = model;
                return advice;
            }
            catch (ModelNotSupportedException ex)
            {
                lastNotSupported = ex;
            }
        }

        throw new HttpRequestException(
            $"None of the candidate models are supported by any Inference Provider enabled on this account " +
            $"(tried: {string.Join(", ", CandidateModels())}). " +
            $"Enable more providers at https://huggingface.co/settings/inference-providers. " +
            $"Last error: {lastNotSupported?.Message}");
    }

    // Preferred model first (HF_MODEL override or DefaultModel), then the built-in fallbacks, de-duplicated.
    private IEnumerable<string> CandidateModels()
    {
        yield return _preferredModel;
        foreach (var model in FallbackModels)
        {
            if (!string.Equals(model, _preferredModel, StringComparison.OrdinalIgnoreCase))
                yield return model;
        }
    }

    private static List<HfMessage> BuildMessages(string scanOutput, string? historicalInsights)
    {
        // Truncate to ~6000 chars to stay within free-tier context limits
        var truncated = scanOutput.Length > 6000
            ? scanOutput[..6000] + "\n[...truncated for context limit...]"
            : scanOutput;

        var userContent =
            $"Based on this Windows system scan, what are the top 3 maintenance actions " +
            $"I should take this week and why? Be specific.\n\nSCAN OUTPUT:\n{truncated}";

        if (!string.IsNullOrWhiteSpace(historicalInsights))
        {
            userContent +=
                $"\n\n{historicalInsights}\n\n" +
                "Use this historical data to prioritize categories that have reliably freed significant " +
                "space in past runs, and call out any category you keep seeing recommended that's never " +
                "actually been cleaned.";
        }

        return
        [
            new HfMessage("system",
                "You are a Windows system administrator assistant. " +
                "Analyze system maintenance scan output and give concise, actionable advice. " +
                "Be specific with folder names and sizes. Prioritize by impact on disk space and performance. " +
                "When historical data from past runs is provided, weigh it into your prioritization."),
            new HfMessage("user", userContent)
        ];
    }

    private async Task<string> RequestChatCompletionAsync(string model, List<HfMessage> messages, CancellationToken ct)
    {
        var request = new HfChatRequest(model, messages, max_tokens: 800, temperature: 0.3f);
        using var response = await _http.PostAsJsonAsync(BaseUrl, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                var error = TryParseError(body);
                if (error?.code == "model_not_supported")
                    throw new ModelNotSupportedException(model, error.message ?? body);
            }

            var preview = body.Length > 500 ? body[..500] : body;
            throw new HttpRequestException($"HF API returned {(int)response.StatusCode}: {preview}");
        }

        var result = await response.Content.ReadFromJsonAsync<HfChatResponse>(cancellationToken: ct);
        return result?.choices?.FirstOrDefault()?.message?.content
               ?? "No response received from the model.";
    }

    private static HfError? TryParseError(string body)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<HfErrorResponse>(body)?.error;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed class ModelNotSupportedException(string model, string reason)
        : Exception($"Model '{model}' is not supported by any enabled provider: {reason}");
}
