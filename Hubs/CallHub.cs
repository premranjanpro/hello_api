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
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
                await db.ExecuteAsync("INSERT INTO user_presence(user_id, status, last_seen_at, updated_at) VALUES(CAST(@userId AS uuid), 'online', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='online', last_seen_at=now(), updated_at=now()", new { userId });
                await Clients.All.SendAsync("presenceChanged", new { userId, status = "online" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CallHub] Error in OnConnectedAsync: {ex.Message}");
            }
        }
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
                await db.ExecuteAsync("UPDATE user_presence SET status='offline', updated_at=now() WHERE user_id=CAST(@userId AS uuid)", new { userId });
                await Clients.All.SendAsync("presenceChanged", new { userId, status = "offline" });
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
            await db.ExecuteAsync("INSERT INTO user_presence(user_id, status, last_seen_at, updated_at) VALUES(CAST(@userId AS uuid), 'online', now(), now()) ON CONFLICT(user_id) DO UPDATE SET status='online', last_seen_at=now(), updated_at=now()", new { userId });
            await Clients.All.SendAsync("presenceChanged", new { userId, status = "online" });
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

    public async Task SendDirectMessage(string recipientUserId, string text, string? messageType = "text")
    {
        var senderId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(senderId)) return;

        var payload = new
        {
            senderId,
            recipientUserId,
            text,
            messageType = messageType ?? "text",
            timestamp = DateTime.UtcNow
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
            await db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS user_chat_message (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    sender_id UUID NOT NULL,
                    recipient_id UUID NOT NULL,
                    text TEXT NOT NULL,
                    message_type VARCHAR(50) DEFAULT 'text',
                    is_read BOOLEAN DEFAULT false,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );
                INSERT INTO user_chat_message(sender_id, recipient_id, text, message_type)
                VALUES(CAST(@senderId AS uuid), CAST(@recipientUserId AS uuid), @text, @messageType);
            ", new { senderId, recipientUserId, text, messageType });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CallHub] Error saving direct chat message: {ex.Message}");
        }

        await Clients.Group($"user:{recipientUserId}").SendAsync("receiveDirectMessage", payload);
        await Clients.Caller.SendAsync("directMessageSent", payload);
    }

    public async Task SendTypingIndicator(string recipientUserId, bool isTyping)
    {
        var senderId = Context.UserIdentifier ?? Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(senderId)) return;
        await Clients.Group($"user:{recipientUserId}").SendAsync("userTyping", new { senderId, isTyping });
    }
}

