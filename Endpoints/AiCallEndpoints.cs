using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using PruvaVoice.Api.Hubs;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AiCallEndpoints
{
    private static async Task EnsureTablesAsync(IDbConnection db)
    {
        await db.ExecuteAsync(@"
            ALTER TABLE call_session ADD COLUMN IF NOT EXISTS telephony_provider VARCHAR(50) DEFAULT 'livekit';
            ALTER TABLE ai_campaign ADD COLUMN IF NOT EXISTS telephony_provider VARCHAR(50) DEFAULT 'livekit';

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

            CREATE TABLE IF NOT EXISTS ai_user_family_member (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id UUID NOT NULL,
                relation VARCHAR(50) NOT NULL,
                name VARCHAR(150) NOT NULL,
                nickname VARCHAR(100) NULL,
                age INT NULL,
                birthday VARCHAR(50) NULL,
                favorite_color VARCHAR(50) NULL,
                favorite_food VARCHAR(150) NULL,
                hobbies VARCHAR(255) NULL,
                health_notes TEXT NULL,
                preferences JSONB NOT NULL DEFAULT '{}'::jsonb,
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_user_family_uid ON ai_user_family_member(user_id);
            CREATE INDEX IF NOT EXISTS idx_user_family_rel ON ai_user_family_member(user_id, relation);

            CREATE TABLE IF NOT EXISTS ai_call_learning_report (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                call_session_id VARCHAR(100) NOT NULL,
                caller_id VARCHAR(100) NOT NULL DEFAULT '',
                duration_seconds INT NOT NULL DEFAULT 0,
                duration_formatted VARCHAR(50) NOT NULL DEFAULT '',
                curriculum_tier VARCHAR(50) NOT NULL DEFAULT 'primary',
                stars_earned INT NOT NULL DEFAULT 0,
                words_learned JSONB NOT NULL DEFAULT '[]'::jsonb,
                topics_explored JSONB NOT NULL DEFAULT '[]'::jsonb,
                parent_tip TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_ai_learning_report_call ON ai_call_learning_report(call_session_id);


            ALTER TABLE app_user ADD COLUMN IF NOT EXISTS referral_code VARCHAR(32);
            ALTER TABLE app_user ADD COLUMN IF NOT EXISTS referred_by_user_id UUID REFERENCES app_user(id);
            ALTER TABLE app_user ADD COLUMN IF NOT EXISTS assigned_persona_id VARCHAR(100) DEFAULT 'persona_role_kids_learning';
            ALTER TABLE ai_user_memory ADD COLUMN IF NOT EXISTS user_name VARCHAR(200) DEFAULT '';
            ALTER TABLE ai_user_memory ADD COLUMN IF NOT EXISTS child_name VARCHAR(200) DEFAULT '';
            ALTER TABLE ai_call_learning_report ADD COLUMN IF NOT EXISTS child_name VARCHAR(200) DEFAULT '';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_app_user_refcode ON app_user(referral_code) WHERE referral_code IS NOT NULL;

            CREATE TABLE IF NOT EXISTS app_user_referral (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                referrer_user_id UUID NOT NULL REFERENCES app_user(id),
                referred_user_id UUID NOT NULL REFERENCES app_user(id),
                reward_amount NUMERIC(12,2) NOT NULL DEFAULT 10.00,
                reward_status VARCHAR(32) NOT NULL DEFAULT 'credited',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_user_ref_referrer ON app_user_referral(referrer_user_id);
            CREATE INDEX IF NOT EXISTS idx_user_ref_referred ON app_user_referral(referred_user_id);

            INSERT INTO app_setting(key, value, updated_at) 
            VALUES ('referral_bonus_amount', '10.00', NOW()) 
            ON CONFLICT (key) DO NOTHING;

            INSERT INTO app_setting(key, value, updated_at) 
            VALUES ('referred_user_bonus_amount', '5.00', NOW()) 
            ON CONFLICT (key) DO NOTHING;

            INSERT INTO app_setting(key, value, updated_at) 
            VALUES ('referral_program_active', 'true', NOW()) 
            ON CONFLICT (key) DO NOTHING;

            UPDATE app_user 
            SET referral_code = 'REF' || UPPER(SUBSTRING(REPLACE(id::text, '-', ''), 1, 6))
            WHERE referral_code IS NULL OR referral_code = '';
        ");

        try
        {
            await db.ExecuteAsync("ALTER TABLE call_session ADD COLUMN IF NOT EXISTS is_outbound_ai BOOLEAN NOT NULL DEFAULT FALSE;");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiCallEndpoints] Note on is_outbound_ai column: {ex.Message}");
        }

        await db.ExecuteAsync(@"

            CREATE TABLE IF NOT EXISTS ai_persona_config (
                id VARCHAR(100) PRIMARY KEY,
                name VARCHAR(150) NOT NULL,
                subtitle VARCHAR(255) NOT NULL DEFAULT '',
                category VARCHAR(100) NOT NULL DEFAULT 'General',
                rate_per_minute NUMERIC(10,2) NOT NULL DEFAULT 3.00,
                system_prompt TEXT NOT NULL DEFAULT '',
                greeting_text TEXT NOT NULL DEFAULT '',
                tts_engine VARCHAR(50) NOT NULL DEFAULT 'edge',
                tts_voice VARCHAR(100) NOT NULL DEFAULT 'hi-IN-SwaraNeural',
                temperature NUMERIC(3,2) NOT NULL DEFAULT 0.35,
                custom_questions JSONB NOT NULL DEFAULT '[]'::jsonb,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
                updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS ai_campaign (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                title VARCHAR(200) NOT NULL,
                persona_id VARCHAR(100) NOT NULL,
                campaign_type VARCHAR(50) NOT NULL DEFAULT 'general',
                custom_prompt TEXT NOT NULL DEFAULT '',
                custom_questions JSONB NOT NULL DEFAULT '[]'::jsonb,
                status VARCHAR(50) NOT NULL DEFAULT 'active',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS ai_campaign_lead (
                id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                campaign_id UUID NOT NULL,
                phone_number VARCHAR(50) NOT NULL,
                recipient_name VARCHAR(150) NOT NULL DEFAULT '',
                status VARCHAR(50) NOT NULL DEFAULT 'pending',
                call_session_id UUID NULL,
                duration_seconds INT NOT NULL DEFAULT 0,
                extracted_responses JSONB NOT NULL DEFAULT '{}'::jsonb,
                call_summary TEXT NOT NULL DEFAULT '',
                created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
            );
        ");

        // Seed default personas with initial rates & questions if not present
        await db.ExecuteAsync(@"
            INSERT INTO ai_persona_config (id, name, subtitle, category, rate_per_minute, greeting_text, tts_engine, tts_voice, custom_questions)
            VALUES
            ('persona_role_cab_booking', 'Cab Booking Assistant', 'Voice Cab Booking & Live Rate Card', 'Cab Booking', 2.00, 'Namaste! Cab booking service mein aapka swagat hai. Aapko kahan se kahan jaana hai?', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""pickup"",""label"":""Pickup Location""},{""id"":""dropoff"",""label"":""Drop Location""},{""id"":""cab_type"",""label"":""Cab Type""}]'::jsonb),
            ('persona_role_software_sales', 'Software Sales Consultant', 'Cab Fleet & Travel CRM SaaS', 'Sales & B2B', 3.00, 'Namaste! Main Travel aur Cab Software Solutions se baat kar rahi hoon. Kya aap apne cab fleet ya travel agency ke liye software dekh rahe hain?', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""fleet_size"",""label"":""Fleet Size""},{""id"":""current_software"",""label"":""Current System""},{""id"":""demo_time"",""label"":""Demo Time""}]'::jsonb),
            ('persona_role_hr_interview', 'HR Mock Interviewer', 'Mock Interviews & Candidate Feedback', 'Hiring', 5.00, 'Hello! Welcome to your HR screening round. Please introduce yourself briefly.', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""experience"",""label"":""Total Experience""},{""id"":""notice_period"",""label"":""Notice Period""},{""id"":""expected_ctc"",""label"":""Expected CTC""}]'::jsonb),
            ('persona_role_software_developer', 'Senior Tech Interviewer', 'Software Developer & Engineering Screening', 'Hiring', 6.00, 'Hi there! I am your Technical Assessment Interviewer for the Software Developer role. Tell me about your primary tech stack.', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""tech_stack"",""label"":""Core Tech Stack""},{""id"":""system_design"",""label"":""System Design Experience""},{""id"":""notice_period"",""label"":""Notice Period""}]'::jsonb),
            ('persona_role_sales_office', 'Sales Office Executive', 'Inbound Fleet & Travel Agency Sales', 'Sales & B2B', 3.50, 'Hello! Main Sales Office se baat kar raha hoon. Travel packages aur fleet solutions ki details ke liye bataiye.', 'edge', 'hi-IN-MadhurNeural', '[{""id"":""lead_type"",""label"":""Customer Type""},{""id"":""destinations"",""label"":""Key Routes""}]'::jsonb),
            ('persona_role_hr_job', 'HR Job Screening Officer', 'Job Inquiries & Screening', 'Hiring', 4.00, 'Namaste! Job screening desk par swagat hai. Aap kis role ke liye apply kar rahe hain?', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""role_applied"",""label"":""Role Applied""},{""id"":""experience"",""label"":""Experience""}]'::jsonb),
            ('persona_role_kids_learning', 'Kids Learning Buddy', 'Stories, Rhymes & Curiosity', 'Education', 2.50, 'Yaaay! Hello superstar! Main hoon aapki pyari Puruva AI! Aaj kya masti karein?', 'edge', 'hi-IN-SwaraNeural', '[]'::jsonb),
            ('persona_role_kids_game', 'Kids Game & Quiz Master', 'Interactive 10s Voice Game & Trivia', 'Games & Fun', 2.00, 'Welcome to the Live Quiz Arena! Ready for our first 10-second trivia challenge?', 'edge', 'hi-IN-MadhurNeural', '[]'::jsonb),
            ('persona_role_romantic_chat', 'Romantic Companion', 'Sweet, Caring & Comforting', 'Companionship', 5.00, 'Hey there! How was your day today? I was waiting to talk to you.', 'edge', 'hi-IN-SwaraNeural', '[]'::jsonb),
            ('persona_role_parents_care', 'Parents Care Assistant', 'Health, Routine & Daily Checks', 'Family & Health', 3.00, 'Pranam! Aapki sehat kaisi hai? Aaj ki dawai le li aapne?', 'edge', 'hi-IN-SwaraNeural', '[{""id"":""medicine_taken"",""label"":""Medicine Status""},{""id"":""bp_sugar"",""label"":""BP/Sugar Check""}]'::jsonb),
            ('persona_role_english_tutor', 'Spoken English Coach', 'Practice English with Hinglish guidance', 'Learning', 4.00, 'Hello! Ready to practice your spoken English today? Speak in English and I will guide you.', 'edge', 'hi-IN-SwaraNeural', '[]'::jsonb)
            ON CONFLICT (id) DO NOTHING;
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

        // 2. Available AI Personas catalog & options (Dynamic from ai_persona_config)
        app.MapGet("/api/ai/options", async (IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            var dbPersonas = await db.QueryAsync<dynamic>(@"
                SELECT id, name, subtitle, category, rate_per_minute, greeting_text, tts_engine, tts_voice, custom_questions::text as questions_json, is_active
                FROM ai_persona_config
                WHERE is_active = TRUE
                ORDER BY created_at ASC");

            var iconMap = new Dictionary<string, (string icon, string color)>
            {
                ["persona_role_cab_booking"] = ("local_taxi", "#F59E0B"),
                ["persona_role_software_sales"] = ("storefront", "#6366F1"),
                ["persona_role_sales_office"] = ("business", "#0EA5E9"),
                ["persona_role_hr_interview"] = ("work_outline", "#3B82F6"),
                ["persona_role_software_developer"] = ("code", "#10B981"),
                ["persona_role_hr_job"] = ("badge", "#0284C7"),
                ["persona_role_kids_learning"] = ("child_care", "#F59E0B"),
                ["persona_role_kids_game"] = ("sports_esports", "#F97316"),
                ["persona_role_romantic_chat"] = ("favorite", "#EC4899"),
                ["persona_role_parents_care"] = ("health_and_safety", "#10B981"),
                ["persona_role_english_tutor"] = ("school", "#8B5CF6")
            };

            var personas = dbPersonas.Select(p =>
            {
                var pid = (string)p.id;
                var (icon, color) = iconMap.TryGetValue(pid, out var ic) ? ic : ("smart_toy", "#6366F1");
                return new
                {
                    id = pid,
                    name = (string)p.name,
                    subtitle = (string)p.subtitle,
                    category = (string)p.category,
                    rate_per_minute = (decimal)p.rate_per_minute,
                    ratePerMinute = (decimal)p.rate_per_minute,
                    greeting_text = (string)p.greeting_text,
                    tts_engine = (string)p.tts_engine,
                    tts_voice = (string)p.tts_voice,
                    custom_questions = (string)p.questions_json,
                    icon,
                    color
                };
            }).ToList();

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

        // 3. Start AI Voice Call Session (Caller-Pays Model: Checks User Wallet Balance)
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

            // Read user's specific assigned persona (fallback to dto.PersonaId or default 'persona_role_kids_learning')
            var assignedPersona = await db.ExecuteScalarAsync<string?>("SELECT assigned_persona_id FROM app_user WHERE id=@callerId", new { callerId });
            var effectivePersona = !string.IsNullOrWhiteSpace(assignedPersona) ? assignedPersona : (string.IsNullOrWhiteSpace(dto.PersonaId) ? "persona_role_kids_learning" : dto.PersonaId);
            var cleanPersonaId = effectivePersona.StartsWith("persona_role_") ? effectivePersona : $"persona_role_{effectivePersona}";

            // Fetch per-persona rate
            var personaRate = await db.ExecuteScalarAsync<decimal?>("SELECT rate_per_minute FROM ai_persona_config WHERE id=@cleanPersonaId", new { cleanPersonaId }) ?? 3.00m;

            // Check caller wallet balance (must have at least 2 minutes of balance)
            var accBalance = await db.ExecuteScalarAsync<decimal?>("SELECT balance FROM wallet_account WHERE user_id=@callerId", new { callerId }) ?? 0m;
            var txnBalance = await db.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(amount), 0) FROM wallet_transaction WHERE user_id=@callerId", new { callerId }) ?? 0m;
            var balance = Math.Max(accBalance, txnBalance);

            if (balance > accBalance)
            {
                await db.ExecuteAsync("INSERT INTO wallet_account (user_id, balance, currency, updated_at) VALUES (@callerId, @balance, 'INR', now()) ON CONFLICT (user_id) DO UPDATE SET balance=@balance, updated_at=now()", new { callerId, balance });
            }

            var minRequired = personaRate * 2.0m;
            if (balance < minRequired)
            {
                return Results.BadRequest(new
                {
                    error = "Insufficient wallet balance",
                    code = "LOW_BALANCE",
                    message = $"AI Call requires at least ₹{minRequired:F2} (2 minutes balance @ ₹{personaRate:F2}/min). Current balance: ₹{balance:F2}",
                    currentBalance = balance,
                    requiredBalance = minRequired,
                    ratePerMinute = personaRate
                });
            }

            var roomName = $"ai_call_{cleanPersonaId}_{Guid.NewGuid():N}";

            // Insert into call_session: is_outbound_ai = FALSE (User initiated, so user pays)
            var callId = await db.ExecuteScalarAsync<Guid>(
                "INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, is_outbound_ai, started_at, call_type) " +
                "VALUES(@callerId, @callerId, @roomName, 'connected', @personaRate, FALSE, now(), 'ai') RETURNING id",
                new { callerId, roomName, personaRate }
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
                rate_per_minute = personaRate,
                is_outbound = false,
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
                ratePerMinute = personaRate,
                language = dto.Language,
                gender = dto.Gender
            });
        }).RequireAuthorization();

        // 3a. Bridge Call: Find matching online host (girl or boy based on requirement) and invite them into the ongoing LiveKit room
        app.MapPost("/api/ai/bridge-call", async (BridgeAiCallDto dto, IDbConnection db, LiveKitTokenService livekit, CallNotifier notifier, IHubContext<CallHub> hubContext) =>
        {
            if (string.IsNullOrWhiteSpace(dto.RoomName))
            {
                return Results.BadRequest(new { success = false, message = "RoomName is required" });
            }

            var genderFilter = (dto.DesiredGender?.Trim().ToLower() == "male" || dto.DesiredGender?.Trim().ToLower() == "boy") ? "male" : "female";
            var genderLabel = genderFilter == "female" ? "ladki" : "ladka";
            var cleanCity = dto.City?.Trim();
            var hasCity = !string.IsNullOrWhiteSpace(cleanCity);
            var cleanLanguage = dto.Language?.Trim();
            var hasLanguage = !string.IsNullOrWhiteSpace(cleanLanguage);

            // Resolve Caller User ID to strictly ensure AI never calls the caller themselves!
            Guid? callerId = null;
            if (!string.IsNullOrWhiteSpace(dto.CallerUserId) && Guid.TryParse(dto.CallerUserId, out var parsedCaller))
            {
                callerId = parsedCaller;
            }
            else
            {
                callerId = await db.QueryFirstOrDefaultAsync<Guid?>(
                    "SELECT caller_user_id FROM call_session WHERE room_name=@RoomName ORDER BY started_at DESC LIMIT 1",
                    new { dto.RoomName });
            }

            dynamic? host = null;
            bool isExactMatch = false;

            // 0. If specific target user requested (e.g. "priya", "user_5542", "rahul", "@username")
            if (host == null && !string.IsNullOrWhiteSpace(dto.TargetUsername))
            {
                var cleanTarget = dto.TargetUsername.Trim().TrimStart('@');
                host = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.id, u.username, u.display_name, u.display_gender, u.city, u.languages, 
                           COALESCE(hp.rate_per_minute, 5.00) as rate_per_minute, 
                           COALESCE(hp.rating_avg, 4.85) as rating_avg, 
                           COALESCE(p.status, 'offline') as presence
                    FROM app_user u
                    LEFT JOIN host_profile hp ON hp.user_id = u.id AND hp.status = 'approved'
                    LEFT JOIN user_presence p ON p.user_id = u.id
                    WHERE u.status = 'active'
                      AND (@callerId IS NULL OR u.id <> @callerId)
                      AND (u.username ILIKE @cleanTarget 
                           OR u.display_name ILIKE @cleanTarget
                           OR u.username ILIKE '%' || @cleanTarget || '%'
                           OR u.display_name ILIKE '%' || @cleanTarget || '%')
                    ORDER BY CASE WHEN u.username ILIKE @cleanTarget THEN 0 ELSE 1 END,
                             CASE WHEN COALESCE(p.status, 'offline') = 'online' THEN 0 ELSE 1 END
                    LIMIT 1",
                    new { cleanTarget, callerId });

                if (host != null)
                {
                    string targetPresence = (string)(host.presence ?? "offline");
                    if (targetPresence != "online")
                    {
                        string foundName = (string)(host.display_name ?? host.username ?? cleanTarget);
                        string foundCity = (string)(host.city ?? "unke shahar");
                        return Results.Ok(new
                        {
                            success = false,
                            exactMatch = false,
                            targetUserFound = true,
                            isOnline = false,
                            message = $"@{foundName} abhi offline hain. Kya main {foundCity} se kisi doosri online host ko connect karoon?"
                        });
                    }
                    isExactMatch = true;
                }
                else
                {
                    return Results.Ok(new
                    {
                        success = false,
                        exactMatch = false,
                        targetUserFound = false,
                        message = $"Maaf kijiye, '@{cleanTarget}' naam ka koi registered user nahi mil paaya. Kya main kisi doosri online host se baat karaoon?"
                    });
                }
            }

            // Check if this is a confirmed bridge for a specific alternate host
            Guid confirmedGuid = Guid.Empty;
            if (host == null && !string.IsNullOrWhiteSpace(dto.ConfirmedHostId) && Guid.TryParse(dto.ConfirmedHostId, out confirmedGuid))
            {
                host = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.id, u.username, u.display_gender, u.city, u.languages, hp.rate_per_minute, hp.rating_avg, COALESCE(p.status, 'offline') as presence
                    FROM host_profile hp
                    JOIN app_user u ON u.id = hp.user_id
                    LEFT JOIN user_presence p ON p.user_id = u.id
                    WHERE u.id = @confirmedGuid 
                      AND hp.status = 'approved' 
                      AND u.status = 'active'
                      AND (@callerId IS NULL OR u.id <> @callerId)",
                    new { confirmedGuid, callerId });
                isExactMatch = true;
            }

            // 1. If city requested (e.g. 'Delhi'), search for an active, approved REAL ONLINE host from that city
            if (host == null && hasCity)
            {
                host = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.id, u.username, u.display_gender, u.city, u.languages, hp.rate_per_minute, hp.rating_avg, COALESCE(p.status, 'offline') as presence
                    FROM host_profile hp
                    JOIN app_user u ON u.id = hp.user_id
                    LEFT JOIN user_presence p ON p.user_id = u.id
                    WHERE hp.status = 'approved' 
                      AND u.status = 'active'
                      AND LOWER(u.display_gender) = @genderFilter
                      AND COALESCE(p.status, 'offline') = 'online'
                      AND (@callerId IS NULL OR u.id <> @callerId)
                      AND (u.city ILIKE '%' || @cleanCity || '%' OR array_to_string(hp.languages, ', ') ILIKE '%' || @cleanCity || '%' OR u.languages ILIKE '%' || @cleanCity || '%')
                    ORDER BY hp.rating_avg DESC
                    LIMIT 1",
                    new { genderFilter, cleanCity, callerId });

                if (host != null)
                {
                    isExactMatch = true;
                }
            }

            // 2. If city was requested but NO online host found in that city:
            // Check if an alternate ONLINE host of that gender is available elsewhere!
            if (host == null && hasCity)
            {
                var alternateHost = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.id, u.username, u.display_gender, u.city, u.languages, hp.rate_per_minute, hp.rating_avg, COALESCE(p.status, 'offline') as presence
                    FROM host_profile hp
                    JOIN app_user u ON u.id = hp.user_id
                    LEFT JOIN user_presence p ON p.user_id = u.id
                    WHERE hp.status = 'approved' 
                      AND u.status = 'active'
                      AND LOWER(u.display_gender) = @genderFilter
                      AND COALESCE(p.status, 'offline') = 'online'
                      AND (@callerId IS NULL OR u.id <> @callerId)
                    ORDER BY CASE WHEN (@hasLanguage AND (u.languages ILIKE '%' || @cleanLanguage || '%' OR array_to_string(hp.languages, ', ') ILIKE '%' || @cleanLanguage || '%')) THEN 0 ELSE 1 END,
                             hp.rating_avg DESC
                    LIMIT 1",
                    new { genderFilter, hasLanguage, cleanLanguage, callerId });

                if (alternateHost != null)
                {
                    if (dto.ForceConnectAlternate)
                    {
                        host = alternateHost;
                        isExactMatch = true;
                    }
                    else
                    {
                        // Return alternate option HONESTLY without fake commitment
                        string altUsername = (string)alternateHost.username ?? "User";
                        string altCity = (string)(alternateHost.city ?? "dusre shahar");
                        string altGender = (string)(alternateHost.display_gender ?? genderFilter);
                        return Results.Ok(new
                        {
                            success = false,
                            exactMatch = false,
                            alternateFound = true,
                            alternateHostId = alternateHost.id.ToString(),
                            alternateUsername = altUsername,
                            alternateCity = altCity,
                            alternateGender = altGender,
                            message = $"{cleanCity} ki to abhi koi {genderLabel} online nahi hai, lekin ek {genderLabel} ({altCity} se) abhi online hain. Kya unke sath call laga doon?"
                        });
                    }
                }
            }

            // 3. If no specific city was requested (e.g. user simply asked for a Hindi-speaking boy or girl who is not boring)
            if (host == null && !hasCity)
            {
                host = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT u.id, u.username, u.display_gender, u.city, u.languages, hp.rate_per_minute, hp.rating_avg, COALESCE(p.status, 'offline') as presence
                    FROM host_profile hp
                    JOIN app_user u ON u.id = hp.user_id
                    LEFT JOIN user_presence p ON p.user_id = u.id
                    WHERE hp.status = 'approved' 
                      AND u.status = 'active'
                      AND LOWER(u.display_gender) = @genderFilter
                      AND COALESCE(p.status, 'offline') = 'online'
                      AND (@callerId IS NULL OR u.id <> @callerId)
                    ORDER BY CASE WHEN (@hasLanguage AND (u.languages ILIKE '%' || @cleanLanguage || '%' OR array_to_string(hp.languages, ', ') ILIKE '%' || @cleanLanguage || '%')) THEN 0 ELSE 1 END,
                             hp.rating_avg DESC
                    LIMIT 1",
                    new { genderFilter, hasLanguage, cleanLanguage, callerId });

                if (host != null)
                {
                    isExactMatch = true;
                }
            }

            // 4. No online host available at all
            if (host == null)
            {
                var cityMention = hasCity ? $" {cleanCity} se" : "";
                return Results.Ok(new
                {
                    success = false,
                    exactMatch = false,
                    alternateFound = false,
                    message = $"Maaf kijiye, is samay koi bhi {genderLabel}{cityMention} online nahi mil paaye. Lekin main yahin hoon aapke saath, bataiye aapka din kaisa raha?"
                });
            }

            Guid hostUserId = (Guid)host.id;
            string hostUsername = (string)host.username ?? "Host";
            string matchedGender = (string)(host.display_gender ?? genderFilter);
            string hostCity = (string)(host.city ?? "Online");

            // Generate LiveKit token for the host into the SAME room
            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='livekit' AND is_active=true");
            var config = string.IsNullOrWhiteSpace(credJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(credJson);
            var apiKey = config != null && config.TryGetValue("apiKey", out var ak) ? ak : "devkey";
            var apiSecret = config != null && config.TryGetValue("apiSecret", out var asec) ? asec : "devsecretkeyshouldbe48characterslongforsecurity!";
            var hostToken = livekit.CreateToken(apiKey, apiSecret, dto.RoomName, hostUserId.ToString());

            // Create call_session entry for this bridged call so /api/calls/{callId}/accept succeeds
            var bridgeCallId = Guid.NewGuid();
            decimal hostRate = host.rate_per_minute is decimal rm ? rm : (decimal.TryParse(host.rate_per_minute?.ToString(), out decimal pr) ? pr : 5.0m);
            await db.ExecuteAsync(@"
                INSERT INTO call_session(id, caller_user_id, host_user_id, room_name, status, rate_per_minute, is_outbound_ai, started_at)
                VALUES(@bridgeCallId, @callerGuid, @hostUserId, @RoomName, 'ringing', @hostRate, false, now())",
                new { bridgeCallId, callerGuid = callerId ?? hostUserId, hostUserId, dto.RoomName, hostRate });

            // Notify host via SignalR and FCM push (Strictly Anonymous: Caller phone/real name hidden!)
            await hubContext.Clients.Group($"user:{hostUserId}").SendAsync("incomingCall", new
            {
                callId = bridgeCallId.ToString(),
                callerUsername = "AI Matchmaker • Live Connect",
                roomName = dto.RoomName,
                token = hostToken
            });

            var latestDeviceToken = await db.ExecuteScalarAsync<string?>("SELECT fcm_token FROM user_device WHERE user_id=@hostUserId ORDER BY last_seen_at DESC LIMIT 1", new { hostUserId });
            await notifier.NotifyIncomingCallAsync(db, hostUserId, bridgeCallId, "AI Matchmaker • Live Connect", latestDeviceToken);

            return Results.Ok(new
            {
                success = true,
                exactMatch = isExactMatch,
                hostId = hostUserId,
                hostUsername,
                gender = matchedGender,
                city = hostCity,
                rating = host.rating_avg ?? 4.8,
                roomName = dto.RoomName,
                message = $"Badhai ho! {hostUsername} ({matchedGender}{(string.IsNullOrWhiteSpace(hostCity) ? "" : $", {hostCity}")}) ko aapki call me connect kiya ja raha hai. Bas do second hold kijiye."
            });
        });

        // 3b. User-to-User Direct Chat: Fetch recent messages between 2 users
        app.MapGet("/api/chat/messages/{otherUserId:guid}", async (Guid otherUserId, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var myId = CurrentUser.Id(cp);
            if (myId == Guid.Empty) return Results.Unauthorized();

            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS user_chat_message (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    sender_id UUID NOT NULL,
                    recipient_id UUID NOT NULL,
                    text TEXT NOT NULL,
                    message_type VARCHAR(50) DEFAULT 'text',
                    is_read BOOLEAN DEFAULT false,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );");

            var messages = await db.QueryAsync(@"
                SELECT id, sender_id, recipient_id, text, message_type, is_read, created_at
                FROM user_chat_message
                WHERE (sender_id = @myId AND recipient_id = @otherUserId)
                   OR (sender_id = @otherUserId AND recipient_id = @myId)
                ORDER BY created_at ASC
                LIMIT 100", new { myId, otherUserId });

            // Mark unread messages as read
            await db.ExecuteAsync(@"
                UPDATE user_chat_message 
                SET is_read = true 
                WHERE recipient_id = @myId AND sender_id = @otherUserId AND is_read = false",
                new { myId, otherUserId });

            return Results.Ok(messages);
        }).RequireAuthorization();

        // 3c. User-to-User Direct Chat: Send a message via REST
        app.MapPost("/api/chat/send", async (SendChatRestDto dto, ClaimsPrincipal cp, IDbConnection db, IHubContext<CallHub> hubContext) =>
        {
            var myId = CurrentUser.Id(cp);
            if (myId == Guid.Empty) return Results.Unauthorized();

            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS user_chat_message (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    sender_id UUID NOT NULL,
                    recipient_id UUID NOT NULL,
                    text TEXT NOT NULL,
                    message_type VARCHAR(50) DEFAULT 'text',
                    is_read BOOLEAN DEFAULT false,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );");

            var msgId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO user_chat_message(sender_id, recipient_id, text, message_type)
                VALUES(@myId, @RecipientId, @Text, @MessageType)
                RETURNING id",
                new { myId, dto.RecipientId, dto.Text, MessageType = dto.MessageType ?? "text" });

            var payload = new
            {
                id = msgId,
                senderId = myId.ToString(),
                recipientUserId = dto.RecipientId.ToString(),
                text = dto.Text,
                messageType = dto.MessageType ?? "text",
                timestamp = DateTime.UtcNow
            };

            await hubContext.Clients.Group($"user:{dto.RecipientId}").SendAsync("receiveDirectMessage", payload);

            return Results.Ok(new { success = true, id = msgId, message = "Message sent" });
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
                "INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, started_at, call_type) " +
                "VALUES(@callerId, @callerId, @roomName, 'connected', 0, now(), 'ai') RETURNING id",
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

        // 4. End AI Call & Log Duration + Audio Recording (Enforces Caller-Pays Wallet Deduction)
        app.MapPost("/api/ai/calls/{callId:guid}/end", async (Guid callId, EndAiCallDto dto, IDbConnection db) =>
        {
            var session = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM call_session WHERE id=@callId", new { callId });
            if (session == null) return Results.NotFound("Call session not found");

            var durationSeconds = dto.DurationSeconds > 0 ? dto.DurationSeconds : 0;
            var endReason = string.IsNullOrWhiteSpace(dto.RecordingFile) ? "ai_completed" : $"recording:{dto.RecordingFile}";

            var llmProvider = string.IsNullOrWhiteSpace(dto.LlmProvider) ? "groq" : dto.LlmProvider;
            var llmModel = string.IsNullOrWhiteSpace(dto.LlmModel) ? "qwen/qwen3.8-27b" : dto.LlmModel;
            var latencyTelemetry = string.IsNullOrWhiteSpace(dto.LatencyTelemetry) ? "{}" : dto.LatencyTelemetry;

            bool isOutbound = session.is_outbound_ai ?? false;
            decimal ratePerMinute = (decimal)(session.rate_per_minute ?? 0m);
            decimal amountDeducted = 0m;

            // Caller-Pays Rule:
            // - If user called AI (!isOutbound): Deduct from caller's wallet at persona rate.
            // - If AI/Admin called user (isOutbound == true): User is NOT charged (amountDeducted = 0).
            if (!isOutbound && ratePerMinute > 0 && durationSeconds > 0 && session.caller_user_id != null)
            {
                var callerUserId = (Guid)session.caller_user_id;
                amountDeducted = Math.Round(((decimal)durationSeconds / 60m) * ratePerMinute, 2);
                if (amountDeducted > 0)
                {
                    await db.ExecuteAsync("UPDATE wallet_account SET balance = balance - @amountDeducted WHERE user_id = @callerUserId", new { amountDeducted, callerUserId });
                    await db.ExecuteAsync(@"
                        INSERT INTO wallet_transaction(user_id, txn_type, amount, reference_type, reference_id, note)
                        VALUES(@callerUserId, 'ai_call_charge', -@amountDeducted, 'call', @callId, @note)",
                        new { callerUserId, amountDeducted, callId, note = $"AI Voice Call charge ({durationSeconds}s @ ₹{ratePerMinute:F2}/min)" });
                }
            }

            await db.ExecuteAsync(@"
                UPDATE call_session 
                SET status='ended', 
                    ended_at=now(), 
                    end_reason=@endReason,
                    total_amount=@amountDeducted,
                    billable_seconds=@durationSeconds,
                    llm_provider=@llmProvider,
                    llm_model=@llmModel,
                    latency_telemetry=@latencyTelemetry::jsonb
                WHERE id=@callId",
                new { callId, endReason, amountDeducted, durationSeconds, llmProvider, llmModel, latencyTelemetry }
            );

            if (session.caller_user_id != null)
            {
                await db.ExecuteAsync(
                    "INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, @eventType, @callerId)",
                    new { callId, eventType = $"ai_duration:{durationSeconds}s,deducted:{amountDeducted}", callerId = (Guid)session.caller_user_id }
                );
            }

            return Results.Ok(new { success = true, callId, durationSeconds, amountDeducted, isOutbound });
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
                    c.rate_per_minute,
                    c.total_amount,
                    c.is_outbound_ai,
                    c.room_name,
                    CASE 
                        WHEN c.billable_seconds > 0 THEN c.billable_seconds
                        WHEN c.ended_at IS NOT NULL AND c.started_at IS NOT NULL THEN GREATEST(0, ROUND(EXTRACT(EPOCH FROM (c.ended_at - c.started_at))))
                        WHEN c.ended_at IS NOT NULL AND c.created_at IS NOT NULL THEN GREATEST(0, ROUND(EXTRACT(EPOCH FROM (c.ended_at - c.created_at))))
                        WHEN c.status = 'connected' AND c.connected_at IS NOT NULL THEN GREATEST(0, ROUND(EXTRACT(EPOCH FROM (now() - c.connected_at))))
                        ELSE 0
                    END AS duration_seconds,
                    c.created_at,
                    COALESCE(
                        m.persona_id,
                        tr.persona_id,
                        CASE 
                            WHEN c.room_name LIKE 'ai_call_persona_role_%' THEN SUBSTRING(c.room_name FROM 'ai_call_(persona_role_[a-zA-Z0-9_]+)_[a-f0-9]+')
                            WHEN c.room_name LIKE 'ai_call_%' THEN SUBSTRING(c.room_name FROM 'ai_call_([a-zA-Z0-9_]+)_[a-f0-9]+')
                            WHEN c.room_name LIKE 'task_call_%' THEN SUBSTRING(c.room_name FROM 'task_call_([a-zA-Z0-9_]+)_[a-f0-9]+')
                            ELSE 'persona_role_companion'
                        END,
                        'persona_role_companion'
                    ) AS persona_id,
                    COALESCE(tr.task_title, apt.title, '') AS task_title,
                    COALESCE(tr.match_score, 0) AS match_score,
                    COALESCE(mem.memory_summary, tr.summary, '') AS memory_summary,
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
                LEFT JOIN ai_persona_task_response tr ON tr.call_session_id = c.id
                LEFT JOIN ai_persona_task apt ON apt.id = tr.task_id
                LEFT JOIN ai_user_memory mem ON mem.user_id = c.caller_user_id AND mem.persona_id = m.persona_id
                LEFT JOIN ai_call_rating r ON r.call_session_id = c.id
                WHERE (c.caller_user_id = @uid OR c.host_user_id = @uid)
                  AND (
                    c.call_type IN ('ai', 'ai_call', 'task_call', 'outbound_ai')
                    OR c.is_outbound_ai = TRUE 
                    OR c.room_name LIKE 'ai_%' 
                    OR c.room_name LIKE 'task_%'
                    OR c.caller_user_id = c.host_user_id
                  )
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
            
            DateTime? lastCallAt = null;
            if (memory != null && memory.last_call_at != null)
            {
                lastCallAt = (DateTime)memory.last_call_at;
            }
            double? hoursSinceLastCall = lastCallAt.HasValue ? Math.Round((DateTime.UtcNow - lastCallAt.Value).TotalHours, 1) : null;
            double? daysSinceLastCall = lastCallAt.HasValue ? Math.Round((DateTime.UtcNow - lastCallAt.Value).TotalDays, 1) : null;
            var lastTopic = memory != null && memory.last_topic != null ? (string)memory.last_topic : "";
            var lastUserStatement = memory != null && memory.last_user_statement != null ? (string)memory.last_user_statement : "";
            var lastOpeningStyle = memory != null && memory.last_opening_style != null ? (string)memory.last_opening_style : "default";

            var familyMembers = await db.QueryAsync<dynamic>(
                "SELECT id, relation, name, nickname, age, birthday, favorite_color, favorite_food, hobbies, health_notes, preferences::text AS preferences_json FROM ai_user_family_member WHERE user_id=@userGuid ORDER BY created_at ASC",
                new { userGuid });

            string rememberedName = memory != null ? (string)(memory.child_name ?? memory.user_name ?? "") : "";
            if (string.IsNullOrWhiteSpace(rememberedName))
            {
                var dbName = await db.ExecuteScalarAsync<string?>(
                    "SELECT COALESCE(NULLIF(full_name, ''), NULLIF(name, ''), NULLIF(username, '')) FROM app_user WHERE id=@userGuid",
                    new { userGuid });
                if (!string.IsNullOrWhiteSpace(dbName)) rememberedName = dbName;
            }
            if (string.IsNullOrWhiteSpace(rememberedName))
            {
                var childFromRep = await db.ExecuteScalarAsync<string?>(
                    "SELECT child_name FROM ai_call_learning_report WHERE (caller_id=@cleanUserId OR caller_id=@userGuidStr) AND child_name IS NOT NULL AND child_name != '' ORDER BY created_at DESC LIMIT 1",
                    new { cleanUserId, userGuidStr = userGuid.ToString() });
                if (!string.IsNullOrWhiteSpace(childFromRep)) rememberedName = childFromRep;
            }

            return Results.Ok(new
            {
                hasHistory,
                userName = rememberedName,
                childName = rememberedName,
                totalCalls,
                daysSinceLastCall,
                hoursSinceLastCall,
                lastTopic,
                lastUserStatement,
                lastOpeningStyle,
                memorySummary = summary,
                familyMembers,
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
            var lastTopic = dto.LastTopic ?? "";
            var lastUserStatement = dto.LastUserStatement ?? "";
            var lastOpeningStyle = dto.LastOpeningStyle ?? "default";
            var userName = dto.UserName ?? "";

            await db.ExecuteAsync(@"
                INSERT INTO ai_user_memory(user_id, persona_id, memory_summary, last_topic, last_user_statement, last_opening_style, user_name, child_name, last_call_at, total_calls, updated_at)
                VALUES(@userGuid, @cleanPersonaId, @summary, @lastTopic, @lastUserStatement, @lastOpeningStyle, @userName, @userName, now(), 1, now())
                ON CONFLICT (user_id, persona_id) DO UPDATE 
                SET memory_summary = CASE WHEN @summary <> '' THEN @summary ELSE ai_user_memory.memory_summary END,
                    last_topic = CASE WHEN @lastTopic <> '' THEN @lastTopic ELSE ai_user_memory.last_topic END,
                    last_user_statement = CASE WHEN @lastUserStatement <> '' THEN @lastUserStatement ELSE ai_user_memory.last_user_statement END,
                    last_opening_style = CASE WHEN @lastOpeningStyle <> '' THEN @lastOpeningStyle ELSE ai_user_memory.last_opening_style END,
                    user_name = CASE WHEN @userName <> '' THEN @userName ELSE ai_user_memory.user_name END,
                    child_name = CASE WHEN @userName <> '' THEN @userName ELSE ai_user_memory.child_name END,
                    total_calls = ai_user_memory.total_calls + 1,
                    last_call_at = now(),
                    updated_at = now()",
                new { userGuid, cleanPersonaId, summary, lastTopic, lastUserStatement, lastOpeningStyle, userName });

            return Results.Ok(new { success = true, savedTurns = dto.Turns?.Count ?? 0 });
        });

        // 6c. Get User Family Members
        app.MapGet("/api/ai/user-family", async (string? userId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var rawUser = (userId ?? "").StartsWith("admin_") ? (userId ?? "").Substring(6) : (userId ?? "");
            if (!Guid.TryParse(rawUser, out var userGuid))
                return Results.Ok(Array.Empty<object>());

            var family = await db.QueryAsync<dynamic>(
                "SELECT id, user_id, relation, name, nickname, age, birthday, favorite_color, favorite_food, hobbies, health_notes, preferences::text AS preferences_json, created_at, updated_at FROM ai_user_family_member WHERE user_id=@userGuid ORDER BY created_at ASC",
                new { userGuid });
            return Results.Ok(family);
        });

        // 6d. Upsert User Family Member (by user_id + relation + name)
        app.MapPost("/api/ai/user-family", async (UpsertFamilyMemberDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var rawUser = (dto.UserId ?? "").StartsWith("admin_") ? (dto.UserId ?? "").Substring(6) : (dto.UserId ?? "");
            if (!Guid.TryParse(rawUser, out var userGuid))
                return Results.BadRequest(new { error = "Valid user ID is required" });

            if (string.IsNullOrWhiteSpace(dto.Relation) || string.IsNullOrWhiteSpace(dto.Name))
                return Results.BadRequest(new { error = "Relation and Name are required" });

            var rel = dto.Relation.Trim().ToLower();
            var name = dto.Name.Trim();
            var prefs = string.IsNullOrWhiteSpace(dto.PreferencesJson) ? "{}" : dto.PreferencesJson;

            Guid memberId = dto.Id ?? Guid.Empty;
            if (memberId == Guid.Empty)
            {
                var existingId = await db.ExecuteScalarAsync<Guid?>(
                    "SELECT id FROM ai_user_family_member WHERE user_id=@userGuid AND LOWER(relation)=@rel AND (LOWER(name)=LOWER(@name) OR @rel IN ('mother', 'father', 'spouse')) LIMIT 1",
                    new { userGuid, rel, name });
                if (existingId.HasValue) memberId = existingId.Value;
            }

            if (memberId != Guid.Empty)
            {
                await db.ExecuteAsync(@"
                    UPDATE ai_user_family_member
                    SET name = @name,
                        nickname = COALESCE(NULLIF(@Nickname, ''), nickname),
                        age = COALESCE(@Age, age),
                        birthday = COALESCE(NULLIF(@Birthday, ''), birthday),
                        favorite_color = COALESCE(NULLIF(@FavoriteColor, ''), favorite_color),
                        favorite_food = COALESCE(NULLIF(@FavoriteFood, ''), favorite_food),
                        hobbies = COALESCE(NULLIF(@Hobbies, ''), hobbies),
                        health_notes = COALESCE(NULLIF(@HealthNotes, ''), health_notes),
                        preferences = CASE WHEN @prefs <> '{}' THEN @prefs::jsonb ELSE preferences END,
                        updated_at = now()
                    WHERE id = @memberId",
                    new {
                        memberId,
                        name,
                        dto.Nickname,
                        dto.Age,
                        dto.Birthday,
                        dto.FavoriteColor,
                        dto.FavoriteFood,
                        dto.Hobbies,
                        dto.HealthNotes,
                        prefs
                    });
            }
            else
            {
                memberId = await db.ExecuteScalarAsync<Guid>(@"
                    INSERT INTO ai_user_family_member(user_id, relation, name, nickname, age, birthday, favorite_color, favorite_food, hobbies, health_notes, preferences, created_at, updated_at)
                    VALUES(@userGuid, @rel, @name, @Nickname, @Age, @Birthday, @FavoriteColor, @FavoriteFood, @Hobbies, @HealthNotes, @prefs::jsonb, now(), now())
                    RETURNING id",
                    new {
                        userGuid,
                        rel,
                        name,
                        dto.Nickname,
                        dto.Age,
                        dto.Birthday,
                        dto.FavoriteColor,
                        dto.FavoriteFood,
                        dto.Hobbies,
                        dto.HealthNotes,
                        prefs
                    });
            }

            return Results.Ok(new { success = true, id = memberId, message = $"Family member {name} ({rel}) saved successfully." });
        });

        // 6e. Delete User Family Member
        app.MapDelete("/api/ai/user-family/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            await db.ExecuteAsync("DELETE FROM ai_user_family_member WHERE id=@id", new { id });
            return Results.Ok(new { success = true });
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
                    sd.summary AS structured_summary,
                    COALESCE(cs.telephony_provider, 'livekit') AS telephony_provider
                FROM call_session cs
                JOIN app_user u ON u.id = cs.caller_user_id
                LEFT JOIN ai_call_rating r ON r.call_session_id = cs.id
                LEFT JOIN ai_call_structured_data sd ON sd.call_session_id = cs.id
                WHERE (cs.room_name LIKE 'ai_call_%' OR cs.room_name LIKE 'task_%' OR cs.call_type = 'ai' OR cs.is_outbound_ai = TRUE)
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
                else if (room.StartsWith("task_call_"))
                {
                    persona = "outbound_task";
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
                    telephonyProvider = (string)(r.telephony_provider ?? "livekit"),
                    telephony_provider = (string)(r.telephony_provider ?? "livekit"),
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

        // 11b-post. Save Kids Learning Report for Call Session
        app.MapPost("/api/ai/calls/{callId}/learning-report", async (string callId, System.Text.Json.JsonElement body, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            string callSessionIdStr = callId;
            Guid callSessionId = Guid.Empty;
            if (Guid.TryParse(callId, out var parsedId)) callSessionId = parsedId;

            if (callSessionId == Guid.Empty)
            {
                var found = await db.ExecuteScalarAsync<Guid?>("SELECT id FROM call_session WHERE room_name LIKE @likeRoom OR id::text=@callId LIMIT 1", new { callId, likeRoom = $"%{callId}%" });
                if (found.HasValue) callSessionId = found.Value;
            }
            if (callSessionId == Guid.Empty) callSessionId = Guid.NewGuid();

            Guid userId = Guid.Empty;
            var foundUser = await db.ExecuteScalarAsync<Guid?>("SELECT caller_user_id FROM call_session WHERE id=@callSessionId", new { callSessionId });
            if (foundUser.HasValue) userId = foundUser.Value;
            if (userId == Guid.Empty) userId = Guid.NewGuid();

            string rawJson = body.GetRawText();
            string callerId = body.TryGetProperty("caller_id", out var cProp) ? cProp.GetString() ?? "" : (body.TryGetProperty("callerId", out var ciProp) ? ciProp.GetString() ?? "" : "");
            int durationSec = body.TryGetProperty("duration_seconds", out var dProp) ? dProp.GetInt32() : (body.TryGetProperty("durationSeconds", out var dsProp) ? dsProp.GetInt32() : 0);
            string durationFmt = body.TryGetProperty("duration_formatted", out var dfProp) ? dfProp.GetString() ?? "" : (body.TryGetProperty("durationFormatted", out var dfsProp) ? dfsProp.GetString() ?? "" : "");
            if (string.IsNullOrWhiteSpace(durationFmt) && durationSec > 0)
            {
                durationFmt = $"{durationSec / 60}m {durationSec % 60}s";
            }
            string tier = body.TryGetProperty("curriculum_tier", out var tProp) ? tProp.GetString() ?? "primary" : (body.TryGetProperty("curriculumTier", out var ctProp) ? ctProp.GetString() ?? "primary" : "primary");
            int stars = body.TryGetProperty("stars_earned", out var sProp) ? sProp.GetInt32() : (body.TryGetProperty("starsEarned", out var seProp) ? seProp.GetInt32() : 0);
            string parentTip = body.TryGetProperty("parent_tip", out var ptProp) ? ptProp.GetString() ?? "" : (body.TryGetProperty("parentTip", out var pt2Prop) ? pt2Prop.GetString() ?? "" : "");

            // 1. Insert into dedicated ai_call_learning_report table
            await db.ExecuteAsync(@"
                INSERT INTO ai_call_learning_report (call_session_id, caller_id, duration_seconds, duration_formatted, curriculum_tier, stars_earned, parent_tip)
                VALUES (@callSessionIdStr, @callerId, @durationSec, @durationFmt, @tier, @stars, @parentTip)",
                new { callSessionIdStr, callerId, durationSec, durationFmt, tier, stars, parentTip });

            // 2. Also insert into ai_call_structured_data for unified query consistency
            await db.ExecuteAsync(@"
                INSERT INTO ai_call_structured_data (call_session_id, user_id, persona_id, data_type, structured_json, summary)
                VALUES (@callSessionId, @userId, 'persona_role_kids_learning', 'kids_learning_report', @rawJson::jsonb, @parentTip)",
                new { callSessionId, userId, rawJson, parentTip });

            return Results.Ok(new { success = true, callSessionId, durationFormatted = durationFmt, starsEarned = stars });
        });

        // 11b. Get Kids Learning Report for Call Session
        app.MapGet("/api/ai/calls/{callId}/learning-report", async (string callId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            var repRow = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT call_session_id, caller_id, duration_seconds, duration_formatted, curriculum_tier, stars_earned, parent_tip, created_at
                FROM ai_call_learning_report
                WHERE call_session_id = @callId
                ORDER BY created_at DESC LIMIT 1",
                new { callId });

            if (repRow != null)
            {
                return Results.Ok(new
                {
                    success = true,
                    callId = (string)repRow.call_session_id,
                    callerId = (string)repRow.caller_id,
                    durationSeconds = (int)repRow.duration_seconds,
                    durationFormatted = (string)repRow.duration_formatted,
                    curriculumTier = (string)repRow.curriculum_tier,
                    starsEarned = (int)repRow.stars_earned,
                    parentTip = (string)repRow.parent_tip,
                    createdAt = (DateTime)repRow.created_at
                });
            }

            var row = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT structured_json::text AS json_text, summary, created_at, persona_id
                FROM ai_call_structured_data
                WHERE (call_session_id::text = @callId OR call_session_id IN (SELECT id FROM call_session WHERE room_name LIKE @likeRoom))
                  AND data_type = 'kids_learning_report'
                ORDER BY created_at DESC LIMIT 1",
                new { callId, likeRoom = $"%{callId}%" });

            if (row == null) return Results.NotFound(new { error = "Learning report not found" });


            return Results.Ok(new
            {
                success = true,
                report = System.Text.Json.JsonDocument.Parse((string)row.json_text).RootElement,
                summary = (string)row.summary,
                createdAt = (DateTime)row.created_at
            });
        });

        // 11c. Get All Kids Learning Reports for User
        app.MapGet("/api/ai/users/{userId}/learning-reports", async (string userId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            Guid uid = Guid.Empty;
            if (Guid.TryParse(userId, out var g)) uid = g;

            var rows = await db.QueryAsync<dynamic>(@"
                SELECT id, call_session_id, structured_json::text AS json_text, summary, created_at
                FROM ai_call_structured_data
                WHERE user_id = @uid AND data_type = 'kids_learning_report'
                ORDER BY created_at DESC LIMIT 20",
                new { uid });

            var list = rows.Select(r => new
            {
                id = (Guid)r.id,
                callSessionId = (Guid)r.call_session_id,
                report = System.Text.Json.JsonDocument.Parse((string)r.json_text).RootElement,
                summary = (string)r.summary,
                createdAt = (DateTime)r.created_at
            });

            return Results.Ok(new { success = true, reports = list });
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
        // 13. Admin AI Personas: List all configured personas
        app.MapGet("/api/admin/ai/personas", async (IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var personas = await db.QueryAsync<dynamic>(@"
                SELECT id, name, subtitle, category, rate_per_minute, system_prompt, greeting_text, tts_engine, tts_voice, temperature, 
                       custom_questions::text AS custom_questions_json, is_active, created_at, updated_at
                FROM ai_persona_config
                ORDER BY created_at ASC");
            return Results.Ok(personas);
        });

        // 14. Admin AI Personas: Update persona rates, prompts, voice engine, and custom questions
        app.MapPut("/api/admin/ai/personas/{personaId}", async (string personaId, UpdateAiPersonaDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var questionsJson = string.IsNullOrWhiteSpace(dto.CustomQuestionsJson) ? "[]" : dto.CustomQuestionsJson;
            var rows = await db.ExecuteAsync(@"
                UPDATE ai_persona_config
                SET name = COALESCE(@Name, name),
                    subtitle = COALESCE(@Subtitle, subtitle),
                    category = COALESCE(@Category, category),
                    rate_per_minute = COALESCE(@RatePerMinute, rate_per_minute),
                    system_prompt = COALESCE(@SystemPrompt, system_prompt),
                    greeting_text = COALESCE(@GreetingText, greeting_text),
                    tts_engine = COALESCE(@TtsEngine, tts_engine),
                    tts_voice = COALESCE(@TtsVoice, tts_voice),
                    custom_questions = @questionsJson::jsonb,
                    is_active = COALESCE(@IsActive, is_active),
                    updated_at = now()
                WHERE id = @personaId",
                new {
                    personaId,
                    dto.Name,
                    dto.Subtitle,
                    dto.Category,
                    dto.RatePerMinute,
                    dto.SystemPrompt,
                    dto.GreetingText,
                    dto.TtsEngine,
                    dto.TtsVoice,
                    questionsJson,
                    dto.IsActive
                });
            return Results.Ok(new { success = rows > 0, personaId });
        });

        // 15. Admin AI Personas: Create new custom persona
        app.MapPost("/api/admin/ai/personas", async (CreateAiPersonaDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var cleanId = dto.Id.StartsWith("persona_role_") ? dto.Id : $"persona_role_{dto.Id}";
            var questionsJson = string.IsNullOrWhiteSpace(dto.CustomQuestionsJson) ? "[]" : dto.CustomQuestionsJson;
            await db.ExecuteAsync(@"
                INSERT INTO ai_persona_config (id, name, subtitle, category, rate_per_minute, system_prompt, greeting_text, tts_engine, tts_voice, custom_questions, is_active)
                VALUES (@cleanId, @Name, @Subtitle, @Category, @RatePerMinute, @SystemPrompt, @GreetingText, @TtsEngine, @TtsVoice, @questionsJson::jsonb, @IsActive)
                ON CONFLICT (id) DO UPDATE
                SET name = EXCLUDED.name,
                    subtitle = EXCLUDED.subtitle,
                    category = EXCLUDED.category,
                    rate_per_minute = EXCLUDED.rate_per_minute,
                    system_prompt = EXCLUDED.system_prompt,
                    greeting_text = EXCLUDED.greeting_text,
                    tts_engine = EXCLUDED.tts_engine,
                    tts_voice = EXCLUDED.tts_voice,
                    custom_questions = EXCLUDED.custom_questions,
                    is_active = EXCLUDED.is_active,
                    updated_at = now()",
                new {
                    cleanId,
                    dto.Name,
                    dto.Subtitle,
                    dto.Category,
                    RatePerMinute = dto.RatePerMinute > 0 ? dto.RatePerMinute : 3.00m,
                    SystemPrompt = dto.SystemPrompt ?? "",
                    GreetingText = dto.GreetingText ?? "",
                    TtsEngine = dto.TtsEngine ?? "edge",
                    TtsVoice = dto.TtsVoice ?? "hi-IN-SwaraNeural",
                    questionsJson,
                    IsActive = dto.IsActive ?? true
                });
            return Results.Ok(new { success = true, id = cleanId });
        });

        // 16. Fast Persona Config Endpoint for AI Agent (fetches live custom questions & prompt)
        app.MapGet("/api/ai/personas/{personaId}/config", async (string personaId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var cleanPersonaId = personaId.StartsWith("persona_role_") ? personaId : $"persona_role_{personaId}";
            var persona = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT id, name, subtitle, category, rate_per_minute, system_prompt, greeting_text, tts_engine, tts_voice, 
                       custom_questions::text AS custom_questions_json
                FROM ai_persona_config
                WHERE id = @cleanPersonaId",
                new { cleanPersonaId });

            if (persona == null)
                return Results.NotFound(new { error = "Persona not found", personaId });

            return Results.Ok(new
            {
                id = (string)persona.id,
                name = (string)persona.name,
                subtitle = (string)persona.subtitle,
                category = (string)persona.category,
                ratePerMinute = (decimal)persona.rate_per_minute,
                systemPrompt = (string)persona.system_prompt,
                greetingText = (string)persona.greeting_text,
                ttsEngine = (string)persona.tts_engine,
                ttsVoice = (string)persona.tts_voice,
                customQuestions = (string)persona.custom_questions_json
            });
        });

        // 17. Direct Outbound Call Box: Admin enters user phone/username -> Triggers Outbound AI Call (FREE for recipient)
        app.MapPost("/api/admin/ai/trigger-call", async (TriggerAiCallDto dto, IDbConnection db, LiveKitTokenService livekit, CallNotifier notifier, IHubContext<CallHub> hubContext, HttpContext httpContext) =>
        {
            await EnsureTablesAsync(db);

            if (string.IsNullOrWhiteSpace(dto.TargetPhoneOrUsername))
                return Results.BadRequest(new { error = "Target phone or username is required" });

            var cleanTarget = dto.TargetPhoneOrUsername.Trim();
            var targetUser = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, phone, username FROM app_user WHERE phone=@cleanTarget OR username=@cleanTarget OR id::text=@cleanTarget LIMIT 1",
                new { cleanTarget });

            Guid targetUserId;
            string targetPhone = cleanTarget;
            string targetName = cleanTarget;

            if (targetUser != null)
            {
                targetUserId = (Guid)targetUser.id;
                targetPhone = (string)(targetUser.phone ?? cleanTarget);
                targetName = (string)(targetUser.username ?? targetPhone);
            }
            else
            {
                targetUserId = await db.ExecuteScalarAsync<Guid>(
                    "INSERT INTO app_user(username, phone, role, status) VALUES(@cleanTarget, @cleanTarget, 'user', 'active') RETURNING id",
                    new { cleanTarget });
            }

            var cleanPersonaId = (dto.PersonaId ?? "persona_role_software_sales").StartsWith("persona_role_") 
                ? dto.PersonaId! 
                : $"persona_role_{dto.PersonaId}";

            var persona = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT name, rate_per_minute, greeting_text, tts_engine, tts_voice FROM ai_persona_config WHERE id=@cleanPersonaId",
                new { cleanPersonaId });

            decimal personaRate = persona != null ? (decimal)persona.rate_per_minute : 3.00m;
            string personaName = persona != null ? (string)persona.name : "AI Voice Assistant";

            var adminId = await db.ExecuteScalarAsync<Guid?>("SELECT id FROM app_user WHERE role='admin' ORDER BY created_at ASC LIMIT 1")
                ?? await db.ExecuteScalarAsync<Guid>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");

            var roomName = $"ai_call_{cleanPersonaId}_{Guid.NewGuid():N}";

            // is_outbound_ai = TRUE: The user recipient is NEVER charged!
            var callId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, is_outbound_ai, started_at)
                VALUES(@adminId, @targetUserId, @roomName, 'ringing', @personaRate, TRUE, now()) RETURNING id",
                new { adminId, targetUserId, roomName, personaRate });

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

            var metadataObj = new
            {
                call_id = callId.ToString(),
                caller_id = targetUserId.ToString(),
                target_user_id = targetUserId.ToString(),
                persona_id = cleanPersonaId,
                rate_per_minute = personaRate,
                is_outbound = true,
                campaign_id = dto.CampaignId,
                lead_id = dto.LeadId,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            var metadataJson = JsonSerializer.Serialize(metadataObj);

            var userToken = livekit.CreateToken(apiKey, apiSecret, roomName, targetUserId.ToString(), metadataJson);

            // Notify user via SignalR and FCM
            var fcmToken = await db.ExecuteScalarAsync<string?>("SELECT fcm_token FROM user_device WHERE user_id=@targetUserId ORDER BY last_seen_at DESC LIMIT 1", new { targetUserId });
            await notifier.NotifyIncomingCallAsync(db, targetUserId, callId, personaName, fcmToken);

            await db.ExecuteAsync("INSERT INTO call_event(call_session_id, event_type, actor_user_id) VALUES(@callId, 'admin_outbound_ai_dialed', @adminId)", new { callId, adminId });

            return Results.Ok(new
            {
                success = true,
                callId,
                roomName,
                targetUserId,
                targetName,
                targetPhone,
                personaName,
                liveKitUrl,
                userToken,
                isOutbound = true,
                message = $"Outbound AI call initiated to {targetName} ({targetPhone}) using persona {personaName}."
            });
        });

        // 18. AI Campaigns: Create Bulk Campaign with 10, 50, 100 Phone Numbers & Custom Questions
        app.MapPost("/api/admin/ai/campaigns", async (CreateAiCampaignDto dto, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);

            if (string.IsNullOrWhiteSpace(dto.Title))
                return Results.BadRequest(new { error = "Campaign title is required" });

            var questionsJson = string.IsNullOrWhiteSpace(dto.CustomQuestionsJson) ? "[]" : dto.CustomQuestionsJson;
            var telephonyProvider = string.IsNullOrWhiteSpace(dto.TelephonyProvider) ? "livekit" : dto.TelephonyProvider.Trim().ToLower();
            var campaignId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO ai_campaign (title, persona_id, campaign_type, custom_prompt, custom_questions, telephony_provider, status, created_at)
                VALUES (@Title, @PersonaId, @CampaignType, @CustomPrompt, @questionsJson::jsonb, @telephonyProvider, 'active', now()) RETURNING id",
                new {
                    dto.Title,
                    PersonaId = dto.PersonaId ?? "persona_role_software_sales",
                    CampaignType = dto.CampaignType ?? "general",
                    CustomPrompt = dto.CustomPrompt ?? "",
                    questionsJson,
                    telephonyProvider
                });

            int addedCount = 0;
            if (dto.PhoneNumbers != null && dto.PhoneNumbers.Count > 0)
            {
                foreach (var rawPhone in dto.PhoneNumbers)
                {
                    var phone = rawPhone.Trim();
                    if (!string.IsNullOrWhiteSpace(phone))
                    {
                        await db.ExecuteAsync(@"
                            INSERT INTO ai_campaign_lead (campaign_id, phone_number, recipient_name, status, created_at)
                            VALUES (@campaignId, @phone, @phone, 'pending', now())",
                            new { campaignId, phone });
                        addedCount++;
                    }
                }
            }

            return Results.Ok(new { success = true, campaignId, addedLeads = addedCount });
        });

        // 19. AI Campaigns: List all bulk campaigns with progress stats
        app.MapGet("/api/admin/ai/campaigns", async (IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var campaigns = await db.QueryAsync<dynamic>(@"
                SELECT 
                    c.id,
                    c.title,
                    c.persona_id,
                    c.campaign_type,
                    c.custom_prompt,
                    c.custom_questions::text AS custom_questions_json,
                    COALESCE(c.telephony_provider, 'livekit') AS telephony_provider,
                    c.status,
                    c.created_at,
                    p.name AS persona_name,
                    p.rate_per_minute,
                    COUNT(l.id) AS total_leads,
                    COUNT(CASE WHEN l.status = 'completed' THEN 1 END) AS completed_leads,
                    COUNT(CASE WHEN l.status = 'answered' THEN 1 END) AS answered_leads,
                    COUNT(CASE WHEN l.status = 'dialing' THEN 1 END) AS dialing_leads
                FROM ai_campaign c
                LEFT JOIN ai_persona_config p ON p.id = c.persona_id
                LEFT JOIN ai_campaign_lead l ON l.campaign_id = c.id
                GROUP BY c.id, c.title, c.persona_id, c.campaign_type, c.custom_prompt, c.custom_questions, c.telephony_provider, c.status, c.created_at, p.name, p.rate_per_minute
                ORDER BY c.created_at DESC");
            return Results.Ok(campaigns);
        });

        // 20. AI Campaigns: Get leads & extracted scorecard responses for a campaign
        app.MapGet("/api/admin/ai/campaigns/{campaignId:guid}/leads", async (Guid campaignId, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var leads = await db.QueryAsync<dynamic>(@"
                SELECT 
                    id, campaign_id, phone_number, recipient_name, status, 
                    call_session_id, duration_seconds, 
                    extracted_responses::text AS extracted_responses_json, 
                    call_summary, created_at
                FROM ai_campaign_lead
                WHERE campaign_id = @campaignId
                ORDER BY created_at ASC",
                new { campaignId });
            return Results.Ok(leads);
        });

        // 21. AI Campaigns: Dial single lead in campaign (Outbound AI Call)
        app.MapPost("/api/admin/ai/campaigns/{campaignId:guid}/dial-lead/{leadId:guid}", async (Guid campaignId, Guid leadId, IDbConnection db, LiveKitTokenService livekit, CallNotifier notifier, IHubContext<CallHub> hubContext, HttpContext httpContext) =>
        {
            await EnsureTablesAsync(db);

            var lead = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT phone_number, recipient_name, status FROM ai_campaign_lead WHERE id=@leadId AND campaign_id=@campaignId",
                new { leadId, campaignId });

            if (lead == null) return Results.NotFound("Lead not found");

            var campaign = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT persona_id, custom_questions::text as questions_json FROM ai_campaign WHERE id=@campaignId",
                new { campaignId });

            var personaId = campaign != null ? (string)campaign.persona_id : "persona_role_software_sales";

            // Mark lead status as dialing
            await db.ExecuteAsync("UPDATE ai_campaign_lead SET status='dialing' WHERE id=@leadId", new { leadId });

            // Trigger Outbound AI Call
            var triggerDto = new TriggerAiCallDto((string)lead.phone_number, personaId, campaignId.ToString(), leadId.ToString());
            
            // Invoke the trigger logic
            var cleanTarget = triggerDto.TargetPhoneOrUsername.Trim();
            var targetUser = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, phone, username FROM app_user WHERE phone=@cleanTarget OR username=@cleanTarget OR id::text=@cleanTarget LIMIT 1",
                new { cleanTarget });

            Guid targetUserId = targetUser != null 
                ? (Guid)targetUser.id 
                : await db.ExecuteScalarAsync<Guid>("INSERT INTO app_user(username, phone, role, status) VALUES(@cleanTarget, @cleanTarget, 'user', 'active') RETURNING id", new { cleanTarget });

            var persona = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT name, rate_per_minute FROM ai_persona_config WHERE id=@personaId",
                new { personaId });

            decimal personaRate = persona != null ? (decimal)persona.rate_per_minute : 3.00m;
            string personaName = persona != null ? (string)persona.name : "AI Voice Assistant";

            var adminId = await db.ExecuteScalarAsync<Guid?>("SELECT id FROM app_user WHERE role='admin' ORDER BY created_at ASC LIMIT 1")
                ?? await db.ExecuteScalarAsync<Guid>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");

            var roomName = $"ai_call_{personaId}_{Guid.NewGuid():N}";

            // Outbound AI Call is FREE for user
            var callId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO call_session(caller_user_id, host_user_id, room_name, status, rate_per_minute, is_outbound_ai, started_at)
                VALUES(@adminId, @targetUserId, @roomName, 'ringing', @personaRate, TRUE, now()) RETURNING id",
                new { adminId, targetUserId, roomName, personaRate });

            // Link call_session_id to campaign lead
            await db.ExecuteAsync("UPDATE ai_campaign_lead SET call_session_id=@callId WHERE id=@leadId", new { callId, leadId });

            // LiveKit token
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

            var metadataObj = new
            {
                call_id = callId.ToString(),
                caller_id = targetUserId.ToString(),
                target_user_id = targetUserId.ToString(),
                persona_id = personaId,
                rate_per_minute = personaRate,
                is_outbound = true,
                campaign_id = campaignId.ToString(),
                lead_id = leadId.ToString(),
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            var metadataJson = JsonSerializer.Serialize(metadataObj);

            var userToken = livekit.CreateToken(apiKey, apiSecret, roomName, targetUserId.ToString(), metadataJson);
            var fcmToken = await db.ExecuteScalarAsync<string?>("SELECT fcm_token FROM user_device WHERE user_id=@targetUserId ORDER BY last_seen_at DESC LIMIT 1", new { targetUserId });
            await notifier.NotifyIncomingCallAsync(db, targetUserId, callId, personaName, fcmToken);

            return Results.Ok(new
            {
                success = true,
                callId,
                leadId,
                phone = cleanTarget,
                status = "dialing",
                message = $"Calling lead {cleanTarget} for campaign."
            });
        });

        app.MapGet("/api/referral/my-info", async (string? userId, ClaimsPrincipal cp, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            Guid uid;
            if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var parsedUid))
            {
                uid = parsedUid;
            }
            else
            {
                var sub = cp.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out uid))
                {
                    uid = await db.ExecuteScalarAsync<Guid>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");
                }
            }

            var user = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT id, username, phone, referral_code FROM app_user WHERE id = @uid", new { uid });
            if (user == null) return Results.NotFound("User not found");

            string refCode = (string)user.referral_code ?? "";
            if (string.IsNullOrEmpty(refCode))
            {
                refCode = "REF" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
                await db.ExecuteAsync("UPDATE app_user SET referral_code = @refCode WHERE id = @uid", new { refCode, uid });
            }

            var rewardVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='referral_bonus_amount'");
            decimal rewardAmount = 10m;
            if (!string.IsNullOrEmpty(rewardVal) && decimal.TryParse(rewardVal, out var parsedReward))
            {
                rewardAmount = parsedReward;
            }

            int totalInvited = await db.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM app_user_referral WHERE referrer_user_id = @uid", new { uid });
            decimal totalEarned = await db.ExecuteScalarAsync<decimal?>("SELECT COALESCE(SUM(reward_amount), 0) FROM app_user_referral WHERE referrer_user_id = @uid", new { uid }) ?? 0m;

            var recentReferrals = await db.QueryAsync(@"
                SELECT r.id, r.reward_amount, r.reward_status, r.created_at, u.username, u.phone
                FROM app_user_referral r
                JOIN app_user u ON u.id = r.referred_user_id
                WHERE r.referrer_user_id = @uid
                ORDER BY r.created_at DESC
                LIMIT 20", new { uid });

            string shareLink = $"https://pruva.app/join?ref={refCode}";
            string shareMessage = $"Hey! Join Pruva Voice App using my referral code {refCode} to get instant bonus wallet balance! Download now: {shareLink}";

            return Results.Ok(new
            {
                userId = uid,
                referral_code = refCode,
                share_link = shareLink,
                share_message = shareMessage,
                reward_per_referral = rewardAmount,
                total_invited = totalInvited,
                total_earned = totalEarned,
                recent_referrals = recentReferrals
            });
        });

        app.MapPost("/api/referral/apply", async (ApplyReferralDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var sub = cp.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(sub, out var uid))
            {
                uid = await db.ExecuteScalarAsync<Guid>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");
            }

            if (string.IsNullOrWhiteSpace(dto.ReferralCode)) return Results.BadRequest("Referral code is required");
            string cleanCode = dto.ReferralCode.Trim().ToUpperInvariant();

            var currentUser = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT id, referred_by_user_id, referral_code FROM app_user WHERE id = @uid", new { uid });
            if (currentUser == null) return Results.NotFound("User not found");

            if (currentUser.referred_by_user_id != null)
            {
                return Results.BadRequest("You have already applied a referral code.");
            }

            if (string.Equals((string)currentUser.referral_code, cleanCode, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("You cannot use your own referral code.");
            }

            var referrer = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT id, username FROM app_user WHERE UPPER(referral_code) = @cleanCode", new { cleanCode });
            if (referrer == null)
            {
                return Results.BadRequest("Invalid referral code. Please check and try again.");
            }

            Guid referrerId = (Guid)referrer.id;
            var rewardVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='referral_bonus_amount'");
            decimal referralReward = 10m;
            if (!string.IsNullOrEmpty(rewardVal) && decimal.TryParse(rewardVal, out var parsedReward))
            {
                referralReward = parsedReward;
            }

            var refereeBonusVal = await db.QueryFirstOrDefaultAsync<string>("SELECT value FROM app_setting WHERE key='referred_user_bonus_amount'");
            decimal refereeReward = 5m;
            if (!string.IsNullOrEmpty(refereeBonusVal) && decimal.TryParse(refereeBonusVal, out var parsedReferee))
            {
                refereeReward = parsedReferee;
            }

            await db.ExecuteAsync("UPDATE app_user SET referred_by_user_id = @referrerId WHERE id = @uid", new { referrerId, uid });

            await db.ExecuteAsync(@"
                INSERT INTO wallet_account(user_id, balance, currency, updated_at) 
                VALUES (@referrerId, @referralReward, 'INR', now()) 
                ON CONFLICT (user_id) DO UPDATE SET balance = wallet_account.balance + @referralReward, updated_at = now();

                INSERT INTO wallet_transaction(user_id, txn_type, amount, note) 
                VALUES (@referrerId, 'referral_bonus', @referralReward, 'Referral bonus for inviting new user');

                INSERT INTO app_user_referral(referrer_user_id, referred_user_id, reward_amount, reward_status)
                VALUES (@referrerId, @uid, @referralReward, 'credited');
            ", new { referrerId, referralReward, uid });

            if (refereeReward > 0)
            {
                await db.ExecuteAsync(@"
                    INSERT INTO wallet_account(user_id, balance, currency, updated_at) 
                    VALUES (@uid, @refereeReward, 'INR', now()) 
                    ON CONFLICT (user_id) DO UPDATE SET balance = wallet_account.balance + @refereeReward, updated_at = now();

                    INSERT INTO wallet_transaction(user_id, txn_type, amount, note) 
                    VALUES (@uid, 'recharge', @refereeReward, 'Referral sign-up bonus credited');
                ", new { uid, refereeReward });
            }

            return Results.Ok(new
            {
                success = true,
                message = $"Referral code applied! You received ₹{refereeReward} bonus, and referrer was rewarded ₹{referralReward}!",
                referee_reward = refereeReward,
                referrer_reward = referralReward
            });
        });

        app.MapGet("/api/admin/referrals", async (IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            var rows = await db.QueryAsync(@"
                SELECT r.id, r.reward_amount, r.reward_status, r.created_at,
                       ref.id AS referrer_id, ref.username AS referrer_username, ref.phone AS referrer_phone,
                       usr.id AS referred_id, usr.username AS referred_username, usr.phone AS referred_phone, usr.created_at AS referred_joined_at
                FROM app_user_referral r
                JOIN app_user ref ON ref.id = r.referrer_user_id
                JOIN app_user usr ON usr.id = r.referred_user_id
                ORDER BY r.created_at DESC
                LIMIT 500");
            return Results.Ok(rows);
        });

        app.MapPost("/api/ai/text-chat", async (AiTextChatDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            await EnsureTablesAsync(db);
            if (string.IsNullOrWhiteSpace(dto.Message))
            {
                return Results.BadRequest("Message cannot be empty");
            }

            Guid userId = dto.UserId ?? Guid.Empty;
            if (userId == Guid.Empty)
            {
                var sub = cp.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out userId))
                {
                    userId = await db.ExecuteScalarAsync<Guid>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");
                }
            }

            string personaId = dto.PersonaId ?? "persona_role_companion";

            await db.ExecuteAsync(@"
                INSERT INTO ai_call_message(user_id, persona_id, role, content, created_at)
                VALUES(@userId, @personaId, 'user', @Message, now())",
                new { userId, personaId, dto.Message });

            var persona = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT name, subtitle, category, system_prompt FROM ai_persona_config WHERE id = @personaId",
                new { personaId });

            string personaName = persona?.name ?? "Pruva AI";

            string reply;
            string lowerMsg = dto.Message.ToLowerInvariant();
            if (lowerMsg.Contains("namaste") || lowerMsg.Contains("hello") || lowerMsg.Contains("hi") || lowerMsg.Contains("kaisa"))
            {
                reply = $"Namaste! Main {personaName} bol rahi hoon. Aapka message mil gaya! Bataiye, aaj main aapki kya madad kar sakti hoon?";
            }
            else if (lowerMsg.Contains("kya kar") || lowerMsg.Contains("busy"))
            {
                reply = $"Main to bas aapke message ka wait kar rahi thi! Aap bataiye, aapka din kaisa chal raha hai?";
            }
            else if (lowerMsg.Contains("refer") || lowerMsg.Contains("paisa") || lowerMsg.Contains("wallet") || lowerMsg.Contains("10"))
            {
                reply = "Haan! Hamare app me Refer & Earn program live hai. Aap Host Studio se apna link share karke har dost ke join karne par ₹10 wallet cash kama sakte hain!";
            }
            else if (lowerMsg.Contains("gana") || lowerMsg.Contains("song"))
            {
                reply = "Mujhe gaane sunna aur sunana dono bahut pasand hai! Aap call par bhi gaana sunne ke liye bol sakte hain, main turant player chala dungi!";
            }
            else
            {
                reply = $"Aapne bilkul sahi kaha! Main samajh rahi hoon. Iske baare me aur detail me baat karni ho to aap mujhe Call History se wapas call bhi kar sakte hain!";
            }

            await db.ExecuteAsync(@"
                INSERT INTO ai_call_message(user_id, persona_id, role, content, created_at)
                VALUES(@userId, @personaId, 'assistant', @reply, now())",
                new { userId, personaId, reply });

            return Results.Ok(new
            {
                success = true,
                persona_id = personaId,
                persona_name = personaName,
                reply,
                timestamp = DateTime.UtcNow
            });
        });
    }
}

public record StartAiCallDto(string PersonaId, string Language, string Gender, string? TtsProvider = null, string? Pitch = null, string? Rate = null);
public record EndAiCallDto(int DurationSeconds, string? RecordingFile, string? LlmProvider = null, string? LlmModel = null, string? LatencyTelemetry = null);
public record AiSettingsDto(string Mode, string? Whitelist, string? Blacklist);
public record AiTurnDto(string Role, string Content);
public record AppendTurnDto(string? CallId, string? UserId, string PersonaId, string Role, string Content);
public record SaveAiMemoryDto(string? CallId, string? UserId, string PersonaId, List<AiTurnDto>? Turns, string? MemorySummary, string? LastTopic = null, string? LastUserStatement = null, string? LastOpeningStyle = null, string? UserName = null);
public record RateAiCallDto(int Rating, string? PersonaId, string? Takeaways, string? Feedback);
public record SaveStructuredDataDto(string? CallSessionId, string? UserId, string? PersonaId, string? DataType, string? StructuredJson, string? Summary);
public record UpdateAiPersonaDto(string? Name, string? Subtitle, string? Category, decimal? RatePerMinute, string? SystemPrompt, string? GreetingText, string? TtsEngine, string? TtsVoice, string? CustomQuestionsJson, bool? IsActive);
public record CreateAiPersonaDto(string Id, string Name, string Subtitle, string Category, decimal RatePerMinute, string? SystemPrompt, string? GreetingText, string? TtsEngine, string? TtsVoice, string? CustomQuestionsJson, bool? IsActive);
public record TriggerAiCallDto(string TargetPhoneOrUsername, string PersonaId, string? CampaignId = null, string? LeadId = null);
public record CreateAiCampaignDto(string Title, string PersonaId, string? CampaignType, string? CustomPrompt, string? CustomQuestionsJson, List<string> PhoneNumbers, string? TelephonyProvider = "livekit");
public record BridgeAiCallDto(string RoomName, string? DesiredGender = "female", string? Requirement = null, string? CallerUserId = null, string? City = null, string? Language = null, string? ConfirmedHostId = null, bool ForceConnectAlternate = false, string? TargetUsername = null);
public record SendChatRestDto(Guid RecipientId, string Text, string? MessageType = "text");
public record UpsertFamilyMemberDto(Guid? Id, string? UserId, string Relation, string Name, string? Nickname = null, int? Age = null, string? Birthday = null, string? FavoriteColor = null, string? FavoriteFood = null, string? Hobbies = null, string? HealthNotes = null, string? PreferencesJson = null);

