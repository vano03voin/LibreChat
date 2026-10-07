using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeMind.Assistant.Gateway;

/// <summary>Steam-account scoped library. Original Workshop bytes and every version are immutable.</summary>
public sealed class WorkshopScriptLibrary
{
    private const int MaximumCharacters = 100000;
    private readonly string root;
    private readonly Func<string, CancellationToken, Task<JsonObject>> download;
    private static readonly SemaphoreSlim SteamLock = new(1, 1);
    private sealed record Version(int Number, string Sha256, int Bytes, DateTimeOffset CreatedAt, string Reason);
    private sealed record Script(string ScriptId, string OwnerSteamId, string ItemId, string Title, string Description,
        string OriginalSha256, DateTimeOffset ImportedAt, int CurrentVersion, List<Version> Versions);

    public WorkshopScriptLibrary(string? rootDirectory = null,
        Func<string, CancellationToken, Task<JsonObject>>? downloader = null)
    {
        root = Path.GetFullPath(rootDirectory ?? Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_LIBRARY_ROOT") ??
            Path.Combine(Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_STATE_ROOT") ?? "D:/semind-librechat/gateway-dev", "scripts"));
        DurableFiles.RejectLinks(root);
        Directory.CreateDirectory(root);
        download = downloader ?? DownloadAsync;
    }

    public Task<JsonObject> ResolveSourceAsync(string ownerSteamId, string scriptId, int? version, CancellationToken ct)
    {
        var args = new JsonObject { ["script_id"] = scriptId };
        if (version != null) args["version"] = version.Value;
        return ExecuteAsync(ownerSteamId, "script.library.read", args,
            static (_, _, _) => throw new InvalidOperationException("Read cannot invoke native operations"), ct);
    }

    public async Task<JsonObject> ExecuteAsync(string ownerSteamId, string name, JsonObject args,
        Func<string, JsonObject, CancellationToken, Task<JsonObject>> nativeCall, CancellationToken ct)
    {
        if (!ulong.TryParse(ownerSteamId, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner == 0 ||
            owner.ToString(CultureInfo.InvariantCulture) != ownerSteamId)
            throw new BrokerError("script_owner_invalid", 403);
        var ownerRoot = Path.Combine(root, ownerSteamId);
        DurableFiles.RejectLinks(ownerRoot);
        Directory.CreateDirectory(ownerRoot);
        var action = name.StartsWith("script.library.", StringComparison.Ordinal) ? name[15..] : name;
        if (action == "import") return await ImportAsync(ownerRoot, ownerSteamId, args, ct);
        if (action == "list")
        {
            var items = new JsonArray();
            await using var admission = await DurableFiles.LockAsync(Path.Combine(ownerRoot, "library.lock"), ct);
            foreach (var directory in Directory.EnumerateDirectories(ownerRoot).Order(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                if (!File.Exists(Path.Combine(directory, "manifest.json"))) continue; // Interrupted imports were never committed.
                var script = await LoadAsync(ownerRoot, ownerSteamId, Path.GetFileName(directory), ct);
                items.Add(Metadata(script));
            }
            return new JsonObject { ["items"] = items, ["complete"] = true };
        }
        string id = Required(args, "script_id");
        await using var scriptLock = await DurableFiles.LockAsync(Path.Combine(ownerRoot, "library.lock"), ct);
        var current = await LoadAsync(ownerRoot, ownerSteamId, id, ct);
        int version = Integer(args, "version", current.CurrentVersion);
        if (action == "versions")
        {
            var metadata = Metadata(current);
            metadata["versions"] = JsonSerializer.SerializeToNode(current.Versions, Wire.Json);
            return metadata;
        }
        if (action == "read")
        {
            var result = await ReadAsync(ownerRoot, current, version, ct);
            if (args["offset"] != null || args["limit"] != null)
            {
                string source = (string)result["source"]!;
                int offset = Integer(args, "offset", 0), limit = Integer(args, "limit", 12000);
                if (offset < 0 || offset > source.Length || limit is < 1 or > 20000)
                    throw new BrokerError("script_page_invalid");
                int count = Math.Min(limit, source.Length - offset);
                result["source"] = source.Substring(offset, count);
                result["source_offset"] = offset;
                result["source_total_characters"] = source.Length;
                result["next_offset"] = offset + count < source.Length ? offset + count : null;
                result["complete"] = offset + count == source.Length;
            }
            return result;
        }
        if (action == "compare")
        {
            var before = await ReadAsync(ownerRoot, current, Integer(args, "from_version", 1), ct);
            var after = await ReadAsync(ownerRoot, current, Integer(args, "to_version", current.CurrentVersion), ct);
            return new JsonObject { ["script_id"] = id, ["before"] = before, ["after"] = after,
                ["identical"] = (string?)before["source_sha256"] == (string?)after["source_sha256"] };
        }
        if (action is "edit" or "restore")
        {
            if (Integer(args, "expected_version", 0) != current.CurrentVersion)
                throw new BrokerError("script_version_conflict", 409);
            byte[] bytes;
            string reason;
            if (action == "restore")
            {
                bytes = await VersionBytesAsync(ownerRoot, current, Integer(args, "restore_version", 1), ct);
                reason = "restore:" + Integer(args, "restore_version", 1);
            }
            else
            {
                string source = Decode(await VersionBytesAsync(ownerRoot, current, current.CurrentVersion, ct));
                if (args["patches"] is JsonArray patches)
                {
                    if (patches.Count is 0 or > 100 || args["source"] != null) throw new BrokerError("script_patch_invalid");
                    foreach (var patch in patches)
                    {
                        if (patch is not JsonObject change) throw new BrokerError("script_patch_invalid");
                        string find = Required(change, "find"), replace = Required(change, "replace", allowEmpty: true);
                        int offset = source.IndexOf(find, StringComparison.Ordinal);
                        if (offset < 0 || source.IndexOf(find, offset + find.Length, StringComparison.Ordinal) >= 0)
                            throw new BrokerError("script_patch_must_match_exactly_once", 409);
                        source = source[..offset] + replace + source[(offset + find.Length)..];
                    }
                }
                else source = Required(args, "source");
                Validate(source);
                bytes = Encoding.UTF8.GetBytes(source);
                reason = "edit";
            }
            Validate(Decode(bytes));
            int next = current.Versions.Max(entry => entry.Number) + 1;
            // An orphan immutable file after an interrupted metadata commit is skipped, never overwritten.
            while (File.Exists(VersionPath(ownerRoot, id, next))) next++;
            await ImmutableWriteAsync(VersionPath(ownerRoot, id, next), bytes, ct);
            current.Versions.Add(new Version(next, Hash(bytes), bytes.Length, DateTimeOffset.UtcNow, reason));
            current = current with { CurrentVersion = next };
            await DurableFiles.WriteAsync(ManifestPath(ownerRoot, id), current, ct);
            return await ReadAsync(ownerRoot, current, next, ct);
        }
        // Resolve a selected immutable source inside the tenant. Native admission still validates task/world/owner.
        if (action is "check" or "run" or "deploy")
        {
            var source = Decode(await VersionBytesAsync(ownerRoot, current, version, ct));
            Validate(source);
            var request = new JsonObject { ["source"] = source };
            string operation = "virtual.check_code";
            if (action == "run")
            {
                operation = "virtual.run";
                request["grid_id"] = Required(args, "grid_id");
                request["argument"] = (string?)args["argument"] ?? "";
            }
            else if (action == "deploy")
            {
                if (Integer(args, "expected_version", 0) != current.CurrentVersion)
                    throw new BrokerError("script_version_conflict", 409);
                operation = "pb.deploy";
                request["entity_id"] = Required(args, "entity_id");
                request["read_receipt"] = Required(args, "read_receipt");
            }
            var result = await nativeCall(operation, request, ct);
            result["library"] = new JsonObject { ["script_id"] = id, ["version"] = version,
                ["source_sha256"] = Hash(Encoding.UTF8.GetBytes(source)), ["original_sha256"] = current.OriginalSha256 };
            return result;
        }
        throw new BrokerError("script_library_operation_invalid");
    }

    private async Task<JsonObject> ImportAsync(string ownerRoot, string owner, JsonObject args, CancellationToken ct)
    {
        string itemId = ItemId((string?)args["item_id"] ?? (string?)args["workshop_url"] ?? Required(args, "url"));
        var result = await download(itemId, ct);
        if ((string?)result["item_id"] != itemId || (string?)result["transport"] != "anonymous-steam-gameserver-ugc")
            throw new BrokerError("workshop_provenance_invalid", 502);
        byte[] original;
        try { original = Convert.FromBase64String(Required(result, "source_bytes_base64")); }
        catch (FormatException) { throw new BrokerError("workshop_source_invalid", 502); }
        string source = Decode(original);
        Validate(source);
        string sha = Hash(original);
        if (sha != (string?)result["source_sha256"] || source != (string?)result["source"])
            throw new BrokerError("workshop_source_hash_mismatch", 502);
        await using var admission = await DurableFiles.LockAsync(Path.Combine(ownerRoot, "library.lock"), ct);
        foreach (var directory in Directory.EnumerateDirectories(ownerRoot))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            if (!File.Exists(Path.Combine(directory, "manifest.json"))) continue;
            var existing = await LoadAsync(ownerRoot, owner, Path.GetFileName(directory), ct);
            if (existing.ItemId == itemId && existing.OriginalSha256 == sha)
            {
                var present = await ReadAsync(ownerRoot, existing, existing.CurrentVersion, ct);
                present["already_imported"] = true;
                return present;
            }
        }
        string id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var script = new Script(id, owner, itemId, (string?)result["title"] ?? "Workshop " + itemId,
            (string?)result["description"] ?? "", sha, now, 1, [new Version(1, sha, original.Length, now, "workshop-original")]);
        string directoryPath = Path.Combine(ownerRoot, id);
        Directory.CreateDirectory(directoryPath);
        await ImmutableWriteAsync(Path.Combine(directoryPath, "original.cs"), original, ct);
        await ImmutableWriteAsync(VersionPath(ownerRoot, id, 1), original, ct);
        await DurableFiles.WriteAsync(ManifestPath(ownerRoot, id), script, ct);
        return await ReadAsync(ownerRoot, script, 1, ct);
    }

    private static async Task<Script> LoadAsync(string ownerRoot, string owner, string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new BrokerError("script_unavailable", 404);
        string path = ManifestPath(ownerRoot, id);
        if (!File.Exists(path)) throw new BrokerError("script_unavailable", 404);
        var script = JsonSerializer.Deserialize<Script>(await DurableFiles.ReadBytesAsync(path, ct), Wire.Json)
            ?? throw new BrokerError("script_metadata_invalid", 503);
        if (script.OwnerSteamId != owner || script.ScriptId != id) throw new BrokerError("script_unavailable", 404);
        return script;
    }

    private static async Task<JsonObject> ReadAsync(string ownerRoot, Script script, int version, CancellationToken ct)
    {
        var bytes = await VersionBytesAsync(ownerRoot, script, version, ct);
        var result = Metadata(script);
        string source = Decode(bytes);
        result["version"] = version;
        result["source"] = source;
        result["source_sha256"] = Hash(Encoding.UTF8.GetBytes(source));
        result["source_file_sha256"] = Hash(bytes);
        result["source_size"] = bytes.Length;
        result["complete"] = true;
        return result;
    }

    private static async Task<byte[]> VersionBytesAsync(string ownerRoot, Script script, int version, CancellationToken ct)
    {
        var expected = script.Versions.SingleOrDefault(entry => entry.Number == version)
            ?? throw new BrokerError("script_version_unavailable", 404);
        var bytes = await DurableFiles.ReadBytesAsync(VersionPath(ownerRoot, script.ScriptId, version), ct);
        if (bytes.Length != expected.Bytes || Hash(bytes) != expected.Sha256)
            throw new BrokerError("script_integrity_failed", 503);
        return bytes;
    }

    private static JsonObject Metadata(Script script) => new()
    {
        ["script_id"] = script.ScriptId, ["workshop_item_id"] = script.ItemId,
        ["workshop_url"] = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + script.ItemId,
        ["title"] = script.Title, ["description"] = script.Description,
        ["original_sha256"] = script.OriginalSha256, ["current_version"] = script.CurrentVersion,
        ["original_bytes"] = script.Versions.Single(entry => entry.Number == 1).Bytes,
        ["imported_at"] = script.ImportedAt, ["transport"] = "anonymous-steam-gameserver-ugc"
    };

    private static string ManifestPath(string root, string id) => Path.Combine(root, id, "manifest.json");
    private static string VersionPath(string root, string id, int version) => Path.Combine(root, id, "v" + version + ".cs");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new BrokerError("script_encoding_invalid"); }
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }
    private static void Validate(string source)
    {
        if (source.Length is 0 or > MaximumCharacters || source.Contains('\0'))
            throw new BrokerError("script_source_invalid_or_too_large");
    }
    private static string Required(JsonObject args, string key, bool allowEmpty = false)
    {
        var value = args[key]?.GetValue<string>();
        if (value == null || !allowEmpty && value.Length == 0) throw new BrokerError("script_" + key + "_required");
        return value;
    }
    private static int Integer(JsonObject args, string key, int fallback) => args[key]?.GetValue<int>() ?? fallback;
    private static string ItemId(string input)
    {
        if (!ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/sharedfiles/filedetails/") throw new BrokerError("workshop_url_invalid");
            var entries = uri.Query.TrimStart('?').Split('&').Where(part => part.StartsWith("id=", StringComparison.Ordinal)).ToArray();
            if (entries.Length != 1 || !ulong.TryParse(entries[0][3..], NumberStyles.None, CultureInfo.InvariantCulture, out id))
                throw new BrokerError("workshop_url_invalid");
        }
        if (id == 0) throw new BrokerError("workshop_item_id_invalid");
        return id.ToString(CultureInfo.InvariantCulture);
    }
    private static async Task ImmutableWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        DurableFiles.RejectLinks(path);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16384, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await file.WriteAsync(bytes, ct);
        await file.FlushAsync(ct);
        file.Flush(flushToDisk: true);
    }

    private async Task<JsonObject> DownloadAsync(string itemId, CancellationToken ct)
    {
        var helper = Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_WORKSHOP_HELPER") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../workshop-downloader/bin/Release/net10.0/SeMind.Workshop.Downloader.dll"));
        if (!File.Exists(helper)) throw new BrokerError("workshop_downloader_unavailable", 503);
        var cache = Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_WORKSHOP_CACHE") ?? Path.Combine(root, "workshop-cache");
        DurableFiles.RejectLinks(cache);
        Directory.CreateDirectory(cache);
        await SteamLock.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(190));
            var processInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!
            };
            processInfo.ArgumentList.Add(helper);
            processInfo.ArgumentList.Add(itemId);
            processInfo.ArgumentList.Add(Path.GetFullPath(cache));
            using var process = Process.Start(processInfo) ?? throw new BrokerError("workshop_downloader_failed", 502);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (ct.IsCancellationRequested) throw;
                throw new BrokerError("workshop_download_timeout", 504);
            }
            var output = await stdout;
            var diagnostic = await stderr;
            if (output.Length > 2000000 || diagnostic.Length > 100000) throw new BrokerError("workshop_output_invalid", 502);
            if (process.ExitCode != 0)
            {
                var error = diagnostic.Split('\n').Reverse().Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith('{'));
                var code = error == null ? "workshop_download_failed" : (string?)JsonNode.Parse(error)?["error"] ?? "workshop_download_failed";
                throw new BrokerError(code, 502);
            }
            var line = output.Split('\n').Reverse().Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith('{'))
                ?? throw new BrokerError("workshop_output_invalid", 502);
            return JsonNode.Parse(line) as JsonObject ?? throw new BrokerError("workshop_output_invalid", 502);
        }
        finally { SteamLock.Release(); }
    }
}
