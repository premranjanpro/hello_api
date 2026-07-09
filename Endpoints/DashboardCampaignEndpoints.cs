using System.Data;
using Dapper;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class DashboardCampaignEndpoints
{
    public static void MapDashboardCampaignEndpoints(this WebApplication app)
    {
        // Admin: List all dashboard campaigns
        app.MapGet("/api/admin/dashboard-campaigns", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM dashboard_campaign ORDER BY created_at DESC");
            return Results.Ok(rows);
        });

        // Admin: Create dashboard campaign
        app.MapPost("/api/admin/dashboard-campaigns", async (AddDashboardCampaignDto dto, IDbConnection db, FcmService fcm) =>
        {
            var id = await db.ExecuteScalarAsync<int>(@"
                INSERT INTO dashboard_campaign (title, body, image_url, is_dismissible, schedule_time, expired_time, target_type, target_user_id)
                VALUES (@Title, @Body, @ImageUrl, @IsDismissible, @ScheduleTime, @ExpiredTime, @TargetType, @TargetUserId)
                RETURNING id", dto);

            // Optional: Broadcast via FCM instantly if SendFcm is true
            if (dto.SendFcm)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var dataPayload = new Dictionary<string, string>
                        {
                            { "type", "dashboard_broadcast" },
                            { "campaignId", id.ToString() }
                        };

                        if (dto.TargetType == "all")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_all", dto.Title, dto.Body, dto.ImageUrl, dataPayload);
                        }
                        else if (dto.TargetType == "male")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_male", dto.Title, dto.Body, dto.ImageUrl, dataPayload);
                        }
                        else if (dto.TargetType == "female")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_female", dto.Title, dto.Body, dto.ImageUrl, dataPayload);
                        }
                        else if (dto.TargetType == "single" && dto.TargetUserId != null)
                        {
                            var tokens = (await db.QueryAsync<string>("SELECT DISTINCT fcm_token FROM user_device WHERE user_id=@TargetUserId AND fcm_token IS NOT NULL AND fcm_token <> ''", new { dto.TargetUserId })).ToList();
                            foreach (var token in tokens)
                            {
                                try
                                {
                                    await fcm.SendNotificationAsync(db, token, dto.Title, dto.Body, dto.ImageUrl, dataPayload);
                                }
                                catch {}
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FCM campaign broadcast failed: {ex.Message}");
                    }
                });
            }

            return Results.Ok(new { id, message = "Dashboard campaign created successfully" });
        });

        // Admin: Update dashboard campaign
        app.MapPut("/api/admin/dashboard-campaigns/{id:int}", async (int id, AddDashboardCampaignDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync(@"
                UPDATE dashboard_campaign SET 
                    title=@Title, 
                    body=@Body, 
                    image_url=@ImageUrl, 
                    is_dismissible=@IsDismissible, 
                    schedule_time=@ScheduleTime, 
                    expired_time=@ExpiredTime,
                    target_type=@TargetType,
                    target_user_id=@TargetUserId
                WHERE id=@id", new { 
                    id, 
                    dto.Title, 
                    dto.Body, 
                    dto.ImageUrl, 
                    dto.IsDismissible, 
                    dto.ScheduleTime, 
                    dto.ExpiredTime,
                    dto.TargetType,
                    dto.TargetUserId
                });
            return Results.Ok(new { message = "Dashboard campaign updated successfully" });
        });

        // Admin: Delete dashboard campaign
        app.MapDelete("/api/admin/dashboard-campaigns/{id:int}", async (int id, IDbConnection db) =>
        {
            await db.ExecuteAsync("DELETE FROM dashboard_campaign WHERE id=@id", new { id });
            return Results.Ok(new { message = "Dashboard campaign deleted successfully" });
        });

        // User: Get active campaigns for dashboard
        app.MapGet("/api/dashboard-campaigns", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var userId = CurrentUser.Id(cp);
            var userGender = await db.ExecuteScalarAsync<string>("SELECT display_gender FROM app_user WHERE id=@userId", new { userId });
            var genderLower = userGender?.ToLower() ?? "";

            var rows = await db.QueryAsync(@"
                SELECT * FROM dashboard_campaign 
                WHERE (schedule_time IS NULL OR schedule_time <= NOW()) 
                  AND (expired_time IS NULL OR expired_time > NOW()) 
                  AND (
                      target_type = 'all'
                      OR (target_type = 'male' AND @genderLower = 'male')
                      OR (target_type = 'female' AND @genderLower = 'female')
                      OR (target_type = 'single' AND target_user_id = @userId)
                  )
                ORDER BY created_at DESC", new { userId, genderLower });
            return Results.Ok(rows);
        }).RequireAuthorization();
    }
}

public record AddDashboardCampaignDto(
    string Title,
    string Body,
    string? ImageUrl,
    bool IsDismissible,
    DateTime? ScheduleTime,
    DateTime? ExpiredTime,
    bool SendFcm,
    string TargetType = "all",
    Guid? TargetUserId = null
);
