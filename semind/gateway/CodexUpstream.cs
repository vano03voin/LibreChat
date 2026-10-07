using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeMind.Assistant.Gateway;

public sealed record CodexRelayConfig
{
    public string AuthPath { get; init; } = "";
    public string Model { get; init; } = "gpt-6-luna";
}
public sealed record CodexCredentials(string Access, string Account);

/// <summary>Existing host-owned Codex sign-in; never copies subscription credentials into a tenant.</summary>
public sealed class CodexUpstream(BrowsecTransport routes)
{
    public static async Task RejectionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Only API field names and known error classes may enter private diagnostics.
        // Provider messages can echo submitted content, so never log their text.
        var bytes = new byte[16384];
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        string? parameter = null, classification = null;
        string[] tokens = [];
        try
        {
            var body = JsonNode.Parse(bytes.AsSpan(0, count));
            static IEnumerable<string> Strings(JsonNode? value)
            {
                if (value is JsonValue text && text.TryGetValue<string>(out var result)) yield return result;
                else if (value is JsonObject obj) foreach (var property in obj) foreach (var leaf in Strings(property.Value)) yield return leaf;
                else if (value is JsonArray array) foreach (var item in array) foreach (var leaf in Strings(item)) yield return leaf;
            }
            var message = string.Join(" ", Strings(body));
            var param = body is JsonObject obj && obj["error"] is JsonObject detail ? detail["param"]?.ToString() ?? "" : "";
            string[] fields = ["model", "input", "instructions", "text", "tools", "strict", "reasoning", "include", "tool_choice", "parallel_tool_calls", "service_tier", "truncation", "store", "stream", "prompt_cache_key"];
            string[] vocabulary = ["required", "missing", "unsupported", "invalid", "schema", "boolean", "null", "strict", "object", "system", "developer", "input", "instructions", "text", "tools", "properties", "additionalProperties", "reasoning", "effort", "format", "model", "body", "function", "parameters", "array", "empty", "string", "content", "role"];
            tokens = vocabulary.Where(token => System.Text.RegularExpressions.Regex.IsMatch(message, @"\b" + token + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
            parameter = fields.FirstOrDefault(field => param == field || System.Text.RegularExpressions.Regex.IsMatch(message, @"\b" + field + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            if (message.Contains("context", StringComparison.OrdinalIgnoreCase) && (message.Contains("length", StringComparison.OrdinalIgnoreCase) || message.Contains("large", StringComparison.OrdinalIgnoreCase))) classification = "context_limit";
            else if (message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase) || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)) classification = "unsupported_parameter";
            else if (message.Contains("required", StringComparison.OrdinalIgnoreCase) || message.Contains("missing", StringComparison.OrdinalIgnoreCase)) classification = "missing_parameter";
            else if (message.Contains("Invalid", StringComparison.OrdinalIgnoreCase)) classification = "invalid_parameter";
            else classification = "upstream_rejected";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { classification = "unstructured_rejection"; }
        Console.Error.WriteLine(JsonSerializer.Serialize(new { diagnostic = "codex_upstream_rejected", status = (int)response.StatusCode, classification, parameter, tokens }, Wire.Json));
    }
    public static bool IsEventStream(string? contentType, ReadOnlySpan<byte> prefix)
    {
        if (contentType == "text/event-stream") return true;
        if (contentType is not null) return false;
        // The Codex subscription backend sometimes omits Content-Type for tool
        // streams. Validate the actual SSE prefix before forwarding any bytes.
        string text = Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', ' ', '\r', '\n');
        return text.StartsWith("event: response.", StringComparison.Ordinal) ||
            System.Text.RegularExpressions.Regex.IsMatch(text, "^data: \\{\\s*\"type\"\\s*:\\s*\"response\\.");
    }
    public Task ResponseFailedAsync(HttpResponseMessage response) => routes.ResponseFailedAsync(response);
    public static bool Configured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_CODEX_CONFIG"));
    public static async Task<CodexCredentials> CredentialsAsync(CancellationToken ct)
    {
        string? path = Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_CODEX_CONFIG");
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) throw new BrokerError("codex_not_configured", 503);
        try
        {
            var config = JsonSerializer.Deserialize<CodexRelayConfig>(await DurableFiles.ReadBytesAsync(path, ct), Wire.Json);
            if (config?.Model != "gpt-6-luna" || !Path.IsPathFullyQualified(config.AuthPath)) throw new BrokerError("codex_config_invalid", 503);
            // Read fresh every call; the existing Codex application owns token refresh.
            var auth = JsonNode.Parse(await DurableFiles.ReadBytesAsync(config.AuthPath, ct));
            string? token = auth?["tokens"]?["access_token"]?.GetValue<string>(), account = auth?["tokens"]?["account_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(token) || token.Length < 16 || token.Contains('\r') || token.Contains('\n') ||
                string.IsNullOrWhiteSpace(account) || account.Contains('\r') || account.Contains('\n')) throw new BrokerError("codex_auth_unavailable", 503);
            return new(token, account);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw new BrokerError("codex_auth_unavailable", 503); }
    }

    public static JsonObject Prepare(JsonObject request, string steam)
    {
        if (request["model"]?.GetValue<string>() != "gpt-6-luna") throw new BrokerError("model_not_allowed", 403);
        if (request["input"] is not JsonArray || request["instructions"] is not JsonValue)
            throw new BrokerError("codex_input_required");
        foreach (string field in new[] { "previous_response_id", "conversation", "background" })
            if (request[field] is not null) throw new BrokerError("provider_state_not_allowed", 403);
        RejectReferences(request["input"]);
        if (request["tools"] is JsonArray tools)
            foreach (var tool in tools)
                if (tool?["type"]?.GetValue<string>() != "function") throw new BrokerError("provider_tool_not_allowed", 403);
        var payload = (JsonObject)request.DeepClone();
        // LibreChat emits native system messages. The subscription Responses
        // endpoint names the same privileged instruction role "developer".
        // Keep content/order intact; tenant/user messages remain unchanged.
        foreach (var item in payload["input"]!.AsArray())
            if (item is JsonObject message && message["role"]?.GetValue<string>() == "system") message["role"] = "developer";
        payload["store"] = false; payload["stream"] = true;
        // The subscription endpoint differs from the public API. LibreChat' native
        // Responses transport owns message/tool conversion; only wire options change here.
        foreach (string field in new[] { "metadata", "user", "safety_identifier", "max_output_tokens", "temperature", "top_p",
            "prompt_cache_retention" }) payload.Remove(field);
        payload["prompt_cache_key"] = RegistryAuth.Hash(steam + "\0" + (request["prompt_cache_key"]?.ToString() ?? "default"));
        return payload;
    }
    private static void RejectReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "item_reference" || obj.ContainsKey("file_id") || obj.ContainsKey("vector_store_id"))
                throw new BrokerError("provider_resource_reference_not_allowed", 403);
            foreach (var child in obj) RejectReferences(child.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) RejectReferences(child);
    }
    public Task<HttpResponseMessage> SendAsync(JsonObject payload, CodexCredentials credentials, CancellationToken ct) => routes.SendAsync(() =>
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/codex/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Access);
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", credentials.Account);
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        request.Headers.TryAddWithoutValidation("User-Agent", "SeMind-Assistant/1.0");
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        return request;
    }, ct);

    public async Task<JsonObject> JsonAsync(string steam, string instructions, JsonObject data, CancellationToken ct)
    {
        var payload = Prepare(new JsonObject { ["model"] = "gpt-6-luna", ["instructions"] = instructions,
            ["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = data.ToJsonString() }),
            ["reasoning"] = new JsonObject { ["effort"] = "low" } }, steam);
        using var response = await SendAsync(payload, await CredentialsAsync(ct), ct);
        if (!response.IsSuccessStatusCode) throw new BrokerError("utility_model_unavailable", 503);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var text = new StringBuilder(); int received = 0; bool completed = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            received += line.Length;
            if (received > 1048576) throw new BrokerError("utility_output_too_large", 502);
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]") continue;
            var item = JsonNode.Parse(line[6..]); string? type = item?["type"]?.GetValue<string>();
            if (type == "response.output_text.delta") text.Append(item?["delta"]?.GetValue<string>());
            if (type == "response.completed") { completed = true; break; }
            if (type is "response.failed" or "error") throw new BrokerError("utility_incomplete", 502);
        }
        if (!completed) throw new BrokerError("utility_incomplete", 502);
        return JsonNode.Parse(text.ToString()) as JsonObject ?? throw new BrokerError("utility_invalid_json", 502);
    }
}
