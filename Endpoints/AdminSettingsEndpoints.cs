using System.Data;
using Dapper;

namespace PruvaVoice.Api.Endpoints;

public static class AdminSettingsEndpoints
{
    public static void MapAdminSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/settings", async (IDbConnection db) =>
        {
            var settings = await db.QueryAsync("SELECT * FROM app_setting ORDER BY key");
            return Results.Ok(settings);
        });

        app.MapPost("/api/admin/settings/{key}", async (string key, SettingUpdateDto dto, IDbConnection db) =>
        {
            await db.ExecuteAsync("INSERT INTO app_setting(key,value,updated_at) VALUES(@key,@Value,now()) ON CONFLICT(key) DO UPDATE SET value=@Value, updated_at=now()", new { key, dto.Value });
            return Results.Ok(new { message = "Setting saved" });
        });

        app.MapGet("/api/admin/integrations", async (IDbConnection db) =>
        {
            var rows = await db.QueryAsync("SELECT id,provider_type,provider_name,display_name,config_json,is_active,created_at,updated_at FROM integration_credential ORDER BY provider_type,provider_name");
            return Results.Ok(rows);
        });

        app.MapPost("/api/admin/integrations/{id:guid}", async (Guid id, IntegrationUpdateDto dto, IDbConnection db) =>
        {
            if (dto.IsActive)
            {
                var current = await db.QueryFirstAsync<dynamic>("SELECT provider_type FROM integration_credential WHERE id=@id", new { id });
                await db.ExecuteAsync("UPDATE integration_credential SET is_active=false WHERE provider_type=@providerType", new { providerType = (string)current.provider_type });
            }
            await db.ExecuteAsync("UPDATE integration_credential SET config_json=@ConfigJson::jsonb, is_active=@IsActive, updated_at=now() WHERE id=@id", new { id, dto.ConfigJson, dto.IsActive });
            return Results.Ok(new { message = "Integration saved" });
        });
    }
}

public record SettingUpdateDto(string Value);
public record IntegrationUpdateDto(string ConfigJson, bool IsActive);
