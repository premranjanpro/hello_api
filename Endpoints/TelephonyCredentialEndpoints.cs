using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Telephony;

namespace PruvaVoice.Api.Endpoints;

public static class TelephonyCredentialEndpoints
{
    private static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void MapTelephonyCredentialEndpoints(this WebApplication app)
    {
        // 1. Supported Providers catalog and their configuration schemas
        app.MapGet("/api/telephony/providers/supported", () =>
        {
            var providers = new[]
            {
                new
                {
                    id = "livekit",
                    name = "LiveKit WebRTC",
                    description = "Browser and mobile app-to-app real-time WebRTC audio calling with ultra-low latency.",
                    icon = "Radio",
                    color = "#3B82F6",
                    fields = new[]
                    {
                        new { id = "url", label = "WebRTC Cluster URL", type = "text", placeholder = "ws://localhost:7880 or wss://livekit.domain.com", required = true },
                        new { id = "apiKey", label = "API Key", type = "text", placeholder = "devkey", required = true },
                        new { id = "apiSecret", label = "API Secret", type = "password", placeholder = "devsecret...", required = true }
                    }
                },
                new
                {
                    id = "twilio",
                    name = "Twilio Programmable Voice",
                    description = "Global carrier-grade PSTN calling, outbound campaigns, phone number provisioning, and bi-directional Media Streams.",
                    icon = "PhoneCall",
                    color = "#EF4444",
                    fields = new[]
                    {
                        new { id = "accountSid", label = "Twilio Account SID", type = "text", placeholder = "ACxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", required = true },
                        new { id = "authToken", label = "Auth Token", type = "password", placeholder = "Your Twilio Auth Token", required = true },
                        new { id = "phoneNumber", label = "Default Caller Phone Number", type = "text", placeholder = "+1234567890 or +91XXXXXXXXXX", required = true }
                    }
                },
                new
                {
                    id = "exotel",
                    name = "Exotel Cloud Telephony",
                    description = "Enterprise Indian PSTN calling, automated lead dialing, virtual numbers, and secure customer connect.",
                    icon = "Zap",
                    color = "#10B981",
                    fields = new[]
                    {
                        new { id = "accountSid", label = "Exotel Account SID", type = "text", placeholder = "Your Exotel Account SID", required = true },
                        new { id = "apiKey", label = "API Key", type = "text", placeholder = "Exotel API Key", required = true },
                        new { id = "apiToken", label = "API Token", type = "password", placeholder = "Exotel API Token", required = true },
                        new { id = "subdomain", label = "API Subdomain", type = "text", placeholder = "api.exotel.com", required = false },
                        new { id = "virtualNumber", label = "Virtual Caller Number", type = "text", placeholder = "080XXXXXXXX / Virtual Number", required = true }
                    }
                },
                new
                {
                    id = "sip_trunk",
                    name = "Generic SIP Trunk / BYOC",
                    description = "Bring Your Own Carrier (BYOC) supporting Asterisk, FreeSWITCH, Kamailio, Telnyx, Plivo, and Cisco CallManager.",
                    icon = "PhoneCall",
                    color = "#8B5CF6",
                    fields = new[]
                    {
                        new { id = "sipServer", label = "SIP Server / Gateway Host", type = "text", placeholder = "sip.carrier.com or 192.168.1.100", required = true },
                        new { id = "sipPort", label = "SIP Port", type = "number", placeholder = "5060 (UDP/TCP) or 5061 (TLS)", required = false },
                        new { id = "authUsername", label = "SIP Auth Username", type = "text", placeholder = "sip_trunk_user", required = false },
                        new { id = "authPassword", label = "SIP Auth Password", type = "password", placeholder = "Secret SIP Digest Password", required = false },
                        new { id = "transport", label = "Transport Protocol", type = "text", placeholder = "UDP / TCP / TLS (default: UDP)", required = false },
                        new { id = "gatewayApiUrl", label = "Gateway REST / Webhook API (Optional)", type = "text", placeholder = "http://pbx.carrier.internal:8088/ari", required = false }
                    }
                }
            };

            return Results.Ok(providers);
        });

        // 2. GET all telephony credentials for current tenant
        app.MapGet("/api/telephony/credentials", async (Guid? tenantId, IDbConnection db) =>
        {
            var targetTenant = tenantId ?? DefaultTenantId;

            var sql = @"
                SELECT c.*, 
                       (SELECT COUNT(*) FROM telephony_caller_number n WHERE n.credential_id = c.id AND n.is_active = true) as caller_numbers_count
                FROM telephony_provider_credential c
                WHERE c.tenant_id = @targetTenant
                ORDER BY c.priority ASC, c.created_at ASC";

            var rows = await db.QueryAsync<dynamic>(sql, new { targetTenant });

            var result = rows.Select(r =>
            {
                object parsedMasked = new { };
                string rawMasked = (string)r.masked_config ?? "{}";
                try { parsedMasked = JsonSerializer.Deserialize<JsonElement>(rawMasked); } catch { }

                object parsedCapabilities = new { };
                string rawCap = (string)r.capabilities ?? "{}";
                try { parsedCapabilities = JsonSerializer.Deserialize<JsonElement>(rawCap); } catch { }

                return new
                {
                    id = (Guid)r.id,
                    tenant_id = (Guid)r.tenant_id,
                    user_id = (Guid?)r.user_id,
                    provider_type = (string)r.provider_type,
                    name = (string)r.name,
                    status = (string)r.status,
                    masked_config = parsedMasked,
                    capabilities = parsedCapabilities,
                    priority = (int)r.priority,
                    is_active = (bool)r.is_active,
                    is_default = (bool)r.is_default,
                    caller_numbers_count = (long)r.caller_numbers_count,
                    last_tested_at = (DateTimeOffset?)r.last_tested_at,
                    last_error_message = (string?)r.last_error_message,
                    created_at = (DateTimeOffset)r.created_at,
                    updated_at = (DateTimeOffset)r.updated_at
                };
            });

            return Results.Ok(result);
        });

        // 3. POST create new telephony provider credentials
        app.MapPost("/api/telephony/credentials", async (TelephonyCredentialCreateDto dto, IDbConnection db, CredentialEncryptionService crypto, TelephonyProviderFactory factory) =>
        {
            if (string.IsNullOrWhiteSpace(dto.ProviderType) || string.IsNullOrWhiteSpace(dto.Name))
            {
                return Results.BadRequest(new { message = "provider_type and name are required." });
            }

            var targetTenant = dto.TenantId ?? DefaultTenantId;
            var plainConfigJson = dto.Config.ValueKind != JsonValueKind.Undefined 
                ? dto.Config.GetRawText() 
                : "{}";

            var encrypted = crypto.Encrypt(plainConfigJson);
            var maskedNode = crypto.MaskConfig(dto.ProviderType, plainConfigJson);
            var maskedJson = maskedNode.ToJsonString();

            ProviderCapabilities capabilities = new();
            if (factory.IsProviderSupported(dto.ProviderType))
            {
                capabilities = factory.GetProvider(dto.ProviderType).GetCapabilities();
            }

            var capJson = JsonSerializer.Serialize(capabilities);
            var newId = Guid.NewGuid();

            await db.ExecuteAsync(@"
                INSERT INTO telephony_provider_credential (
                    id, tenant_id, user_id, provider_type, name, encrypted_credentials, masked_config, status, capabilities, priority, is_active, is_default, created_at, updated_at
                ) VALUES (
                    @newId, @targetTenant, @UserId, @ProviderType, @Name, @encrypted, @maskedJson::jsonb, 'not_connected', @capJson::jsonb, @Priority, @IsActive, @IsDefault, now(), now()
                )",
                new
                {
                    newId,
                    targetTenant,
                    dto.UserId,
                    dto.ProviderType,
                    dto.Name,
                    encrypted,
                    maskedJson,
                    capJson,
                    Priority = dto.Priority ?? 1,
                    IsActive = dto.IsActive ?? true,
                    IsDefault = dto.IsDefault ?? false
                });

            // If caller phone number was provided in config, seed into telephony_caller_number
            string callerNum = "";
            if (dto.Config.TryGetProperty("phoneNumber", out var pn)) callerNum = pn.GetString() ?? "";
            else if (dto.Config.TryGetProperty("virtualNumber", out var vn)) callerNum = vn.GetString() ?? "";

            if (!string.IsNullOrWhiteSpace(callerNum))
            {
                await db.ExecuteAsync(@"
                    INSERT INTO telephony_caller_number (credential_id, tenant_id, phone_number, friendly_name, is_verified, is_active)
                    VALUES (@newId, @targetTenant, @callerNum, @friendly, true, true)
                    ON CONFLICT (credential_id, phone_number) DO NOTHING",
                    new { newId, targetTenant, callerNum, friendly = $"{dto.Name} Default Number" });
            }

            return Results.Ok(new { message = "Telephony credential created successfully", id = newId });
        });

        // 4. PUT update existing credentials
        app.MapPut("/api/telephony/credentials/{id:guid}", async (Guid id, TelephonyCredentialUpdateDto dto, IDbConnection db, CredentialEncryptionService crypto) =>
        {
            var existing = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM telephony_provider_credential WHERE id = @id", new { id });
            if (existing == null) return Results.NotFound(new { message = "Credential not found" });

            string providerType = (string)existing.provider_type;
            string encrypted = (string)existing.encrypted_credentials;
            string maskedJson = (string)existing.masked_config;

            // If user updated config fields
            if (dto.Config.HasValue && dto.Config.Value.ValueKind != JsonValueKind.Undefined)
            {
                var incomingJson = dto.Config.Value.GetRawText();
                var existingPlain = crypto.Decrypt(encrypted);

                // Merge incoming config onto existing config so masked secrets don't overwrite real secrets
                var mergedNode = JsonNode.Parse(existingPlain) as JsonObject ?? new JsonObject();
                var incomingNode = JsonNode.Parse(incomingJson) as JsonObject ?? new JsonObject();

                foreach (var prop in incomingNode)
                {
                    var valStr = prop.Value?.ToString() ?? "";
                    if (!valStr.Contains("••••")) // only update if not masked placeholder
                    {
                        mergedNode[prop.Key] = prop.Value?.DeepClone();
                    }
                }

                var mergedPlain = mergedNode.ToJsonString();
                encrypted = crypto.Encrypt(mergedPlain);
                maskedJson = crypto.MaskConfig(providerType, mergedPlain).ToJsonString();
            }

            await db.ExecuteAsync(@"
                UPDATE telephony_provider_credential SET
                    name = COALESCE(@Name, name),
                    encrypted_credentials = @encrypted,
                    masked_config = @maskedJson::jsonb,
                    priority = COALESCE(@Priority, priority),
                    is_active = COALESCE(@IsActive, is_active),
                    is_default = COALESCE(@IsDefault, is_default),
                    updated_at = now()
                WHERE id = @id",
                new
                {
                    id,
                    dto.Name,
                    encrypted,
                    maskedJson,
                    dto.Priority,
                    dto.IsActive,
                    dto.IsDefault
                });

            return Results.Ok(new { message = "Telephony credential updated successfully" });
        });

        // 5. DELETE credential
        app.MapDelete("/api/telephony/credentials/{id:guid}", async (Guid id, IDbConnection db) =>
        {
            var rows = await db.ExecuteAsync("DELETE FROM telephony_provider_credential WHERE id = @id", new { id });
            if (rows == 0) return Results.NotFound(new { message = "Credential not found" });
            return Results.Ok(new { message = "Telephony credential deleted successfully" });
        });

        // 6. POST test connection
        app.MapPost("/api/telephony/credentials/{id:guid}/test", async (Guid id, IDbConnection db, TelephonyProviderFactory factory, CredentialEncryptionService crypto) =>
        {
            var row = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT * FROM telephony_provider_credential WHERE id = @id", new { id });
            if (row == null) return Results.NotFound(new { message = "Credential not found" });

            string providerType = (string)row.provider_type;
            string encrypted = (string)row.encrypted_credentials;
            string plainConfig = crypto.Decrypt(encrypted);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plainConfig) ? "{}" : plainConfig);
            var config = doc.RootElement.Clone();

            var provider = factory.GetProvider(providerType);
            var result = await provider.ValidateCredentialsAsync(config);

            string newStatus = result.Success ? "connected" : "error";
            string? errorMsg = result.Success ? null : result.Message;

            await db.ExecuteAsync(@"
                UPDATE telephony_provider_credential SET
                    status = @newStatus,
                    last_tested_at = now(),
                    last_error_message = @errorMsg,
                    updated_at = now()
                WHERE id = @id",
                new { id, newStatus, errorMsg });

            return Results.Ok(new
            {
                success = result.Success,
                status = newStatus,
                message = result.Message,
                capabilities = result.Capabilities,
                details = result.Details,
                last_tested_at = DateTimeOffset.UtcNow
            });
        });

        // 6b. POST test configuration before saving
        app.MapPost("/api/telephony/credentials/validate", async (ValidateCredentialConfigDto dto, TelephonyProviderFactory factory) =>
        {
            try
            {
                var provider = factory.GetProvider(dto.ProviderType);
                using var doc = JsonDocument.Parse(dto.Config.GetRawText());
                var result = await provider.ValidateCredentialsAsync(doc.RootElement);

                return Results.Ok(new
                {
                    success = result.Success,
                    message = result.Message,
                    capabilities = result.Capabilities,
                    details = result.Details
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new
                {
                    success = false,
                    message = ex.Message
                });
            }
        });

        // 7. GET caller numbers for a provider credential
        app.MapGet("/api/telephony/credentials/{id:guid}/numbers", async (Guid id, IDbConnection db) =>
        {
            var numbers = await db.QueryAsync("SELECT * FROM telephony_caller_number WHERE credential_id = @id ORDER BY created_at ASC", new { id });
            return Results.Ok(numbers);
        });

        // 8. POST add caller number
        app.MapPost("/api/telephony/credentials/{id:guid}/numbers", async (Guid id, AddCallerNumberDto dto, IDbConnection db) =>
        {
            if (string.IsNullOrWhiteSpace(dto.PhoneNumber))
            {
                return Results.BadRequest(new { message = "PhoneNumber is required" });
            }

            var cred = await db.QueryFirstOrDefaultAsync<dynamic>("SELECT tenant_id FROM telephony_provider_credential WHERE id = @id", new { id });
            if (cred == null) return Results.NotFound(new { message = "Credential not found" });

            var numberId = Guid.NewGuid();
            await db.ExecuteAsync(@"
                INSERT INTO telephony_caller_number (id, credential_id, tenant_id, phone_number, friendly_name, is_verified, is_active, created_at)
                VALUES (@numberId, @id, @tenantId, @PhoneNumber, @FriendlyName, true, true, now())
                ON CONFLICT (credential_id, phone_number) DO UPDATE SET friendly_name = @FriendlyName, is_active = true",
                new
                {
                    numberId,
                    id,
                    tenantId = (Guid)cred.tenant_id,
                    dto.PhoneNumber,
                    FriendlyName = string.IsNullOrWhiteSpace(dto.FriendlyName) ? dto.PhoneNumber : dto.FriendlyName
                });

            return Results.Ok(new { message = "Caller number saved successfully", id = numberId });
        });

        // 9. DELETE caller number
        app.MapDelete("/api/telephony/credentials/{id:guid}/numbers/{numberId:guid}", async (Guid id, Guid numberId, IDbConnection db) =>
        {
            var rows = await db.ExecuteAsync("DELETE FROM telephony_caller_number WHERE id = @numberId AND credential_id = @id", new { id, numberId });
            if (rows == 0) return Results.NotFound(new { message = "Caller number not found" });
            return Results.Ok(new { message = "Caller number removed successfully" });
        });
    }
}

public record TelephonyCredentialCreateDto(
    Guid? TenantId = null,
    Guid? UserId = null,
    string ProviderType = "",
    string Name = "",
    JsonElement Config = default,
    int? Priority = null,
    bool? IsActive = null,
    bool? IsDefault = null
);

public record TelephonyCredentialUpdateDto(
    string? Name = null,
    JsonElement? Config = null,
    int? Priority = null,
    bool? IsActive = null,
    bool? IsDefault = null
);

public record AddCallerNumberDto(string PhoneNumber = "", string? FriendlyName = null);
public record ValidateCredentialConfigDto(string ProviderType, JsonElement Config);

