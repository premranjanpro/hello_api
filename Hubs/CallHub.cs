using System;
using System.Data;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Dapper;

namespace PruvaVoice.Api.Hubs;

public class CallHub(IServiceScopeFactory scopeFactory) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
                // Automatically mark host as offline in database when connection is closed
                await db.ExecuteAsync("UPDATE host_presence SET status='offline', updated_at=now() WHERE user_id=CAST(@userId AS uuid)", new { userId });
                await Clients.Group($"user:{userId}").SendAsync("presenceChanged", new { userId, status = "offline" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHub] Error in OnDisconnectedAsync: {ex.Message}");
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task HostOnline(string userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
            await db.ExecuteAsync("INSERT INTO host_presence(user_id, status, last_seen_at, updated_at) VALUES(CAST(@userId AS uuid), 'online', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='online', last_seen_at=now(), updated_at=now()", new { userId });
            await Clients.Group($"user:{userId}").SendAsync("presenceChanged", new { userId, status = "online" });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CallHub] Error setting host online: {ex.Message}");
        }
    }

    public async Task AcceptCall(string callId)
    {
        await Clients.Caller.SendAsync("callAcceptedAck", new { callId });
    }

    public async Task RejectCall(string callId)
    {
        await Clients.Caller.SendAsync("callRejectedAck", new { callId });
    }
}
