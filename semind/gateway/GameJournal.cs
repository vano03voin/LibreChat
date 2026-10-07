using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace SeMind.Assistant.Gateway;

/// <summary>SQLite transaction journal. Accepted inputs, operation admission and outbox acknowledgements commit together.</summary>
public sealed class GameJournal : IDisposable
{
    private readonly string path;
    private readonly FileStream ownership;
    private readonly Dictionary<string, string> committed = new(StringComparer.Ordinal);
    private readonly Action<string>? fault;
    private bool disposed;
    public long LastRowsWritten { get; private set; }
    public sealed record PersonalOperation(string Hash, JsonObject? Result, string State);

    public GameJournal(string path, Action<string>? faultInjector = null)
    {
        this.path = Path.GetFullPath(path);
        fault = faultInjector;
        DurableFiles.RejectLinks(this.path);
        DurableFiles.RejectLinks(this.path + "-wal");
        DurableFiles.RejectLinks(this.path + "-shm");
        DurableFiles.RejectLinks(this.path + ".owner.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        ownership = new FileStream(this.path + ".owner.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using var connection = Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var found = Convert.ToInt32(version.ExecuteScalar());
            if (found is not (0 or 1 or 2)) throw new InvalidDataException("Unsupported game journal schema");
            using var transaction = connection.BeginTransaction(deferred: false);
            if (found == 1)
                Execute(connection, transaction, "ALTER TABLE owners ADD COLUMN agent_selection_initialized INTEGER NOT NULL DEFAULT 0; ALTER TABLE owners ADD COLUMN selected_agent_id TEXT; ALTER TABLE tasks ADD COLUMN detached INTEGER NOT NULL DEFAULT 0; UPDATE owners SET row_hash=''; UPDATE tasks SET row_hash='';");
            Execute(connection, transaction, Schema);
            transaction.Commit();
        }
        catch { ownership.Dispose(); throw; }
    }

    public GameBridge.State Load()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var state = new GameBridge.State();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = Query(connection, transaction, "SELECT * FROM owners ORDER BY owner_key"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string key = Text(reader, "owner_key");
                state.Owners.Add(key, new GameBridge.Owner
                {
                    ConversationId = Text(reader, "conversation_id"), PreviousConversationId = Nullable(reader, "previous_conversation_id"),
                    ParentMessageId = Nullable(reader, "parent_message_id"), ActiveTaskId = Nullable(reader, "active_task_id"),
                    AgentSelectionInitialized = Number(reader,"agent_selection_initialized") == 1, SelectedAgentId = Nullable(reader,"selected_agent_id"),
                    LastUserAt = Number(reader, "last_user_at"), LastFinalAt = Number(reader, "last_final_at"),
                    History = JsonNode.Parse(Text(reader, "history_json")) as JsonArray ?? throw new InvalidDataException("Invalid owner history")
                });
                committed["owners/" + key] = Text(reader, "row_hash");
            }
        using (var command = Query(connection, transaction, "SELECT t.*, i.* FROM tasks t JOIN inputs i ON i.input_id=t.input_id ORDER BY t.accepted_at, t.input_id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string key = Text(reader, "input_id");
                var input = new GameBridge.Input
                {
                    InputId = key, ServerId = Text(reader, "server_id"), WorldId = Text(reader, "world_id"), SteamId = Text(reader, "steam_id"),
                    IdentityId = Text(reader, "identity_id"), Text = Text(reader, "input_text"), Modality = Text(reader, "modality"),
                    SourceRunId = Text(reader, "source_run_id"), SourceSequence = Number(reader, "source_sequence")
                };
                state.Tasks.Add(key, new GameBridge.TaskRecord
                {
                    Input = input, TaskId = Text(reader, "task_id"), Hash = Text(reader, "input_hash"), Status = Text(reader, "status"),
                    ConversationId = Text(reader, "conversation_id"), AcceptedAt = Number(reader, "accepted_at"),
                    SteeringTaskId = Nullable(reader, "steering_task_id"),
                    Detached = Number(reader,"detached") == 1,
                    SteeringIds = JsonSerializer.Deserialize<List<string>>(Text(reader, "steering_ids_json"), Wire.Json)
                        ?? throw new InvalidDataException("Invalid steering ids")
                });
                committed["inputs/" + key] = Fingerprint(new { Input = input, Hash = Text(reader, "input_hash") });
                committed["tasks/" + key] = Text(reader, "row_hash");
            }
        using (var command = Query(connection, transaction, "SELECT * FROM operations ORDER BY input_id, operation_id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string key = Text(reader, "input_id"), operation = Text(reader, "operation_id");
                if (!state.Tasks.TryGetValue(key, out var task)) throw new InvalidDataException("Orphan operation");
                string? json = Nullable(reader, "result_json");
                task.Operations.Add(operation, new GameBridge.Receipt
                {
                    Hash = Text(reader, "operation_hash"),
                    Result = json == null ? null : JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Invalid operation result")
                });
                committed["operations/" + key + "/" + operation] = Text(reader, "row_hash");
            }
        using (var command = Query(connection, transaction, "SELECT * FROM outbox ORDER BY sequence"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                string key = Text(reader, "output_id");
                state.Outputs.Add(new GameBridge.Output
                {
                    OutputId = key, TaskId = Text(reader, "task_id"), ServerId = Text(reader, "server_id"),
                    WorldId = Text(reader, "world_id"), SteamId = Text(reader, "steam_id"), SourceRunId = Text(reader, "source_run_id"),
                    SourceSequence = Number(reader, "source_sequence"), Modality = Text(reader, "modality"),
                    Kind = Text(reader, "kind"), Text = Text(reader, "output_text"), Acknowledged = Number(reader, "acknowledged") == 1
                });
                committed["outbox/" + key] = Text(reader, "row_hash");
            }
        transaction.Commit();
        return state;
    }

    public Task CommitAsync(GameBridge.State state, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var updated = new Dictionary<string, string>(StringComparer.Ordinal);
        long written = 0;
        foreach (var (key, owner) in state.Owners)
        {
            ct.ThrowIfCancellationRequested();
            string hash = Fingerprint(owner);
            if (!Changed("owners/" + key, hash)) continue;
            written += Execute(connection, transaction,
                """
                INSERT INTO owners(owner_key,conversation_id,previous_conversation_id,parent_message_id,active_task_id,last_user_at,last_final_at,history_json,row_hash,agent_selection_initialized,selected_agent_id)
                VALUES($key,$conversation,$previous,$parent,$active,$user,$final,$history,$hash,$initialized,$selected)
                ON CONFLICT(owner_key) DO UPDATE SET conversation_id=excluded.conversation_id,previous_conversation_id=excluded.previous_conversation_id,
                parent_message_id=excluded.parent_message_id,active_task_id=excluded.active_task_id,last_user_at=excluded.last_user_at,
                last_final_at=excluded.last_final_at,history_json=excluded.history_json,row_hash=excluded.row_hash,
                agent_selection_initialized=excluded.agent_selection_initialized,selected_agent_id=excluded.selected_agent_id
                """, ("key", key), ("conversation", owner.ConversationId), ("previous", owner.PreviousConversationId),
                ("parent", owner.ParentMessageId), ("active", owner.ActiveTaskId), ("user", owner.LastUserAt), ("final", owner.LastFinalAt),
                ("history", owner.History.ToJsonString()), ("hash", hash), ("initialized",owner.AgentSelectionInitialized ? 1 : 0),("selected",owner.SelectedAgentId));
            updated["owners/" + key] = hash;
        }
        foreach (var (key, task) in state.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            if (key != task.Input.InputId) throw new InvalidDataException("Input dictionary key mismatch");
            var input = task.Input;
            string inputHash = Fingerprint(new { Input = input, task.Hash });
            if (Changed("inputs/" + key, inputHash))
            {
                int count = Execute(connection, transaction,
                    """
                    INSERT INTO inputs(input_id,input_hash,server_id,world_id,steam_id,identity_id,input_text,modality,source_run_id,source_sequence)
                    VALUES($id,$hash,$server,$world,$steam,$identity,$text,$modality,$run,$sequence)
                    ON CONFLICT(input_id) DO NOTHING
                    """, ("id", key), ("hash", task.Hash), ("server", input.ServerId), ("world", input.WorldId),
                    ("steam", input.SteamId), ("identity", input.IdentityId), ("text", input.Text), ("modality", input.Modality),
                    ("run", input.SourceRunId), ("sequence", input.SourceSequence));
                if (count == 0)
                {
                    using var existing = Query(connection, transaction, "SELECT input_hash FROM inputs WHERE input_id=$id", ("id", key));
                    if ((string?)existing.ExecuteScalar() != task.Hash || committed.TryGetValue("inputs/" + key, out var old) && old != inputHash)
                        throw new BrokerError("input_id_conflict", 409);
                }
                written += count;
                updated["inputs/" + key] = inputHash;
            }
            var row = new { task.TaskId, task.Hash, task.Status, task.ConversationId, task.AcceptedAt, task.SteeringIds, task.SteeringTaskId, task.Detached };
            string hash = Fingerprint(row);
            if (Changed("tasks/" + key, hash))
            {
                written += Execute(connection, transaction,
                    """
                    INSERT INTO tasks(input_id,task_id,status,conversation_id,accepted_at,steering_ids_json,steering_task_id,row_hash,detached)
                    VALUES($id,$task,$status,$conversation,$accepted,$steers,$steering,$hash,$detached)
                    ON CONFLICT(input_id) DO UPDATE SET status=excluded.status,conversation_id=excluded.conversation_id,
                    steering_ids_json=excluded.steering_ids_json,steering_task_id=excluded.steering_task_id,row_hash=excluded.row_hash,detached=excluded.detached
                    """, ("id", key), ("task", task.TaskId), ("status", task.Status), ("conversation", task.ConversationId),
                    ("accepted", task.AcceptedAt), ("steers", JsonSerializer.Serialize(task.SteeringIds, Wire.Json)),
                    ("steering", task.SteeringTaskId), ("hash", hash), ("detached",task.Detached ? 1 : 0));
                updated["tasks/" + key] = hash;
            }
            foreach (var (operationId, receipt) in task.Operations)
            {
                string operationKey = "operations/" + key + "/" + operationId, operationHash = Fingerprint(receipt);
                if (!Changed(operationKey, operationHash)) continue;
                int count = Execute(connection, transaction,
                    """
                    INSERT INTO operations(input_id,operation_id,operation_hash,state,result_json,row_hash)
                    VALUES($id,$operation,$hash,$state,$result,$row)
                    ON CONFLICT(input_id,operation_id) DO UPDATE SET state=excluded.state,result_json=excluded.result_json,row_hash=excluded.row_hash
                    WHERE operations.operation_hash=excluded.operation_hash AND (operations.result_json IS NULL OR operations.result_json=excluded.result_json)
                    """, ("id", key), ("operation", operationId), ("hash", receipt.Hash),
                    ("state", receipt.Result == null ? "reserved" : "completed"), ("result", receipt.Result?.ToJsonString()), ("row", operationHash));
                if (count == 0) throw new BrokerError("operation_id_conflict", 409);
                written += count;
                updated[operationKey] = operationHash;
            }
        }
        foreach (var output in state.Outputs)
        {
            ct.ThrowIfCancellationRequested();
            string key = "outbox/" + output.OutputId, hash = Fingerprint(output);
            if (!Changed(key, hash)) continue;
            written += Execute(connection, transaction,
                """
                INSERT INTO outbox(output_id,task_id,server_id,world_id,steam_id,source_run_id,source_sequence,modality,kind,output_text,acknowledged,row_hash)
                VALUES($id,$task,$server,$world,$steam,$run,$source,$modality,$kind,$text,$ack,$hash)
                ON CONFLICT(output_id) DO UPDATE SET acknowledged=MAX(outbox.acknowledged,excluded.acknowledged),row_hash=excluded.row_hash
                """, ("id", output.OutputId), ("task", output.TaskId), ("server", output.ServerId), ("world", output.WorldId),
                ("steam", output.SteamId), ("run", output.SourceRunId), ("source", output.SourceSequence), ("modality", output.Modality),
                ("kind", output.Kind), ("text", output.Text), ("ack", output.Acknowledged ? 1 : 0), ("hash", hash));
            updated[key] = hash;
        }
        ct.ThrowIfCancellationRequested();
        fault?.Invoke("before_commit");
        transaction.Commit();
        foreach (var (key, hash) in updated) committed[key] = hash;
        LastRowsWritten = written;
        return Task.CompletedTask;
    }

    private bool Changed(string key, string hash) => !committed.TryGetValue(key, out var previous) || previous != hash;
    public Task<PersonalOperation?> ReservePersonalAsync(string ownerSteamId,string operationId,string hash,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ct.ThrowIfCancellationRequested();
        using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);
        var prior=ReadPersonal(connection,transaction,ownerSteamId,operationId);
        if(prior!=null)
        {
            if(prior.Hash!=hash)throw new BrokerError("operation_id_conflict",409);
            transaction.Commit();return Task.FromResult<PersonalOperation?>(prior);
        }
        Execute(connection,transaction,"INSERT INTO personal_operations(owner_steam_id,operation_id,operation_hash,state,result_json) VALUES($owner,$operation,$hash,'reserved',NULL)",
            ("owner",ownerSteamId),("operation",operationId),("hash",hash));
        fault?.Invoke("before_commit");transaction.Commit();return Task.FromResult<PersonalOperation?>(null);
    }
    public Task<PersonalOperation?> GetPersonalAsync(string ownerSteamId,string operationId,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ct.ThrowIfCancellationRequested();
        using var connection=Open();using var transaction=connection.BeginTransaction();
        var result=ReadPersonal(connection,transaction,ownerSteamId,operationId);transaction.Commit();return Task.FromResult(result);
    }
    public Task CompletePersonalAsync(string ownerSteamId,string operationId,string hash,JsonObject result,CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed,this);ct.ThrowIfCancellationRequested();
        using var connection=Open();using var transaction=connection.BeginTransaction(deferred:false);
        int count=Execute(connection,transaction,
            "UPDATE personal_operations SET state='completed',result_json=$result WHERE owner_steam_id=$owner AND operation_id=$operation AND operation_hash=$hash AND (result_json IS NULL OR result_json=$result)",
            ("owner",ownerSteamId),("operation",operationId),("hash",hash),("result",result.ToJsonString()));
        if(count!=1)throw new BrokerError("operation_id_conflict",409);
        fault?.Invoke("before_commit");transaction.Commit();return Task.CompletedTask;
    }
    private static PersonalOperation? ReadPersonal(SqliteConnection connection,SqliteTransaction transaction,string owner,string operation)
    {
        using var command=Query(connection,transaction,"SELECT operation_hash,state,result_json FROM personal_operations WHERE owner_steam_id=$owner AND operation_id=$operation",("owner",owner),("operation",operation));
        using var reader=command.ExecuteReader();if(!reader.Read())return null;
        string? json=Nullable(reader,"result_json");
        return new PersonalOperation(Text(reader,"operation_hash"),json==null?null:JsonNode.Parse(json) as JsonObject??throw new InvalidDataException("Invalid personal operation result"),Text(reader,"state"));
    }
    private static string Fingerprint(object value) => RegistryAuth.Hash(JsonSerializer.Serialize(value, Wire.Json));
    private SqliteConnection Open()
    {
        DurableFiles.RejectLinks(path);
        DurableFiles.RejectLinks(path + "-wal");
        DurableFiles.RejectLinks(path + "-shm");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    private static SqliteCommand Query(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Key, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue("$" + key, value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Key, object? Value)[] values)
    {
        using var command = Query(connection, transaction, sql, values);
        return command.ExecuteNonQuery();
    }
    private static string Text(SqliteDataReader reader, string key) => reader.GetString(reader.GetOrdinal(key));
    private static string? Nullable(SqliteDataReader reader, string key) => reader.IsDBNull(reader.GetOrdinal(key)) ? null : Text(reader, key);
    private static long Number(SqliteDataReader reader, string key) => reader.GetInt64(reader.GetOrdinal(key));
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ownership.Dispose();
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS owners(
            owner_key TEXT PRIMARY KEY, conversation_id TEXT NOT NULL, previous_conversation_id TEXT,
            parent_message_id TEXT, active_task_id TEXT, last_user_at INTEGER NOT NULL,
            last_final_at INTEGER NOT NULL, history_json TEXT NOT NULL, row_hash TEXT NOT NULL,
            agent_selection_initialized INTEGER NOT NULL DEFAULT 0,selected_agent_id TEXT);
        CREATE TABLE IF NOT EXISTS inputs(
            input_id TEXT PRIMARY KEY, input_hash TEXT NOT NULL, server_id TEXT NOT NULL, world_id TEXT NOT NULL,
            steam_id TEXT NOT NULL, identity_id TEXT NOT NULL, input_text TEXT NOT NULL, modality TEXT NOT NULL,
            source_run_id TEXT NOT NULL, source_sequence INTEGER NOT NULL CHECK(source_sequence>0),
            UNIQUE(server_id,world_id,steam_id,source_run_id,source_sequence));
        CREATE TABLE IF NOT EXISTS tasks(
            input_id TEXT PRIMARY KEY REFERENCES inputs(input_id), task_id TEXT NOT NULL UNIQUE,
            status TEXT NOT NULL CHECK(status IN('queued','running','steered','completed','canceled','failed','outcome_unknown')),
            conversation_id TEXT NOT NULL, accepted_at INTEGER NOT NULL, steering_ids_json TEXT NOT NULL,
            steering_task_id TEXT, row_hash TEXT NOT NULL,detached INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS tasks_ready ON tasks(status,accepted_at);
        CREATE TABLE IF NOT EXISTS operations(
            input_id TEXT NOT NULL REFERENCES tasks(input_id), operation_id TEXT NOT NULL,
            operation_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('reserved','completed')),
            result_json TEXT, row_hash TEXT NOT NULL, PRIMARY KEY(input_id,operation_id));
        CREATE TABLE IF NOT EXISTS outbox(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT, output_id TEXT NOT NULL UNIQUE,
            task_id TEXT NOT NULL, server_id TEXT NOT NULL, world_id TEXT NOT NULL, steam_id TEXT NOT NULL,
            source_run_id TEXT NOT NULL, source_sequence INTEGER NOT NULL, modality TEXT NOT NULL,
            kind TEXT NOT NULL, output_text TEXT NOT NULL, acknowledged INTEGER NOT NULL CHECK(acknowledged IN(0,1)), row_hash TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS outbox_pending ON outbox(server_id,world_id,acknowledged,sequence);
        CREATE TABLE IF NOT EXISTS personal_operations(owner_steam_id TEXT NOT NULL,operation_id TEXT NOT NULL,operation_hash TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN('reserved','completed')),result_json TEXT,PRIMARY KEY(owner_steam_id,operation_id));
        PRAGMA user_version=2;
        """;
}
