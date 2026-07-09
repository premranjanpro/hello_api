using System.Data;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/dashboard", async (IDbConnection db) =>
        {
            var row = await db.QueryFirstOrDefaultAsync("SELECT * FROM v_admin_dashboard");
            return Results.Ok(row);
        });

        app.MapGet("/api/admin/users", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT u.*, COALESCE((SELECT SUM(amount) FROM wallet_transaction WHERE user_id = u.id), 0) AS wallet_balance, COALESCE(p.status, 'offline') AS presence FROM app_user u LEFT JOIN wallet_account w ON w.user_id = u.id LEFT JOIN user_presence p ON p.user_id = u.id WHERE u.role <> 'admin' ORDER BY u.created_at DESC LIMIT 500");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/users/{userId:guid}/make-host", async (Guid userId, AdminMakeHostDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE app_user SET is_host=true,is_host_approved=@Approved, role=(CASE WHEN @Approved THEN 'host' ELSE 'user' END) WHERE id=@userId; INSERT INTO host_profile(user_id,category_id,rate_per_minute,sort_order,status) VALUES(@userId,@CategoryId,@RatePerMinute,@SortOrder,CASE WHEN @Approved THEN 'approved' ELSE 'pending' END) ON CONFLICT(user_id) DO UPDATE SET category_id=@CategoryId,rate_per_minute=@RatePerMinute,sort_order=@SortOrder,status=CASE WHEN @Approved THEN 'approved' ELSE 'pending' END; INSERT INTO user_presence(user_id,status) VALUES(@userId,'offline') ON CONFLICT(user_id) DO NOTHING;", new { userId, dto.CategoryId, dto.RatePerMinute, dto.SortOrder, dto.Approved });
            return Results.Ok(new { message = "User host status updated" });
        });

        app.MapPost("/api/admin/users/{userId:guid}/remove-host", async (Guid userId, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE app_user SET is_host=false,is_host_approved=false, role='user' WHERE id=@userId; DELETE FROM host_profile WHERE user_id=@userId; DELETE FROM user_presence WHERE user_id=@userId;", new { userId });
            return Results.Ok(new { message = "User host status removed" });
        });

        app.MapPost("/api/admin/users/{userId:guid}/status", async (Guid userId, AdminStatusDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE app_user SET status=@Status WHERE id=@userId", new { userId, dto.Status });
            return Results.Ok(new { message = "User status updated" });
        });

        app.MapPost("/api/admin/users/{userId:guid}/update", async (Guid userId, AdminUpdateUserDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE app_user SET username=@Username, phone=@Phone, display_gender=@Gender, display_name=@Username WHERE id=@userId", new { userId, dto.Username, dto.Phone, dto.Gender });
            var currentBalance = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id = @userId", new { userId });
            var diff = dto.WalletBalance - currentBalance;
            if (diff != 0)
            {
                await db.ExecuteAsync("INSERT INTO wallet_transaction(user_id, txn_type, amount, note) VALUES(@userId, 'adjustment', @diff, 'Admin manual balance adjustment')", new { userId, diff });
            }
            await db.ExecuteAsync("INSERT INTO wallet_account(user_id, balance, currency, updated_at) VALUES(@userId, @WalletBalance, 'INR', now()) ON CONFLICT(user_id) DO UPDATE SET balance=@WalletBalance, updated_at=now();", new { userId, dto.WalletBalance });
            return Results.Ok(new { message = "User details updated successfully" });
        });



        app.MapGet("/api/admin/withdrawals", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT wr.*,u.username,pm.upi_id,pm.qr_image_url FROM host_withdrawal_request wr JOIN app_user u ON u.id=wr.host_user_id LEFT JOIN host_payout_method pm ON pm.id=wr.payout_method_id ORDER BY requested_at DESC LIMIT 200");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/payouts/{requestId:guid}/mark-paid", async (Guid requestId, MarkPaidDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE host_withdrawal_request SET status='paid',utr_reference=@UtrReference,proof_image_url=@ProofImageUrl,admin_note=@AdminNote,processed_at=now() WHERE id=@requestId", new { requestId, dto.UtrReference, dto.ProofImageUrl, dto.AdminNote });
            return Results.Ok(new { message = "Payout marked paid" });
        });

        app.MapGet("/api/admin/reports", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM call_report ORDER BY created_at DESC LIMIT 200");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/notifications/send", async (AdminSendNotificationDto dto, IDbConnection db, FcmService fcm) =>
        {
            List<string> tokens;
            if (dto.UserId.HasValue)
            {
                tokens = (await db.QueryAsync<string>("SELECT fcm_token FROM user_device WHERE user_id=@UserId AND fcm_token IS NOT NULL AND fcm_token <> ''", new { UserId = dto.UserId.Value })).ToList();
            }
            else
            {
                tokens = (await db.QueryAsync<string>("SELECT DISTINCT fcm_token FROM user_device WHERE fcm_token IS NOT NULL AND fcm_token <> ''")).ToList();
            }

            if (tokens.Count == 0)
            {
                return Results.Ok(new { sentCount = 0, message = "No FCM tokens found." });
            }

            // Dispatches notifications in a non-blocking background task
            _ = Task.Run(async () =>
            {
                foreach (var token in tokens)
                {
                    await fcm.SendNotificationAsync(db, token, dto.Title, dto.Body, dto.ImageUrl, dto.Data);
                }
            });

            return Results.Ok(new { sentCount = tokens.Count, message = $"FCM notification dispatch started for {tokens.Count} devices." });
        });

        app.MapGet("/api/admin/calls", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT c.*, cu.username AS caller_username, hu.username AS host_username FROM call_session c JOIN app_user cu ON cu.id=c.caller_user_id JOIN app_user hu ON hu.id=c.host_user_id ORDER BY c.created_at DESC LIMIT 50");
            return Results.Ok(rows);
        });

        app.MapGet("/api/admin/recharges", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT t.*, u.username FROM wallet_transaction t JOIN app_user u ON u.id=t.user_id WHERE t.txn_type='recharge' ORDER BY t.created_at DESC LIMIT 50");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/users/{userId:guid}/recharge", async (Guid userId, AdminRechargeDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("UPDATE wallet_account SET balance=balance+@Amount WHERE user_id=@userId; INSERT INTO wallet_transaction(user_id,txn_type,amount,note) VALUES(@userId,'recharge',@Amount,'Admin manual recharge')", new { userId, dto.Amount });
            return Results.Ok(new { message = "Wallet successfully recharged by Admin" });
        });

        app.MapGet("/api/admin/whitelist", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM whitelist_user ORDER BY created_at DESC");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/whitelist", async (AddWhitelistDto dto, IDbConnection db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Phone)) return Results.BadRequest("Phone number is required");
            string otp = string.IsNullOrWhiteSpace(dto.FixedOtp) ? "1234" : dto.FixedOtp;
            await db.ExecuteAsync("INSERT INTO whitelist_user(phone, fixed_otp) VALUES(@Phone, @Otp) ON CONFLICT(phone) DO UPDATE SET fixed_otp=@Otp", new { dto.Phone, Otp = otp });
            return Results.Ok(new { message = "Phone successfully whitelisted" });
        });

        app.MapDelete("/api/admin/whitelist/{phone}", async (string phone, IDbConnection db) =>
        {
            await db.ExecuteAsync("DELETE FROM whitelist_user WHERE phone=@phone", new { phone });
            return Results.Ok(new { message = "Phone successfully removed from whitelist" });
        });

        app.MapGet("/api/admin/plans", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM recharge_plan ORDER BY amount ASC");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/plans", async (AddRechargePlanDto dto, IDbConnection db) =>
        {
            if (dto.Amount <= 0) return Results.BadRequest("Amount must be greater than zero");
            string desc = string.IsNullOrWhiteSpace(dto.Description) ? $"Get ₹{dto.Amount} talktime credit." : dto.Description;
            await db.ExecuteAsync("INSERT INTO recharge_plan(amount, description, minutes) VALUES(@Amount, @Desc, @Minutes) ON CONFLICT(amount) DO NOTHING", new { dto.Amount, Desc = desc, dto.Minutes });
            return Results.Ok(new { message = "Recharge plan added successfully" });
        });

        app.MapDelete("/api/admin/plans/{id:int}", async (int id, IDbConnection db) =>
        {
            await db.ExecuteAsync("DELETE FROM recharge_plan WHERE id=@id", new { id });
            return Results.Ok(new { message = "Recharge plan deleted successfully" });
        });

        // Host categories CRUD
        app.MapGet("/api/admin/categories", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT * FROM host_category ORDER BY sort_order, name");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/categories", async (AdminCategoryDto dto, IDbConnection db) =>
        {
            var id = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO host_category(name, description, category_type, min_rate_amount, max_rate_amount, default_duration_minutes, sort_order, is_active)
                VALUES(@Name, @Description, @CategoryType, @MinRateAmount, @MaxRateAmount, @DefaultDurationMinutes, @SortOrder, @IsActive)
                RETURNING id", dto);
            return Results.Ok(new { id });
        });

        app.MapPut("/api/admin/categories/{id:guid}", async (Guid id, AdminCategoryDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync(@"
                UPDATE host_category SET 
                    name=@Name, 
                    description=@Description, 
                    category_type=@CategoryType, 
                    min_rate_amount=@MinRateAmount, 
                    max_rate_amount=@MaxRateAmount, 
                    default_duration_minutes=@DefaultDurationMinutes, 
                    sort_order=@SortOrder, 
                    is_active=@IsActive 
                WHERE id=@id", new { 
                    id, 
                    dto.Name, 
                    dto.Description, 
                    dto.CategoryType, 
                    dto.MinRateAmount, 
                    dto.MaxRateAmount, 
                    dto.DefaultDurationMinutes, 
                    dto.SortOrder, 
                    dto.IsActive 
                });
            return Results.Ok(new { message = "Category updated successfully" });
        });

        app.MapDelete("/api/admin/categories/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            await db.ExecuteAsync("DELETE FROM host_category WHERE id=@id", new { id });
            return Results.Ok(new { message = "Category deleted successfully" });
        });

        // Notification Campaigns CRUD
        app.MapGet("/api/admin/notifications", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT nc.*, u.username AS target_username FROM notification_campaign nc LEFT JOIN app_user u ON u.id = nc.target_user_id ORDER BY nc.created_at DESC");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/notifications", async (AddNotificationCampaignDto dto, IDbConnection db, FcmService fcm) =>
        {
            var id = await db.ExecuteScalarAsync<int>(@"
                INSERT INTO notification_campaign (title, body, image_url, target_type, target_user_id, schedule_time)
                VALUES (@Title, @Body, @ImageUrl, @TargetType, @TargetUserId, @ScheduleTime)
                RETURNING id", dto);

            // Trigger background FCM dispatch if not scheduled for later
            if (!dto.ScheduleTime.HasValue || dto.ScheduleTime.Value <= DateTime.UtcNow)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (dto.TargetType == "all")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_all", dto.Title, dto.Body, dto.ImageUrl, null);
                        }
                        else if (dto.TargetType == "male")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_male", dto.Title, dto.Body, dto.ImageUrl, null);
                        }
                        else if (dto.TargetType == "female")
                        {
                            await fcm.SendNotificationAsync(db, "topic:hello24_female", dto.Title, dto.Body, dto.ImageUrl, null);
                        }
                        else if (dto.TargetType == "single" && dto.TargetUserId != null)
                        {
                            var tokens = (await db.QueryAsync<string>("SELECT fcm_token FROM user_device WHERE user_id=@TargetUserId AND fcm_token IS NOT NULL AND fcm_token <> ''", new { dto.TargetUserId })).ToList();
                            foreach (var token in tokens)
                            {
                                try
                                {
                                    await fcm.SendNotificationAsync(db, token, dto.Title, dto.Body, dto.ImageUrl, null);
                                }
                                catch {}
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"FCM notification campaign failed: {ex.Message}");
                    }
                });
            }

            return Results.Ok(new { id, message = "Campaign created and broadcast initiated" });
        });

        app.MapPut("/api/admin/notifications/{id:int}", async (int id, AddNotificationCampaignDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync(@"
                UPDATE notification_campaign SET 
                    title=@Title, 
                    body=@Body, 
                    image_url=@ImageUrl, 
                    target_type=@TargetType, 
                    target_user_id=@TargetUserId, 
                    schedule_time=@ScheduleTime 
                WHERE id=@id", new { 
                    id, 
                    dto.Title, 
                    dto.Body, 
                    dto.ImageUrl, 
                    dto.TargetType, 
                    dto.TargetUserId, 
                    dto.ScheduleTime 
                });
            return Results.Ok(new { message = "Campaign updated successfully" });
        });

        app.MapDelete("/api/admin/notifications/{id:int}", async (int id, IDbConnection db) =>
        {
            await db.ExecuteAsync("DELETE FROM notification_campaign WHERE id=@id", new { id });
            return Results.Ok(new { message = "Campaign deleted successfully" });
        });
    }
}

public record AdminRechargeDto(decimal Amount);
public record AddWhitelistDto(string Phone, string FixedOtp);
public record AddRechargePlanDto(decimal Amount, string Description, int Minutes);

public record AdminCategoryDto(
    string Name, 
    string Description, 
    string CategoryType, 
    decimal MinRateAmount, 
    decimal MaxRateAmount, 
    int DefaultDurationMinutes, 
    int SortOrder, 
    bool IsActive
);

public record AddNotificationCampaignDto(
    string Title,
    string Body,
    string? ImageUrl,
    string TargetType,
    Guid? TargetUserId,
    DateTime? ScheduleTime
);

public record AdminUpdateUserDto(string Username, string Phone, string Gender, decimal WalletBalance);
