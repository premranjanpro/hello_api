using System.Data;
using Dapper;

namespace PruvaVoice.Api.Services;

public class PresenceSchedulerWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PresenceSchedulerWorker> _logger;

    public PresenceSchedulerWorker(IServiceProvider serviceProvider, ILogger<PresenceSchedulerWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Presence Scheduler Worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();

                    // 1. Process scheduled online changes
                    var onlineRows = await db.ExecuteAsync(
                        "UPDATE host_presence SET status = 'online', schedule_online_at = NULL, updated_at = now() WHERE schedule_online_at IS NOT NULL AND schedule_online_at <= now()");
                    if (onlineRows > 0)
                    {
                        _logger.LogInformation("Presence Scheduler: Toggled {Count} hosts to ONLINE.", onlineRows);
                    }

                    // 2. Process scheduled offline changes
                    var offlineRows = await db.ExecuteAsync(
                        "UPDATE host_presence SET status = 'offline', schedule_offline_at = NULL, updated_at = now() WHERE schedule_offline_at IS NOT NULL AND schedule_offline_at <= now()");
                    if (offlineRows > 0)
                    {
                        _logger.LogInformation("Presence Scheduler: Toggled {Count} hosts to OFFLINE.", offlineRows);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scheduled presence changes.");
            }

            // Check every 10 seconds
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
