using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SeMind.Assistant.Gateway;

/// <summary>Durable accepted inputs, operation receipts and replies backed by SQLite transactions.</summary>
public sealed class GameBridge : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    private readonly Dictionary<string, CancellationTokenSource> running = new();
    private readonly List<Task> workers = new();
    private readonly string server, gameKey, internalKey, liveUrl, runtimeUrl, path;
    private readonly GameJournal? journal;
    private State state = new();
    private bool storageFailed;
    private long detachedSequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private readonly WorkshopScriptLibrary scripts = new();

    public sealed class Input
    {
        public string InputId { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string WorldId { get; set; } = "";
        public string SteamId { get; set; } = "";
        public string IdentityId { get; set; } = "";
        public string Text { get; set; } = "";
        public string Modality { get; set; } = "text";
        public string SourceRunId { get; set; } = "";
        public long SourceSequence { get; set; }
    }
    public sealed class Owner
    {
        public string ConversationId { get; set; } = Guid.NewGuid().ToString();
        public string? PreviousConversationId { get; set; }
        public string? ParentMessageId { get; set; }
        public string? ActiveTaskId { get; set; }
        public bool AgentSelectionInitialized { get; set; }
        public string? SelectedAgentId { get; set; }
        public long LastUserAt { get; set; }
        public long LastFinalAt { get; set; }
        public JsonArray History { get; set; } = new();
    }
    public sealed class TaskRecord
    {
        public Input Input { get; set; } = new();
        public string TaskId { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Status { get; set; } = "queued";
        public string ConversationId { get; set; } = "";
        public long AcceptedAt { get; set; }
        public List<string> SteeringIds { get; set; } = new();
        public string? SteeringTaskId { get; set; }
        public bool Detached { get; set; }
        public Dictionary<string, Receipt> Operations { get; set; } = new();
    }
    public sealed class Receipt
    {
        public string Hash { get; set; } = "";
        public JsonObject? Result { get; set; }
    }
    public sealed class Output
    {
        public string OutputId { get; set; } = Guid.NewGuid().ToString("N");
        public string TaskId { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string WorldId { get; set; } = "";
        public string SteamId { get; set; } = "";
        public string SourceRunId { get; set; } = "";
        public long SourceSequence { get; set; }
        public string Modality { get; set; } = "text";
        public string Kind { get; set; } = "reply";
        public string Text { get; set; } = "";
        public bool Acknowledged { get; set; }
    }
    public sealed class State
    {
        public Dictionary<string, Owner> Owners { get; set; } = new();
        public Dictionary<string, TaskRecord> Tasks { get; set; } = new();
        public List<Output> Outputs { get; set; } = new();
    }

    public GameBridge()
    {
        server = Environment.GetEnvironmentVariable("SEMIND_GAME_SERVER_ID") ?? "";
        liveUrl = Environment.GetEnvironmentVariable("SEMIND_GAME_LIVE_URL")?.TrimEnd('/') ?? "";
        runtimeUrl = Environment.GetEnvironmentVariable("SEMIND_LIBRECHAT_URL")?.TrimEnd('/') ?? "http://127.0.0.1:49390";
        internalKey = Environment.GetEnvironmentVariable("SEMIND_INTERNAL_KEY") ?? "";
        var keyPath = Environment.GetEnvironmentVariable("SEMIND_GAME_KEY_PATH");
        gameKey = keyPath == null ? "" : JsonNode.Parse(File.ReadAllText(keyPath))?["game_ai_key"]?.GetValue<string>() ?? "";
        path = Path.Combine(Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_STATE_ROOT") ?? "D:/semind-librechat/gateway-dev/game", "journal.sqlite");
        if (server.Length == 0) return; // The model transport can run without a game host.
        if (gameKey.Length < 32 || internalKey.Length < 20 || !Uri.TryCreate(liveUrl, UriKind.Absolute, out var target) || !target.IsLoopback)
            throw new InvalidOperationException("Invalid private game bridge configuration");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFiles.RejectLinks(path);
        journal = new GameJournal(path);
        state = journal.Load();
        detachedSequence = Math.Max(detachedSequence, state.Tasks.Values.Select(t => t.Input.SourceSequence).DefaultIfEmpty().Max());
        foreach (var task in state.Tasks.Values.Where(t => t.Status is "running" or "steered"))
        {
            task.Status = "outcome_unknown";
            if (!task.Detached) AddOutput(task, "Запуск прерван при восстановлении службы. Уже выполненные действия не повторяются автоматически.", "error");
        }
        foreach (var owner in state.Owners.Values) owner.ActiveTaskId = null;
        journal.CommitAsync(state, CancellationToken.None).GetAwaiter().GetResult();
    }
    private void RequireStorage() { if (server.Length == 0 || storageFailed) throw new BrokerError("game_bridge_unavailable", 503); }
    public object Health() => new { game_enabled = server.Length != 0, storage_ready = server.Length != 0 && !storageFailed, storage = "sqlite" };
    public void Authenticate(string? supplied, bool internalCall = false)
    {
        RequireStorage();
        var expected = internalCall ? "Bearer " + internalKey : gameKey;
        if (supplied == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
            throw new BrokerError("unauthorized", 401);
    }
    private static string OwnerKey(Input input) => input.ServerId + "/" + input.WorldId + "/" + input.SteamId;
    private static string Hash(object value) => RegistryAuth.Hash(JsonSerializer.Serialize(value, Wire.Json));
    private static bool Token(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':');
    private bool ActorMatches(JsonObject? scope, string world, string steam) => scope != null &&
        scope["server_id"]?.GetValue<string>() == server && scope["world_id"]?.GetValue<string>() == world && scope["steam_id"]?.GetValue<string>() == steam &&
        long.TryParse(scope["identity_id"]?.GetValue<string>(), out var identity) && identity != 0 && Token(scope["source_run_id"]?.GetValue<string>() ?? "");
    private void Validate(Input input)
    {
        if (input.ServerId != server || !Guid.TryParseExact(input.WorldId, "D", out _) || !Regex.IsMatch(input.SteamId, "^[0-9]{17}$") ||
            !long.TryParse(input.IdentityId, out var identity) || identity == 0 || !Token(input.InputId) || !Token(input.SourceRunId) || input.SourceSequence < 1 ||
            string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 16000 || input.Modality != "text")
            throw new BrokerError("invalid_game_input");
    }
    private async Task Persist(CancellationToken ct)
    {
        try { await journal!.CommitAsync(state, ct); }
        catch { storageFailed = true; throw new BrokerError("game_storage_unavailable", 503); }
    }
    private async Task<JsonObject> Post(string url, JsonObject body, bool internalCall, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add(internalCall ? "Authorization" : "X-SeMind-Key", internalCall ? "Bearer " + internalKey : gameKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new BrokerError(internalCall ? "librechat_runtime_unavailable" : "game_host_rejected", (int)response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject ?? throw new BrokerError("invalid_game_response", 502);
    }
    private static JsonObject Scope(TaskRecord task) => new()
    {
        ["server_id"] = task.Input.ServerId, ["world_id"] = task.Input.WorldId, ["steam_id"] = task.Input.SteamId,
        ["identity_id"] = task.Input.IdentityId, ["source_run_id"] = task.Input.SourceRunId, ["task_id"] = task.TaskId
    };
    public async Task<object> Accept(Input input, CancellationToken ct)
    {
        Validate(input);
        // The native host supplies the current identity/run before intake commits.
        var actor = await Post(liveUrl + "/actor", new JsonObject { ["server_id"] = input.ServerId, ["world_id"] = input.WorldId, ["steam_id"] = input.SteamId }, false, ct);
        var result = actor["result"] as JsonObject;
        if (actor["ok"]?.GetValue<bool>() != true || !ActorMatches(result, input.WorldId, input.SteamId) || result?["identity_id"]?.GetValue<string>() != input.IdentityId || result?["source_run_id"]?.GetValue<string>() != input.SourceRunId)
            throw new BrokerError("game_actor_changed", 409);
        TaskRecord task;
        TaskRecord? active = null;
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage();
            var hash = Hash(input);
            if (state.Tasks.TryGetValue(input.InputId, out task!))
            {
                if (task.Hash != hash) throw new BrokerError("input_id_conflict", 409);
                return new { accepted = true, input_id = input.InputId, task_id = task.SteeringTaskId ?? task.TaskId, conversation_id = task.ConversationId, duplicate = true };
            }
            if (state.Tasks.Values.Any(t => t.Input.ServerId == input.ServerId && t.Input.WorldId == input.WorldId && t.Input.SteamId == input.SteamId &&
                t.Input.SourceRunId == input.SourceRunId && t.Input.SourceSequence == input.SourceSequence)) throw new BrokerError("source_sequence_conflict", 409);
            if (state.Tasks.Values.Count(t => t.Status is "queued" or "running" or "steered") >= 512) throw new BrokerError("game_queue_full", 429);
            string ownerKey = OwnerKey(input);
            if (!state.Owners.TryGetValue(ownerKey, out var owner)) state.Owners[ownerKey] = owner = new Owner();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long idle = long.TryParse(Environment.GetEnvironmentVariable("SEMIND_GAME_IDLE_MINUTES"), out var configuredIdle) ? Math.Max(1, configuredIdle) * 60000 : 1200000;
            if (owner.ActiveTaskId == null && owner.LastUserAt != 0 && now - Math.Max(owner.LastUserAt, owner.LastFinalAt) > idle)
            {
                owner.PreviousConversationId = owner.ConversationId; owner.ConversationId = Guid.NewGuid().ToString(); owner.ParentMessageId = null; owner.History = new JsonArray(); owner.AgentSelectionInitialized = false; owner.SelectedAgentId = null;
            }
            owner.LastUserAt = now;
            task = new TaskRecord { Input = input, TaskId = input.InputId, Hash = hash, ConversationId = owner.ConversationId, AcceptedAt = now };
            if (owner.ActiveTaskId != null && state.Tasks.TryGetValue(owner.ActiveTaskId, out active) && active.Status == "running")
            {
                task.Status = "steered"; task.SteeringTaskId = active.TaskId; active.SteeringIds.Add(task.TaskId);
            }
            state.Tasks.Add(input.InputId, task);
            await Persist(CancellationToken.None); // A committed acceptance survives client disconnect.
        }
        finally { gate.Release(); }
        if (active != null)
        {
            // Failure leaves the durable steer queued for the next boundary/turn.
            try { await Post(runtimeUrl + "/internal/semind/game/steer", new JsonObject { ["task_id"] = active.TaskId, ["input"] = JsonSerializer.SerializeToNode(input, Wire.Json) }, true, ct); }
            catch { }
        }
        return new { accepted = true, input_id = input.InputId, task_id = task.SteeringTaskId ?? task.TaskId, conversation_id = task.ConversationId };
    }
    public async Task<object> Outbox(string serverId, string worldId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { RequireStorage(); if (serverId != server) throw new BrokerError("server_mismatch", 403); return new { items = state.Outputs.Where(o => !o.Acknowledged && o.ServerId == serverId && o.WorldId == worldId).Take(100).ToArray() }; }
        finally { gate.Release(); }
    }
    public async Task<object> Ack(string outputId, string serverId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage(); var output = state.Outputs.SingleOrDefault(o => o.OutputId == outputId);
            if (output == null || output.ServerId != serverId || serverId != server) throw new BrokerError("output_not_found", 404);
            output.Acknowledged = true; await Persist(CancellationToken.None); return new { acknowledged = true, output_id = outputId };
        }
        finally { gate.Release(); }
    }
    private void AddOutput(TaskRecord task, string text, string kind)
    {
        state.Outputs.Add(new Output { TaskId = task.SteeringTaskId ?? task.TaskId, ServerId = task.Input.ServerId, WorldId = task.Input.WorldId, SteamId = task.Input.SteamId,
            SourceRunId = task.Input.SourceRunId, SourceSequence = task.Input.SourceSequence, Modality = task.Input.Modality, Text = text, Kind = kind });
    }
    public async Task<object> Cancel(JsonObject scope, bool reset, CancellationToken ct)
    {
        string serverId = scope["server_id"]?.GetValue<string>() ?? "", world = scope["world_id"]?.GetValue<string>() ?? "", steam = scope["steam_id"]?.GetValue<string>() ?? "";
        string run = scope["source_run_id"]?.GetValue<string>() ?? "";
        long through = scope["through_source_sequence"]?.GetValue<long>() ?? 0;
        if (serverId != server || !Guid.TryParseExact(world, "D", out _) || !Regex.IsMatch(steam, "^[0-9]{17}$") || !Token(run) || through < 1) throw new BrokerError("invalid_game_scope");
        List<TaskRecord> canceled;
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage(); canceled = state.Tasks.Values.Where(t => t.Input.ServerId == serverId && t.Input.WorldId == world && t.Input.SteamId == steam && t.Input.SourceRunId == run && t.Input.SourceSequence <= through && t.Status is "queued" or "running" or "steered").ToList();
            foreach (var task in canceled) { task.Status = "canceled"; if (running.TryGetValue(task.TaskId, out var active)) active.Cancel(); }
            string key = serverId + "/" + world + "/" + steam;
            if (state.Owners.TryGetValue(key, out var owner))
            {
                if (canceled.Any(task => task.TaskId == owner.ActiveTaskId)) owner.ActiveTaskId = null;
                if (reset && owner.ActiveTaskId == null) { owner.PreviousConversationId = owner.ConversationId; owner.ConversationId = Guid.NewGuid().ToString(); owner.ParentMessageId = null; owner.History = new JsonArray(); owner.LastUserAt = owner.LastFinalAt = 0; owner.AgentSelectionInitialized = false; owner.SelectedAgentId = null; }
            }
            await Persist(CancellationToken.None);
        }
        finally { gate.Release(); }
        foreach (var task in canceled)
        {
            try { await Post(liveUrl + "/tasks/cancel", Scope(task), false, ct); } catch { }
            try { await Post(runtimeUrl + "/internal/semind/game/cancel", new JsonObject { ["task_id"] = task.TaskId }, true, ct); } catch { }
        }
        return reset ? new { reset = true } : (object)new { canceled = true };
    }
    public async Task<JsonObject> Tool(JsonObject body, CancellationToken ct)
    {
        string taskId = body["task_id"]?.GetValue<string>() ?? "", operationId = body["operation_id"]?.GetValue<string>() ?? "";
        string name = body["name"]?.GetValue<string>() ?? "";
        var args = body["args"] as JsonObject ?? throw new BrokerError("invalid_tool_arguments");
        if (!Token(operationId)) throw new BrokerError("invalid_operation_id");
        TaskRecord task; string hash = Hash(new { name, args });
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage(); if (!state.Tasks.TryGetValue(taskId, out task!) || task.Status != "running") throw new BrokerError("task_canceled", 409);
            if (task.Operations.TryGetValue(operationId, out var prior))
            {
                if (prior.Hash != hash) throw new BrokerError("operation_id_conflict", 409);
                return prior.Result?.DeepClone() as JsonObject ?? new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "outcome_unknown" } };
            }
            task.Operations[operationId] = new Receipt { Hash = hash }; await Persist(CancellationToken.None);
        }
        finally { gate.Release(); }
        JsonObject result;
        try
        {
            async Task<JsonObject> Native(string operation, JsonObject nativeArgs, CancellationToken token)
            {
                await gate.WaitAsync(token);
                try { if (task.Status != "running") throw new BrokerError("task_canceled", 409); }
                finally { gate.Release(); }
                var supplied = nativeArgs.DeepClone() as JsonObject ?? throw new BrokerError("invalid_tool_arguments");
                if (operation is "pb.check_code" or "pb.deploy" or "virtual.check_code" or "virtual.run" && supplied["script_id"] != null)
                {
                    var script = await scripts.ResolveSourceAsync(task.Input.SteamId, supplied["script_id"]!.GetValue<string>(), supplied["version"]?.GetValue<int>(), token);
                    supplied.Remove("script_id"); supplied.Remove("version"); supplied["source"] = script["source"]?.DeepClone() ?? throw new BrokerError("script_source_missing");
                }
                var request = Scope(task); request["operation_id"] = operationId; request["operation"] = operation; request["args"] = supplied;
                return await Post(liveUrl + "/live", request, false, token);
            }
            result = name.StartsWith("script.library.", StringComparison.Ordinal)
                ? await scripts.ExecuteAsync(task.Input.SteamId, name, args, Native, ct)
                : await Native(name, args, ct);
        }
        catch (BrokerError error) { result = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = error.Code } }; }
        catch { result = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "outcome_unknown" } }; }
        await gate.WaitAsync(CancellationToken.None);
        try { task.Operations[operationId].Result = result.DeepClone() as JsonObject; await Persist(CancellationToken.None); }
        finally { gate.Release(); }
        return result;
    }
    public async Task<JsonObject> PersonalScriptTool(JsonObject body, CancellationToken ct)
    {
        string steam = body["steam_id"]?.GetValue<string>() ?? "", operationId = body["operation_id"]?.GetValue<string>() ?? "";
        string action = body["action"]?.GetValue<string>() ?? "";
        var args = body["args"] as JsonObject ?? throw new BrokerError("invalid_tool_arguments");
        if (!Regex.IsMatch(steam, "^[0-9]{17}$") || !Token(operationId)) throw new BrokerError("invalid_personal_scope");
        if (action is not ("import" or "list" or "read" or "edit" or "restore" or "compare" or "versions")) throw new BrokerError("personal_library_action_rejected");
        string hash = Hash(new { action, args });
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage();
            var prior = await journal!.ReservePersonalAsync(steam, operationId, hash, CancellationToken.None);
            if (prior != null)
            {
                if (prior.Hash != hash) throw new BrokerError("operation_id_conflict", 409);
                return prior.Result?.DeepClone() as JsonObject ?? new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "outcome_unknown" } };
            }
        }
        catch (BrokerError) { throw; }
        catch { storageFailed = true; throw new BrokerError("game_storage_unavailable", 503); }
        finally { gate.Release(); }
        JsonObject result;
        try
        {
            result = await scripts.ExecuteAsync(steam, "script.library." + action, args,
                (_, _, _) => throw new BrokerError("personal_library_action_rejected"), ct);
        }
        catch (BrokerError error) { result = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = error.Code } }; }
        catch { result = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "outcome_unknown" } }; }
        await gate.WaitAsync(CancellationToken.None);
        try { await journal!.CompletePersonalAsync(steam, operationId, hash, result, CancellationToken.None); }
        catch { storageFailed = true; throw new BrokerError("game_storage_unavailable", 503); }
        finally { gate.Release(); }
        return result;
    }
    public async Task<object> PrepareDetached(JsonObject body, CancellationToken ct)
    {
        string taskId = body["task_id"]?.GetValue<string>() ?? "", world = body["world_id"]?.GetValue<string>() ?? "", steam = body["steam_id"]?.GetValue<string>() ?? "";
        if (!Token(taskId) || !Guid.TryParseExact(world, "D", out _) || !Regex.IsMatch(steam, "^[0-9]{17}$")) throw new BrokerError("invalid_game_scope");
        var actor = await Post(liveUrl + "/actor", new JsonObject { ["server_id"] = server, ["world_id"] = world, ["steam_id"] = steam }, false, ct);
        if (actor["ok"]?.GetValue<bool>() != true || actor["result"] is not JsonObject scope || !ActorMatches(scope, world, steam)) throw new BrokerError("game_actor_changed", 409);
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage();
            if (state.Tasks.TryGetValue(taskId, out var prior))
            {
                if (!prior.Detached || prior.Input.SteamId != steam || prior.Input.WorldId != world || prior.Input.SourceRunId != scope["source_run_id"]?.GetValue<string>() || prior.Status != "running") throw new BrokerError("game_session_conflict", 409);
                return new { prepared = true, task_id = taskId };
            }
            var input = new Input { InputId = taskId, ServerId = server, WorldId = world, SteamId = steam, IdentityId = scope["identity_id"]!.GetValue<string>(), SourceRunId = scope["source_run_id"]!.GetValue<string>(),
                SourceSequence = Interlocked.Increment(ref detachedSequence), Text = "Website or scheduled game execution" };
            state.Tasks.Add(taskId, new TaskRecord { TaskId = taskId, Input = input, Hash = Hash(input), Detached = true, Status = "running", AcceptedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await Persist(CancellationToken.None);
            return new { prepared = true, task_id = taskId };
        }
        finally { gate.Release(); }
    }
    public async Task<object> CloseDetached(JsonObject body, CancellationToken ct)
    {
        string taskId = body["task_id"]?.GetValue<string>() ?? "";
        bool canceled = body["canceled"]?.GetValue<bool>() ?? false;
        TaskRecord task;
        await gate.WaitAsync(ct);
        try
        {
            RequireStorage();
            if (!state.Tasks.TryGetValue(taskId, out task!) || !task.Detached) throw new BrokerError("game_session_not_found", 404);
            task.Status = canceled ? "canceled" : "completed";
            await Persist(CancellationToken.None);
        }
        finally { gate.Release(); }
        if (canceled) await Post(liveUrl + "/tasks/cancel", Scope(task), false, ct);
        return new { closed = true, task_id = taskId };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (server.Length == 0) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            TaskRecord? next = null; Owner? owner = null; CancellationTokenSource? cancel = null;
            await gate.WaitAsync(stoppingToken);
            try
            {
                if (!storageFailed && running.Count < 4)
                {
                    next = state.Tasks.Values.Where(t => t.Status == "queued" && state.Owners[OwnerKey(t.Input)].ActiveTaskId == null).OrderBy(t => t.AcceptedAt).FirstOrDefault();
                    if (next != null)
                    {
                        owner = state.Owners[OwnerKey(next.Input)]; owner.ActiveTaskId = next.TaskId; next.Status = "running";
                        cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); running[next.TaskId] = cancel;
                        AddOutput(next, "Запрос принят. Выполняю инструменты и готовлю ответ.", "progress");
                        await Persist(CancellationToken.None);
                    }
                }
            }
            finally { gate.Release(); }
            workers.RemoveAll(worker => worker.IsCompleted);
            if (next != null) workers.Add(RunTask(next, owner!, cancel!));
            await Task.Delay(200, stoppingToken);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await Task.WhenAll(workers).WaitAsync(cancellationToken);
    }
    private async Task RunTask(TaskRecord task, Owner owner, CancellationTokenSource cancel)
    {
        JsonObject? result = null; string? code = null;
        try
        {
            result = await Post(runtimeUrl + "/internal/semind/game/run", new JsonObject
            {
                ["task_id"] = task.TaskId, ["conversation_id"] = task.ConversationId, ["parent_message_id"] = owner.ParentMessageId,
                ["previous_conversation_id"] = owner.PreviousConversationId, ["history"] = owner.History.DeepClone(), ["input"] = JsonSerializer.SerializeToNode(task.Input, Wire.Json)
                ,["agent_selection_initialized"] = owner.AgentSelectionInitialized, ["selected_agent_id"] = owner.SelectedAgentId
            }, true, cancel.Token);
        }
        catch (OperationCanceledException) { code = "canceled"; }
        catch (BrokerError error) { code = error.Code; }
        catch { code = "runtime_outcome_unknown"; }
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            running.Remove(task.TaskId); cancel.Dispose();
            if (task.Status == "canceled") return;
            if (result?["ok"]?.GetValue<bool>() == true)
            {
                task.Status = "completed";
                var applied = (result["steering_ids"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet() ?? new HashSet<string?>();
                TaskRecord replyTask = task;
                foreach (var id in task.SteeringIds)
                    if (state.Tasks.TryGetValue(id, out var steer) && steer.Status == "steered")
                    {
                        if (applied.Contains(id)) { steer.Status = "completed"; replyTask = steer; }
                        else { steer.Status = "queued"; steer.SteeringTaskId = null; }
                    }
                if (owner.ConversationId == task.ConversationId)
                {
                    owner.History = result["history"]?.DeepClone() as JsonArray ?? owner.History;
                    owner.ParentMessageId = result["message_id"]?.GetValue<string>(); owner.LastFinalAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    owner.AgentSelectionInitialized = true; owner.SelectedAgentId = result["selected_agent_id"]?.GetValue<string>();
                }
                AddOutput(replyTask, result["text"]?.GetValue<string>() ?? "Готово.", "reply");
            }
            else
            {
                task.Status = code == "canceled" ? "canceled" : "failed";
                if (owner.ConversationId == task.ConversationId && result?["history"] is JsonArray partialHistory)
                {
                    owner.History = partialHistory.DeepClone().AsArray();
                    owner.ParentMessageId = task.TaskId + "-reply";
                    owner.LastFinalAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    owner.AgentSelectionInitialized = true; owner.SelectedAgentId = result["selected_agent_id"]?.GetValue<string>();
                }
                var applied = (result?["steering_ids"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet() ?? new HashSet<string?>();
                foreach (var id in task.SteeringIds)
                    if (state.Tasks.TryGetValue(id, out var steer) && steer.Status == "steered")
                    {
                        // A consumed clarification can have caused a mutation. Never replay it after an uncertain failure.
                        if (result == null || applied.Contains(id)) { steer.Status = "outcome_unknown"; AddOutput(steer, "Уточнение принято, но завершение запроса не подтверждено. Выполненные действия не повторяются автоматически.", "error"); }
                        else { steer.Status = "queued"; steer.SteeringTaskId = null; }
                    }
                if (code != "canceled") AddOutput(task, "Не удалось завершить запрос. Выполненные действия сохранены и не повторяются автоматически. Подробности доступны в истории чата.", "error");
            }
            if (owner.ActiveTaskId == task.TaskId) owner.ActiveTaskId = null;
            AddOutput(task, "", "execution_finished");
            await Persist(CancellationToken.None);
        }
        finally { gate.Release(); }
    }
    public override void Dispose()
    {
        base.Dispose();
        http.Dispose();
        journal?.Dispose();
    }
}
