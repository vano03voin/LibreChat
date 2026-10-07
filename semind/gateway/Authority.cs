using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SeMind.Assistant.Gateway;

public sealed record Owner(string UserId, string SteamId);

/// <summary>Only LibreChat's trusted backend can mint owner-scoped model grants.</summary>
public sealed class Authority(byte[] key)
{
    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    public Owner Verify(string authorization)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal) || authorization.Length > 4096)
            throw new BrokerError("owner_grant_required", 401);
        try
        {
            var parts = authorization[7..].Split('.');
            if (parts.Length != 3) throw new BrokerError("owner_grant_invalid", 401);
            var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
            if (!CryptographicOperations.FixedTimeEquals(expected, Decode(parts[2])))
                throw new BrokerError("owner_grant_invalid", 401);
            var header = JsonNode.Parse(Decode(parts[0]));
            var claims = JsonNode.Parse(Decode(parts[1]));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var issued = claims?["iat"]?.GetValue<long>() ?? 0;
            var expiry = claims?["exp"]?.GetValue<long>() ?? 0;
            var user = claims?["sub"]?.GetValue<string>() ?? "";
            var steam = claims?["steam_id"]?.GetValue<string>() ?? "";
            if (header?["alg"]?.GetValue<string>() != "HS256" ||
                claims?["iss"]?.GetValue<string>() != "semind-librechat" ||
                claims?["aud"]?.GetValue<string>() != "semind-model" ||
                issued > now + 5 || issued < now - 300 || expiry <= now || expiry > issued + 300 ||
                user.Length is < 1 or > 128 || steam.Length != 17 || steam.Any(c => c is < '0' or > '9'))
                throw new BrokerError("owner_grant_invalid", 401);
            return new Owner(user, steam);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new BrokerError("owner_grant_invalid", 401);
        }
    }
}
