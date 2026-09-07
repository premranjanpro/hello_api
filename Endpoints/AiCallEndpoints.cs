using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AiCallEndpoints
{
    private static async Task EnsureTablesAsync(IDbConnection db)
    {
        await db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ai_call_message (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                call_session_id UUID NULL,
                user_id UUID NOT NULL,
                persona_id VARCHAR(100) NOT NULL,
                role VARCHAR(20) NOT NULL,
                content TEXT NOT NULL,
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_ai_call_msg_user ON ai_call_message(user_id, persona_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS ai_user_memory (
                user_id UUID NOT NULL,
                persona_id VARCHAR(100) NOT NULL,
                memory_summary TEXT NOT NULL DEFAULT '',
                last_call_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                total_calls INT NOT NULL DEFAULT 1,
                updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                PRIMARY KEY (user_id, persona_id)
            );

            CREATE TABLE IF NOT EXISTS ai_call_rating (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                call_session_id UUID NOT NULL,
                user_id UUID NOT NULL,
                persona_id VARCHAR(100) NOT NULL,
                rating INT NOT NULL CHECK (rating >= 1 AND rating <= 5),
                takeaways TEXT NOT NULL DEFAULT '',
                feedback TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_ai_call_rating_call ON ai_call_rating(call_session_id);

            CREATE TABLE IF NOT EXISTS ai_call_structured_data (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                call_session_id UUID NOT NULL,
                user_id UUID NOT NULL,
                persona_id VARCHAR(100) NOT NULL,
                data_type VARCHAR(50) NOT NULL,
                structured_json JSONB NOT NULL,
                summary TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_ai_call_structured_call ON ai_call_structured_data(call_session_id);
        ");
    }

    public static void MapAiCallEndpoints(this WebApplication app)
    {
        // 1. Check AI feature visibility for current user
        app.MapGet("/api/ai/visibility", async (ClaimsPrincipal? cp, IDbConnection db) =>
        {
            var mode = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_visibility_mode'") ?? "all";

            if (mode == "none") return Results.Ok(new { isVisible = false, mode });
            if (mode == "all") return Results.Ok(new { isVisible = true, mode });

            var userId = cp != null ? CurrentUser.Id(cp) : Guid.Empty;
            if (userId == Guid.Empty) return Results.Ok(new { isVisible = false, mode, reason = "Authentication required for targeted access" });

            var userPhone = await db.ExecuteScalarAsync<string?>("SELECT phone FROM app_user WHERE id=@userId", new { userId }) ?? "";
            var userIdStr = userId.ToString();

            if (mode == "whitelist")
            {
                var whitelist = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_whitelist_users'") ?? "";
                var isAllowed = whitelist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(entry => entry.Equals(userPhone, StringComparison.OrdinalIgnoreCase) || entry.Equals(userIdStr, StringComparison.OrdinalIgnoreCase));
                return Results.Ok(new { isVisible = isAllowed, mode });
            }

            if (mode == "blacklist")
            {
                var blacklist = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_blacklist_users'") ?? "";
                var isBlocked = blacklist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(entry => entry.Equals(userPhone, StringComparison.OrdinalIgnoreCase) || entry.Equals(userIdStr, StringComparison.OrdinalIgnoreCase));
                return Results.Ok(new { isVisible = !isBlocked, mode });
            }

            return Results.Ok(new { isVisible = true, mode });
        });

        // 2. Available AI Personas catalog & options (Standard persona_role_* naming)
        app.MapGet("/api/ai/options", () =>
        {
            var personas = new[]
            {
                new { id = "persona_role_cab_booking", name = "Cab Booking Assistant", subtitle = "Voice Cab Booking & Live Rate Card", icon = "local_taxi", color = "#F59E0B", category = "Travel & Cab" },
                new { id = "persona_role_hr_job", name = "HR Job Screening Officer", subtitle = "Job Inquiries & Screening", icon = "badge", color = "#0284C7", category = "Career & Hiring" },
                new { id = "persona_role_hr_interview", name = "HR Job Interviewer", subtitle = "Mock Interviews & Feedback", icon = "work_outline", color = "#3B82F6", category = "Career" },
                new { id = "persona_role_kids_learning", name = "Kids Learning Buddy", subtitle = "Stories, Rhymes & Curiosity", icon = "child_care", color = "#F59E0B", category = "Education" },
                new { id = "persona_role_romantic_chat", name = "Romantic Companion", subtitle = "Sweet, Caring & Comforting", icon = "favorite", color = "#EC4899", category = "Companionship" },
                new { id = "persona_role_parents_care", name = "Parents Care Assistant", subtitle = "Health, Routine & Daily Checks", icon = "health_and_safety", color = "#10B981", category = "Family & Health" },
                new { id = "persona_role_english_tutor", name = "Spoken English Coach", subtitle = "Practice English with Hinglish guidance", icon = "school", color = "#8B5CF6", category = "Learning" }
            };

            var languages = new[]
            {
                new { id = "hindi", name = "हिन्दी (Hindi)", flag = "🇮🇳" },
                new { id = "hinglish", name = "Hinglish", flag = "🗣️" },
                new { id = "english", name = "English (India)", flag = "🇬🇧" }
            };

            var genders = new[]
            {
                new { id = "female", title = "Female Character", voice = "Swara / Neerja", icon = "woman" },
                new { id = "male", title = "Male Character", voice = "Madhur / Prabhat", icon = "man" }
            };

            return Results.Ok(new { success = true, personas, languages, genders });
        });

        // 3. Start AI Voice Call Session
        app.MapPost("/api/ai/calls/start", async (StartAiCallDto dto, ClaimsPrincipal cp, IDbConnection db, LiveKitTokenService livekit, HttpContext httpContext) =>
        {
            await EnsureTablesAsync(db);

            var callerId = CurrentUser.Id(cp);
            if (callerId == Guid.Empty) return Results.Unauthorized();

            // Check visibility permission
            var mode = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_visibility_mode'") ?? "all";
            if (mode == "none") return Results.Problem("AI Calling is currently disabled by administrator.", statusCode: 403);

            if (mode == "whitelist" || mode == "blacklist")
            {
                var userPhone = await db.ExecuteScalarAsync<string?>("SELECT phone FROM app_user WHERE id=@callerId", new { callerId }) ?? "";
                var targetList = await db.ExecuteScalarAsync<string?>($"SELECT value FROM app_setting WHERE key='ai_{mode}_users'") ?? "";
                var isInList = targetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(entry => entry.Equals(userPhone, StringComparison.OrdinalIgnoreCase) || entry.Equals(callerId.ToString(), StringComparison.OrdinalIgnoreCase));

                if (mode == "whitelist" && !isInList) return Results.Problem("AI Calling is currently restricted.", statusCode: 403);
                if (mode == "blacklist" && isInList) return Results.Problem("AI Calling is not available for this account.", statusCode: 403);
            }

            var cleanPersonaId = dto.PersonaId.StartsWith("persona_role_") ? dto.PersonaId : $"persona_role_{dto.PersonaId}";
            var roomName = $"ai_call_{cleanPersonaId}_{Guid.NewGuid():N}";

            // Insert into call_session for tracking duration and billing
            var callId = await db.ExecuteScalarAsync<Guid>(
                "INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, started_at) " +
                "VALUES(@callerId, @callerId, @roomName, 'connected', 0, now()) RETURNING id",
                new { callerId, roomName }
            );

            // Fetch LiveKit credentials
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

            // Build metadata payload for Python Agent
            var metadataObj = new
            {
                call_id = callId.ToString(),
                caller_id = callerId.ToString(),
                persona_id = cleanPersonaId,
                language = dto.Language,
                gender = dto.Gender,
                tts_provider = dto.TtsProvider,
                pitch = dto.Pitch,
                rate = dto.Rate,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            var metadataJson = JsonSerializer.Serialize(metadataObj);

            // Generate JWT token with metadata
            var token = livekit.CreateToken(apiKey, apiSecret, roomName, callerId.ToString(), metadataJson);

            // Log event
            await db.ExecuteAsync("INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, 'ai_call_started', @callerId)", new { callId, callerId });

            return Results.Ok(new
            {
                callId,
                roomName,
                liveKitUrl,
                token,
                personaId = cleanPersonaId,
                language = dto.Language,
                gender = dto.Gender
            });
        }).RequireAuthorization();

        // 3b. Start AI Voice Call Session for Admin (Browser Dashboard)
        app.MapPost("/api/admin/ai/calls/start", async (StartAiCallDto dto, IDbConnection db, LiveKitTokenService livekit, HttpContext httpContext) =>
        {
            await EnsureTablesAsync(db);

            // Find or create admin user for caller ID foreign key
            var adminId = await db.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM app_user WHERE role='admin' ORDER BY created_at ASC LIMIT 1"
            );

            if (!adminId.HasValue)
            {
                adminId = await db.ExecuteScalarAsync<Guid?>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");
                if (!adminId.HasValue)
                {
                    adminId = await db.ExecuteScalarAsync<Guid>(
                        "INSERT INTO app_user(username, phone, role, status) VALUES('Admin', 'admin', 'admin', 'active') RETURNING id"
                    );
                }
            }

            var callerId = adminId.Value;
            var cleanPersonaId = dto.PersonaId.StartsWith("persona_role_") ? dto.PersonaId : $"persona_role_{dto.PersonaId}";
            var roomName = $"ai_call_{cleanPersonaId}_{Guid.NewGuid():N}";

            // Insert into call_session for tracking duration and billing
            var callId = await db.ExecuteScalarAsync<Guid>(
                "INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, started_at) " +
                "VALUES(@callerId, @callerId, @roomName, 'connected', 0, now()) RETURNING id",
                new { callerId, roomName }
            );

            // Fetch LiveKit credentials
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

            // Build metadata payload for Python Agent
            var metadataObj = new
            {
                call_id = callId.ToString(),
                caller_id = callerId.ToString(),
                persona_id = cleanPersonaId,
                language = dto.Language,
                gender = dto.Gender,
                tts_provider = dto.TtsProvider,
                pitch = dto.Pitch,
                rate = dto.Rate,
                is_admin = true,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            var metadataJson = JsonSerializer.Serialize(metadataObj);

            // Generate JWT token with metadata
            var token = livekit.CreateToken(apiKey, apiSecret, roomName, $"admin_{callerId}", metadataJson);

            // Log event
            await db.ExecuteAsync("INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, 'admin_ai_call_started', @callerId)", new { callId, callerId });

            return Results.Ok(new
            {
                callId,
                roomName,
                liveKitUrl,
                token,
                personaId = cleanPersonaId,
                language = dto.Language,
                gender = dto.Gender
            });
        });

        // 4. End AI Call & Log Duration + Audio Recording
        app.MapPost("/api/ai/calls/{callId:guid}/end", async (Guid callId, EndAiCallDto dto, IDbConnection db) =>
        {
            var session = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId", new { callId });
            if (session == null) return Results.NotFound("Call session not found");

            var durationSeconds = dto.DurationSeconds > 0 ? dto.DurationSeconds : 0;
            var endReason = string.IsNullOrWhiteSpace(dto.RecordingFile) ? "ai_completed" : $"recording:{dto.RecordingFile}";

            var llmProvider = string.IsNullOrWhiteSpace(dto.LlmProvider) ? "groq" : dto.LlmProvider;
            var llmModel = string.IsNullOrWhiteSpace(dto.LlmModel) ? "qwen/qwen3.8-27b" : dto.LlmModel;
            var latencyTelemetry = string.IsNullOrWhiteSpace(dto.LatencyTelemetry) ? "{}" : dto.LatencyTelemetry;

            await db.ExecuteAsync(@"
                UPDATE call_session 
                SET status='ended', 
                    ended_at=now(), 
                    end_reason=@endReason,
                    llm_provider=@llmProvider,
                    llm_model=@llmModel,
                    latency_telemetry=@latencyTelemetry::jsonb
                WHERE id=@callId",
                new { callId, endReason, llmProvider, llmModel, latencyTelemetry }
            );

            await db.ExecuteAsync(
                "INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, @eventType, @callerId)",
                new { callId, eventType = $"ai_duration:{durationSeconds}s", callerId = (Guid)session.caller_user_id }
            );

            return Results.Ok(new { success = true, callId, durationSeconds });
        });

        // 4b. Rate AI Call Session & Save Takeaways
        app.MapPost("/api/ai/calls/{callId:guid}/rate", async (Guid callId, RateAiCallDto dto, ClaimsPrincipal? cp, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var session = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId", new { callId });
            if (session == null) return Results.NotFound("Call session not found");

            var userId = session.caller_user_id != null ? (Guid)session.caller_user_id : (cp != null ? CurrentUser.Id(cp) : Guid.Empty);
            var rating = Math.Clamp(dto.Rating, 1, 5);
            var takeaways = dto.Takeaways ?? "";
            var feedback = dto.Feedback ?? "";
            var personaId = dto.PersonaId ?? "ai";

            await db.ExecuteAsync(@"
                INSERT INTO ai_call_rating(call_session_id, user_id, persona_id, rating, takeaways, feedback, created_at)
                VALUES(@callId, @userId, @personaId, @rating, @takeaways, @feedback, now())",
                new { callId, userId, personaId, rating, takeaways, feedback }
            );

            return Results.Ok(new { success = true, callId, rating });
        });

        // 4c. Get User's AI Call History & Past Conversations
        app.MapGet("/api/ai/calls/my-history", async (string? userId, ClaimsPrincipal? cp, IDbConnection db) =>
        {
            var uid = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var clean = userId.StartsWith("admin_") ? userId.Substring(6) : userId;
                Guid.TryParse(clean, out uid);
            }
            if (uid == Guid.Empty && cp != null)
            {
                try { uid = CurrentUser.Id(cp); } catch (Exception) {}
            }

            if (uid == Guid.Empty)
                return Results.Ok(Array.Empty<object>());

            await EnsureTablesAsync(db);
            var calls = await db.QueryAsync<dynamic>(@"
                SELECT 
                    c.id AS call_id,
                    c.status,
                    c.started_at,
                    c.ended_at,
                    c.end_reason,
                    c.llm_provider,
                    c.llm_model,
                    COALESCE(ROUND(EXTRACT(EPOCH FROM (COALESCE(c.ended_at, now()) - COALESCE(c.started_at, c.created_at)))), 0) AS duration_seconds,
                    c.created_at,
                    COALESCE(m.persona_id, 'persona_role_cab_booking') AS persona_id,
                    COALESCE(mem.memory_summary, '') AS memory_summary,
                    r.rating,
                    r.takeaways,
                    CASE 
                        WHEN c.end_reason LIKE 'recording:%' THEN REPLACE(c.end_reason, 'recording:', '')
                        ELSE CONCAT(c.id::text, '.wav')
                    END AS recording_file
                FROM call_session c
                LEFT JOIN (
                    SELECT DISTINCT ON (call_session_id) call_session_id, persona_id 
                    FROM ai_call_message 
                    WHERE call_session_id IS NOT NULL 
                    ORDER BY call_session_id, created_at DESC
                ) m ON m.call_session_id = c.id
                LEFT JOIN ai_user_memory mem ON mem.user_id = c.caller_user_id AND mem.persona_id = m.persona_id
                LEFT JOIN ai_call_rating r ON r.call_session_id = c.id
                WHERE c.caller_user_id = @uid AND (c.call_type = 'ai' OR c.host_user_id IS NULL)
                ORDER BY c.created_at DESC
                LIMIT 50",
                new { uid });

            return Results.Ok(calls);
        });

        // 5. Get User Memory & Previous Call History Context
        app.MapGet("/api/ai/memory", async (string? userId, string? personaId, IDbConnection db) =>
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(personaId))
                return Results.Ok(new { hasHistory = false, memorySummary = "", recentTurns = Array.Empty<object>() });

            await EnsureTablesAsync(db);

            var cleanUserId = userId.StartsWith("admin_") ? userId.Substring(6) : userId;
            if (!Guid.TryParse(cleanUserId, out var userGuid))
            {
                // Fallback: Check if userId is actually a callId
                if (Guid.TryParse(userId, out var possibleCallId))
                {
                    var callerId = await db.ExecuteScalarAsync<Guid?>("SELECT caller_user_id FROM call_session WHERE id=@possibleCallId", new { possibleCallId });
                    if (callerId.HasValue) userGuid = callerId.Value;
                }
            }

            if (userGuid == Guid.Empty)
                return Results.Ok(new { hasHistory = false, memorySummary = "", recentTurns = Array.Empty<object>() });

            var cleanPersonaId = personaId.StartsWith("persona_role_") ? personaId : $"persona_role_{personaId}";

            var memory = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT * FROM ai_user_memory WHERE user_id=@userGuid AND persona_id=@cleanPersonaId",
                new { userGuid, cleanPersonaId });

            var turns = await db.QueryAsync<dynamic>(
                "SELECT role, content FROM ai_call_message WHERE user_id=@userGuid AND persona_id=@cleanPersonaId ORDER BY created_at DESC LIMIT 10",
                new { userGuid, cleanPersonaId });

            var recentTurns = turns.Reverse().Select(t => new { role = (string)t.role, content = (string)t.content }).ToList();
            var hasHistory = memory != null || recentTurns.Count > 0;
            var summary = memory != null ? (string)memory.memory_summary : "";
            var totalCalls = memory != null ? (int)memory.total_calls : (recentTurns.Count > 0 ? 1 : 0);

            return Results.Ok(new
            {
                hasHistory,
                totalCalls,
                memorySummary = summary,
                recentTurns
            });
        });

        // 6a. Append Single Turn in Real-Time (Fail-Safe for long calls so no data is lost)
        app.MapPost("/api/ai/memory/append-turn", async (AppendTurnDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            if (string.IsNullOrWhiteSpace(dto.Content))
                return Results.Ok(new { success = true });

            Guid.TryParse(dto.CallId, out var callGuid);
            var rawUser = (dto.UserId ?? "").StartsWith("admin_") ? (dto.UserId ?? "").Substring(6) : (dto.UserId ?? "");
            if (!Guid.TryParse(rawUser, out var userGuid) && callGuid != Guid.Empty)
            {
                var cid = await db.ExecuteScalarAsync<Guid?>("SELECT caller_user_id FROM call_session WHERE id=@callGuid", new { callGuid });
                if (cid.HasValue) userGuid = cid.Value;
            }

            if (userGuid != Guid.Empty)
            {
                var cleanPersonaId = (dto.PersonaId ?? "").StartsWith("persona_role_") ? dto.PersonaId : $"persona_role_{dto.PersonaId}";
                await db.ExecuteAsync(
                    "INSERT INTO ai_call_message(call_session_id, user_id, persona_id, role, content, created_at) " +
                    "VALUES(@callGuid, @userGuid, @cleanPersonaId, @Role, @Content, now())",
                    new { callGuid = (callGuid == Guid.Empty ? (Guid?)null : callGuid), userGuid, cleanPersonaId, dto.Role, dto.Content }
                );
            }
            return Results.Ok(new { success = true });
        });

        // 6b. Save User Memory & Call Transcript Turns (End of Call or Checkpoint)
        app.MapPost("/api/ai/memory/save", async (SaveAiMemoryDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            Guid.TryParse(dto.CallId, out var callGuid);
            var rawUser = (dto.UserId ?? "").StartsWith("admin_") ? (dto.UserId ?? "").Substring(6) : (dto.UserId ?? "");
            if (!Guid.TryParse(rawUser, out var userGuid) && callGuid != Guid.Empty)
            {
                var cid = await db.ExecuteScalarAsync<Guid?>("SELECT caller_user_id FROM call_session WHERE id=@callGuid", new { callGuid });
                if (cid.HasValue) userGuid = cid.Value;
            }

            if (userGuid == Guid.Empty)
                return Results.Ok(new { success = false, reason = "User GUID could not be resolved" });

            var cleanPersonaId = (dto.PersonaId ?? "").StartsWith("persona_role_") ? dto.PersonaId : $"persona_role_{dto.PersonaId}";

            if (dto.Turns != null && dto.Turns.Count > 0)
            {
                foreach (var turn in dto.Turns)
                {
                    if (!string.IsNullOrWhiteSpace(turn.Content))
                    {
                        await db.ExecuteAsync(
                            "INSERT INTO ai_call_message(call_session_id, user_id, persona_id, role, content, created_at) " +
                            "VALUES(@callGuid, @userGuid, @cleanPersonaId, @Role, @Content, now())",
                            new { callGuid = (callGuid == Guid.Empty ? (Guid?)null : callGuid), userGuid, cleanPersonaId, turn.Role, turn.Content }
                        );
                    }
                }
            }

            var summary = dto.MemorySummary ?? "";
            await db.ExecuteAsync(@"
                INSERT INTO ai_user_memory(user_id, persona_id, memory_summary, last_call_at, total_calls, updated_at)
                VALUES(@userGuid, @cleanPersonaId, @summary, now(), 1, now())
                ON CONFLICT (user_id, persona_id) DO UPDATE 
                SET memory_summary = CASE WHEN @summary <> '' THEN @summary ELSE ai_user_memory.memory_summary END,
                    total_calls = ai_user_memory.total_calls + 1,
                    last_call_at = now(),
                    updated_at = now()",
                new { userGuid, cleanPersonaId, summary });

            return Results.Ok(new { success = true, savedTurns = dto.Turns?.Count ?? 0 });
        });

        // 7. Get Call Transcript for Admin
        app.MapGet("/api/admin/ai/calls/{callId:guid}/transcript", async (Guid callId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var messages = await db.QueryAsync<dynamic>(
                "SELECT id, role, content, created_at FROM ai_call_message WHERE call_session_id=@callId ORDER BY created_at ASC",
                new { callId });
            return Results.Ok(messages);
        });

        // 8. Admin Settings for AI Visibility & Controls
        app.MapGet("/api/admin/ai/settings", async (IDbConnection db) =>
        {
            var mode = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_visibility_mode'") ?? "all";
            var whitelist = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_whitelist_users'") ?? "";
            var blacklist = await db.ExecuteScalarAsync<string?>("SELECT value FROM app_setting WHERE key='ai_blacklist_users'") ?? "";
            return Results.Ok(new { mode, whitelist, blacklist });
        });

        app.MapPost("/api/admin/ai/settings", async (AiSettingsDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("INSERT INTO app_setting(key, value, updated_at) VALUES('ai_visibility_mode', @Mode, now()) ON CONFLICT(key) DO UPDATE SET value=@Mode, updated_at=now()", new { dto.Mode });
            await db.ExecuteAsync("INSERT INTO app_setting(key, value, updated_at) VALUES('ai_whitelist_users', @Whitelist, now()) ON CONFLICT(key) DO UPDATE SET value=@Whitelist, updated_at=now()", new { Whitelist = dto.Whitelist ?? "" });
            await db.ExecuteAsync("INSERT INTO app_setting(key, value, updated_at) VALUES('ai_blacklist_users', @Blacklist, now()) ON CONFLICT(key) DO UPDATE SET value=@Blacklist, updated_at=now()", new { Blacklist = dto.Blacklist ?? "" });
            return Results.Ok(new { message = "AI settings updated successfully" });
        });

        // 9. Admin AI Call History with Recordings, Ratings & Structured Data
        app.MapGet("/api/admin/ai/calls", async (IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            var sql = @"
                SELECT 
                    cs.id AS call_id,
                    cs.room_name,
                    cs.status,
                    cs.started_at,
                    cs.ended_at,
                    cs.end_reason,
                    COALESCE(ROUND(EXTRACT(EPOCH FROM (cs.ended_at - cs.started_at))), 0) AS duration_seconds,
                    u.phone AS caller_phone,
                    COALESCE(u.username, u.phone) AS caller_name,
                    r.rating,
                    r.takeaways,
                    r.feedback,
                    sd.data_type,
                    sd.structured_json::text AS structured_json_text,
                    sd.summary AS structured_summary
                FROM call_session cs
                JOIN app_user u ON u.id = cs.caller_user_id
                LEFT JOIN ai_call_rating r ON r.call_session_id = cs.id
                LEFT JOIN ai_call_structured_data sd ON sd.call_session_id = cs.id
                WHERE cs.room_name LIKE 'ai_call_%'
                ORDER BY cs.started_at DESC
                LIMIT 100";

            var rows = await db.QueryAsync<dynamic>(sql);
            var results = rows.Select(r =>
            {
                string endReason = r.end_reason ?? "";
                string recordingFile = "";
                if (endReason.StartsWith("recording:"))
                {
                    recordingFile = endReason.Substring("recording:".Length);
                }

                // Extract persona from room_name e.g. ai_call_persona_role_cab_booking_...
                var room = (string)r.room_name;
                var persona = "ai";
                if (room.StartsWith("ai_call_"))
                {
                    var afterPrefix = room.Substring("ai_call_".Length);
                    var lastUnderscore = afterPrefix.LastIndexOf('_');
                    persona = lastUnderscore > 0 ? afterPrefix.Substring(0, lastUnderscore) : afterPrefix;
                }

                return new
                {
                    id = r.call_id,
                    callId = r.call_id,
                    roomName = r.room_name,
                    callerName = r.caller_name,
                    callerPhone = r.caller_phone,
                    persona = persona,
                    personaId = persona,
                    status = r.status,
                    startedAt = r.started_at,
                    endedAt = r.ended_at,
                    durationSeconds = r.duration_seconds,
                    recordingFile = recordingFile,
                    recordingUrl = string.IsNullOrEmpty(recordingFile) ? null : $"/api/ai/recordings/{recordingFile}",
                    rating = r.rating != null ? (int)r.rating : (int?)null,
                    takeaways = (string)(r.takeaways ?? ""),
                    feedback = (string)(r.feedback ?? ""),
                    dataType = (string)(r.data_type ?? ""),
                    structuredJson = (string)(r.structured_json_text ?? ""),
                    structuredSummary = (string)(r.structured_summary ?? "")
                };
            });

            return Results.Ok(results);
        });

        // 10. Get complete call inspection details: Audio + Transcript + Structured Data
        app.MapGet("/api/admin/ai/calls/{callId}/details", async (string callId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            Guid sessionId = Guid.Empty;
            if (Guid.TryParse(callId, out var g)) sessionId = g;

            var sqlSession = @"
                SELECT 
                    cs.id AS call_id,
                    cs.room_name,
                    cs.status,
                    cs.started_at,
                    cs.ended_at,
                    cs.end_reason,
                    COALESCE(ROUND(EXTRACT(EPOCH FROM (cs.ended_at - cs.started_at))), 0) AS duration_seconds,
                    u.phone AS caller_phone,
                    COALESCE(u.username, u.phone) AS caller_name,
                    r.rating,
                    r.takeaways,
                    r.feedback
                FROM call_session cs
                JOIN app_user u ON u.id = cs.caller_user_id
                LEFT JOIN ai_call_rating r ON r.call_session_id = cs.id
                WHERE cs.id = @sessionId OR cs.room_name LIKE @likeRoom
                LIMIT 1";

            var sessionRow = await db.QueryFirstOrDefaultAsync<dynamic>(sqlSession, new { sessionId, likeRoom = $"%{callId}%" });
            if (sessionRow == null) return Results.NotFound(new { error = "Call session not found" });

            Guid realCallId = sessionRow.call_id;

            // Fetch structured data
            var structuredRow = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT data_type, structured_json::text AS json_text, summary
                FROM ai_call_structured_data
                WHERE call_session_id = @realCallId
                ORDER BY created_at DESC LIMIT 1",
                new { realCallId });

            // Fetch turn-by-turn dialogue transcript
            var messages = await db.QueryAsync<dynamic>(@"
                SELECT role, content, created_at
                FROM ai_call_message
                WHERE call_session_id = @realCallId
                ORDER BY created_at ASC",
                new { realCallId });

            string endReason = sessionRow.end_reason ?? "";
            string recordingFile = endReason.StartsWith("recording:") ? endReason.Substring("recording:".Length) : "";

            var result = new
            {
                callId = realCallId,
                roomName = (string)sessionRow.room_name,
                callerName = (string)sessionRow.caller_name,
                callerPhone = (string)sessionRow.caller_phone,
                status = (string)sessionRow.status,
                startedAt = sessionRow.started_at,
                endedAt = sessionRow.ended_at,
                durationSeconds = sessionRow.duration_seconds,
                recordingFile = recordingFile,
                recordingUrl = string.IsNullOrEmpty(recordingFile) ? null : $"/api/ai/recordings/{recordingFile}",
                rating = sessionRow.rating != null ? (int)sessionRow.rating : (int?)null,
                takeaways = (string)(sessionRow.takeaways ?? ""),
                feedback = (string)(sessionRow.feedback ?? ""),
                structuredData = structuredRow != null ? new {
                    dataType = (string)structuredRow.data_type,
                    json = (string)structuredRow.json_text,
                    summary = (string)structuredRow.summary
                } : null,
                transcript = messages.Select(m => new {
                    role = (string)m.role,
                    content = (string)m.content,
                    createdAt = m.created_at
                })
            };

            return Results.Ok(result);
        });

        // 10b. Get raw transcript messages
        app.MapGet("/api/admin/ai/calls/{callId}/transcript", async (string callId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            Guid sessionId = Guid.Empty;
            if (Guid.TryParse(callId, out var g)) sessionId = g;

            var realCallId = sessionId != Guid.Empty ? sessionId : (await db.ExecuteScalarAsync<Guid?>("SELECT id FROM call_session WHERE room_name LIKE @likeRoom OR id::text=@callId LIMIT 1", new { callId, likeRoom = $"%{callId}%" })) ?? Guid.Empty;

            var messages = await db.QueryAsync<dynamic>(@"
                SELECT role, content, created_at
                FROM ai_call_message
                WHERE call_session_id = @realCallId
                ORDER BY created_at ASC",
                new { realCallId });

            return Results.Ok(messages);
        });

        // 11. Save Structured Data from AI Agent
        app.MapPost("/api/ai/calls/{callId}/structured-data", async (string callId, SaveStructuredDataDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            Guid callSessionId = Guid.Empty;
            if (Guid.TryParse(callId, out var parsedId))
            {
                callSessionId = parsedId;
            }
            if (callSessionId == Guid.Empty && !string.IsNullOrWhiteSpace(dto.CallSessionId) && Guid.TryParse(dto.CallSessionId, out var parsedDtoId))
            {
                callSessionId = parsedDtoId;
            }
            if (callSessionId == Guid.Empty)
            {
                var queryId = !string.IsNullOrWhiteSpace(dto.CallSessionId) ? dto.CallSessionId : callId;
                var found = await db.ExecuteScalarAsync<Guid?>("SELECT id FROM call_session WHERE room_name LIKE @likeRoom OR id::text=@queryId LIMIT 1", new { queryId, likeRoom = $"%{queryId}%" });
                if (found.HasValue) callSessionId = found.Value;
            }
            if (callSessionId == Guid.Empty)
            {
                callSessionId = Guid.NewGuid();
            }

            Guid userId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(dto.UserId) && Guid.TryParse(dto.UserId, out var parsedUser))
            {
                userId = parsedUser;
            }
            if (userId == Guid.Empty && callSessionId != Guid.Empty)
            {
                var foundUser = await db.ExecuteScalarAsync<Guid?>("SELECT caller_user_id FROM call_session WHERE id=@callSessionId", new { callSessionId });
                if (foundUser.HasValue) userId = foundUser.Value;
            }
            if (userId == Guid.Empty)
            {
                userId = Guid.NewGuid();
            }

            var safeJson = !string.IsNullOrWhiteSpace(dto.StructuredJson) ? dto.StructuredJson : "{}";

            await db.ExecuteAsync(@"
                INSERT INTO ai_call_structured_data (call_session_id, user_id, persona_id, data_type, structured_json, summary)
                VALUES (@callSessionId, @userId, @personaId, @dataType, @safeJson::jsonb, @summary)",
                new
                {
                    callSessionId,
                    userId,
                    personaId = dto.PersonaId ?? "persona_role_cab_booking",
                    dataType = dto.DataType ?? "general",
                    safeJson,
                    summary = dto.Summary ?? ""
                });

            return Results.Ok(new { success = true, callSessionId });
        });

        // 12. Stream audio recording file
        app.MapGet("/api/ai/recordings/{filename}", (string filename) =>
        {
            var safeFilename = Path.GetFileName(filename);
            var possiblePaths = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "..", "ai-agent", "recordings", safeFilename),
                Path.Combine(Directory.GetCurrentDirectory(), "recordings", safeFilename)
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path).ToLower();
                    var contentType = ext == ".wav" ? "audio/wav" : (ext == ".mp3" ? "audio/mpeg" : "application/octet-stream");
                    return Results.File(path, contentType, enableRangeProcessing: true);
                }
            }

            return Results.NotFound(new { error = "Recording file not found" });
        });
    }
}

public record StartAiCallDto(string PersonaId, string Language, string Gender, string? TtsProvider = null, string? Pitch = null, string? Rate = null);
public record EndAiCallDto(int DurationSeconds, string? RecordingFile, string? LlmProvider = null, string? LlmModel = null, string? LatencyTelemetry = null);
public record AiSettingsDto(string Mode, string? Whitelist, string? Blacklist);
public record AiTurnDto(string Role, string Content);
public record AppendTurnDto(string? CallId, string? UserId, string PersonaId, string Role, string Content);
public record SaveAiMemoryDto(string? CallId, string? UserId, string PersonaId, List<AiTurnDto>? Turns, string? MemorySummary);
public record RateAiCallDto(int Rating, string? PersonaId, string? Takeaways, string? Feedback);
public record SaveStructuredDataDto(string? CallSessionId, string? UserId, string? PersonaId, string? DataType, string? StructuredJson, string? Summary);
