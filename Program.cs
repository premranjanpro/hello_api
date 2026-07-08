using System.Data;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Endpoints;
using PruvaVoice.Api.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR().AddStackExchangeRedis(builder.Configuration["Redis:Configuration"] ?? "localhost:6379");
builder.Services.AddScoped<CallNotifier>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddSingleton<LiveKitTokenService>();
builder.Services.AddSingleton<FcmService>();
builder.Services.AddSingleton<PaymentService>();
builder.Services.AddHostedService<PresenceSchedulerWorker>();

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
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { app = "Hello24", status = "running" }));
app.MapHub<CallHub>("/hubs/calls");

app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapHostEndpoints();
app.MapCallEndpoints();
app.MapWalletEndpoints();
app.MapAdminEndpoints();
app.MapAdminSettingsEndpoints();
app.MapPaymentEndpoints();

app.Run();
