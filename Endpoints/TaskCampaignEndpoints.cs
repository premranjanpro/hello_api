using System.Data;
using System.Text.Json;
using Dapper;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Telephony;

namespace PruvaVoice.Api.Endpoints;

public static class TaskCampaignEndpoints
{
    private static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void MapTaskCampaignEndpoints(this WebApplication app)
    {
        // 1. List all Tasks / Campaigns for tenant
        app.MapGet("/api/tasks", async (Guid? tenantId, string? status, string? purpose, IDbConnection db) =>
        {
            var targetTenant = tenantId ?? DefaultTenantId;
            var sql = @"
                SELECT t.*,
                       p.name as provider_name,
                       (SELECT COUNT(*) FROM task_target_contact c WHERE c.task_id = t.id) as total_targets,
                       (SELECT COUNT(*) FROM task_target_contact c WHERE c.task_id = t.id AND c.status = 'completed') as completed_targets,
                       (SELECT COUNT(*) FROM task_target_contact c WHERE c.task_id = t.id AND c.outcome IN ('interested', 'qualified', 'interview_completed')) as successful_targets
                FROM task_campaign t
                LEFT JOIN telephony_provider_credential p ON p.id = t.telephony_provider_id
                WHERE t.tenant_id = @targetTenant
                  AND (@status IS NULL OR t.status = @status)
                  AND (@purpose IS NULL OR t.purpose = @purpose)
                ORDER BY t.created_at DESC";

            var rows = await db.QueryAsync<dynamic>(sql, new { targetTenant, status, purpose });
            return Results.Ok(rows);
        });

        // 2. GET single task by id
        app.MapGet("/api/tasks/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            var task = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.*, 
                       p.name as provider_name,
                       p.masked_config as provider_config,
                       n.phone_number as caller_number_formatted
                FROM task_campaign t
                LEFT JOIN telephony_provider_credential p ON p.id = t.telephony_provider_id
                LEFT JOIN telephony_caller_number n ON n.id = t.caller_number_id
                WHERE t.id = @id", new { id });

            if (task == null) return Results.NotFound(new { message = "Task not found" });
            return Results.Ok(task);
        });

        // 3. POST create Task / Campaign (Enforces locked telephony provider & caller number)
        app.MapPost("/api/tasks", async (TaskCampaignCreateDto dto, IDbConnection db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Title))
            {
                return Results.BadRequest(new { message = "Task Title is required" });
            }
            if (dto.TelephonyProviderId == Guid.Empty)
            {
                return Results.BadRequest(new { message = "Telephony Provider must be selected when creating a task" });
            }

            var targetTenant = dto.TenantId ?? DefaultTenantId;

            // Validate provider existence and active status
            var provider = await db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT id, provider_type, is_active FROM telephony_provider_credential WHERE id = @id AND tenant_id = @targetTenant",
                new { id = dto.TelephonyProviderId, targetTenant });

            if (provider == null || !(bool)provider.is_active)
            {
                return Results.BadRequest(new { message = "Selected Telephony Provider is invalid or not active for this tenant" });
            }

            string providerType = (string)provider.provider_type;

            // Resolve Caller Number
            string callerNum = dto.CallerNumber ?? "";
            Guid? callerNumberId = dto.CallerNumberId;

            if (callerNumberId.HasValue)
            {
                var numRow = await db.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT phone_number FROM telephony_caller_number WHERE id = @id",
                    new { id = callerNumberId.Value });
                if (numRow != null) callerNum = (string)numRow.phone_number;
            }

            var scheduleConfigJson = dto.ScheduleConfig.ValueKind != JsonValueKind.Undefined
                ? dto.ScheduleConfig.GetRawText()
                : "{\"calling_hours_start\": \"09:00\", \"calling_hours_end\": \"19:00\", \"timezone\": \"Asia/Kolkata\", \"max_concurrent_calls\": 2}";

            var callRulesJson = dto.CallRules.ValueKind != JsonValueKind.Undefined
                ? dto.CallRules.GetRawText()
                : "{\"max_attempts\": 3, \"retry_delay_minutes\": 30, \"answer_timeout_seconds\": 45, \"recording_enabled\": true, \"voicemail_action\": \"hangup\"}";

            var aiBehaviorJson = dto.AiBehaviorConfig.ValueKind != JsonValueKind.Undefined
                ? dto.AiBehaviorConfig.GetRawText()
                : "{\"language\": \"hinglish\", \"personality\": \"friendly\", \"objective\": \"\", \"questions\": []}";

            var taskId = Guid.NewGuid();

            await db.ExecuteAsync(@"
                INSERT INTO task_campaign (
                    id, tenant_id, user_id, title, description, purpose, agent_id,
                    telephony_provider_id, provider_type, caller_number_id, caller_number,
                    schedule_config, call_rules, ai_behavior_config, status, created_at, updated_at
                ) VALUES (
                    @taskId, @targetTenant, @UserId, @Title, @Description, @Purpose, @AgentId,
                    @TelephonyProviderId, @providerType, @callerNumberId, @callerNum,
                    @scheduleConfigJson::jsonb, @callRulesJson::jsonb, @aiBehaviorJson::jsonb, 'draft', now(), now()
                )",
                new
                {
                    taskId,
                    targetTenant,
                    dto.UserId,
                    dto.Title,
                    Description = dto.Description ?? "",
                    Purpose = string.IsNullOrWhiteSpace(dto.Purpose) ? "sales" : dto.Purpose.ToLowerInvariant(),
                    AgentId = string.IsNullOrWhiteSpace(dto.AgentId) ? "persona_role_software_sales" : dto.AgentId,
                    dto.TelephonyProviderId,
                    providerType,
                    callerNumberId,
                    callerNum,
                    scheduleConfigJson,
                    callRulesJson,
                    aiBehaviorJson
                });

            // Insert initial targets if provided
            int targetCount = 0;
            if (dto.Targets != null && dto.Targets.Count > 0)
            {
                foreach (var t in dto.Targets)
                {
                    if (string.IsNullOrWhiteSpace(t.PhoneNumber)) continue;
                    var metaJson = t.Metadata != null ? JsonSerializer.Serialize(t.Metadata) : "{}";
                    await db.ExecuteAsync(@"
                        INSERT INTO task_target_contact (task_id, tenant_id, phone_number, contact_name, metadata, status, created_at, updated_at)
                        VALUES (@taskId, @targetTenant, @PhoneNumber, @ContactName, @metaJson::jsonb, 'pending', now(), now())",
                        new { taskId, targetTenant, t.PhoneNumber, ContactName = t.ContactName ?? "", metaJson });
                    targetCount++;
                }

                await db.ExecuteAsync("UPDATE task_campaign SET total_targets = @targetCount WHERE id = @taskId", new { targetCount, taskId });
            }

            return Results.Ok(new { message = "Task created successfully", id = taskId, totalTargets = targetCount });
        });

        // 4. Update task status (start, pause, cancel)
        app.MapPost("/api/tasks/{id:guid}/status", async (Guid id, TaskStatusUpdateDto dto, IDbConnection db) =>
        {
            var rows = await db.ExecuteAsync(
                "UPDATE task_campaign SET status = @Status, updated_at = now() WHERE id = @id",
                new { id, Status = dto.Status.ToLowerInvariant() });

            if (rows == 0) return Results.NotFound(new { message = "Task not found" });
            return Results.Ok(new { message = $"Task status updated to {dto.Status}" });
        });

        // 5. List target contacts of a task
        app.MapGet("/api/tasks/{id:guid}/targets", async (Guid id, string? status, int? limit, int? offset, IDbConnection db) =>
        {
            var sql = @"
                SELECT * FROM task_target_contact
                WHERE task_id = @id
                  AND (@status IS NULL OR status = @status)
                ORDER BY created_at ASC
                LIMIT @take OFFSET @skip";

            var targets = await db.QueryAsync(sql, new { id, status, take = limit ?? 100, skip = offset ?? 0 });
            var totalCount = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM task_target_contact WHERE task_id = @id", new { id });

            return Results.Ok(new { total = totalCount, targets });
        });

        // 6. Bulk add targets to task
        app.MapPost("/api/tasks/{id:guid}/targets", async (Guid id, List<TargetContactDto> targets, IDbConnection db) =>
        {
            var task = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT tenant_id FROM task_campaign WHERE id = @id", new { id });
            if (task == null) return Results.NotFound(new { message = "Task not found" });

            Guid tenantId = (Guid)task.tenant_id;
            int added = 0;

            foreach (var t in targets)
            {
                if (string.IsNullOrWhiteSpace(t.PhoneNumber)) continue;
                var metaJson = t.Metadata != null ? JsonSerializer.Serialize(t.Metadata) : "{}";
                await db.ExecuteAsync(@"
                    INSERT INTO task_target_contact (task_id, tenant_id, phone_number, contact_name, metadata, status, created_at, updated_at)
                    VALUES (@id, @tenantId, @PhoneNumber, @ContactName, @metaJson::jsonb, 'pending', now(), now())",
                    new { id, tenantId, t.PhoneNumber, ContactName = t.ContactName ?? "", metaJson });
                added++;
            }

            await db.ExecuteAsync(@"
                UPDATE task_campaign SET 
                    total_targets = (SELECT COUNT(*) FROM task_target_contact WHERE task_id = @id),
                    updated_at = now()
                WHERE id = @id", new { id });

            return Results.Ok(new { message = $"Added {added} targets to campaign", count = added });
        });

        // 7. GET Task analytics & outcomes summary
        app.MapGet("/api/tasks/{id:guid}/analytics", async (Guid id, IDbConnection db) =>
        {
            var task = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM task_campaign WHERE id = @id", new { id });
            if (task == null) return Results.NotFound(new { message = "Task not found" });

            var statusCounts = await db.QueryAsync<dynamic>(@"
                SELECT status, COUNT(*) as count 
                FROM task_target_contact 
                WHERE task_id = @id 
                GROUP BY status", new { id });

            var outcomeCounts = await db.QueryAsync<dynamic>(@"
                SELECT COALESCE(outcome, 'pending') as outcome, COUNT(*) as count 
                FROM task_target_contact 
                WHERE task_id = @id 
                GROUP BY outcome", new { id });

            var avgScore = await db.ExecuteScalarAsync<double?>("SELECT AVG(match_score) FROM task_target_contact WHERE task_id = @id AND match_score > 0", new { id }) ?? 0;

            return Results.Ok(new
            {
                task,
                statusCounts,
                outcomeCounts,
                averageMatchScore = Math.Round(avgScore, 1)
            });
        });

        // 8. GET Live Execution Feed (Active in-flight calls, dials progress, real-time metrics)
        app.MapGet("/api/tasks/{id:guid}/live-feed", async (Guid id, IDbConnection db) =>
        {
            var task = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.*, p.name as provider_name, p.provider_type
                FROM task_campaign t
                LEFT JOIN telephony_provider_credential p ON p.id = t.telephony_provider_id
                WHERE t.id = @id", new { id });

            if (task == null) return Results.NotFound(new { message = "Task not found" });

            // Real-time counter metrics
            var counts = await db.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT 
                    COUNT(*) as total_targets,
                    COUNT(*) FILTER (WHERE status = 'pending') as pending_count,
                    COUNT(*) FILTER (WHERE status = 'calling') as calling_count,
                    COUNT(*) FILTER (WHERE status = 'completed') as completed_count,
                    COUNT(*) FILTER (WHERE status = 'failed') as failed_count,
                    COUNT(*) FILTER (WHERE status = 'retry_scheduled') as retry_count,
                    COUNT(*) FILTER (WHERE outcome IN ('interested', 'qualified', 'interview_completed', 'demo_scheduled')) as converted_count,
                    AVG(match_score) FILTER (WHERE match_score > 0) as avg_match_score
                FROM task_target_contact
                WHERE task_id = @id", new { id });

            // Active in-flight calls currently live
            var activeCalls = await db.QueryAsync<dynamic>(@"
                SELECT 
                    s.id as call_session_id,
                    s.provider_call_sid,
                    s.caller_number,
                    s.destination_number,
                    s.status as call_status,
                    s.started_at,
                    ROUND(EXTRACT(EPOCH FROM (now() - s.started_at)))::int as duration_seconds,
                    c.id as target_contact_id,
                    c.contact_name,
                    c.metadata as target_metadata
                FROM call_session s
                LEFT JOIN task_target_contact c ON (c.task_id = s.task_id AND c.phone_number = s.destination_number)
                WHERE s.task_id = @id 
                  AND s.status IN ('initiated', 'ringing', 'in-progress', 'connected', 'queued')
                ORDER BY s.started_at DESC
                LIMIT 20", new { id });

            // Recent completed calls with recordings & scorecards
            var recentCalls = await db.QueryAsync<dynamic>(@"
                SELECT 
                    s.id as call_session_id,
                    s.provider_call_sid,
                    s.destination_number,
                    s.caller_number,
                    s.status as call_status,
                    s.duration_seconds,
                    s.recording_storage_path,
                    s.structured_outcome,
                    s.ended_at,
                    c.contact_name,
                    c.outcome as disposition,
                    c.match_score
                FROM call_session s
                LEFT JOIN task_target_contact c ON (c.task_id = s.task_id AND c.phone_number = s.destination_number)
                WHERE s.task_id = @id
                  AND s.status IN ('completed', 'ended', 'failed', 'no-answer', 'busy')
                ORDER BY COALESCE(s.ended_at, s.started_at) DESC
                LIMIT 30", new { id });

            // Dispositions breakdown
            var outcomes = await db.QueryAsync<dynamic>(@"
                SELECT COALESCE(outcome, 'pending') as outcome, COUNT(*) as count 
                FROM task_target_contact 
                WHERE task_id = @id 
                GROUP BY outcome", new { id });

            return Results.Ok(new
            {
                task,
                metrics = counts,
                activeCalls,
                recentCalls,
                outcomes
            });
        });
    }
}

public record TaskCampaignCreateDto(
    Guid? TenantId = null,
    Guid? UserId = null,
    string Title = "",
    string? Description = null,
    string Purpose = "sales",
    string AgentId = "persona_role_software_sales",
    Guid TelephonyProviderId = default,
    Guid? CallerNumberId = null,
    string? CallerNumber = null,
    JsonElement ScheduleConfig = default,
    JsonElement CallRules = default,
    JsonElement AiBehaviorConfig = default,
    List<TargetContactDto>? Targets = null
);

public record TargetContactDto(
    string PhoneNumber = "",
    string? ContactName = null,
    Dictionary<string, object>? Metadata = null
);

public record TaskStatusUpdateDto(string Status = "");

