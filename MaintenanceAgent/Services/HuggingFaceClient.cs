using System.Net.Http.Headers;
using System.Net.Http.Json;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public class HuggingFaceClient
{
    // Free-tier model with good reasoning and long-context support
    public const string DefaultModel = "Qwen/Qwen2.5-7B-Instruct";

    // HF uses an OpenAI-compatible chat completions endpoint
    private const string BaseUrl = "https://api-inference.huggingface.co/models/{0}/v1/chat/completions";

    private readonly HttpClient _http;
    private readonly string     _model;

    public HuggingFaceClient(HttpClient httpClient, string? model = null)
    {
        _http  = httpClient;
        _model = model ?? DefaultModel;
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

    public async Task<string> GetMaintenanceAdviceAsync(string scanOutput, CancellationToken ct = default)
    {
        // Truncate to ~6000 chars to stay within free-tier context limits
        var truncated = scanOutput.Length > 6000
            ? scanOutput[..6000] + "\n[...truncated for context limit...]"
            : scanOutput;

        var request = new HfChatRequest(
            model: _model,
            messages:
            [
                new HfMessage("system",
                    "You are a Windows system administrator assistant. " +
                    "Analyze system maintenance scan output and give concise, actionable advice. " +
                    "Be specific with folder names and sizes. Prioritize by impact on disk space and performance."),
                new HfMessage("user",
                    $"Based on this Windows system scan, what are the top 3 maintenance actions " +
                    $"I should take this week and why? Be specific.\n\nSCAN OUTPUT:\n{truncated}")
            ],
            max_tokens: 800,
            temperature: 0.3f
        );

        var url = string.Format(BaseUrl, Uri.EscapeDataString(_model));
        using var response = await _http.PostAsJsonAsync(url, request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var preview = body.Length > 500 ? body[..500] : body;
            throw new HttpRequestException(
                $"HF API returned {(int)response.StatusCode}: {preview}");
        }

        var result = await response.Content.ReadFromJsonAsync<HfChatResponse>(cancellationToken: ct);
        return result?.choices?.FirstOrDefault()?.message?.content
               ?? "No response received from the model.";
    }
}
