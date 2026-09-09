using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class WebhookEndpoints
{
    private static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void MapWebhookManagementEndpoints(this WebApplication app)
    {
        // 1. List Webhook Subscriptions
        app.MapGet("/api/webhooks/subscriptions", async (IDbConnection db) =>
        {
            var subs = await db.QueryAsync<dynamic>(
                @"SELECT id, tenant_id, target_url, secret_hash, description, 
                         subscribed_events, is_active, created_at, updated_at
                  FROM webhook_subscription
                  WHERE tenant_id = @DefaultTenantId
                  ORDER BY created_at DESC",
                new { DefaultTenantId }
            );

            var result = subs.Select(s => new
            {
                id = s.id.ToString(),
                tenant_id = s.tenant_id.ToString(),
                target_url = s.target_url,
                secret_masked = MaskSecret(s.secret_hash?.ToString()),
                secret = s.secret_hash?.ToString(),
                description = s.description,
                subscribed_events = ParseJsonList(s.subscribed_events?.ToString()),
                is_active = (bool)s.is_active,
                created_at = s.created_at,
                updated_at = s.updated_at
            });

            return Results.Ok(result);
        });

        // 2. Create Webhook Subscription
        app.MapPost("/api/webhooks/subscriptions", async (CreateWebhookSubscriptionDto dto, IDbConnection db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.TargetUrl) || !Uri.TryCreate(dto.TargetUrl, UriKind.Absolute, out _))
            {
                return Results.BadRequest(new { error = "Valid absolute TargetUrl is required." });
            }

            var id = Guid.NewGuid();
            var secret = $"whsec_{Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant()}";
            var eventsJson = JsonSerializer.Serialize(dto.SubscribedEvents ?? new List<string> { "call.completed", "lead.qualified" });

            await db.ExecuteAsync(
                @"INSERT INTO webhook_subscription (
                    id, tenant_id, target_url, secret_hash, description, subscribed_events, is_active
                ) VALUES (
                    @id, @DefaultTenantId, @TargetUrl, @secret, @Description, @eventsJson::jsonb, TRUE
                )",
                new
                {
                    id,
                    DefaultTenantId,
                    dto.TargetUrl,
                    secret,
                    dto.Description,
                    eventsJson
                }
            );

            return Results.Created($"/api/webhooks/subscriptions/{id}", new
            {
                id = id.ToString(),
                target_url = dto.TargetUrl,
                secret, // Returned in plaintext ONCE upon creation
                description = dto.Description,
                subscribed_events = dto.SubscribedEvents,
                is_active = true
            });
        });

        // 3. Delete Subscription
        app.MapDelete("/api/webhooks/subscriptions/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            var rows = await db.ExecuteAsync(
                "DELETE FROM webhook_subscription WHERE id = @id AND tenant_id = @DefaultTenantId",
                new { id, DefaultTenantId }
            );

            return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
        });

        // 4. Test Webhook Ping
        app.MapPost("/api/webhooks/subscriptions/{id:guid}/test", async (
            Guid id, 
            IDbConnection db, 
            IWebhookDispatcherService dispatcher, 
            IHttpClientFactory clientFactory
        ) =>
        {
            var sub = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, target_url, secret_hash FROM webhook_subscription WHERE id = @id",
                new { id }
            );

            if (sub == null) return Results.NotFound();

            string targetUrl = sub.target_url;
            string secret = sub.secret_hash ?? "test_secret";

            var testPayload = new
            {
                @event = "test.ping",
                timestamp = DateTime.UtcNow.ToString("o"),
                tenant_id = DefaultTenantId,
                message = "This is a verification ping from Pruva Voice Webhook Dispatcher.",
                sample_data = new
                {
                    caller = "+15550192834",
                    destination = "+919811223344",
                    status = "verified"
                }
            };

            var payloadJson = JsonSerializer.Serialize(testPayload);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string signature = dispatcher.ComputeSignature(secret, timestamp, payloadJson);

            var client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);

            using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl)
            {
                Content = new StringContent(payloadJson, System.Text.Encoding.UTF8, "application/json")
            };
            req.Headers.Add("User-Agent", "PruvaVoice-Webhook/2.0");
            req.Headers.Add("X-Pruva-Event", "test.ping");
            req.Headers.Add("X-Pruva-Signature", signature);

            try
            {
                var resp = await client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (body.Length > 500) body = body.Substring(0, 500);

                return Results.Ok(new
                {
                    success = resp.IsSuccessStatusCode,
                    status_code = (int)resp.StatusCode,
                    response_body = body,
                    signature_sent = signature
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    success = false,
                    status_code = 599,
                    response_body = ex.Message,
                    signature_sent = signature
                });
            }
        });

        // 5. Get Delivery Logs
        app.MapGet("/api/webhooks/logs", async (IDbConnection db, [FromQuery] int limit = 50) =>
        {
            var logs = await db.QueryAsync<dynamic>(
                @"SELECT l.id, l.subscription_id, l.event_type, l.payload, l.target_url,
                         l.response_status, l.response_body, l.attempt_count, l.duration_ms, l.delivered_at,
                         s.description as subscription_name
                  FROM webhook_delivery_log l
                  LEFT JOIN webhook_subscription s ON l.subscription_id = s.id
                  WHERE l.tenant_id = @DefaultTenantId
                  ORDER BY l.delivered_at DESC
                  LIMIT @limit",
                new { DefaultTenantId, limit }
            );

            return Results.Ok(logs);
        });

        // 6. Audio Recording Streaming Endpoint (HTTP 206 Partial Content)
        app.MapGet("/api/audio/recordings/{tenantId:guid}/{filename}", async (
            Guid tenantId, 
            string filename, 
            IAudioStorageService audioStorage, 
            HttpContext httpContext
        ) =>
        {
            var stream = await audioStorage.GetRecordingStreamAsync(tenantId, filename);
            if (stream == null) return Results.NotFound(new { error = "Audio file not found." });

            // Return stream with range request support for seeking in web audio player
            return Results.File(stream, "audio/wav", enableRangeProcessing: true);
        });

        // 7. Audio Metadata Inspection Endpoint
        app.MapGet("/api/audio/recordings/{tenantId:guid}/{filename}/metadata", async (
            Guid tenantId, 
            string filename, 
            IAudioStorageService audioStorage
        ) =>
        {
            var metadata = await audioStorage.GetMetadataAsync(tenantId, filename);
            if (metadata == null) return Results.NotFound(new { error = "Audio metadata could not be extracted." });

            return Results.Ok(metadata);
        });
    }

    private static string MaskSecret(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length < 10) return "••••••••••••";
        return $"{s.Substring(0, 6)}••••••••{s.Substring(s.Length - 4)}";
    }

    private static List<string> ParseJsonList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

public record CreateWebhookSubscriptionDto(
    string TargetUrl,
    string? Description,
    List<string>? SubscribedEvents
);
