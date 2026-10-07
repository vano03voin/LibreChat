using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SeMind.Assistant.Gateway;

public sealed record BrowsecRoute(string Country, string Host, int Port)
{
    public Uri ProxyUri => new($"https://{Host}:{Port}");
}

/// <summary>Free routes from the installed Browsec extension's public catalog. TLS is never disabled.</summary>
public sealed class BrowsecTransport : IDisposable
{
    private static readonly string[] Catalogs = ["https://dikpnngjfcoep.cloudfront.net/v1/servers?stdomains=1", "https://e4.bmtr.org/api/v1/servers?stdomains=1", "https://e5.bmtr.org/api/v1/servers?stdomains=1"];
    private readonly HttpClient catalog;
    private readonly Func<BrowsecRoute, HttpClient>? factory;
    public BrowsecTransport() : this(new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }), null) { }
    public BrowsecTransport(HttpClient catalogClient, Func<BrowsecRoute, HttpClient>? routeFactory)
    { catalog = catalogClient; factory = routeFactory; }
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, HttpClient> clients = new();
    private readonly Dictionary<string, DateTimeOffset> unavailable = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpResponseMessage, BrowsecRoute> responses = new();
    private BrowsecRoute[] routes = [];
    private DateTimeOffset refreshed;
    private string? preferred;

    public static BrowsecRoute[] ParseCatalog(JsonObject data)
    {
        if (data["countries"] is not JsonObject countries) throw new BrokerError("browsec_catalog_invalid", 502);
        var result = new List<BrowsecRoute>();
        foreach (var country in countries.OrderBy(c => c.Key == "de" ? 0 : c.Key == "lt" ? 1 : c.Key == "lv" ? 2 : 3))
        {
            // premium_servers are deliberately never read or used.
            if (country.Value?["servers"] is not JsonArray free) continue;
            foreach (var item in free.OfType<JsonObject>())
            {
                string? host = item["host"]?.GetValue<string>();
                int port = item["port"]?.GetValue<int>() ?? 0;
                if (host is null || !System.Text.RegularExpressions.Regex.IsMatch(host, "^[a-z0-9][a-z0-9.-]{1,250}\\.[a-z]{2,24}$") ||
                    host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) || port is < 1 or > 65535) continue;
                result.Add(new(country.Key, host, port));
            }
        }
        return result.DistinctBy(r => r.ProxyUri.AbsoluteUri).Take(64).ToArray();
    }

    public static bool RotateStatus(int status) => status is 403 or 407 or 408 or 421 or 502 or 503 or 504;

    private async Task<BrowsecRoute[]> CandidatesAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (routes.Length == 0 || DateTimeOffset.UtcNow - refreshed > TimeSpan.FromHours(1))
            {
                foreach (string url in Catalogs)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        using var response = await catalog.GetAsync(url, deadline.Token);
                        response.EnsureSuccessStatusCode();
                        var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                        if (bytes.Length > 1048576) continue;
                        var parsed = ParseCatalog(JsonNode.Parse(bytes)!.AsObject());
                        if (parsed.Length == 0) continue;
                        routes = parsed; refreshed = DateTimeOffset.UtcNow; break;
                    }
                    catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException)
                    { ct.ThrowIfCancellationRequested(); }
                }
            }
            if (routes.Length == 0) throw new BrokerError("browsec_catalog_unavailable", 503);
            return routes.Where(r => !unavailable.TryGetValue(r.ProxyUri.AbsoluteUri, out var until) || until <= DateTimeOffset.UtcNow)
                .OrderBy(r => r.ProxyUri.AbsoluteUri == preferred ? 0 : 1).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<HttpClient> ClientAsync(BrowsecRoute route, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!clients.TryGetValue(route.ProxyUri.AbsoluteUri, out var client))
            {
                client = factory?.Invoke(route) ?? new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy(route.ProxyUri), UseProxy = true,
                    AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(6),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = Timeout.InfiniteTimeSpan };
                clients.Add(route.ProxyUri.AbsoluteUri, client);
            }
            return client;
        }
        finally { gate.Release(); }
    }

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        // At most four route attempts per inference; LibreChat owns inference/model retries.
        foreach (var route in (await CandidatesAsync(ct)).Take(4))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew(); int? status = null;
            try
            {
                using var request = createRequest();
                if (request.RequestUri?.Host != "chatgpt.com" || request.RequestUri.Scheme != "https" ||
                    !request.RequestUri.AbsolutePath.StartsWith("/backend-api/codex/", StringComparison.Ordinal))
                    throw new BrokerError("codex_endpoint_not_allowed", 403);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(18));
                var response = await (await ClientAsync(route, ct)).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                status = (int)response.StatusCode;
                bool accountForbidden = status == 403 && response.Content.Headers.ContentType?.MediaType == "application/json";
                if (!RotateStatus(status.Value) || accountForbidden)
                {
                    await gate.WaitAsync(ct);
                    try { preferred = route.ProxyUri.AbsoluteUri; unavailable.Remove(preferred); }
                    finally { gate.Release(); }
                    await LogAsync(route, status, "headers_received", watch.Elapsed);
                    responses.Add(response, route);
                    return response; // No retry after this point, including partial SSE failure.
                }
                response.Dispose();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            { ct.ThrowIfCancellationRequested(); }
            await gate.WaitAsync(ct);
            try { unavailable[route.ProxyUri.AbsoluteUri] = DateTimeOffset.UtcNow.AddMinutes(2); }
            finally { gate.Release(); }
            await LogAsync(route, status, "route_unavailable", watch.Elapsed);
        }
        throw new BrokerError("browsec_routes_unavailable", 503);
    }

    public async Task ResponseFailedAsync(HttpResponseMessage response)
    {
        if (!responses.TryGetValue(response, out var route)) return;
        await gate.WaitAsync();
        try { unavailable[route.ProxyUri.AbsoluteUri] = DateTimeOffset.UtcNow.AddMinutes(2); }
        finally { gate.Release(); }
        await LogAsync(route, (int)response.StatusCode, "stream_failed_no_replay", TimeSpan.Zero);
    }

    private static async Task LogAsync(BrowsecRoute route, int? status, string state, TimeSpan elapsed)
    {
        string? folder = Environment.GetEnvironmentVariable("SEMIND_ASSISTANT_ROUTE_LOG");
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return;
        try
        {
            DurableFiles.RejectLinks(folder); Directory.CreateDirectory(folder);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await DurableFiles.WriteAsync(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"),
                new { utc = DateTimeOffset.UtcNow, route.Country, route.Host, route.Port, status, state, elapsed_ms = elapsed.TotalMilliseconds }, timeout.Token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { Console.Error.WriteLine("LibreChat route diagnostics unavailable."); }
    }
    public void Dispose() { catalog.Dispose(); foreach (var client in clients.Values) client.Dispose(); gate.Dispose(); }
}
