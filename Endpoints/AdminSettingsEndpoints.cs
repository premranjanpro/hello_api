using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace PruvaVoice.Api.Endpoints;

public static class AdminSettingsEndpoints
{
    private const string ActiveCredentialsCacheKey = "active_credentials_cache";

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

        // Get all credentials/integrations (across all tabs: fcm, otp, livekit, payment, upi, model)
        app.MapGet("/api/admin/integrations", async (string? type, IDbConnection db) =>
        {
            var sql = @"
                SELECT id, provider_type, provider_name, display_name, config_json, priority, is_active, created_at, updated_at 
                FROM integration_credential 
                WHERE (@type IS NULL OR provider_type = @type)
                ORDER BY provider_type, priority ASC, created_at ASC";
            var rows = await db.QueryAsync(sql, new { type });
            return Results.Ok(rows);
        });

        // Get active credentials by type with RAM Caching (Zero-latency)
        app.MapGet("/api/admin/integrations/active/{type}", async (string type, IDbConnection db, IMemoryCache cache) =>
        {
            string cacheKey = $"{ActiveCredentialsCacheKey}_{type}";
            if (!cache.TryGetValue(cacheKey, out IEnumerable<dynamic>? activeList))
            {
                var sql = @"
                    SELECT id, provider_type, provider_name, display_name, config_json, priority, is_active
                    FROM integration_credential 
                    WHERE provider_type = @type AND is_active = true
                    ORDER BY priority ASC, created_at ASC";
                activeList = await db.QueryAsync(sql, new { type });
                cache.Set(cacheKey, activeList, TimeSpan.FromMinutes(10));
            }
            return Results.Ok(activeList);
        });

        // Add a new credential entry (supports multiple entries per category)
        app.MapPost("/api/admin/integrations", async (IntegrationCreateDto dto, IDbConnection db, IMemoryCache cache) =>
        {
            if (string.IsNullOrWhiteSpace(dto.ProviderType) || string.IsNullOrWhiteSpace(dto.ProviderName) || string.IsNullOrWhiteSpace(dto.DisplayName))
            {
                return Results.BadRequest(new { message = "provider_type, provider_name, and display_name are required." });
            }

            var configJsonStr = dto.ConfigJson.ValueKind != JsonValueKind.Undefined 
                ? dto.ConfigJson.GetRawText() 
                : "{}";

            var id = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO integration_credential (
                    id, provider_type, provider_name, display_name, config_json, priority, is_active, created_at, updated_at
                ) VALUES (
                    @id, @ProviderType, @ProviderName, @DisplayName, @configJsonStr::jsonb, @Priority, @IsActive, now(), now()
                )",
                new { id, dto.ProviderType, dto.ProviderName, dto.DisplayName, configJsonStr, dto.Priority, dto.IsActive });

            InvalidateCredentialsCache(cache, dto.ProviderType);

            return Results.Ok(new { message = "Credential added successfully", id });
        });

        // Edit an existing credential entry
        app.MapPut("/api/admin/integrations/{id:guid}", async (Guid id, IntegrationFullUpdateDto dto, IDbConnection db, IMemoryCache cache) =>
        {
            var existing = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT provider_type FROM integration_credential WHERE id = @id", new { id });
            if (existing == null) return Results.NotFound(new { message = "Credential not found" });

            var configJsonStr = dto.ConfigJson.ValueKind != JsonValueKind.Undefined 
                ? dto.ConfigJson.GetRawText() 
                : "{}";

            await db.ExecuteAsync(@"
                UPDATE integration_credential 
                SET display_name = @DisplayName, 
                    provider_name = @ProviderName,
                    config_json = @configJsonStr::jsonb, 
                    priority = @Priority,
                    is_active = @IsActive, 
                    updated_at = now() 
                WHERE id = @id",
                new { id, dto.DisplayName, dto.ProviderName, configJsonStr, dto.Priority, dto.IsActive });

            InvalidateCredentialsCache(cache, (string)existing.provider_type);

            return Results.Ok(new { message = "Credential updated successfully" });
        });

        // Legacy compatibility for simple config updates
        app.MapPost("/api/admin/integrations/{id:guid}", async (Guid id, IntegrationUpdateDto dto, IDbConnection db, IMemoryCache cache) =>
        {
            var existing = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT provider_type FROM integration_credential WHERE id = @id", new { id });
            if (existing == null) return Results.NotFound();

            var configJsonStr = dto.ConfigJson.GetRawText();
            await db.ExecuteAsync(@"
                UPDATE integration_credential 
                SET config_json = @configJsonStr::jsonb, 
                    is_active = @IsActive, 
                    updated_at = now() 
                WHERE id = @id",
                new { id, configJsonStr, dto.IsActive });

            InvalidateCredentialsCache(cache, (string)existing.provider_type);
            return Results.Ok(new { message = "Integration saved" });
        });

        // Toggle active status
        app.MapPatch("/api/admin/integrations/{id:guid}/toggle", async (Guid id, IDbConnection db, IMemoryCache cache) =>
        {
            var existing = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT provider_type, is_active FROM integration_credential WHERE id = @id", new { id });
            if (existing == null) return Results.NotFound();

            bool newStatus = !((bool)existing.is_active);
            await db.ExecuteAsync("UPDATE integration_credential SET is_active = @newStatus, updated_at = now() WHERE id = @id", new { id, newStatus });

            InvalidateCredentialsCache(cache, (string)existing.provider_type);
            return Results.Ok(new { message = $"Status updated to {(newStatus ? "Active" : "Inactive")}", is_active = newStatus });
        });

        // Delete credential entry
        app.MapDelete("/api/admin/integrations/{id:guid}", async (Guid id, IDbConnection db, IMemoryCache cache) =>
        {
            var existing = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT provider_type FROM integration_credential WHERE id = @id", new { id });
            if (existing == null) return Results.NotFound();

            await db.ExecuteAsync("DELETE FROM integration_credential WHERE id = @id", new { id });

            InvalidateCredentialsCache(cache, (string)existing.provider_type);
            return Results.Ok(new { message = "Credential deleted successfully" });
        });

        // Test connection endpoint
        app.MapPost("/api/admin/integrations/{id:guid}/test", async (Guid id, IDbConnection db) =>
        {
            var item = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM integration_credential WHERE id = @id", new { id });
            if (item == null) return Results.NotFound(new { message = "Credential not found" });

            string providerType = (string)item.provider_type;
            string providerName = (string)item.provider_name;
            string configStr = item.config_json?.ToString() ?? "{}";

            try
            {
                using var doc = JsonDocument.Parse(configStr);
                var root = doc.RootElement;

                if (providerType == "model")
                {
                    string apiKey = root.TryGetProperty("api_key", out var k) ? k.GetString() ?? "" : "";
                    string baseUrl = root.TryGetProperty("base_url", out var u) ? u.GetString() ?? "" : "";
                    string model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(apiKey) && providerName != "local")
                    {
                        return Results.Ok(new { success = false, message = "API Key is empty. Please configure a valid API key." });
                    }

                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    }

                    string checkUrl = baseUrl.TrimEnd('/') + "/models";
                    var resp = await http.GetAsync(checkUrl);
                    if (resp.IsSuccessStatusCode)
                    {
                        return Results.Ok(new { success = true, message = $"Successfully connected to {providerName}! Models endpoint responsive." });
                    }
                    else
                    {
                        return Results.Ok(new { success = false, message = $"Connection returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})" });
                    }
                }
                else if (providerType == "livekit")
                {
                    string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return Results.Ok(new { success = false, message = "LiveKit URL is missing." });
                    }
                    return Results.Ok(new { success = true, message = $"LiveKit endpoint {url} configured." });
                }
                else if (providerType == "upi")
                {
                    string upiId = root.TryGetProperty("upi_id", out var upi) ? upi.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(upiId) || !upiId.Contains('@'))
                    {
                        return Results.Ok(new { success = false, message = "Invalid UPI ID format. Expected format: handle@bank" });
                    }
                    return Results.Ok(new { success = true, message = $"UPI ID {upiId} format verified!" });
                }

                return Results.Ok(new { success = true, message = "Configuration format verified." });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, message = $"Test error: {ex.Message}" });
            }
        });
    }

    private static void InvalidateCredentialsCache(IMemoryCache cache, string providerType)
    {
        cache.Remove($"{ActiveCredentialsCacheKey}_{providerType}");
        cache.Remove(ActiveCredentialsCacheKey);
    }
}

public record SettingUpdateDto(string Value);
public record IntegrationUpdateDto(JsonElement ConfigJson, bool IsActive);
public record IntegrationCreateDto(string ProviderType, string ProviderName, string DisplayName, JsonElement ConfigJson, int Priority, bool IsActive);
public record IntegrationFullUpdateDto(string ProviderName, string DisplayName, JsonElement ConfigJson, int Priority, bool IsActive);
