using System.Data;
using Dapper;
using PruvaVoice.Api.Models;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/users/me", async (ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            var user = await db.QueryFirstOrDefaultAsync("SELECT * FROM app_user WHERE id=@uid", new { uid });
            return Results.Ok(user);
        }).RequireAuthorization();

        app.MapGet("/api/users/check-username/{username}", async (string username, IDbConnection db) =>
        {
            if (!IsValidUsername(username, out var error))
            {
                return Results.BadRequest(new { message = error, available = false });
            }
            var clean = username.Trim().ToLowerInvariant();
            var exists = await db.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM app_user WHERE lower(username)=@clean)", new { clean });
            return Results.Ok(new { username = clean, available = !exists });
        });

        app.MapPost("/api/users/profile", async (UpdateProfileDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            if (!IsValidUsername(dto.Username, out var error))
            {
                return Results.BadRequest(new { message = error });
            }
            var uid = CurrentUser.Id(cp);
            var old = await db.ExecuteScalarAsync<string?>("SELECT username FROM app_user WHERE id=@uid", new { uid });
            if (!string.Equals(old, dto.Username, StringComparison.OrdinalIgnoreCase))
                await db.ExecuteAsync("INSERT INTO username_history(user_id,old_username,new_username) VALUES(@uid,@old,@New)", new { uid, old, New = dto.Username });

            await db.ExecuteAsync("UPDATE app_user SET username=@Username, display_name=@DisplayName, display_gender=@DisplayGender, profile_icon=@ProfileIcon, updated_at=now() WHERE id=@uid", new { dto.Username, dto.DisplayName, dto.DisplayGender, dto.ProfileIcon, uid });
            return Results.Ok(new { message = "Profile updated" });
        }).RequireAuthorization();

        app.MapPost("/api/users/device", async (RegisterDeviceDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO user_device(user_id,device_id,fcm_token,platform,app_version) VALUES(@uid,@DeviceId,@FcmToken,@Platform,@AppVersion)", new { uid, dto.DeviceId, dto.FcmToken, dto.Platform, dto.AppVersion });
            return Results.Ok(new { message = "Device registered" });
        }).RequireAuthorization();

        app.MapPost("/api/terms/accept", async (AcceptTermsDto dto, ClaimsPrincipal cp, IDbConnection db, HttpContext http) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO user_terms_acceptance(user_id, terms_version_id, ip_address, user_agent) VALUES(@uid,@TermsVersionId,@Ip,@Agent) ON CONFLICT DO NOTHING", new { uid, dto.TermsVersionId, Ip = http.Connection.RemoteIpAddress?.ToString(), Agent = http.Request.Headers.UserAgent.ToString() });
            return Results.Ok(new { message = "Terms accepted" });
        }).RequireAuthorization();

        
        app.MapPost("/api/users/onboarding", async (OnboardingDto dto, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            if (!dto.Is18PlusConfirmed) return Results.BadRequest("18+ confirmation required");
            await db.ExecuteAsync("UPDATE app_user SET display_gender=@DisplayGender,dob=@Dob,is_18_plus_confirmed=@Is18PlusConfirmed,preferred_language=@PreferredLanguage,updated_at=now() WHERE id=@uid", new { uid, dto.DisplayGender, dto.Dob, dto.Is18PlusConfirmed, dto.PreferredLanguage });
            return Results.Ok(new { message = "Onboarding saved" });
        }).RequireAuthorization();

        app.MapPost("/api/users/block/{blockedUserId:guid}", async (Guid blockedUserId, ClaimsPrincipal cp, IDbConnection db) =>
        {
            var uid = CurrentUser.Id(cp);
            await db.ExecuteAsync("INSERT INTO block_list(blocker_user_id,blocked_user_id) VALUES(@uid,@blockedUserId) ON CONFLICT DO NOTHING", new { uid, blockedUserId });
            return Results.Ok(new { message = "Blocked" });
        }).RequireAuthorization();
    }

    private static bool IsValidUsername(string username, out string errorMessage)
    {
        errorMessage = "";
        if (string.IsNullOrWhiteSpace(username))
        {
            errorMessage = "Username cannot be empty";
            return false;
        }

        var clean = username.Trim().ToLowerInvariant();
        if (clean.Length < 4)
        {
            errorMessage = "Username must be at least 4 characters";
            return false;
        }

        // 1. Check for 10 consecutive digits (phone numbers)
        if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"\d{10}"))
        {
            errorMessage = "Username cannot contain phone numbers";
            return false;
        }

        // 2. Check for email patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(clean, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}"))
        {
            errorMessage = "Username cannot contain email addresses";
            return false;
        }

        // 3. Check for general email signatures like .com, .in, etc.
        if (clean.Contains(".com") || clean.Contains(".net") || clean.Contains(".org") || clean.Contains(".in") || clean.Contains(".co"))
        {
            errorMessage = "Username cannot contain domain names or emails";
            return false;
        }

        return true;
    }
}
