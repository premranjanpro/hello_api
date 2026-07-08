using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PruvaVoice.Api.Services;

public class LiveKitTokenService
{
    public string CreateToken(string apiKey, string apiSecret, string room, string identity, bool canPublish = true, bool canSubscribe = true)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(2);
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = apiKey,
            ["sub"] = identity,
            ["nbf"] = new DateTimeOffset(now.AddSeconds(-5)).ToUnixTimeSeconds(),
            ["exp"] = new DateTimeOffset(expiresAt).ToUnixTimeSeconds(),
            ["video"] = new
            {
                roomJoin = true,
                room = room,
                canPublish = canPublish,
                canSubscribe = canSubscribe,
                canPublishData = true
            }
        };

        return CreateHs256Jwt(payload, apiSecret);
    }

    private static string CreateHs256Jwt(Dictionary<string, object?> payload, string secret)
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };

        var headerPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerPart}.{payloadPart}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
