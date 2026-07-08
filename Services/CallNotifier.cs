using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using PruvaVoice.Api.Hubs;

namespace PruvaVoice.Api.Services;

public class CallNotifier(IHubContext<CallHub> hub, FcmService fcm)
{
    public async Task NotifyIncomingCallAsync(IDbConnection db, Guid hostUserId, Guid callId, string callerUsername, string? fcmToken)
    {
        // Foreground path: SignalR. If host app is connected, it receives this instantly.
        await hub.Clients.Group($"user:{hostUserId}").SendAsync("incomingCall", new {
            callId,
            callerUsername,
            type = "voice_call"
        });

        // Background/killed-app path: FCM. Run in non-blocking background task to prevent call start delay.
        if (!string.IsNullOrWhiteSpace(fcmToken))
        {
            _ = Task.Run(async () =>
            {
                await fcm.SendIncomingCallAsync(db, fcmToken, callerUsername, callId);
            });
        }
    }
}
