using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaintenanceAgent.Models;

namespace MaintenanceAgent.Services;

public class HuggingFaceClient
{
    // Omits null fields (tools, tool_calls, tool_call_id) entirely rather than sending them as
    // JSON null, so a plain non-tool-calling request's wire shape is unchanged from before tools existed.
    private static readonly JsonSerializerOptions RequestJsonOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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

    // Bounds how many tool round-trips a single model gets before we give up on it for
    // this run -- not every provider behind the router supports tool calling equally well.
    private const int MaxToolRoundTrips = 4;

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

    // toolExecutor, if given, lets the model request deeper read-only analysis mid-conversation
    // (see Services/MaintenanceTools.cs) -- takes (toolName, argumentsJson) and returns a JSON result.
    public async Task<string> GetMaintenanceAdviceAsync(
        string scanOutput,
        string? historicalInsights = null,
        Func<string, string, string>? toolExecutor = null,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(scanOutput, historicalInsights, toolExecutor != null);

        ModelNotSupportedException? lastNotSupported = null;
        foreach (var model in CandidateModels())
        {
            try
            {
                var advice = await RunConversationAsync(model, messages, toolExecutor, ct);
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

    private static List<HfMessage> BuildMessages(string scanOutput, string? historicalInsights, bool toolsAvailable)
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

        var systemPrompt =
            "You are a Windows system administrator assistant. " +
            "Analyze system maintenance scan output and give concise, actionable advice. " +
            "Be specific with folder names and sizes. Prioritize by impact on disk space and performance. " +
            "When historical data from past runs is provided, weigh it into your prioritization.";

        if (toolsAvailable)
        {
            systemPrompt +=
                " You have optional read-only tools available for deeper analysis (full history for a " +
                "specific category, or a disk-space trend forecast) -- use them if the summary given isn't " +
                "enough to make a confident recommendation, then give your final answer as plain text.";
        }

        return
        [
            new HfMessage("system", systemPrompt),
            new HfMessage("user", userContent)
        ];
    }

    private async Task<string> RunConversationAsync(
        string model, List<HfMessage> messages, Func<string, string, string>? toolExecutor, CancellationToken ct)
    {
        // Copy so a fallback retry with the next candidate model starts from the same clean state
        var conversation = new List<HfMessage>(messages);
        var tools = toolExecutor != null ? MaintenanceTools.Definitions : null;

        for (var round = 0; round < MaxToolRoundTrips; round++)
        {
            var message = await SendOnceAsync(model, conversation, tools, ct);

            if (toolExecutor == null || message.tool_calls is not { Count: > 0 } toolCalls)
                return string.IsNullOrWhiteSpace(message.content) ? "No response received from the model." : message.content;

            conversation.Add(message);
            foreach (var call in toolCalls)
            {
                var result = toolExecutor(call.function.name, call.function.arguments);
                conversation.Add(new HfMessage("tool", result, tool_call_id: call.id));
            }
        }

        return "The AI kept requesting tool calls without giving a final recommendation -- try again.";
    }

    private async Task<HfMessage> SendOnceAsync(
        string model, List<HfMessage> messages, IReadOnlyList<HfTool>? tools, CancellationToken ct)
    {
        // Reasoning-capable models (e.g. GLM-5.2) spend a chunk of the budget on hidden/visible
        // reasoning tokens before the actual answer, and tool-calling rounds add more on top of
        // that -- 800 was truncating real responses (finish_reason: "length") well before the
        // model finished. 2000 leaves enough headroom for reasoning + tool calls + a full answer.
        var request = new HfChatRequest(model, messages, max_tokens: 2000, temperature: 0.3f, tools: tools?.ToList());
        using var response = await _http.PostAsJsonAsync(BaseUrl, request, RequestJsonOptions, ct);

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
        return result?.choices?.FirstOrDefault()?.message
               ?? new HfMessage("assistant", "No response received from the model.");
    }

    private static HfError? TryParseError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<HfErrorResponse>(body)?.error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ModelNotSupportedException(string model, string reason)
        : Exception($"Model '{model}' is not supported by any enabled provider: {reason}");
}
