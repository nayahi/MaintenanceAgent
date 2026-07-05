namespace MaintenanceAgent.Models;

// OpenAI-compatible request to HF chat completions endpoint
public record HfChatRequest(
    string model,
    List<HfMessage> messages,
    int max_tokens = 800,
    float temperature = 0.3f,
    bool stream = false,
    List<HfTool>? tools = null
);

// content is null on assistant messages that only carry tool_calls, and on tool-result
// messages content holds the tool's JSON output with tool_call_id identifying which call it answers.
public record HfMessage(string role, string? content = null, List<HfToolCall>? tool_calls = null, string? tool_call_id = null);

public record HfTool(string type, HfFunctionDef function);
public record HfFunctionDef(string name, string description, object parameters);

public record HfToolCall(string id, string type, HfFunctionCall function);
public record HfFunctionCall(string name, string arguments);

// OpenAI-compatible response from HF API
public record HfChatResponse(
    string? id,
    string? model,
    List<HfChoice>? choices
);

public record HfChoice(int index, HfMessage? message, string? finish_reason);

// Error body shape returned by the router on 4xx responses
public record HfErrorResponse(HfError? error);

public record HfError(string? message, string? type, string? param, string? code);
