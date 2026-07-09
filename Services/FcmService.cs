using System;
using System.Collections.Generic;
using System.Data;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dapper;

namespace PruvaVoice.Api.Services;

public class FcmService
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _accessToken;
    private static DateTime _accessTokenExpiresAtUtc;

    public async Task SendIncomingCallAsync(IDbConnection db, string fcmToken, string callerUsername, Guid callId)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) return;

        try
        {
            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='fcm' AND is_active=true");
            if (string.IsNullOrWhiteSpace(credJson)) return;

            FirebaseServiceAccount? serviceAccount = null;
            try
            {
                serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(credJson);
            }
            catch {}

            if (serviceAccount == null || string.IsNullOrWhiteSpace(serviceAccount.ProjectId))
            {
                try
                {
                    using var doc = JsonDocument.Parse(credJson);
                    if (doc.RootElement.TryGetProperty("serviceAccountJsonBase64", out var base64Prop))
                    {
                        var base64Str = base64Prop.GetString();
                        if (!string.IsNullOrWhiteSpace(base64Str))
                        {
                            var serviceAccountJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64Str));
                            serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(serviceAccountJson);
                        }
                    }
                }
                catch {}
            }

            if (serviceAccount == null || string.IsNullOrWhiteSpace(serviceAccount.ProjectId)) return;

            var accessToken = await GetAccessTokenAsync(serviceAccount);
            using var client = new HttpClient();
            var url = $"https://fcm.googleapis.com/v1/projects/{serviceAccount.ProjectId}/messages:send";

            var data = new Dictionary<string, string>
            {
                ["type"] = "incoming_call",
                ["action"] = "CALL_INCOMING",
                ["event"] = "call_started",
                ["callId"] = callId.ToString(),
                ["roomName"] = $"call_{callId:N}",
                ["callerName"] = callerUsername
            };

            var payload = new
            {
                message = new
                {
                    token = fcmToken,
                    data = data,
                    notification = new
                    {
                        title = "📞 Incoming Voice Call",
                        body = $"{callerUsername} is calling you right now!"
                    },
                    android = new
                    {
                        priority = "HIGH",
                        notification = new
                        {
                            channel_id = "incoming_calls",
                            sound = "default",
                            click_action = "CALL_INCOMING",
                            icon = "@mipmap/ic_launcher",
                            color = "#7C3AED",
                            tag = callId.ToString(),
                            sticky = false,
                            default_vibrate_timings = true,
                            default_sound = true,
                            notification_priority = "PRIORITY_MAX",
                            visibility = "PUBLIC"
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[FCM] Error sending push: {response.StatusCode} - {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FCM] Exception sending push: {ex.Message}");
        }
    }

    private async Task<string> GetAccessTokenAsync(FirebaseServiceAccount serviceAccount)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            return _accessToken;
        }

        await TokenLock.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(5))
            {
                return _accessToken;
            }

            var now = DateTimeOffset.UtcNow;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(serviceAccount.PrivateKey.Replace("\\n", "\n"));

            var header = new { alg = "RS256", typ = "JWT" };
            var claimSet = new
            {
                iss = serviceAccount.ClientEmail,
                scope = "https://www.googleapis.com/auth/firebase.messaging",
                aud = serviceAccount.TokenUri,
                exp = now.AddMinutes(55).ToUnixTimeSeconds(),
                iat = now.ToUnixTimeSeconds()
            };

            var headerPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
            var claimSetPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claimSet));
            var signingInput = $"{headerPart}.{claimSetPart}";

            var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var assertion = $"{signingInput}.{Base64UrlEncode(signature)}";

            using var client = new HttpClient();
            using var response = await client.PostAsync(
                serviceAccount.TokenUri,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                }));

            var body = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(body);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            _accessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);
            return _accessToken!;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async Task<(bool success, string log)> SendNotificationAsync(IDbConnection db, string fcmToken, string title, string body, string? imageUrl, Dictionary<string, string>? data)
    {
        if (string.IsNullOrWhiteSpace(fcmToken)) 
            return (false, "FCM Token is empty");

        try
        {
            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='fcm' AND is_active=true");
            if (string.IsNullOrWhiteSpace(credJson)) 
                return (false, "No active FCM configuration found in integration credentials table");

            FirebaseServiceAccount? serviceAccount = null;
            try
            {
                serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(credJson);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to deserialize JSON credential: {ex.Message}");
            }

            if (serviceAccount == null || string.IsNullOrWhiteSpace(serviceAccount.ProjectId))
            {
                try
                {
                    using var doc = JsonDocument.Parse(credJson);
                    if (doc.RootElement.TryGetProperty("serviceAccountJsonBase64", out var base64Prop))
                    {
                        var base64Str = base64Prop.GetString();
                        if (!string.IsNullOrWhiteSpace(base64Str))
                        {
                            var serviceAccountJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64Str));
                            serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(serviceAccountJson);
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to parse base64 service account JSON: {ex.Message}");
                }
            }

            if (serviceAccount == null || string.IsNullOrWhiteSpace(serviceAccount.ProjectId)) 
                return (false, "FCM Project ID is empty or invalid in credentials config");

            var accessToken = await GetAccessTokenAsync(serviceAccount);
            using var client = new HttpClient();
            var url = $"https://fcm.googleapis.com/v1/projects/{serviceAccount.ProjectId}/messages:send";

            var notificationData = data ?? new Dictionary<string, string>();
            if (!notificationData.ContainsKey("type"))
            {
                notificationData["type"] = "admin_broadcast";
            }

            var messageObj = new Dictionary<string, object>
            {
                ["notification"] = new
                {
                    title = title,
                    body = body,
                    image = imageUrl
                },
                ["data"] = notificationData,
                ["android"] = new
                {
                    priority = "HIGH",
                    notification = new
                    {
                        image = imageUrl
                    }
                },
                ["apns"] = new
                {
                    payload = new
                    {
                        aps = new
                        {
                            mutableContent = 1
                        }
                    },
                    fcmOptions = new
                    {
                        image = imageUrl
                    }
                }
            };

            if (fcmToken.StartsWith("/topics/") || fcmToken.StartsWith("topic:"))
            {
                var topicName = fcmToken.Replace("/topics/", "").Replace("topic:", "");
                messageObj["topic"] = topicName;
            }
            else
            {
                messageObj["token"] = fcmToken;
            }

            var payload = new { message = messageObj };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[FCM Admin] Error sending push: {response.StatusCode} - {responseBody}");
                return (false, $"FCM server error {response.StatusCode}: {responseBody}");
            }
            return (true, $"FCM send success: {responseBody}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FCM Admin] Exception sending push: {ex.Message}");
            return (false, $"Exception during send: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private class FirebaseServiceAccount
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = "";
        [JsonPropertyName("private_key")]
        public string PrivateKey { get; set; } = "";
        [JsonPropertyName("client_email")]
        public string ClientEmail { get; set; } = "";
        [JsonPropertyName("token_uri")]
        public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";
    }
}
