using System.Data;
using System.Text.Json;
using Dapper;
using PruvaVoice.Api.Telephony;

namespace PruvaVoice.Api.Services;

public class CallOrchestratorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CallOrchestratorService> _logger;

    public CallOrchestratorService(IServiceProvider serviceProvider, ILogger<CallOrchestratorService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[CallOrchestrator] Service started. Monitoring active campaigns...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessActiveCampaignsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CallOrchestrator] Error processing campaigns");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public async Task ProcessActiveCampaignsAsync(CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
        var factory = scope.ServiceProvider.GetRequiredService<TelephonyProviderFactory>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Find all running tasks
        var runningTasks = (await db.QueryAsync<dynamic>(
            "SELECT * FROM task_campaign WHERE status = 'running' ORDER BY updated_at ASC")).ToList();

        if (!runningTasks.Any()) return;

        foreach (var task in runningTasks)
        {
            if (ct.IsCancellationRequested) break;

            Guid taskId = (Guid)task.id;
            Guid tenantId = (Guid)task.tenant_id;
            Guid providerCredId = (Guid)task.telephony_provider_id;
            string providerType = (string)task.provider_type;
            string callerNumber = (string)(task.caller_number ?? "");
            string agentId = (string)task.agent_id;

            // Parse schedule config & call rules
            string schedJson = (string)(task.schedule_config?.ToString() ?? "{}");
            using var schedDoc = JsonDocument.Parse(schedJson);
            int maxConcurrent = schedDoc.RootElement.TryGetProperty("max_concurrent_calls", out var mc) ? mc.GetInt32() : 2;

            string rulesJson = (string)(task.call_rules?.ToString() ?? "{}");
            using var rulesDoc = JsonDocument.Parse(rulesJson);
            int maxAttempts = rulesDoc.RootElement.TryGetProperty("max_attempts", out var ma) ? ma.GetInt32() : 3;
            int retryDelayMin = rulesDoc.RootElement.TryGetProperty("retry_delay_minutes", out var rdm) ? rdm.GetInt32() : 30;

            // Check active concurrent calls for this task
            var activeCount = await db.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM call_session 
                WHERE task_id = @taskId AND status IN ('initiated', 'ringing', 'connected', 'in-progress')",
                new { taskId });

            int availableSlots = maxConcurrent - activeCount;
            if (availableSlots <= 0) continue;

            // Fetch next available targets
            var targets = (await db.QueryAsync<dynamic>(@"
                SELECT * FROM task_target_contact 
                WHERE task_id = @taskId 
                  AND (status = 'pending' OR (status = 'retry_scheduled' AND next_retry_at <= now()))
                  AND attempts_count < @maxAttempts
                ORDER BY attempts_count ASC, created_at ASC
                LIMIT @availableSlots",
                new { taskId, maxAttempts, availableSlots })).ToList();

            if (!targets.Any())
            {
                // Check if all targets are finished
                var pendingCount = await db.ExecuteScalarAsync<int>(@"
                    SELECT COUNT(*) FROM task_target_contact 
                    WHERE task_id = @taskId AND status IN ('pending', 'retry_scheduled', 'calling')",
                    new { taskId });

                if (pendingCount == 0)
                {
                    await db.ExecuteAsync("UPDATE task_campaign SET status = 'completed', updated_at = now() WHERE id = @taskId", new { taskId });
                    _logger.LogInformation($"[CallOrchestrator] Task {taskId} completed all targets.");
                }
                continue;
            }

            // Resolve provider and decrypted credentials
            ITelephonyProvider provider;
            JsonElement decryptedConfig;
            try
            {
                var resolved = await factory.ResolveTaskProviderAsync(providerCredId, db);
                provider = resolved.Provider;
                decryptedConfig = resolved.DecryptedConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[CallOrchestrator] Could not resolve provider credentials for task {taskId}");
                continue;
            }

            var apiBaseUrl = config["AppSettings:BaseUrl"] ?? "http://localhost:5063";

            foreach (var target in targets)
            {
                Guid targetId = (Guid)target.id;
                string destNumber = (string)target.phone_number;
                string contactName = (string)(target.contact_name ?? "");
                int currentAttempts = (int)target.attempts_count;

                var callSessionId = Guid.NewGuid();
                var roomName = $"task_call_{taskId:N}_{callSessionId:N}";

                // 1. Insert call_session record
                await db.ExecuteAsync(@"
                    INSERT INTO call_session (
                        id, tenant_id, task_id, telephony_provider_id, provider_type, 
                        caller_number, destination_number, room_name, status, 
                        rate_per_minute, is_outbound_ai, started_at, call_type
                    ) VALUES (
                        @callSessionId, @tenantId, @taskId, @providerCredId, @providerType,
                        @callerNumber, @destNumber, @roomName, 'initiated',
                        0, true, now(), 'ai'
                    )",
                    new { callSessionId, tenantId, taskId, providerCredId, providerType, callerNumber, destNumber, roomName });

                // 2. Mark target as calling
                await db.ExecuteAsync(@"
                    UPDATE task_target_contact SET
                        status = 'calling',
                        attempts_count = attempts_count + 1,
                        last_call_session_id = @callSessionId,
                        last_attempt_at = now()
                    WHERE id = @targetId",
                    new { callSessionId, targetId });

                // 3. Initiate call via provider adapter
                var streamWsUrl = $"wss://{apiBaseUrl.Replace("http://", "").Replace("https://", "")}/api/telephony/media-stream/{callSessionId}";
                var callReq = new CallRequest(
                    TenantId: tenantId,
                    TaskId: taskId,
                    AgentId: agentId,
                    CallerNumber: callerNumber,
                    DestinationNumber: destNumber,
                    ContactName: contactName,
                    WebhookBaseUrl: apiBaseUrl,
                    MediaStreamUrl: streamWsUrl,
                    CallSessionId: callSessionId
                );

                try
                {
                    var result = await provider.MakeCallAsync(callReq, decryptedConfig);

                    if (result.Success)
                    {
                        await db.ExecuteAsync(@"
                            UPDATE call_session SET 
                                provider_call_sid = @sid,
                                status = @status
                            WHERE id = @callSessionId",
                            new { sid = result.ProviderCallSid, status = result.Status, callSessionId });

                        _logger.LogInformation($"[CallOrchestrator] Call initiated for target {destNumber} via {providerType} (SID: {result.ProviderCallSid})");
                    }
                    else
                    {
                        _logger.LogWarning($"[CallOrchestrator] Provider call failed for {destNumber}: {result.ErrorMessage}");
                        await HandleCallFailureAsync(db, targetId, callSessionId, currentAttempts + 1, maxAttempts, retryDelayMin, result.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[CallOrchestrator] Error initiating call for {destNumber}");
                    await HandleCallFailureAsync(db, targetId, callSessionId, currentAttempts + 1, maxAttempts, retryDelayMin, ex.Message);
                }
            }
        }
    }

    private static async Task HandleCallFailureAsync(IDbConnection db, Guid targetId, Guid callSessionId, int newAttempts, int maxAttempts, int retryDelayMin, string? error)
    {
        string newStatus = newAttempts >= maxAttempts ? "failed" : "retry_scheduled";
        DateTimeOffset? nextRetry = newAttempts >= maxAttempts ? null : DateTimeOffset.UtcNow.AddMinutes(retryDelayMin);

        await db.ExecuteAsync(@"
            UPDATE task_target_contact SET
                status = @newStatus,
                next_retry_at = @nextRetry,
                call_summary = COALESCE(@error, call_summary)
            WHERE id = @targetId",
            new { newStatus, nextRetry, error, targetId });

        await db.ExecuteAsync("UPDATE call_session SET status = 'failed', ended_at = now() WHERE id = @callSessionId", new { callSessionId });
    }
}
