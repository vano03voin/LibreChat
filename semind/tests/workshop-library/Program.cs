using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeMind.Assistant.Gateway;

var testRoot = "D:/semind-librechat/temp/library-checks-" + Guid.NewGuid().ToString("N");
var results = new JsonArray();
var actualLine = File.ReadLines("D:/semind-librechat/logs/workshop-914445138.json").Last(line => line.StartsWith('{'));
var actual = JsonNode.Parse(actualLine)!.AsObject();
var library = new WorkshopScriptLibrary(testRoot, (_, _) => Task.FromResult((JsonObject)actual.DeepClone()));
const string owner = "76561198282450531", other = "76561198000000002";
var dispatched = new JsonArray();
Task<JsonObject> Native(string operation, JsonObject request, CancellationToken ct)
{
    dispatched.Add(new JsonObject { ["operation"] = operation, ["args"] = request.DeepClone() });
    return Task.FromResult(new JsonObject { ["state"] = "probe" });
}
Task<JsonObject> Call(string tenant, string operation, JsonObject request) =>
    library.ExecuteAsync(tenant, "script.library." + operation, request, Native, CancellationToken.None);
void Assert(bool state, string name)
{
    if (!state) throw new Exception("FAILED " + name);
    results.Add(new JsonObject { ["check"] = name, ["passed"] = true });
}
async Task Error(Func<Task<JsonObject>> call, string code, string name)
{
    try { await call(); throw new Exception("No expected error: " + name); }
    catch (BrokerError failure) { Assert(failure.Code == code, name); }
}

var imported = await Call(owner, "import", new() { ["workshop_url"] = "https://steamcommunity.com/sharedfiles/filedetails/?id=914445138" });
string id = (string)imported["script_id"]!;
string source = (string)imported["source"]!;
var original = Convert.FromBase64String((string)actual["source_bytes_base64"]!);
string sha = Convert.ToHexStringLower(SHA256.HashData(original));
Assert(sha == (string?)imported["original_sha256"] && original.Length == 40907, "exact-real-workshop-source-sha-and-length");
Assert(File.ReadAllBytes(Path.Combine(testRoot, owner, id, "original.cs")).SequenceEqual(original), "immutable-original-exact-bytes");
var pages = new StringBuilder();
int pageOffset = 0;
while (true)
{
    var page = await Call(owner, "read", new() { ["script_id"] = id, ["offset"] = pageOffset, ["limit"] = 8000 });
    pages.Append((string?)page["source"]);
    if ((bool?)page["complete"] == true) break;
    pageOffset = (int)page["next_offset"]!;
}
Assert(pages.ToString() == source, "paged-source-reconstructs-without-truncation");
var twice = await Call(owner, "import", new() { ["item_id"] = "914445138" });
Assert((string?)twice["script_id"] == id && (bool?)twice["already_imported"] == true, "duplicate-import-idempotent");
Assert((await Call(other, "list", new()))["items"]!.AsArray().Count == 0, "other-owner-list-empty");
await Error(() => Call(other, "read", new() { ["script_id"] = id }), "script_unavailable", "other-owner-cannot-read-id");
await Error(() => Call(other, "edit", new() { ["script_id"] = id, ["expected_version"] = 1, ["source"] = "bad" }),
    "script_unavailable", "other-owner-cannot-edit-id");
await Error(() => Call(owner, "read", new() { ["script_id"] = "../../other" }), "script_unavailable", "path-traversal-id-rejected");
await Error(() => Call(owner, "import", new() { ["url"] = "https://evil.example/?id=914445138" }), "workshop_url_invalid", "import-arbitrary-url-rejected");
await Error(() => Call(owner, "edit", new() { ["script_id"] = id, ["expected_version"] = 1, ["source"] = new string(' ', 100001) }),
    "script_source_invalid_or_too_large", "oversized-source-rejected-without-truncation");
await Error(() => Call(owner, "edit", new() { ["script_id"] = id, ["expected_version"] = 1,
    ["patches"] = new JsonArray(new JsonObject { ["find"] = "Echo", ["replace"] = "Echo" }) }),
    "script_patch_must_match_exactly_once", "ambiguous-patch-rejected");
var edited = await Call(owner, "edit", new() { ["script_id"] = id, ["expected_version"] = 1,
    ["patches"] = new JsonArray(new JsonObject { ["find"] = "string strPriorityTarget = \"none\";", ["replace"] = "string strPriorityTarget = \"Acceptance Player\";" }) });
Assert((int?)edited["version"] == 2 && ((string)edited["source"]!).Contains("Acceptance Player"), "targeted-edit-new-version");
Assert((string?)(await Call(owner, "read", new() { ["script_id"] = id, ["version"] = 1 }))["source"] == source,
    "original-version-stays-unchanged");
await Error(() => Call(owner, "edit", new() { ["script_id"] = id, ["expected_version"] = 1, ["source"] = source }),
    "script_version_conflict", "stale-edit-rejected");
var restored = await Call(owner, "restore", new() { ["script_id"] = id, ["expected_version"] = 2, ["restore_version"] = 1 });
Assert((int?)restored["version"] == 3 && (string?)restored["source_file_sha256"] == sha, "restore-appends-exact-original-version");
Assert((string?)restored["source_sha256"] == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
    "runtime-source-hash-correct-when-original-has-utf8-bom");
var compare = await Call(owner, "compare", new() { ["script_id"] = id, ["from_version"] = 1, ["to_version"] = 3 });
Assert((bool?)compare["identical"] == true, "compare-version-hashes");
var reloaded = new WorkshopScriptLibrary(testRoot, (_, _) => Task.FromResult((JsonObject)actual.DeepClone()));
Assert((string?)(await reloaded.ExecuteAsync(owner, "script.library.read", new() { ["script_id"] = id }, Native, CancellationToken.None))["source"] == source,
    "versions-survive-service-reconstruction");
await Call(owner, "check", new() { ["script_id"] = id });
await Call(owner, "run", new() { ["script_id"] = id, ["grid_id"] = "1234", ["argument"] = "HybernateOff" });
await Error(() => Call(owner, "deploy", new() { ["script_id"] = id, ["entity_id"] = "5678", ["read_receipt"] = "scoped", ["expected_version"] = 2 }),
    "script_version_conflict", "deploy-stale-version-rejected");
await Call(owner, "deploy", new() { ["script_id"] = id, ["entity_id"] = "5678", ["read_receipt"] = "scoped", ["expected_version"] = 3 });
Assert(dispatched.Count == 3 && (string?)dispatched[2]?["operation"] == "pb.deploy" &&
    (string?)dispatched[2]?["args"]?["source"] == source && (string?)dispatched[2]?["args"]?["read_receipt"] == "scoped",
    "native-check-run-deploy-preserve-source-and-read-receipt");

var editAttempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
{
    try
    {
        await Call(owner, "edit", new() { ["script_id"] = id, ["expected_version"] = 3, ["source"] = source + "\n// Concurrent " + index });
        return "edited";
    }
    catch (BrokerError failure) { return failure.Code; }
}));
Assert(editAttempts.Count(outcome => outcome == "edited") == 1 && editAttempts.Count(outcome => outcome == "script_version_conflict") == 1,
    "concurrent-edits-only-one-admitted");
Assert(File.ReadAllBytes(Path.Combine(testRoot, owner, id, "original.cs")).SequenceEqual(original), "original-unmodified-after-all-edits");
if (args.Contains("--live", StringComparer.Ordinal))
{
    Environment.SetEnvironmentVariable("SEMIND_ASSISTANT_WORKSHOP_HELPER",
        "D:/semind-librechat/LibreChat/semind/workshop-downloader/bin/Release/net10.0/SeMind.Workshop.Downloader.dll");
    var liveLibrary = new WorkshopScriptLibrary(testRoot + "-live");
    var live = await liveLibrary.ExecuteAsync(owner, "script.library.import", new() { ["item_id"] = "914445138" }, Native, CancellationToken.None);
    Assert((string?)live["original_sha256"] == sha && (int?)live["source_size"] == original.Length,
        "real-library-tool-anonymous-ugc-import-download");
}
var result = new JsonObject { ["passed"] = results.Count, ["checks"] = results, ["real_workshop_id"] = "914445138",
    ["original_sha256"] = sha, ["original_bytes"] = original.Length, ["fixture_root"] = testRoot };
File.WriteAllText("D:/semind-librechat/logs/workshop-library-checks.json", result.ToJsonString(new() { WriteIndented = true }));
Console.WriteLine(result.ToJsonString());
