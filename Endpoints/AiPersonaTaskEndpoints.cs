using System.Data;
using System.Text.Json;
using Dapper;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class AiPersonaTaskEndpoints
{
    private static async Task EnsureSchemaAsync(IDbConnection db)
    {
        try
        {
            await db.ExecuteAsync(@"
                ALTER TABLE ai_persona_task ADD COLUMN IF NOT EXISTS attributes JSONB DEFAULT '[]'::jsonb;
                ALTER TABLE ai_persona_task ADD COLUMN IF NOT EXISTS telephony_provider VARCHAR(50) DEFAULT 'livekit';
                ALTER TABLE ai_persona_task DROP COLUMN IF EXISTS salary_range;
                ALTER TABLE ai_persona_task DROP COLUMN IF EXISTS experience_range;
                ALTER TABLE ai_persona_task DROP COLUMN IF EXISTS criteria_options;
                ALTER TABLE ai_persona_task_response ADD COLUMN IF NOT EXISTS attributes_evaluation JSONB DEFAULT '[]'::jsonb;
                ALTER TABLE ai_persona_task_response ADD COLUMN IF NOT EXISTS telephony_provider VARCHAR(50) DEFAULT 'livekit';
                ALTER TABLE call_session ADD COLUMN IF NOT EXISTS telephony_provider VARCHAR(50) DEFAULT 'livekit';
            ");
        }
        catch { }
    }

    public static void MapAiPersonaTaskEndpoints(this WebApplication app)
    {
        // 1. GET all persona tasks (optionally filtered by personaId)
        app.MapGet("/api/admin/persona/tasks", async (string? personaId, IDbConnection db) =>
        {
            await EnsureSchemaAsync(db);
            string query = "SELECT *, COALESCE(telephony_provider, 'livekit') AS telephony_provider FROM ai_persona_task";
            if (!string.IsNullOrWhiteSpace(personaId))
            {
                query += " WHERE persona_id = @personaId";
            }
            query += " ORDER BY created_at DESC";

            var tasks = await db.QueryAsync(query, new { personaId });
            return Results.Ok(tasks);
        });

        // 2. GET single persona task by id
        app.MapGet("/api/admin/persona/tasks/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            await EnsureSchemaAsync(db);
            var task = await db.QueryFirstOrDefaultAsync("SELECT *, COALESCE(telephony_provider, 'livekit') AS telephony_provider FROM ai_persona_task WHERE id = @id", new { id });
            if (task == null) return Results.NotFound(new { error = "Task not found" });
            return Results.Ok(task);
        });

        // 3. POST create persona task
        app.MapPost("/api/admin/persona/tasks", async (PersonaTaskUpsertDto dto, IDbConnection db) =>
        {
            await EnsureSchemaAsync(db);
            var attributesJson = dto.Attributes != null ? JsonSerializer.Serialize(dto.Attributes) : "[]";
            var questionsJson = dto.Questions != null ? JsonSerializer.Serialize(dto.Questions) : "[]";
            var criteriaJson = dto.RequirementCriteria != null ? JsonSerializer.Serialize(dto.RequirementCriteria) : "{}";
            var targetsJson = dto.TargetPhoneNumbers != null ? JsonSerializer.Serialize(dto.TargetPhoneNumbers) : "[]";
            string provider = string.IsNullOrWhiteSpace(dto.TelephonyProvider) ? "livekit" : dto.TelephonyProvider.ToLower().Trim();

            var newId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO ai_persona_task (
                    persona_id, task_code, title, description, system_prompt, 
                    attributes, questions, requirement_criteria, target_phone_numbers, status, telephony_provider
                ) VALUES (
                    @PersonaId, @TaskCode, @Title, @Description, @SystemPrompt, 
                    @Attributes::jsonb, @Questions::jsonb, @Criteria::jsonb, @Targets::jsonb, @Status, @TelephonyProvider
                ) RETURNING id",
                new
                {
                    dto.PersonaId,
                    dto.TaskCode,
                    dto.Title,
                    dto.Description,
                    dto.SystemPrompt,
                    Attributes = attributesJson,
                    Questions = questionsJson,
                    Criteria = criteriaJson,
                    Targets = targetsJson,
                    Status = string.IsNullOrWhiteSpace(dto.Status) ? "active" : dto.Status,
                    TelephonyProvider = provider
                });

            return Results.Ok(new { id = newId, message = "Persona task created successfully", telephonyProvider = provider });
        });

        // 4. PUT update persona task
        app.MapPut("/api/admin/persona/tasks/{id:guid}", async (Guid id, PersonaTaskUpsertDto dto, IDbConnection db) =>
        {
            await EnsureSchemaAsync(db);
            var attributesJson = dto.Attributes != null ? JsonSerializer.Serialize(dto.Attributes) : "[]";
            var questionsJson = dto.Questions != null ? JsonSerializer.Serialize(dto.Questions) : "[]";
            var criteriaJson = dto.RequirementCriteria != null ? JsonSerializer.Serialize(dto.RequirementCriteria) : "{}";
            var targetsJson = dto.TargetPhoneNumbers != null ? JsonSerializer.Serialize(dto.TargetPhoneNumbers) : "[]";
            string provider = string.IsNullOrWhiteSpace(dto.TelephonyProvider) ? "livekit" : dto.TelephonyProvider.ToLower().Trim();

            var rows = await db.ExecuteAsync(@"
                UPDATE ai_persona_task SET
                    persona_id = @PersonaId,
                    task_code = @TaskCode,
                    title = @Title,
                    description = @Description,
                    system_prompt = @SystemPrompt,
                    attributes = @Attributes::jsonb,
                    questions = @Questions::jsonb,
                    requirement_criteria = @Criteria::jsonb,
                    target_phone_numbers = @Targets::jsonb,
                    status = @Status,
                    telephony_provider = @TelephonyProvider,
                    updated_at = NOW()
                WHERE id = @id",
                new
                {
                    id,
                    dto.PersonaId,
                    dto.TaskCode,
                    dto.Title,
                    dto.Description,
                    dto.SystemPrompt,
                    Attributes = attributesJson,
                    Questions = questionsJson,
                    Criteria = criteriaJson,
                    Targets = targetsJson,
                    Status = string.IsNullOrWhiteSpace(dto.Status) ? "active" : dto.Status,
                    TelephonyProvider = provider
                });

            if (rows == 0) return Results.NotFound(new { error = "Task not found" });
            return Results.Ok(new { message = "Persona task updated successfully", telephonyProvider = provider });
        });

        // 5. DELETE persona task
        app.MapDelete("/api/admin/persona/tasks/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            var rows = await db.ExecuteAsync("DELETE FROM ai_persona_task WHERE id = @id", new { id });
            if (rows == 0) return Results.NotFound(new { error = "Task not found" });
            return Results.Ok(new { message = "Persona task deleted successfully" });
        });

        // 6. POST Trigger outbound call for a task & target contact
        app.MapPost("/api/admin/persona/tasks/{id:guid}/trigger-call", async (Guid id, TriggerTaskCallDto dto, IDbConnection db, LiveKitTokenService liveKit, IConfiguration config) =>
        {
            var task = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT *, COALESCE(telephony_provider, 'livekit') AS telephony_provider FROM ai_persona_task WHERE id = @id", new { id });
            if (task == null) return Results.NotFound(new { error = "Task not found" });

            string targetPhone = dto.PhoneNumber?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(targetPhone))
            {
                return Results.BadRequest(new { error = "Target phone number is required" });
            }

            // Find or link user if exists in app_user
            var cleanPhone = targetPhone.Replace("+91", "").Replace("-", "").Replace(" ", "").Trim();
            var user = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, username, display_name, phone FROM app_user WHERE phone = @targetPhone OR phone = @cleanPhone OR username = @targetPhone",
                new { targetPhone, cleanPhone });

            // Admin user ID fallback for valid foreign key
            var adminId = await db.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM app_user WHERE role='admin' ORDER BY created_at ASC LIMIT 1")
                ?? await db.ExecuteScalarAsync<Guid?>("SELECT id FROM app_user ORDER BY created_at ASC LIMIT 1");

            Guid callerId = (user != null ? (Guid)user.id : adminId) ?? Guid.NewGuid();
            Guid? userId = user != null ? (Guid)user.id : null;
            string userName = !string.IsNullOrWhiteSpace(dto.UserName)
                ? dto.UserName
                : (user != null ? (string)(user.display_name ?? user.username ?? targetPhone) : targetPhone);

            // Carrier / telephony provider selection
            string provider = (task.telephony_provider != null ? (string)task.telephony_provider : "livekit").ToLower().Trim();
            if (string.IsNullOrWhiteSpace(provider)) provider = "livekit";

            // Generate Room & Session
            var callSessionId = Guid.NewGuid();
            string roomName = $"task_call_{callSessionId:N}";
            string personaId = (string)task.persona_id;
            string taskCode = (string)task.task_code;
            string taskTitle = (string)task.title;

            // Insert call_session record marked as outbound AI with carrier
            await db.ExecuteAsync(@"
                INSERT INTO call_session (id, caller_user_id, host_user_id, room_name, status, rate_per_minute, is_outbound_ai, started_at, call_type, telephony_provider)
                VALUES (@callSessionId, @callerId, @callerId, @roomName, 'initiated', 0.00, true, NOW(), 'ai', @provider)",
                new { callSessionId, callerId, roomName, provider });

            // Create pending response record with selected telephony carrier
            var responseId = await db.ExecuteScalarAsync<Guid>(@"
                INSERT INTO ai_persona_task_response (
                    task_id, persona_id, task_code, task_title, user_id, phone_number, user_name,
                    call_session_id, call_status, match_score, qualification_status, telephony_provider
                ) VALUES (
                    @id, @personaId, @taskCode, @taskTitle, @userId, @targetPhone, @userName,
                    @callSessionId, 'initiated', 0, 'Pending Call', @provider
                ) RETURNING id",
                new { id, personaId, taskCode, taskTitle, userId, targetPhone, userName, callSessionId, provider });

            var credJson = await db.ExecuteScalarAsync<string>("SELECT config_json::text FROM integration_credential WHERE provider_type='livekit' AND is_active=true");
            var configDb = string.IsNullOrWhiteSpace(credJson) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(credJson);
            var apiKey = configDb != null && configDb.TryGetValue("apiKey", out var ak) ? ak : (config["LiveKit:ApiKey"] ?? "devkey");
            var apiSecret = configDb != null && configDb.TryGetValue("apiSecret", out var asec) ? asec : (config["LiveKit:ApiSecret"] ?? "devsecretkeyshouldbe48characterslongforsecurity!");

            var metaPayload = JsonSerializer.Serialize(new
            {
                persona = personaId,
                taskId = id,
                taskCode,
                taskTitle,
                attributes = task.attributes,
                questions = task.questions,
                phone = targetPhone,
                userName,
                responseId,
                callSessionId,
                telephonyProvider = provider
            });
            string token = liveKit.CreateToken(apiKey, apiSecret, roomName, $"lead_{cleanPhone}", metaPayload);

            return Results.Ok(new
            {
                callSessionId,
                responseId,
                roomName,
                token,
                targetPhone,
                userName,
                personaId,
                taskTitle,
                telephonyProvider = provider,
                message = $"Outbound call initiated for {targetPhone} via {provider.ToUpper()} with task '{taskTitle}'"
            });
        });

        // 7. GET all persona task user call responses & match scores
        // Route: /api/admin/persona/task/user
        app.MapGet("/api/admin/persona/task/user", async (
            string? personaId,
            string? taskCode,
            string? status,
            string? search,
            IDbConnection db) =>
        {
            var conditions = new List<string>();
            var param = new DynamicParameters();

            if (!string.IsNullOrWhiteSpace(personaId))
            {
                conditions.Add("r.persona_id = @personaId");
                param.Add("personaId", personaId);
            }
            if (!string.IsNullOrWhiteSpace(taskCode))
            {
                conditions.Add("r.task_code = @taskCode");
                param.Add("taskCode", taskCode);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                conditions.Add("r.qualification_status = @status");
                param.Add("status", status);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(r.phone_number ILIKE @search OR r.user_name ILIKE @search OR r.summary ILIKE @search OR r.task_title ILIKE @search)");
                param.Add("search", $"%{search}%");
            }

            string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            string sql = $@"
                SELECT 
                    r.id, r.task_id, r.persona_id, r.task_code, r.task_title,
                    r.user_id, r.phone_number, r.user_name, r.call_session_id,
                    r.call_status, r.match_score, r.qualification_status,
                    r.summary, r.answers, r.dialogue, r.structured_json,
                    r.duration_seconds, r.recording_url, r.created_at,
                    COALESCE(r.telephony_provider, 'livekit') AS telephony_provider
                FROM ai_persona_task_response r
                {whereClause}
                ORDER BY r.created_at DESC
                LIMIT 100";

            var rows = await db.QueryAsync(sql, param);
            return Results.Ok(rows);
        });

        // 8. POST record or evaluate a task response (called by AI worker or test runner)
        app.MapPost("/api/admin/persona/task/user", async (RecordTaskResponseDto dto, IDbConnection db) =>
        {
            var answersJson = dto.Answers != null ? JsonSerializer.Serialize(dto.Answers) : "[]";
            var dialogueJson = dto.Dialogue != null ? JsonSerializer.Serialize(dto.Dialogue) : "[]";
            var structuredJson = dto.StructuredJson != null ? JsonSerializer.Serialize(dto.StructuredJson) : "{}";
            string provider = string.IsNullOrWhiteSpace(dto.TelephonyProvider) ? "livekit" : dto.TelephonyProvider.ToLower().Trim();

            Guid responseId;
            if (dto.ResponseId.HasValue && dto.ResponseId.Value != Guid.Empty)
            {
                // Update existing call response record
                await db.ExecuteAsync(@"
                    UPDATE ai_persona_task_response SET
                        match_score = @MatchScore,
                        qualification_status = @QualificationStatus,
                        summary = @Summary,
                        answers = @Answers::jsonb,
                        dialogue = @Dialogue::jsonb,
                        structured_json = @StructuredJson::jsonb,
                        call_status = 'completed',
                        duration_seconds = @DurationSeconds,
                        telephony_provider = COALESCE(@TelephonyProvider, telephony_provider, 'livekit')
                    WHERE id = @ResponseId",
                    new
                    {
                        dto.MatchScore,
                        dto.QualificationStatus,
                        dto.Summary,
                        Answers = answersJson,
                        Dialogue = dialogueJson,
                        StructuredJson = structuredJson,
                        dto.DurationSeconds,
                        ResponseId = dto.ResponseId.Value,
                        TelephonyProvider = provider
                    });
                responseId = dto.ResponseId.Value;
            }
            else
            {
                responseId = await db.ExecuteScalarAsync<Guid>(@"
                    INSERT INTO ai_persona_task_response (
                        task_id, persona_id, task_code, task_title, phone_number, user_name,
                        match_score, qualification_status, summary, answers, dialogue, structured_json,
                        duration_seconds, call_status, telephony_provider
                    ) VALUES (
                        @TaskId, @PersonaId, @TaskCode, @TaskTitle, @PhoneNumber, @UserName,
                        @MatchScore, @QualificationStatus, @Summary, @Answers::jsonb, @Dialogue::jsonb, @StructuredJson::jsonb,
                        @DurationSeconds, 'completed', @TelephonyProvider
                    ) RETURNING id",
                    new
                    {
                        dto.TaskId,
                        dto.PersonaId,
                        dto.TaskCode,
                        dto.TaskTitle,
                        dto.PhoneNumber,
                        dto.UserName,
                        dto.MatchScore,
                        dto.QualificationStatus,
                        dto.Summary,
                        Answers = answersJson,
                        Dialogue = dialogueJson,
                        StructuredJson = structuredJson,
                        dto.DurationSeconds,
                        TelephonyProvider = provider
                    });
            }

            return Results.Ok(new { id = responseId, message = "Task response recorded successfully" });
        });
    }
}

public class PersonaTaskUpsertDto
{
    public string PersonaId { get; set; } = "software_dev";
    public string TaskCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? SystemPrompt { get; set; }
    public string? TelephonyProvider { get; set; } = "livekit";
    public object? Attributes { get; set; }
    public object? Questions { get; set; }
    public object? RequirementCriteria { get; set; }
    public object? TargetPhoneNumbers { get; set; }
    public object? SalaryRange { get; set; }
    public object? ExperienceRange { get; set; }
    public object? CriteriaOptions { get; set; }
    public string? Status { get; set; } = "active";
}

public class TriggerTaskCallDto
{
    public string PhoneNumber { get; set; } = "";
    public string? UserName { get; set; }
}

public class RecordTaskResponseDto
{
    public Guid? ResponseId { get; set; }
    public Guid? TaskId { get; set; }
    public string PersonaId { get; set; } = "";
    public string? TaskCode { get; set; }
    public string? TaskTitle { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string? UserName { get; set; }
    public int MatchScore { get; set; }
    public string QualificationStatus { get; set; } = "Strong Match";
    public string? Summary { get; set; }
    public object? Answers { get; set; }
    public object? Dialogue { get; set; }
    public object? StructuredJson { get; set; }
    public int DurationSeconds { get; set; }
    public string? TelephonyProvider { get; set; } = "livekit";
}
