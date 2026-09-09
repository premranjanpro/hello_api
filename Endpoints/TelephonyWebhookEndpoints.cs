using System.Data;
using Dapper;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Telephony;

namespace PruvaVoice.Api.Endpoints;

public static class TelephonyWebhookEndpoints
{
    public static void MapTelephonyWebhookEndpoints(this WebApplication app)
    {
        // 1. Twilio Webhook Handler
        app.MapPost("/api/webhooks/twilio/{tenantId:guid}", async (Guid tenantId, HttpRequest request, IDbConnection db, TelephonyProviderFactory factory, CredentialEncryptionService crypto) =>
        {
            try
            {
                var credRow = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT * FROM telephony_provider_credential WHERE tenant_id = @tenantId AND provider_type = 'twilio' AND is_active = true ORDER BY is_default DESC, priority ASC LIMIT 1",
                    new { tenantId });

                if (credRow == null) return Results.NotFound();

                var provider = factory.GetProvider("twilio");
                var configJson = crypto.Decrypt((string)credRow.encrypted_credentials);
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);

                var normalizedEvent = await provider.ParseWebhookAsync(request, doc.RootElement);

                // Update call_session if matching provider_call_sid exists
                await ProcessNormalizedCallEvent(normalizedEvent, tenantId, db);

                return Results.Ok(new { received = true, eventType = normalizedEvent.EventType });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelephonyWebhook] Twilio error: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });

        // 2. Exotel Webhook Handler
        app.MapMethods("/api/webhooks/exotel/{tenantId:guid}", new[] { "GET", "POST" }, async (Guid tenantId, HttpRequest request, IDbConnection db, TelephonyProviderFactory factory, CredentialEncryptionService crypto) =>
        {
            try
            {
                var credRow = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT * FROM telephony_provider_credential WHERE tenant_id = @tenantId AND provider_type = 'exotel' AND is_active = true ORDER BY is_default DESC, priority ASC LIMIT 1",
                    new { tenantId });

                if (credRow == null) return Results.NotFound();

                var provider = factory.GetProvider("exotel");
                var configJson = crypto.Decrypt((string)credRow.encrypted_credentials);
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);

                var normalizedEvent = await provider.ParseWebhookAsync(request, doc.RootElement);

                await ProcessNormalizedCallEvent(normalizedEvent, tenantId, db);

                return Results.Ok(new { received = true, eventType = normalizedEvent.EventType });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelephonyWebhook] Exotel error: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });

        // 3. LiveKit Webhook Handler
        app.MapPost("/api/webhooks/livekit/{tenantId:guid}", async (Guid tenantId, HttpRequest request, IDbConnection db, TelephonyProviderFactory factory, CredentialEncryptionService crypto) =>
        {
            try
            {
                var credRow = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT * FROM telephony_provider_credential WHERE tenant_id = @tenantId AND provider_type = 'livekit' AND is_active = true ORDER BY is_default DESC, priority ASC LIMIT 1",
                    new { tenantId });

                if (credRow == null) return Results.NotFound();

                var provider = factory.GetProvider("livekit");
                var configJson = crypto.Decrypt((string)credRow.encrypted_credentials);
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);

                var normalizedEvent = await provider.ParseWebhookAsync(request, doc.RootElement);

                await ProcessNormalizedCallEvent(normalizedEvent, tenantId, db);

                return Results.Ok(new { received = true, eventType = normalizedEvent.EventType });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TelephonyWebhook] LiveKit error: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });
    }

    private static async Task ProcessNormalizedCallEvent(NormalizedCallEvent ev, Guid tenantId, IDbConnection db)
    {
        if (string.IsNullOrWhiteSpace(ev.ProviderCallSid)) return;

        // Find matching call session by provider_call_sid or room_name
        var call = await db.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT id, task_id, status FROM call_session WHERE (provider_call_sid = @sid OR room_name = @sid) AND tenant_id = @tenantId LIMIT 1",
            new { sid = ev.ProviderCallSid, tenantId });

        if (call == null) return;

        Guid callId = (Guid)call.id;
        Guid? taskId = (Guid?)call.task_id;

        string updateStatus = ev.EventType switch
        {
            CallEventTypes.CallAnswered => "connected",
            CallEventTypes.CallEnded => "completed",
            CallEventTypes.CallFailed => "failed",
            CallEventTypes.CallBusy => "busy",
            CallEventTypes.CallNoAnswer => "no_answer",
            CallEventTypes.RecordingReady when ev.Status == "completed" => "completed",
            _ => (string)call.status
        };


        await db.ExecuteAsync(@"
            UPDATE call_session SET
                status = @updateStatus,
                ended_at = CASE WHEN @updateStatus IN ('completed', 'failed', 'busy', 'no_answer') THEN now() ELSE ended_at END,
                recording_storage_path = COALESCE(NULLIF(@RecordingUrl, ''), recording_storage_path),
                duration_seconds = CASE WHEN @DurationSeconds > 0 THEN @DurationSeconds ELSE duration_seconds END
            WHERE id = @callId",
            new { updateStatus, ev.RecordingUrl, ev.DurationSeconds, callId });

        // Update task contact queue status if part of a campaign
        if (taskId.HasValue)
        {
            await db.ExecuteAsync(@"
                UPDATE task_target_contact SET
                    status = CASE 
                        WHEN @updateStatus = 'completed' THEN 'completed'
                        WHEN @updateStatus IN ('busy', 'no_answer', 'failed') THEN 'retry_scheduled'
                        ELSE status
                    END,
                    outcome = CASE
                        WHEN @updateStatus = 'busy' THEN 'busy'
                        WHEN @updateStatus = 'no_answer' THEN 'no_answer'
                        WHEN @updateStatus = 'failed' THEN 'failed'
                        ELSE outcome
                    END,
                    last_attempt_at = now()
                WHERE task_id = @taskId AND last_call_session_id = @callId",
                new { updateStatus, taskId = taskId.Value, callId });
        }
    }
}
