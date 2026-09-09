using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;

namespace PruvaVoice.Api.Services;

public record WebhookTask(
    Guid TenantId,
    string EventType,
    object Payload,
    int Attempt = 1
);

public interface IWebhookDispatcherService
{
    ValueTask EnqueueEventAsync(Guid tenantId, string eventType, object payload);
    string ComputeSignature(string secret, long timestamp, string payloadJson);
}

public class WebhookDispatcherService : BackgroundService, IWebhookDispatcherService
{
    private readonly Channel<WebhookTask> _queue = Channel.CreateUnbounded<WebhookTask>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcherService> _logger;

    public WebhookDispatcherService(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatcherService> logger
    )
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public ValueTask EnqueueEventAsync(Guid tenantId, string eventType, object payload)
    {
        return _queue.Writer.WriteAsync(new WebhookTask(tenantId, eventType, payload));
    }

    public string ComputeSignature(string secret, long timestamp, string payloadJson)
    {
        var stringToSign = $"{timestamp}.{payloadJson}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"t={timestamp},v1={hex}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 Webhook Dispatcher Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var task = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessWebhookTaskAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing webhook queue item.");
            }
        }
    }

    private async Task ProcessWebhookTaskAsync(WebhookTask task, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();

        // Query active subscriptions for tenant
        var subscriptions = (await db.QueryAsync<dynamic>(
            @"SELECT id, target_url, secret_hash, subscribed_events 
              FROM webhook_subscription 
              WHERE tenant_id = @TenantId AND is_active = TRUE",
            new { task.TenantId }
        )).ToList();

        if (!subscriptions.Any()) return;

        var payloadJson = JsonSerializer.Serialize(task.Payload, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        foreach (var sub in subscriptions)
        {
            try
            {
                // Check if subscribed to this event
                string eventsRaw = sub.subscribed_events?.ToString() ?? "[]";
                var subscribedList = JsonSerializer.Deserialize<List<string>>(eventsRaw) ?? new List<string>();

                if (!subscribedList.Contains(task.EventType) && !subscribedList.Contains("*"))
                {
                    continue;
                }

                string targetUrl = sub.target_url;
                string secret = sub.secret_hash ?? "default_secret";
                Guid subId = Guid.Parse(sub.id.ToString());

                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string signature = ComputeSignature(secret, timestamp, payloadJson);
                var deliveryId = Guid.NewGuid();

                using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl)
                {
                    Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                };

                request.Headers.Add("User-Agent", "PruvaVoice-Webhook/2.0");
                request.Headers.Add("X-Pruva-Event", task.EventType);
                request.Headers.Add("X-Pruva-Delivery-Id", deliveryId.ToString("N"));
                request.Headers.Add("X-Pruva-Signature", signature);

                var sw = Stopwatch.StartNew();
                HttpResponseMessage? response = null;
                int statusCode = 0;
                string responseBody = "";

                try
                {
                    response = await client.SendAsync(request, stoppingToken);
                    sw.Stop();
                    statusCode = (int)response.StatusCode;
                    responseBody = await response.Content.ReadAsStringAsync(stoppingToken);
                    if (responseBody.Length > 1000) responseBody = responseBody.Substring(0, 1000) + "...";
                }
                catch (Exception reqEx)
                {
                    sw.Stop();
                    statusCode = 599; // Network error
                    responseBody = reqEx.Message;
                }

                // Log delivery attempt
                await db.ExecuteAsync(
                    @"INSERT INTO webhook_delivery_log (
                        id, subscription_id, tenant_id, event_type, payload, target_url, 
                        response_status, response_body, attempt_count, duration_ms, delivered_at
                    ) VALUES (
                        @deliveryId, @subId, @TenantId, @EventType, @payloadJson::jsonb, @targetUrl,
                        @statusCode, @responseBody, @Attempt, @durationMs, NOW()
                    )",
                    new
                    {
                        deliveryId,
                        subId,
                        task.TenantId,
                        task.EventType,
                        payloadJson,
                        targetUrl,
                        statusCode,
                        responseBody,
                        task.Attempt,
                        durationMs = (int)sw.ElapsedMilliseconds
                    }
                );

                // Retry logic on server 5xx error or connection error if under max attempts
                if ((statusCode >= 500 || statusCode == 599) && task.Attempt < 3)
                {
                    _logger.LogWarning("Webhook delivery failed ({StatusCode}) to {TargetUrl}. Retrying attempt {NextAttempt}...",
                        statusCode, targetUrl, task.Attempt + 1);

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, task.Attempt) * 2), stoppingToken);
                        await _queue.Writer.WriteAsync(task with { Attempt = task.Attempt + 1 });
                    }, stoppingToken);
                }
            }
            catch (Exception subEx)
            {
                _logger.LogError(subEx, "Error dispatching webhook to subscription {SubId}", (object)sub.id);
            }
        }
    }
}
