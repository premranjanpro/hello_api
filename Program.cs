using System.Data;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Endpoints;
using PruvaVoice.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5063");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CallNotifier>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddSingleton<LiveKitTokenService>();
builder.Services.AddSingleton<FcmService>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddHostedService<PresenceSchedulerWorker>();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<CredentialEncryptionService>();
builder.Services.AddSingleton<PruvaVoice.Api.Telephony.MediaStream.TelephonyMediaStreamHandler>();
builder.Services.AddScoped<PruvaVoice.Api.Telephony.Adapters.LiveKitTelephonyAdapter>();
builder.Services.AddScoped<PruvaVoice.Api.Telephony.Adapters.TwilioTelephonyAdapter>();
builder.Services.AddScoped<PruvaVoice.Api.Telephony.Adapters.ExotelTelephonyAdapter>();
builder.Services.AddScoped<PruvaVoice.Api.Telephony.Adapters.SipTrunkTelephonyAdapter>();
builder.Services.AddSingleton<PruvaVoice.Api.Telephony.TelephonyProviderFactory>();
builder.Services.AddSingleton<IAudioStorageService, LocalStorageAudioService>();
builder.Services.AddScoped<ICallAnalyticsService, CallAnalyticsService>();
builder.Services.AddSingleton<WebhookDispatcherService>();
builder.Services.AddSingleton<IWebhookDispatcherService>(sp => sp.GetRequiredService<WebhookDispatcherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcherService>());
builder.Services.AddHostedService<CallOrchestratorService>();

var secret = builder.Configuration["Jwt:Secret"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
        ValidateLifetime = true
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseCors();
app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbConnection>();
    db.Execute(@"
        ALTER TABLE app_user ADD COLUMN IF NOT EXISTS age INT;
        ALTER TABLE app_user ADD COLUMN IF NOT EXISTS city VARCHAR(100);
        ALTER TABLE app_user ADD COLUMN IF NOT EXISTS languages VARCHAR(255);
        UPDATE user_presence SET status = 'offline' WHERE status = 'online';
        UPDATE call_session SET call_type = 'ai' WHERE COALESCE(call_type, '') != 'ai' AND (caller_user_id = host_user_id OR room_name LIKE 'ai_%' OR room_name LIKE 'task_%' OR is_outbound_ai = TRUE);
    ");

    // Ensure default LiveKit telephony provider credential is seeded if none exists
    var hasCred = db.ExecuteScalar<int>("SELECT COUNT(*) FROM telephony_provider_credential WHERE provider_type='livekit'");
    if (hasCred == 0)
    {
        var crypto = scope.ServiceProvider.GetRequiredService<CredentialEncryptionService>();
        var lkPlain = "{\"url\":\"ws://localhost:7880\",\"apiKey\":\"devkey\",\"apiSecret\":\"devsecretkeyshouldbe48characterslongforsecurity!\"}";
        var enc = crypto.Encrypt(lkPlain);
        var masked = crypto.MaskConfig("livekit", lkPlain).ToJsonString();

        db.Execute(@"
            INSERT INTO telephony_provider_credential (
                tenant_id, provider_type, name, encrypted_credentials, masked_config, status, priority, is_active, is_default, created_at, updated_at
            ) VALUES (
                '00000000-0000-0000-0000-000000000001', 'livekit', 'Primary LiveKit WebRTC', @enc, @masked::jsonb, 'connected', 1, true, true, now(), now()
            )", new { enc, masked });
    }
}

app.UseWebSockets();
app.MapGet("/", () => Results.Ok(new { app = "Hello24", status = "running" }));
app.MapHub<CallHub>("/hubs/calls");

// Telephony bidirectional media stream WebSocket endpoint (Twilio Media Streams / Exotel Audio Streams)
app.Map("/api/telephony/media-stream/{callSessionId}", async (HttpContext context, string callSessionId, PruvaVoice.Api.Telephony.MediaStream.TelephonyMediaStreamHandler handler) =>
{
    await handler.HandleWebSocketAsync(context, callSessionId);
});

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapHostEndpoints();
app.MapCallEndpoints();
app.MapAiCallEndpoints();
app.MapWalletEndpoints();
app.MapAdminEndpoints();
app.MapAdminSettingsEndpoints();
app.MapDashboardCampaignEndpoints();
app.MapAiPersonaTaskEndpoints();
app.MapPaymentEndpoints();
app.MapUpiRechargeEndpoints();
app.MapTelephonyCredentialEndpoints();
app.MapTelephonyWebhookEndpoints();
app.MapTaskCampaignEndpoints();
app.MapWebhookManagementEndpoints();
app.MapCallAnalyticsEndpoints();

app.Run();
