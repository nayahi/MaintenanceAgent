namespace MaintenanceAgent.Models;

// OpenAI-compatible request to HF chat completions endpoint
public record HfChatRequest(
    string model,
    List<HfMessage> messages,
    int max_tokens = 800,
    float temperature = 0.3f,
    bool stream = false
);

public record HfMessage(string role, string content);

// OpenAI-compatible response from HF API
public record HfChatResponse(
    string? id,
    string? model,
    List<HfChoice>? choices
);

public record HfChoice(int index, HfMessage? message, string? finish_reason);
