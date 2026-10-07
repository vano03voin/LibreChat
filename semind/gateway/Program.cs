using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeMind.Assistant.Gateway;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // Request headers and bodies must never enter default diagnostics.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024 * 1024);
var keyPath = Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_GRANT_KEY_PATH")
    ?? throw new InvalidOperationException("SEMIND_ASSISTANT_GRANT_KEY_PATH is required");
var key = await DurableFiles.ReadBytesAsync(keyPath, CancellationToken.None);
if (key.Length < 32) throw new InvalidOperationException("Model grant key must have at least 32 bytes");
builder.Services.AddSingleton(new Authority(key));
builder.Services.AddSingleton<BrowsecTransport>();
builder.Services.AddSingleton<CodexUpstream>();
builder.Services.AddSingleton<GameBridge>();
builder.Services.AddHostedService(services => services.GetRequiredService<GameBridge>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (BrokerError error)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = new { code = error.Code } });
    }
    catch (Exception)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = new { code = "model_transport_failed" } });
    }
});
app.MapGet("/health", (GameBridge bridge) => Results.Ok(new { service = "semind-assistant-gateway", game = bridge.Health() }));
app.MapPost("/v1/input", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers["X-SeMind-Key"].ToString());
    var input = await JsonSerializer.DeserializeAsync<GameBridge.Input>(context.Request.Body, Wire.Json, context.RequestAborted) ?? throw new BrokerError("invalid_game_input");
    return Results.Json(await bridge.Accept(input, context.RequestAborted), Wire.Json);
});
app.MapGet("/v1/outbox", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers["X-SeMind-Key"].ToString());
    return Results.Json(await bridge.Outbox(context.Request.Query["server_id"].ToString(), context.Request.Query["world_id"].ToString(), context.RequestAborted), Wire.Json);
});
app.MapPost("/v1/outbox/{id}/ack", async (string id, HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers["X-SeMind-Key"].ToString());
    var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_game_scope");
    return Results.Json(await bridge.Ack(id, body["server_id"]?.GetValue<string>() ?? "", context.RequestAborted), Wire.Json);
});
foreach (var route in new[] { "cancel", "reset" })
{
    bool reset = route == "reset";
    app.MapPost("/v1/" + route, async (HttpContext context, GameBridge bridge) =>
    {
        bridge.Authenticate(context.Request.Headers["X-SeMind-Key"].ToString());
        var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_game_scope");
        return Results.Json(await bridge.Cancel(body, reset, context.RequestAborted), Wire.Json);
    });
}
app.MapPost("/internal/game/tool", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers.Authorization.ToString(), internalCall: true);
    var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_tool_request");
    return Results.Json(await bridge.Tool(body, context.RequestAborted), Wire.Json);
});
app.MapPost("/internal/game/session", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers.Authorization.ToString(), internalCall: true);
    var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_game_scope");
    return Results.Json(await bridge.PrepareDetached(body, context.RequestAborted), Wire.Json);
});
app.MapPost("/internal/script-library/tool", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers.Authorization.ToString(), internalCall: true);
    var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_tool_request");
    return Results.Json(await bridge.PersonalScriptTool(body, context.RequestAborted), Wire.Json);
});
app.MapPost("/internal/game/session/close", async (HttpContext context, GameBridge bridge) =>
{
    bridge.Authenticate(context.Request.Headers.Authorization.ToString(), internalCall: true);
    var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject ?? throw new BrokerError("invalid_game_scope");
    return Results.Json(await bridge.CloseDetached(body, context.RequestAborted), Wire.Json);
});
app.MapPost("/model/v1/responses", async (HttpContext context, Authority authority, CodexUpstream upstream) =>
{
    var owner = authority.Verify(context.Request.Headers.Authorization.ToString());
    var ct = context.RequestAborted;
    var request = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) as JsonObject
        ?? throw new BrokerError("invalid_model_request");
    bool streaming = request["stream"]?.GetValue<bool>() ?? false;
    request["instructions"] ??= "";
    var payload = CodexUpstream.Prepare(request, owner.UserId);
    using var response = await upstream.SendAsync(payload, await CodexUpstream.CredentialsAsync(ct), ct);
    if (!response.IsSuccessStatusCode)
    {
        await CodexUpstream.RejectionAsync(response, ct);
        throw new BrokerError("model_upstream_rejected", (int)response.StatusCode);
    }
    await using var incoming = await response.Content.ReadAsStreamAsync(ct);
    var prefix = new byte[64];
    int count = await incoming.ReadAtLeastAsync(prefix, 32, throwOnEndOfStream: false, cancellationToken: ct);
    if (!CodexUpstream.IsEventStream(response.Content.Headers.ContentType?.MediaType, prefix.AsSpan(0, count)))
        throw new BrokerError("model_content_invalid", 502);
    if (streaming)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(prefix.AsMemory(0, count), ct);
        await context.Response.Body.FlushAsync(ct);
        var buffer = new byte[16384];
        while ((count = await incoming.ReadAsync(buffer, ct)) > 0)
        {
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), ct);
            await context.Response.Body.FlushAsync(ct);
        }
        return;
    }
    // The upstream is always SSE. Auxiliary LibreChat calls also need a normal JSON response.
    using var combined = new MemoryStream();
    await combined.WriteAsync(prefix.AsMemory(0, count), ct);
    var chunk = new byte[16384];
    while ((count = await incoming.ReadAsync(chunk, ct)) > 0)
    {
        if (combined.Length + count > 32 * 1024 * 1024) throw new BrokerError("model_output_too_large", 502);
        await combined.WriteAsync(chunk.AsMemory(0, count), ct);
    }
    var events = Encoding.UTF8.GetString(combined.ToArray()).Replace("\r\n", "\n").Split("\n\n");
    foreach (var item in events)
    {
        var data = string.Join("\n", item.Split('\n').Where(line => line.StartsWith("data:")).Select(line => line[5..].TrimStart(' ')));
        if (string.IsNullOrEmpty(data) || data == "[DONE]") continue;
        var value = JsonNode.Parse(data);
        if (value?["type"]?.GetValue<string>() == "response.completed" && value["response"] is JsonObject result)
        {
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(result, ct);
            return;
        }
    }
    throw new BrokerError("model_response_incomplete", 502);
});
app.Run();
