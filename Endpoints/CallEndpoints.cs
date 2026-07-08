using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using PruvaVoice.Api.Hubs;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class CallEndpoints
{
    public static void MapCallEndpoints(this WebApplication app)
    {
        app.MapPost("/api/calls/start", async (StartCallDto dto, ClaimsPrincipal cp, IDbConnection db, LiveKitTokenService livekit, CallNotifier notifier, IHubContext<CallHub> hubContext, HttpContext httpContext) =>
        {
            var callerId = CurrentUser.Id(cp);
            var host = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT hp.*,u.username,u.status AS user_status,COALESCE(p.status,'offline') AS presence FROM host_profile hp JOIN app_user u ON u.id=hp.user_id LEFT JOIN user_presence p ON p.user_id=u.id WHERE hp.user_id=@HostUserId AND hp.status='approved'", new { dto.HostUserId });
            if (host == null || host.user_status != "active" || host.presence != "online") return Results.BadRequest("Host not available");
            var blocked = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM block_list WHERE (blocker_user_id=@callerId AND blocked_user_id=@HostUserId) OR (blocker_user_id=@HostUserId AND blocked_user_id=@callerId))", new { callerId, dto.HostUserId });
            if (blocked) return Results.BadRequest("User blocked");

            var balance = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id=@callerId", new { callerId });
            var minHold = Math.Max(20, (decimal)host.rate_per_minute * 5);
            if (balance < minHold) return Results.BadRequest("Insufficient wallet balance");

            var room = "call_" + Guid.NewGuid().ToString("N");
            var callId = await db.ExecuteScalarAsync<Guid>("INSERT INTO call_session(caller_user_id,host_user_id,room_name,status,rate_per_minute) VALUES(@callerId,@HostUserId,@room,'ringing',@Rate) RETURNING id", new { callerId, dto.HostUserId, room, Rate = (decimal)host.rate_per_minute });
            await db.ExecuteAsync("INSERT INTO call_event(call_session_id,event_type,actor_user_id) VALUES(@callId,'created',@callerId)", new { callId, callerId });
            var latestToken = await db.ExecuteScalarAsync<string?>("SELECT fcm_token FROM user_device WHERE user_id=@HostUserId ORDER BY last_seen_at DESC LIMIT 1", new { dto.HostUserId });
            var callerUsername = await db.ExecuteScalarAsync<string?>("SELECT COALESCE(username, phone) FROM app_user WHERE id=@callerId", new { callerId });
            await notifier.NotifyIncomingCallAsync(db, dto.HostUserId, callId, callerUsername ?? "Caller", latestToken);
            await db.ExecuteAsync("INSERT INTO user_presence(user_id, status, last_seen_at, updated_at) VALUES(@callerId, 'busy', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='busy', last_seen_at=now(), updated_at=now()", new { callerId });
            await db.ExecuteAsync("INSERT INTO user_presence(user_id, status, last_seen_at, updated_at) VALUES(@HostUserId, 'busy', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='busy', last_seen_at=now(), updated_at=now()", new { dto.HostUserId });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = callerId.ToString(), status = "busy" });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = dto.HostUserId.ToString(), status = "busy" });

            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='livekit' AND is_active=true");
            var config = string.IsNullOrWhiteSpace(credJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(credJson);
            var apiKey = config != null && config.TryGetValue("apiKey", out var ak) ? ak : "devkey";
            var apiSecret = config != null && config.TryGetValue("apiSecret", out var asec) ? asec : "devsecret";
            var liveKitUrl = config != null && config.TryGetValue("url", out var u) ? u : "ws://localhost:7880";
            if (liveKitUrl.Contains("localhost"))
            {
                var requestHost = httpContext.Request.Host.Host;
                liveKitUrl = liveKitUrl.Replace("localhost", requestHost);
            }

            return Results.Ok(new { callId, roomName = room, liveKitUrl, callerToken = livekit.CreateToken(apiKey, apiSecret, room, callerId.ToString()) });
        }).RequireAuthorization();

        app.MapPost("/api/calls/{callId:guid}/accept", async (Guid callId, ClaimsPrincipal cp, IDbConnection db, LiveKitTokenService livekit, HttpContext httpContext) =>
        {
            var hostId = CurrentUser.Id(cp);
            var call = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId AND host_user_id=@hostId", new { callId, hostId });
            if (call == null) return Results.NotFound();
            await db.ExecuteAsync("UPDATE call_session SET status='connected',connected_at=now(),started_at=now() WHERE id=@callId", new { callId });
            await db.ExecuteAsync("INSERT INTO call_event(call_session_id,event_type,actor_user_id) VALUES(@callId,'accepted',@hostId)", new { callId, hostId });

            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='livekit' AND is_active=true");
            var config = string.IsNullOrWhiteSpace(credJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(credJson);
            var apiKey = config != null && config.TryGetValue("apiKey", out var ak) ? ak : "devkey";
            var apiSecret = config != null && config.TryGetValue("apiSecret", out var asec) ? asec : "devsecret";
            var liveKitUrl = config != null && config.TryGetValue("url", out var u) ? u : "ws://localhost:7880";
            if (liveKitUrl.Contains("localhost"))
            {
                var requestHost = httpContext.Request.Host.Host;
                liveKitUrl = liveKitUrl.Replace("localhost", requestHost);
            }

            return Results.Ok(new { liveKitUrl, hostToken = livekit.CreateToken(apiKey, apiSecret, (string)call.room_name, hostId.ToString()) });
        }).RequireAuthorization();

        app.MapPost("/api/calls/{callId:guid}/end", async (Guid callId, ClaimsPrincipal cp, IDbConnection db, IHubContext<CallHub> hubContext) =>
        {
            var call = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId", new { callId });
            if (call == null) return Results.NotFound();
            int seconds = 0;
            if (call.connected_at != null) seconds = (int)Math.Ceiling((DateTimeOffset.UtcNow - (DateTimeOffset)call.connected_at).TotalSeconds);
            var amount = Math.Round(((decimal)seconds / 60m) * (decimal)call.rate_per_minute, 2);
            var commission = Math.Round(amount * 0.25m, 2);
            var hostEarn = amount - commission;

            await db.ExecuteAsync("UPDATE call_session SET status='ended',ended_at=now(),billable_seconds=@seconds,total_amount=@amount,host_earning=@hostEarn,platform_commission=@commission,end_reason='normal' WHERE id=@callId", new { callId, seconds, amount, hostEarn, commission });
            await db.ExecuteAsync("UPDATE wallet_account SET balance=balance-@amount WHERE user_id=@Caller", new { amount, Caller = (Guid)call.caller_user_id });
            await db.ExecuteAsync("INSERT INTO wallet_transaction(user_id,txn_type,amount,reference_type,reference_id,note) VALUES(@Caller,'call_charge',-@amount,'call',@callId,'Voice call charge')", new { Caller = (Guid)call.caller_user_id, amount, callId });
            await db.ExecuteAsync("INSERT INTO host_earning_ledger(host_user_id,call_session_id,txn_type,amount,note) VALUES(@Host,@callId,'earning',@hostEarn,'Call earning')", new { Host = (Guid)call.host_user_id, callId, hostEarn });
            await db.ExecuteAsync("UPDATE host_profile SET completed_calls=completed_calls+1 WHERE user_id=@Host", new { Host = (Guid)call.host_user_id });
            await db.ExecuteAsync("UPDATE user_presence SET status='online',updated_at=now() WHERE user_id=@Host", new { Host = (Guid)call.host_user_id });
            await db.ExecuteAsync("INSERT INTO user_presence(user_id, status, last_seen_at, updated_at) VALUES(@Caller, 'online', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='online', last_seen_at=now(), updated_at=now()", new { Caller = (Guid)call.caller_user_id });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = call.host_user_id.ToString(), status = "online" });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = call.caller_user_id.ToString(), status = "online" });
            await db.ExecuteAsync("INSERT INTO call_event(call_session_id,event_type,metadata) VALUES(@callId,'ended',jsonb_build_object('seconds',@seconds,'amount',@amount))", new { callId, seconds, amount });

            return Results.Ok(new { seconds, amount, hostEarn, commission });
        }).RequireAuthorization();

        app.MapPost("/api/calls/{callId:guid}/rating", async (Guid callId, RateCallDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var call = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId AND caller_user_id=@uid", new { callId, uid });
            if (call == null) return Results.NotFound();

            // 1. Check if rating is enabled in settings
            var askRatingVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='ask_call_rating'");
            if (askRatingVal != null && askRatingVal.ToLower() == "false")
            {
                return Results.BadRequest("Ratings are disabled by the administrator.");
            }

            // 2. Check if the call connected
            if (call.connected_at == null)
            {
                return Results.BadRequest("Ratings are only allowed for connected calls.");
            }

            // 3. Check minimum call duration
            var minDurationVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='min_call_duration_rating_minutes'");
            int minMinutes = 10;
            if (!string.IsNullOrEmpty(minDurationVal) && int.TryParse(minDurationVal, out int parsedMinutes))
            {
                minMinutes = parsedMinutes;
            }

            int billableSeconds = call.billable_seconds ?? 0;
            if (billableSeconds < minMinutes * 60)
            {
                return Results.BadRequest($"Ratings are only allowed for calls lasting at least {minMinutes} minutes.");
            }

            // 4. One user can give only one time rating
            var existingRating = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT id FROM call_rating WHERE call_session_id=@callId", new { callId });
            if (existingRating != null)
            {
                return Results.BadRequest("You have already rated this call session.");
            }

            await db.ExecuteAsync(@"
                INSERT INTO call_rating(
                    call_session_id, caller_user_id, host_user_id, 
                    rating_communication, rating_politeness, rating_expertise, rating_audio_clarity, rating_overall, 
                    comment
                ) 
                VALUES(
                    @callId, @uid, @Host, 
                    @RatingCommunication, @RatingPoliteness, @RatingExpertise, @RatingAudioClarity, @RatingOverall, 
                    @Comment
                )", 
                new { 
                    callId, 
                    uid, 
                    Host = (Guid)call.host_user_id, 
                    dto.RatingCommunication, 
                    dto.RatingPoliteness, 
                    dto.RatingExpertise, 
                    dto.RatingAudioClarity, 
                    dto.RatingOverall, 
                    dto.Comment 
                });
            await db.ExecuteAsync("UPDATE host_profile SET rating_avg=(SELECT COALESCE(AVG(rating),0) FROM call_rating WHERE host_user_id=@Host), rating_count=(SELECT COUNT(*) FROM call_rating WHERE host_user_id=@Host) WHERE user_id=@Host", new { Host = (Guid)call.host_user_id });
            return Results.Ok(new { message = "Rating saved" });
        }).RequireAuthorization();

        app.MapGet("/api/calls/history", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var rows = await db.QueryAsync("SELECT c.*,cu.username AS caller_username,hu.username AS host_username FROM call_session c JOIN app_user cu ON cu.id=c.caller_user_id JOIN app_user hu ON hu.id=c.host_user_id WHERE c.caller_user_id=@uid OR c.host_user_id=@uid ORDER BY c.created_at DESC LIMIT 100", new { uid });
            return Results.Ok(rows);
        }).RequireAuthorization();

        app.MapPost("/api/calls/report", async (ReportCallDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO call_report(call_session_id,reporter_user_id,reported_user_id,reason,description) VALUES(@CallSessionId,@uid,@ReportedUserId,@Reason,@Description)", new { dto.CallSessionId, uid, dto.ReportedUserId, dto.Reason, dto.Description });
            return Results.Ok(new { message = "Report submitted" });
        }).RequireAuthorization();
    }
}
