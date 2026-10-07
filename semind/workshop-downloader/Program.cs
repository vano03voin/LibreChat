using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Steamworks;

// One isolated anonymous server per invocation. No game process or user's Steam account is used.
if (args.Length != 2 || !ulong.TryParse(args[0], out var itemId) || itemId == 0)
    throw new ArgumentException("Expected item id and service-owned output root");
var outputRoot = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputRoot);
Environment.SetEnvironmentVariable("SteamAppId", "244850");
Environment.SetEnvironmentVariable("SteamGameId", "244850");
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var start = Stopwatch.StartNew();
if (!GameServer.Init(0, 0, ushort.MaxValue, EServerMode.eServerModeNoAuthentication, "1.0.0"))
    throw new InvalidOperationException("steam_gameserver_init_failed");
try
{
    SteamGameServer.SetProduct("244850");
    SteamGameServer.SetGameDescription("Space Engineers script importer");
    SteamGameServer.SetDedicatedServer(true);
    if (!SteamGameServerUGC.BInitWorkshopForGameServer((DepotId_t)244850, outputRoot))
        throw new InvalidOperationException("steam_workshop_init_failed");
    SteamGameServer.LogOnAnonymous();
    while (!SteamGameServer.BLoggedOn()) Pump();

    SteamUGCDetails_t? details = null;
    string? queryError = null;
    using (var query = CallResult<SteamUGCRequestUGCDetailsResult_t>.Create((result, failed) =>
    {
        if (failed || result.m_details.m_eResult != EResult.k_EResultOK)
            queryError = "workshop_details_" + result.m_details.m_eResult;
        else details = result.m_details;
    }))
    {
        query.Set(SteamGameServerUGC.RequestUGCDetails((PublishedFileId_t)itemId, 0));
        while (details == null && queryError == null) Pump();
    }
    if (queryError != null) throw new InvalidOperationException(queryError);
    var item = details!.Value;
    if (item.m_nPublishedFileId != (PublishedFileId_t)itemId || item.m_nConsumerAppID != (AppId_t)244850 ||
        item.m_eFileType != EWorkshopFileType.k_EWorkshopFileTypeCommunity ||
        !item.m_rgchTags.Split(',').Any(tag => string.Equals(tag.Trim(), "IngameScript", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("workshop_item_is_not_se_ingame_script");

    EResult? downloaded = null;
    using (var callback = Callback<DownloadItemResult_t>.CreateGameServer(result =>
    {
        if (result.m_nPublishedFileId == (PublishedFileId_t)itemId) downloaded = result.m_eResult;
    }))
    {
        if (!SteamGameServerUGC.DownloadItem((PublishedFileId_t)itemId, true))
            throw new InvalidOperationException("workshop_download_not_accepted");
        while (downloaded == null) Pump();
    }
    if (downloaded != EResult.k_EResultOK)
        throw new InvalidOperationException("workshop_download_" + downloaded);
    if (!SteamGameServerUGC.GetItemInstallInfo((PublishedFileId_t)itemId, out var diskBytes,
        out var installFolder, 4096, out var timestamp))
        throw new InvalidOperationException("workshop_install_info_missing");

    var source = ReadScript(installFolder);
    string text = Decode(source);
    if (text.Length is 0 or > 100000 || text.Contains('\0'))
        throw new InvalidOperationException("script_source_invalid_or_too_large");
    var sha = Convert.ToHexStringLower(SHA256.HashData(source));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        item_id = itemId.ToString(), title = item.m_rgchTitle, description = item.m_rgchDescription,
        workshop_updated_at = item.m_rtimeUpdated, downloaded_at = DateTimeOffset.UtcNow,
        source = text, source_bytes_base64 = Convert.ToBase64String(source), source_sha256 = sha,
        source_size = source.Length, workshop_disk_bytes = diskBytes, install_timestamp = timestamp,
        transport = "anonymous-steam-gameserver-ugc"
    }));
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message }));
    Environment.ExitCode = 1;
}
finally
{
    SteamGameServer.LogOff();
    GameServer.Shutdown();
}

void Pump()
{
    if (start.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("steam_workshop_timeout");
    GameServer.RunCallbacks();
    Thread.Sleep(20);
}

static string Decode(byte[] data)
{
    var text = new UTF8Encoding(false, true).GetString(data);
    return text.StartsWith('\uFEFF') ? text[1..] : text;
}

static byte[] ReadScript(string folder)
{
    // Old SE workshop uploads can be ZIP payloads, while current UGC installs are folders.
    // Only one exact Script.cs is admitted; no guessed/fabricated source or author mirrors.
    if (Directory.Exists(folder))
    {
        var candidates = Directory.EnumerateFiles(folder, "Script.cs", SearchOption.AllDirectories).ToArray();
        if (candidates.Length == 1)
        {
            if (new FileInfo(candidates[0]).Length > 400000) throw new InvalidOperationException("script_source_too_large");
            return File.ReadAllBytes(candidates[0]);
        }
        if (candidates.Length > 1) throw new InvalidOperationException("multiple_workshop_scripts");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToArray();
        foreach (var file in files)
        {
            var found = ZipScript(file);
            if (found != null) return found;
        }
    }
    else if (File.Exists(folder))
    {
        var found = ZipScript(folder);
        if (found != null) return found;
    }
    throw new InvalidOperationException("workshop_script_cs_missing");
}

static byte[]? ZipScript(string file)
{
    using var stream = File.OpenRead(file);
    Span<byte> header = stackalloc byte[4];
    if (stream.Read(header) != 4 || header[0] != (byte)'P' || header[1] != (byte)'K') return null;
    stream.Position = 0;
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var entries = archive.Entries.Where(entry => Path.GetFileName(entry.FullName) == "Script.cs").ToArray();
    if (entries.Length != 1) throw new InvalidOperationException("workshop_zip_script_ambiguous");
    if (entries[0].Length > 400000) throw new InvalidOperationException("script_source_too_large");
    using var input = entries[0].Open();
    using var output = new MemoryStream();
    input.CopyTo(output);
    return output.ToArray();
}
