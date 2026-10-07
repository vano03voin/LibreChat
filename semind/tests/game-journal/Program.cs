using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SeMind.Assistant.Gateway;

const string steam = "76561198282450531";
const string world = "b08f6368-8e1f-422a-855e-a42b99bfe336";
const string ownerKey = "dev/" + world + "/" + steam;
if (args.Length == 2 && args[0].StartsWith("--crash-", StringComparison.Ordinal))
{
    bool crashBefore = args[0] == "--crash-before";
    using var crashJournal = new GameJournal(args[1], crashBefore ? _ => Process.GetCurrentProcess().Kill() : null);
    var crashState = crashJournal.Load();
    crashState.Tasks["in-1"].Operations["op-crash"] = new GameBridge.Receipt { Hash = "hash-crash" };
    crashState.Outputs.Add(Output("out-crash", "The operation was reserved"));
    await crashJournal.CommitAsync(crashState, CancellationToken.None);
    Process.GetCurrentProcess().Kill();
    return;
}
var directory = "D:/semind-librechat/temp/journal-checks-" + Guid.NewGuid().ToString("N");
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "journal.sqlite");
var checks = new JsonArray();
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED " + name);
    checks.Add(new JsonObject { ["check"] = name, ["passed"] = true });
}
long Scalar(string sql)
{
    using var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False");
    connection.Open();
    using var command = connection.CreateCommand(); command.CommandText = sql;
    return Convert.ToInt64(command.ExecuteScalar());
}
async Task ExpectedError(Func<Task> action, string code, string name)
{
    try { await action(); throw new Exception("Missing error " + name); }
    catch (BrokerError error) { Assert(error.Code == code, name); }
}
var journal = new GameJournal(path);
var state = journal.Load();
state.Owners[ownerKey] = new GameBridge.Owner { ConversationId = "conv-1", LastUserAt = 1000,AgentSelectionInitialized=true,SelectedAgentId="selected-agent" };
state.Tasks["in-1"] = TaskRecord("in-1", 1);
state.Tasks["in-1"].Detached=true;
state.Outputs.Add(Output("out-1", "First answer"));
await journal.CommitAsync(state, CancellationToken.None);
Assert(Scalar("SELECT COUNT(*) FROM inputs") == 1 && Scalar("SELECT COUNT(*) FROM tasks") == 1 &&
    Scalar("SELECT COUNT(*) FROM outbox") == 1, "acceptance-task-owner-outbox-one-transaction");
Assert(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN('inputs','tasks','owners','operations','outbox')") == 5 &&
    Scalar("PRAGMA user_version") == 2, "normalized-journal-schema-version-two");
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.LastRowsWritten == 0 && Scalar("SELECT COUNT(*) FROM inputs") == 1, "duplicate-state-no-row-rewrites");
try { using var competing = new GameJournal(path); throw new Exception("Expected exclusive writer"); }
catch (IOException) { Assert(true, "second-gateway-writer-rejected"); }
state.Tasks["in-1"].Hash = "different-hash";
await ExpectedError(() => journal.CommitAsync(state, CancellationToken.None), "input_id_conflict", "same-input-id-different-hash-rejected");
state.Tasks["in-1"].Hash = "hash-in-1";
state.Tasks["in-1"].Input.Text = "different text same hash";
await ExpectedError(() => journal.CommitAsync(state, CancellationToken.None), "input_id_conflict", "accepted-input-content-immutable");
state.Tasks["in-1"].Input.Text = "test input in-1";
state.Tasks["in-1"].Operations["op-1"] = new GameBridge.Receipt { Hash = "op-hash-1" };
state.Tasks["in-1"].Status = "running";
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.LastRowsWritten == 2, "operation-reservation-writes-only-dirty-task-and-operation");
journal.Dispose();
journal = new GameJournal(path);
state = journal.Load();
Assert(state.Tasks["in-1"].Status == "running" && state.Tasks["in-1"].Operations["op-1"].Result == null,
    "reserved-operation-survives-process-reconstruction-as-unknown");
Assert(state.Owners[ownerKey].AgentSelectionInitialized&&state.Owners[ownerKey].SelectedAgentId=="selected-agent"&&state.Tasks["in-1"].Detached,
    "selected-agent-and-detached-task-survive-restart");
Assert(await journal.ReservePersonalAsync(steam,"personal-1","hash-1",CancellationToken.None)==null,"personal-operation-first-reservation-new");
var personal=await journal.ReservePersonalAsync(steam,"personal-1","hash-1",CancellationToken.None);
Assert(personal is {Result:null,State:"reserved"},"personal-operation-repeat-is-unknown-and-not-reexecuted");
await ExpectedError(async()=>{await journal.ReservePersonalAsync(steam,"personal-1","hash-conflict",CancellationToken.None);},"operation_id_conflict","personal-operation-hash-conflict");
Assert(await journal.GetPersonalAsync("76561198000000002","personal-1",CancellationToken.None)==null,"personal-operation-owner-isolation");
await journal.CompletePersonalAsync(steam,"personal-1","hash-1",new JsonObject{["ok"]=true},CancellationToken.None);
personal=await journal.ReservePersonalAsync(steam,"personal-1","hash-1",CancellationToken.None);
Assert(personal is {State:"completed"}&&(bool?)personal.Result?["ok"]==true,"personal-operation-completed-result-replayed");
await ExpectedError(()=>journal.CompletePersonalAsync(steam,"personal-1","hash-1",new JsonObject{["ok"]=false},CancellationToken.None),"operation_id_conflict","personal-operation-result-immutable");
state.Tasks["in-1"].Operations["op-1"].Hash = "new-op-hash";
await ExpectedError(() => journal.CommitAsync(state, CancellationToken.None), "operation_id_conflict", "same-operation-id-different-hash-rejected");
state.Tasks["in-1"].Operations["op-1"].Hash = "op-hash-1";
state.Tasks["in-1"].Operations["op-1"].Result = new JsonObject { ["ok"] = true, ["effect"] = "once" };
await journal.CommitAsync(state, CancellationToken.None);
state.Tasks["in-1"].Operations["op-1"].Result = new JsonObject { ["ok"] = true, ["effect"] = "twice" };
await ExpectedError(() => journal.CommitAsync(state, CancellationToken.None), "operation_id_conflict", "completed-operation-result-immutable");
state = journal.Load();
state.Outputs.Single(output => output.OutputId == "out-1").Acknowledged = true;
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.LastRowsWritten == 1, "ack-updates-only-one-outbox-row");
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.LastRowsWritten == 0, "repeat-ack-idempotent");
state.Outputs.Single(output => output.OutputId == "out-1").Acknowledged = false;
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.Load().Outputs.Single(output => output.OutputId == "out-1").Acknowledged, "ack-is-monotonic-durable");
journal.Dispose();
bool inject = true;
journal = new GameJournal(path, _ => { if (inject) throw new IOException("injected disk failure"); });
state = journal.Load();
state.Tasks["in-fault"] = TaskRecord("in-fault", 2);
state.Tasks["in-1"].Operations["op-fault"] = new GameBridge.Receipt { Hash = "fault-operation" };
state.Outputs.Add(Output("out-fault", "Must roll back"));
state.Owners[ownerKey].LastFinalAt = 2000;
try { await journal.CommitAsync(state, CancellationToken.None); throw new Exception("Expected disk failure"); }
catch (IOException) { }
var durable = journal.Load();
Assert(!durable.Tasks.ContainsKey("in-fault") && !durable.Tasks["in-1"].Operations.ContainsKey("op-fault") &&
    durable.Outputs.All(output => output.OutputId != "out-fault") && durable.Owners[ownerKey].LastFinalAt == 0,
    "injected-storage-failure-rolls-back-all-normalized-records");
inject = false;
await journal.CommitAsync(state, CancellationToken.None);
Assert(journal.Load().Tasks.ContainsKey("in-fault"), "failed-commit-does-not-poison-dirty-row-tracking");
state = journal.Load();
state.Tasks["in-sequence-conflict"] = TaskRecord("in-sequence-conflict", 2);
try { await journal.CommitAsync(state, CancellationToken.None); throw new Exception("Expected source sequence conflict"); }
catch (SqliteException error) { Assert(error.SqliteErrorCode == 19, "source-run-sequence-unique-dedupe"); }
Assert(!journal.Load().Tasks.ContainsKey("in-sequence-conflict"), "source-sequence-conflict-transaction-rolled-back");
journal.Dispose();
await Crash("--crash-before");
journal = new GameJournal(path);
durable = journal.Load();
Assert(!durable.Tasks["in-1"].Operations.ContainsKey("op-crash") && durable.Outputs.All(output => output.OutputId != "out-crash"),
    "actual-process-kill-before-commit-no-partial-operation-or-reply");
journal.Dispose();
await Crash("--crash-after");
journal = new GameJournal(path);
durable = journal.Load();
Assert(durable.Tasks["in-1"].Operations["op-crash"].Result == null && durable.Outputs.Any(output => output.OutputId == "out-crash"),
    "actual-process-kill-after-commit-reservation-and-reply-survive");
Assert(durable.Outputs.Single(output => output.OutputId == "out-1").Acknowledged, "ack-survives-two-actual-process-kills");
using (var integrity = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
{
    integrity.Open();
    using var command = integrity.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
    Assert((string?)command.ExecuteScalar() == "ok", "sqlite-integrity-after-kills-and-failed-transactions");
}
journal.Dispose();
var migrationPath=Path.Combine(directory,"v1-migration.sqlite");
using(var migrationSource=new SqliteConnection("Data Source="+path+";Pooling=False"))
using(var migrationTarget=new SqliteConnection("Data Source="+migrationPath+";Pooling=False"))
{
    migrationSource.Open();migrationTarget.Open();migrationSource.BackupDatabase(migrationTarget);
    using var command=migrationTarget.CreateCommand();
    command.CommandText="ALTER TABLE owners DROP COLUMN agent_selection_initialized;ALTER TABLE owners DROP COLUMN selected_agent_id;ALTER TABLE tasks DROP COLUMN detached;DROP TABLE personal_operations;PRAGMA user_version=1";
    command.ExecuteNonQuery();
}
using(var migrated=new GameJournal(migrationPath))
{
    var migratedState=migrated.Load();
    Assert(migratedState.Tasks.Count==durable.Tasks.Count&&migratedState.Outputs.Count==durable.Outputs.Count&&
        migratedState.Tasks["in-1"].Operations.ContainsKey("op-crash"),"v1-sql-schema-upgrade-preserves-inputs-operations-and-outbox");
    migratedState.Owners[ownerKey].AgentSelectionInitialized=true;migratedState.Owners[ownerKey].SelectedAgentId="new-selected";
    migratedState.Tasks["in-1"].Detached=true;
    await migrated.CommitAsync(migratedState,CancellationToken.None);
    var flags=migrated.Load();
    Assert(flags.Owners[ownerKey].SelectedAgentId=="new-selected"&&flags.Tasks["in-1"].Detached,"v1-upgrade-new-selection-and-detached-fields-persist");
}
var result = new JsonObject { ["passed"] = checks.Count, ["checks"] = checks, ["database"] = path };
File.WriteAllText("D:/semind-librechat/logs/game-journal-checks.json", result.ToJsonString(new() { WriteIndented = true }));
Console.WriteLine(result.ToJsonString());

async Task Crash(string mode)
{
    var info = new ProcessStartInfo("C:/Program Files/dotnet/dotnet.exe") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); info.ArgumentList.Add(mode); info.ArgumentList.Add(path);
    using var process = Process.Start(info)!;
    await process.WaitForExitAsync();
    Assert(process.ExitCode != 0, mode + "-child-killed");
}
static GameBridge.TaskRecord TaskRecord(string id, long sequence) => new()
{
    TaskId = id, Hash = "hash-" + id, ConversationId = "conv-1", AcceptedAt = sequence * 1000,
    Input = new GameBridge.Input { InputId = id, ServerId = "dev", WorldId = world, SteamId = steam,
        IdentityId = "1234", SourceRunId = "source-run", SourceSequence = sequence, Text = "test input " + id }
};
static GameBridge.Output Output(string id, string text) => new()
{
    OutputId = id, TaskId = "in-1", ServerId = "dev", WorldId = world, SteamId = steam,
    SourceRunId = "source-run", SourceSequence = 1, Text = text
};
