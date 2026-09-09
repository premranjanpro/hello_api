using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using PruvaVoice.Api.Hubs;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AdminEndpoints
{
    public static (string Name, string Icon, string Role) ResolveAiPersona(string? roomName)
    {
        if (string.IsNullOrEmpty(roomName)) return ("AI Voice Agent", "🤖", "AI Assistant");
        var lower = roomName.ToLowerInvariant();
        if (lower.Contains("cab_booking")) return ("Cab Booking Assistant", "🚖", "Cab Dispatcher");
        if (lower.Contains("hr_job")) return ("HR Job Screening Officer", "💼", "HR Recruiter");
        if (lower.Contains("hr_interview")) return ("HR Job Interviewer", "🎯", "Mock Interviewer");
        if (lower.Contains("kids_learning")) return ("Kids Learning Buddy", "🧸", "Learning Guide");
        if (lower.Contains("romantic_chat") || lower.Contains("romantic")) return ("Romantic Companion", "💖", "Virtual Companion");
        if (lower.Contains("parents_care")) return ("Parents Care Assistant", "🌿", "Elderly Care");
        if (lower.Contains("english_tutor")) return ("Spoken English Coach", "🗣️", "Language Coach");
        if (lower.Contains("software_sales") || lower.Contains("sales")) return ("Software Sales Consultant", "🏢", "SaaS Advisor");
        if (lower.Contains("kids_game") || lower.Contains("game")) return ("Kids Game & Quiz Master", "🎮", "Game Master");

        return ("AI Voice Agent", "🤖", "AI Assistant");
    }

    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/dashboard", async (IDbConnection db) =>
        {
            var row = await db.QueryFirstOrDefaultAsync("SELECT * FROM v_admin_dashboard");
            return Results.Ok(row);
        });

        app.MapGet("/api/admin/users", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync(@"
                SELECT u.*, 
                       COALESCE(u.assigned_persona_id, 'persona_role_kids_learning') AS assigned_persona_id,
                       COALESCE((SELECT SUM(amount) FROM wallet_transaction WHERE user_id = u.id), 0) AS wallet_balance, 
                       COALESCE(p.status, 'offline') AS presence,
                       ref_u.username AS referred_by_username,
                       ref_u.phone AS referred_by_phone,
                       COALESCE((SELECT COUNT(1) FROM app_user_referral WHERE referrer_user_id = u.id), 0) AS referrals_count
                FROM app_user u 
                LEFT JOIN wallet_account w ON w.user_id = u.id 
                LEFT JOIN user_presence p ON p.user_id = u.id 
                LEFT JOIN app_user ref_u ON ref_u.id = u.referred_by_user_id
                WHERE u.role <> 'admin' 
                ORDER BY u.created_at DESC LIMIT 500");
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
            await db.ExecuteAsync(@"
                UPDATE app_user 
                SET username=@Username, 
                    phone=@Phone, 
                    display_gender=@Gender, 
                    display_name=@Username,
                    assigned_persona_id=COALESCE(@AssignedPersonaId, assigned_persona_id, 'persona_role_kids_learning')
                WHERE id=@userId", new { userId, dto.Username, dto.Phone, dto.Gender, dto.AssignedPersonaId });
            var currentBalance = await db.ExecuteScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id = @userId", new { userId });
            var diff = dto.WalletBalance - currentBalance;
            if (diff != 0)
            {
                await db.ExecuteAsync("INSERT INTO wallet_transaction(user_id, txn_type, amount, note) VALUES(@userId, 'adjustment', @diff, 'Admin manual balance adjustment')", new { userId, diff });
            }
            await db.ExecuteAsync("INSERT INTO wallet_account(user_id, balance, currency, updated_at) VALUES(@userId, @WalletBalance, 'INR', now()) ON CONFLICT(user_id) DO UPDATE SET balance=@WalletBalance, updated_at=now();", new { userId, dto.WalletBalance });
            return Results.Ok(new { message = "User details updated successfully" });
        });

        app.MapPost("/api/admin/users/{userId:guid}/persona", async (Guid userId, AdminSetPersonaDto dto, IDbConnection db) =>
        {
            var personaId = string.IsNullOrWhiteSpace(dto.PersonaId) ? "persona_role_kids_learning" : dto.PersonaId;
            await db.ExecuteAsync("UPDATE app_user SET assigned_persona_id=@personaId WHERE id=@userId", new { userId, personaId });
            return Results.Ok(new { message = "User persona updated successfully", userId, personaId });
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
            var rows = await db.QueryAsync<dynamic>(@"
                SELECT 
                    c.id,
                    c.caller_user_id,
                    c.host_user_id,
                    c.room_name,
                    c.status,
                    c.rate_per_minute,
                    c.started_at,
                    c.connected_at,
                    c.ended_at,
                    c.billable_seconds,
                    c.total_amount,
                    c.end_reason,
                    c.call_type,
                    c.created_at,
                    c.llm_provider,
                    c.llm_model,
                    c.latency_telemetry,
                    COALESCE(c.telephony_provider, 'livekit') AS telephony_provider,
                    COALESCE(ROUND(EXTRACT(EPOCH FROM (COALESCE(c.ended_at, now()) - COALESCE(c.started_at, c.created_at)))), 0) AS duration_seconds,
                    cu.username AS caller_username,
                    cu.phone AS caller_phone,
                    cu.role AS caller_role,
                    CASE WHEN cu.profile_icon IN ('neutral_voice', 'admin_shield') OR cu.profile_icon IS NULL OR cu.profile_icon = '' THEN (CASE WHEN cu.role = 'admin' THEN '🛡️' WHEN cu.display_gender = 'female' THEN '👩' WHEN cu.display_gender = 'male' THEN '👨' ELSE '👤' END) ELSE cu.profile_icon END AS caller_icon,
                    hu.username AS host_username,
                    hu.phone AS host_phone,
                    hu.role AS host_role,
                    CASE WHEN hu.profile_icon IN ('neutral_voice', 'admin_shield') OR hu.profile_icon IS NULL OR hu.profile_icon = '' THEN (CASE WHEN hu.role = 'admin' THEN '🛡️' WHEN hu.display_gender = 'female' THEN '👩' WHEN hu.display_gender = 'male' THEN '👨' ELSE '👤' END) ELSE hu.profile_icon END AS host_icon,
                    (SELECT COUNT(*) FROM ai_call_message m WHERE m.call_session_id = c.id) AS message_count
                FROM call_session c 
                JOIN app_user cu ON cu.id=c.caller_user_id 
                JOIN app_user hu ON hu.id=c.host_user_id 
                ORDER BY c.created_at DESC 
                LIMIT 200");

            var results = rows.Select(r =>
            {
                string endReason = r.end_reason ?? "";
                string recordingFile = "";
                if (endReason.StartsWith("recording:"))
                {
                    recordingFile = endReason.Substring("recording:".Length);
                }

                string? recUrl = !string.IsNullOrEmpty(recordingFile) ? $"/api/ai/recordings/{recordingFile}" : null;
                string cType = r.call_type ?? (r.room_name != null && ((string)r.room_name).StartsWith("ai_call_") ? "ai_call" : (((string)r.room_name).StartsWith("admin_call_") ? "admin_call" : "user_call"));

                bool isAiCall = (string)r.host_role == "ai" || 
                                !string.IsNullOrEmpty((string)r.llm_provider) || 
                                cType == "ai_call" || 
                                (r.room_name != null && ((string)r.room_name).StartsWith("ai_call_")) ||
                                (r.caller_user_id == r.host_user_id);

                var persona = ResolveAiPersona((string)r.room_name);

                string hName = isAiCall ? persona.Name : (string)r.host_username;
                string hPhone = isAiCall ? (!string.IsNullOrEmpty((string)r.llm_provider) ? $"LLM: {((string)r.llm_provider).ToUpper()}" : "WebRTC Agent") : (string)r.host_phone;
                string hRole = isAiCall ? persona.Role : (string)r.host_role;
                string hIcon = isAiCall ? persona.Icon : (string)r.host_icon;

                return new Dictionary<string, object?>
                {
                    ["id"] = r.id,
                    ["callId"] = r.id,
                    ["call_id"] = r.id,
                    ["callerUserId"] = r.caller_user_id,
                    ["caller_user_id"] = r.caller_user_id,
                    ["hostUserId"] = r.host_user_id,
                    ["host_user_id"] = r.host_user_id,
                    ["roomName"] = r.room_name,
                    ["room_name"] = r.room_name,
                    ["status"] = r.status,
                    ["ratePerMinute"] = r.rate_per_minute,
                    ["rate_per_minute"] = r.rate_per_minute,
                    ["startedAt"] = r.started_at,
                    ["started_at"] = r.started_at,
                    ["connectedAt"] = r.connected_at,
                    ["connected_at"] = r.connected_at,
                    ["endedAt"] = r.ended_at,
                    ["ended_at"] = r.ended_at,
                    ["durationSeconds"] = r.duration_seconds,
                    ["duration_seconds"] = r.duration_seconds,
                    ["billableSeconds"] = r.billable_seconds,
                    ["billable_seconds"] = r.billable_seconds,
                    ["totalAmount"] = r.total_amount,
                    ["total_amount"] = r.total_amount,
                    ["endReason"] = r.end_reason,
                    ["end_reason"] = r.end_reason,
                    ["callType"] = cType,
                    ["call_type"] = cType,
                    ["createdAt"] = r.created_at,
                    ["created_at"] = r.created_at,
                    ["callerUsername"] = r.caller_username,
                    ["caller_username"] = r.caller_username,
                    ["callerName"] = r.caller_username,
                    ["caller_name"] = r.caller_username,
                    ["callerPhone"] = r.caller_phone,
                    ["caller_phone"] = r.caller_phone,
                    ["callerRole"] = r.caller_role,
                    ["caller_role"] = r.caller_role,
                    ["callerIcon"] = r.caller_icon,
                    ["caller_icon"] = r.caller_icon,
                    ["hostUsername"] = hName,
                    ["host_username"] = hName,
                    ["hostName"] = hName,
                    ["host_name"] = hName,
                    ["hostPhone"] = hPhone,
                    ["host_phone"] = hPhone,
                    ["hostRole"] = hRole,
                    ["host_role"] = hRole,
                    ["hostIcon"] = hIcon,
                    ["host_icon"] = hIcon,
                    ["messageCount"] = r.message_count,
                    ["message_count"] = r.message_count,
                    ["llmProvider"] = r.llm_provider,
                    ["llm_provider"] = r.llm_provider,
                    ["llmModel"] = r.llm_model,
                    ["llm_model"] = r.llm_model,
                    ["latencyTelemetry"] = r.latency_telemetry,
                    ["latency_telemetry"] = r.latency_telemetry,
                    ["telephonyProvider"] = r.telephony_provider ?? "livekit",
                    ["telephony_provider"] = r.telephony_provider ?? "livekit",
                    ["recordingFile"] = recordingFile,
                    ["recording_file"] = recordingFile,
                    ["recordingUrl"] = recUrl,
                    ["recording_url"] = recUrl
                };
            });

            return Results.Ok(results);
        });

        // Get single call full details, audio, and transcript script
        app.MapGet("/api/admin/calls/{callId}/details", async (string callId, IDbConnection db) =>
        {
            Guid sessionId = Guid.Empty;
            if (Guid.TryParse(callId, out var g)) sessionId = g;

            var sqlSession = @"
                SELECT 
                    cs.*,
                    COALESCE(ROUND(EXTRACT(EPOCH FROM (COALESCE(cs.ended_at, now()) - COALESCE(cs.started_at, cs.created_at)))), 0) AS duration_seconds,
                    cu.username AS caller_name,
                    cu.phone AS caller_phone,
                    cu.role AS caller_role,
                    hu.username AS host_name,
                    hu.phone AS host_phone,
                    hu.role AS host_role
                FROM call_session cs
                JOIN app_user cu ON cu.id = cs.caller_user_id
                JOIN app_user hu ON hu.id = cs.host_user_id
                WHERE cs.id = @sessionId OR cs.room_name = @callId
                LIMIT 1";

            var session = await db.QueryFirstOrDefaultAsync<dynamic>(sqlSession, new { sessionId, callId });
            if (session == null) return Results.NotFound(new { error = "Call session not found" });

            Guid realCallId = session.id;

            // Transcript / dialogue
            var messages = await db.QueryAsync<dynamic>(@"
                SELECT role, content, created_at
                FROM ai_call_message
                WHERE call_session_id = @realCallId
                ORDER BY created_at ASC",
                new { realCallId });

            // Call events if no messages
            var events = await db.QueryAsync<dynamic>(@"
                SELECT event_type, created_at
                FROM call_event
                WHERE call_session_id = @realCallId
                ORDER BY created_at ASC",
                new { realCallId });

            string endReason = session.end_reason ?? "";
            string recordingFile = "";
            if (endReason.StartsWith("recording:"))
            {
                recordingFile = endReason.Substring("recording:".Length);
            }

            bool isAiCall = (string)session.host_role == "ai" || 
                            !string.IsNullOrEmpty((string)session.llm_provider) || 
                            ((string)session.room_name ?? "").StartsWith("ai_call_") || 
                            session.caller_user_id == session.host_user_id;
            var persona = ResolveAiPersona((string)session.room_name);

            return Results.Ok(new
            {
                session = new
                {
                    id = session.id,
                    roomName = session.room_name,
                    status = session.status,
                    startedAt = session.started_at,
                    endedAt = session.ended_at,
                    durationSeconds = session.duration_seconds,
                    ratePerMinute = session.rate_per_minute,
                    totalAmount = session.total_amount,
                    callerName = session.caller_name,
                    callerPhone = session.caller_phone,
                    callerRole = session.caller_role,
                    hostName = isAiCall ? persona.Name : session.host_name,
                    hostPhone = isAiCall ? (!string.IsNullOrEmpty((string)session.llm_provider) ? $"LLM: {((string)session.llm_provider).ToUpper()}" : "WebRTC Agent") : session.host_phone,
                    hostRole = isAiCall ? persona.Role : session.host_role,
                    hostIcon = isAiCall ? persona.Icon : (session.host_icon ?? "👤"),
                    personaName = isAiCall ? persona.Name : null,
                    personaIcon = isAiCall ? persona.Icon : null,
                    personaRole = isAiCall ? persona.Role : null,
                    llmProvider = session.llm_provider,
                    llmModel = session.llm_model,
                    latencyTelemetry = session.latency_telemetry,
                    recordingFile = recordingFile,
                    audioUrl = !string.IsNullOrEmpty(recordingFile) ? $"/api/ai/recordings/{recordingFile}" : null
                },
                transcript = messages.Select(m => new {
                    role = (string)m.role,
                    content = (string)m.content,
                    createdAt = m.created_at
                }),
                events = events.Select(e => new {
                    eventType = (string)e.event_type,
                    createdAt = e.created_at
                })
            });
        });

        // Admin dials any user directly via WebRTC LiveKit
        app.MapPost("/api/admin/calls/start-user-call", async (AdminStartUserCallDto dto, IDbConnection db, LiveKitTokenService livekit, CallNotifier notifier, IHubContext<CallHub> hubContext, HttpContext httpContext) =>
        {
            var target = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, username, phone, status FROM app_user WHERE id=@TargetUserId",
                new { dto.TargetUserId }
            );
            if (target == null) return Results.NotFound("Target user not found");

            var adminId = await db.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM app_user WHERE role='admin' ORDER BY created_at ASC LIMIT 1"
            ) ?? (await db.ExecuteScalarAsync<Guid?>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1"));

            var callerId = adminId ?? Guid.NewGuid();
            var room = $"admin_call_{dto.TargetUserId:N}_{Guid.NewGuid():N}";

            var callId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, started_at) 
                VALUES(@callerId, @targetUserId, @room, 'ringing', 0, now()) RETURNING id",
                new { callerId, targetUserId = dto.TargetUserId, room }
            );

            await db.ExecuteAsync("INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, 'admin_call_created', @callerId)", new { callId, callerId });

            // Notify user via FCM & SignalR
            var latestToken = await db.ExecuteScalarAsync<string?>("SELECT fcm_token FROM user_device WHERE user_id=@TargetUserId ORDER BY last_seen_at DESC LIMIT 1", new { dto.TargetUserId });
            await notifier.NotifyIncomingCallAsync(db, dto.TargetUserId, callId, "Admin Support", latestToken);
            await hubContext.Clients.All.SendAsync("callIncoming", new { callId = callId.ToString(), callerId = callerId.ToString(), callerName = "Admin Support", roomName = room });

            // LiveKit credentials
            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='livekit' AND is_active=true");
            var config = string.IsNullOrWhiteSpace(credJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(credJson);
            var apiKey = config != null && config.TryGetValue("apiKey", out var ak) ? ak : "devkey";
            var apiSecret = config != null && config.TryGetValue("apiSecret", out var asec) ? asec : "devsecretkeyshouldbe48characterslongforsecurity!";
            var liveKitUrl = config != null && config.TryGetValue("url", out var u) ? u : "ws://localhost:7880";
            if (liveKitUrl.Contains("localhost"))
            {
                var requestHost = httpContext.Request.Host.Host;
                liveKitUrl = liveKitUrl.Replace("localhost", requestHost);
            }

            var adminToken = livekit.CreateToken(apiKey, apiSecret, room, callerId.ToString());

            return Results.Ok(new 
            { 
                callId, 
                roomName = room, 
                liveKitUrl, 
                token = adminToken, 
                targetUserId = dto.TargetUserId,
                targetUsername = (string)(target.username ?? target.phone ?? "User"),
                targetPhone = (string)(target.phone ?? "")
            });
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

            // Trigger FCM dispatch if not scheduled for later
            if (!dto.ScheduleTime.HasValue || dto.ScheduleTime.Value <= DateTime.UtcNow)
            {
                var status = "success";
                var logsList = new List<string>();
                try
                {
                    if (dto.TargetType == "all")
                    {
                        var res = await fcm.SendNotificationAsync(db, "topic:hello24_all", dto.Title, dto.Body, dto.ImageUrl, null);
                        logsList.Add($"Topic (All): Success={res.success}, Log={res.log}");
                        if (!res.success) status = "failed";
                    }
                    else if (dto.TargetType == "male")
                    {
                        var res = await fcm.SendNotificationAsync(db, "topic:hello24_male", dto.Title, dto.Body, dto.ImageUrl, null);
                        logsList.Add($"Topic (Male): Success={res.success}, Log={res.log}");
                        if (!res.success) status = "failed";
                    }
                    else if (dto.TargetType == "female")
                    {
                        var res = await fcm.SendNotificationAsync(db, "topic:hello24_female", dto.Title, dto.Body, dto.ImageUrl, null);
                        logsList.Add($"Topic (Female): Success={res.success}, Log={res.log}");
                        if (!res.success) status = "failed";
                    }
                    else if (dto.TargetType == "single" && dto.TargetUserId != null)
                    {
                        var tokens = (await db.QueryAsync<string>("SELECT fcm_token FROM user_device WHERE user_id=@TargetUserId AND fcm_token IS NOT NULL AND fcm_token <> ''", new { dto.TargetUserId })).ToList();
                        if (tokens.Count == 0)
                        {
                            status = "failed";
                            logsList.Add("No registered FCM tokens found for the targeted user.");
                        }
                        else
                        {
                            foreach (var token in tokens)
                            {
                                var res = await fcm.SendNotificationAsync(db, token, dto.Title, dto.Body, dto.ImageUrl, null);
                                logsList.Add($"Token ({token.Substring(0, Math.Min(10, token.Length))}...): Success={res.success}, Log={res.log}");
                                if (!res.success) status = "failed";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    status = "failed";
                    logsList.Add($"Unhandled Campaign Exception: {ex.Message}");
                }

                // Update campaign row with execution status & logs
                await db.ExecuteAsync("UPDATE notification_campaign SET status=@status, fcm_logs=@logs WHERE id=@id", new { id, status, logs = string.Join("\n", logsList) });
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

public record AdminUpdateUserDto(string Username, string Phone, string Gender, decimal WalletBalance, string? AssignedPersonaId = null);
public record AdminSetPersonaDto(string PersonaId);
public record AdminStartUserCallDto(Guid TargetUserId);
