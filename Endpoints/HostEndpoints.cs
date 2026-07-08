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

        app.MapGet("/api/hosts", async (IDbConnection db, string? category = null) =>
        {
            var rows = await db.QueryAsync("SELECT u.id,u.phone,u.username,u.display_name,u.display_gender,u.profile_icon,hp.rate_per_minute,hp.rating_avg,hp.rating_count,hp.languages,hp.voice_intro_url,hp.sort_order,hc.name AS category,COALESCE(p.status,'offline') AS presence FROM host_profile hp JOIN app_user u ON u.id=hp.user_id LEFT JOIN host_category hc ON hc.id=hp.category_id LEFT JOIN host_presence p ON p.user_id=u.id WHERE hp.status='approved' AND u.status='active' AND (@category IS NULL OR hc.name=@category) ORDER BY CASE WHEN COALESCE(p.status,'offline')='online' THEN 0 ELSE 1 END,hp.sort_order ASC,hp.rating_avg DESC,hp.completed_calls DESC", new { category });
            return Results.Ok(rows);
        });

        app.MapGet("/api/hosts/{hostUserId:guid}/availability", async (Guid hostUserId, IDbConnection db) =>
        {
            var row = await db.QueryFirstOrDefaultAsync("SELECT status,schedule_online_at,schedule_offline_at FROM host_presence WHERE user_id=@hostUserId", new { hostUserId });
            return Results.Ok(new { hostUserId, available = row?.status == "online", status = row?.status ?? "offline", scheduleOnlineAt = row?.schedule_online_at, scheduleOfflineAt = row?.schedule_offline_at });
        }).RequireAuthorization();

        app.MapPost("/api/hosts/presence", async (HostPresenceDto dto, ClaimsPrincipal cp, IDbConnection db, IHubContext<CallHub> hubContext) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO host_presence(user_id,status,schedule_online_at,schedule_offline_at,last_seen_at,updated_at) VALUES(@uid,@Status,@ScheduleOnlineAt,@ScheduleOfflineAt,now(),now()) ON CONFLICT(user_id) DO UPDATE SET status=@Status,schedule_online_at=@ScheduleOnlineAt,schedule_offline_at=@ScheduleOfflineAt,last_seen_at=now(),updated_at=now()", new { uid, dto.Status, dto.ScheduleOnlineAt, dto.ScheduleOfflineAt });
            await hubContext.Clients.All.SendAsync("presenceChanged", new { userId = uid.ToString(), status = dto.Status });
            return Results.Ok(new { message = "Presence updated" });
        }).RequireAuthorization();

        app.MapPost("/api/hosts/become-host", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var paid = await db.ExecuteScalarAsync<bool>("SELECT host_activation_paid FROM app_user WHERE id=@uid", new { uid });
            if (!paid) return Results.BadRequest(new { message = "Pay ₹499 host activation first" });
            await db.ExecuteAsync("UPDATE app_user SET is_host=true WHERE id=@uid; INSERT INTO host_profile(user_id,status) VALUES(@uid,'pending') ON CONFLICT(user_id) DO NOTHING; INSERT INTO host_presence(user_id,status) VALUES(@uid,'offline') ON CONFLICT(user_id) DO NOTHING;", new { uid });
            return Results.Ok(new { message = "Host application submitted" });
        }).RequireAuthorization();
    }
}
