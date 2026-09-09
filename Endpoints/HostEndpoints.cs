using System.Data;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using PruvaVoice.Api.Hubs;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class HostEndpoints
{
    public static void MapHostEndpoints(this WebApplication app)
    {
        app.MapGet("/api/categories", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM host_category WHERE is_active=true ORDER BY sort_order,name");
            return Results.Ok(rows);
        });

        app.MapGet("/api/hosts", async (IDbConnection db, HttpContext httpContext, string? category = null) =>
        {
            Guid? currentUserId = null;
            if (httpContext.User?.Identity?.IsAuthenticated == true)
            {
                var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!string.IsNullOrEmpty(idClaim) && Guid.TryParse(idClaim, out var parsedId))
                {
                    currentUserId = parsedId;
                }
            }

            // Ensure user_chat_message table exists so the history check is safe
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS user_chat_message (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    sender_id UUID NOT NULL,
                    recipient_id UUID NOT NULL,
                    text TEXT NOT NULL,
                    message_type VARCHAR(50) NOT NULL DEFAULT 'text',
                    is_read BOOLEAN NOT NULL DEFAULT FALSE,
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );
                UPDATE app_user SET city = 'Patna' WHERE username IN ('user_5542', 'priya_singh') AND (city IS NULL OR city = '');
                UPDATE app_user SET city = 'Delhi' WHERE username IN ('user_5724', 'user_4762') AND (city IS NULL OR city = '');
            ");

            var rows = await db.QueryAsync(@"
                SELECT u.id,
                       '' AS phone,
                       u.username,
                       u.display_name,
                       u.display_gender,
                       u.profile_icon,
                       u.age,
                       COALESCE(u.city, CASE WHEN u.username IN ('user_5542', 'priya_singh') THEN 'Patna' ELSE 'Delhi' END) AS city,
                       COALESCE(u.languages, array_to_string(hp.languages, ', '), 'Hindi, English') AS languages,
                       COALESCE(hp.rate_per_minute, 5.00) AS rate_per_minute,
                       COALESCE(hp.rating_avg, 4.85) AS rating_avg,
                       COALESCE(hp.rating_count, 1) AS rating_count,
                       COALESCE(array_to_string(hp.languages, ', '), 'Hindi, English') AS host_languages,
                       hp.voice_intro_url,
                       COALESCE(hp.sort_order, 100) AS sort_order,
                       COALESCE(hc.name, CASE WHEN hp.id IS NOT NULL THEN 'Social' ELSE 'Friend' END) AS category,
                       COALESCE(p.status,'offline') AS presence,
                       CASE WHEN hp.id IS NOT NULL AND hp.status='approved' THEN true ELSE false END AS is_host
                FROM app_user u
                LEFT JOIN host_profile hp ON hp.user_id=u.id AND hp.status='approved'
                LEFT JOIN host_category hc ON hc.id=hp.category_id
                LEFT JOIN user_presence p ON p.user_id=u.id
                WHERE u.status='active'
                  AND (@currentUserId IS NULL OR u.id <> @currentUserId)
                  AND (
                      -- 1. All approved hosts (both online and offline)
                      (hp.id IS NOT NULL AND hp.status='approved')
                      -- 2. Regular users who are currently online
                      OR (hp.id IS NULL AND COALESCE(p.status,'offline')='online')
                      -- 3. Regular users with whom current user previously talked (calls or chats)
                      OR (
                          hp.id IS NULL 
                          AND @currentUserId IS NOT NULL
                          AND (
                              EXISTS (
                                  SELECT 1 FROM call_session cs 
                                  WHERE (cs.caller_user_id=@currentUserId AND cs.host_user_id=u.id)
                                     OR (cs.host_user_id=@currentUserId AND cs.caller_user_id=u.id)
                              )
                              OR EXISTS (
                                  SELECT 1 FROM user_chat_message ucm
                                  WHERE (ucm.sender_id=@currentUserId AND ucm.recipient_id=u.id)
                                     OR (ucm.recipient_id=@currentUserId AND ucm.sender_id=u.id)
                              )
                          )
                      )
                  )
                  AND (@category IS NULL OR hc.name=@category OR (@category='all'))
                ORDER BY 
                    CASE WHEN COALESCE(p.status,'offline')='online' THEN 0 ELSE 1 END ASC,
                    CASE WHEN hp.id IS NOT NULL AND hp.status='approved' THEN 0 ELSE 1 END ASC,
                    COALESCE(hp.sort_order, 100) ASC,
                    COALESCE(hp.rating_avg, 4.8) DESC,
                    COALESCE(hp.completed_calls, 0) DESC", new { currentUserId, category });
            return Results.Ok(rows);
        });

        app.MapGet("/api/hosts/{hostUserId:guid}/availability", async (Guid hostUserId, IDbConnection db) =>
        {
            var row = await db.QueryFirstOrDefaultAsync("SELECT status,schedule_online_at,schedule_offline_at FROM user_presence WHERE user_id=@hostUserId", new { hostUserId });
            return Results.Ok(new { hostUserId, available = row?.status == "online", status = row?.status ?? "offline", scheduleOnlineAt = row?.schedule_online_at, scheduleOfflineAt = row?.schedule_offline_at });
        }).RequireAuthorization();

        app.MapPost("/api/hosts/presence", async (HostPresenceDto dto, ClaimsPrincipal cp, IDbConnection db, IHubContext<CallHub> hubContext) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO user_presence(user_id,status,schedule_online_at,schedule_offline_at,last_seen_at,updated_at) VALUES(@uid,@Status,@ScheduleOnlineAt,@ScheduleOfflineAt,now(),now()) ON CONFLICT(user_id) DO UPDATE SET status=@Status,schedule_online_at=@ScheduleOnlineAt,schedule_offline_at=@ScheduleOfflineAt,last_seen_at=now(),updated_at=now()", new { uid, dto.Status, dto.ScheduleOnlineAt, dto.ScheduleOfflineAt });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = uid.ToString(), status = dto.Status });
            return Results.Ok(new { message = "Presence updated" });
        }).RequireAuthorization();

        app.MapPost("/api/hosts/become-host", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var paid = await db.ExecuteScalarAsync<bool>("SELECT host_activation_paid FROM app_user WHERE id=@uid", new { uid });
            if (!paid) return Results.BadRequest(new { message = "Pay ₹499 host activation first" });
            await db.ExecuteAsync("UPDATE app_user SET is_host=true WHERE id=@uid; INSERT INTO host_profile(user_id,status) VALUES(@uid,'pending') ON CONFLICT(user_id) DO NOTHING; INSERT INTO user_presence(user_id,status) VALUES(@uid,'offline') ON CONFLICT(user_id) DO NOTHING;", new { uid });
            return Results.Ok(new { message = "Host application submitted" });
        }).RequireAuthorization();
    }
}
