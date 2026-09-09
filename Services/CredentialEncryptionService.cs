using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PruvaVoice.Api.Services;

public class CredentialEncryptionService
{
    private readonly byte[] _key;

    public CredentialEncryptionService(IConfiguration configuration)
    {
        var rawKey = configuration["Security:EncryptionKey"] ?? configuration["Jwt:Secret"] ?? "pruva_telephony_vault_master_key_2026";
        using var sha = SHA256.Create();
        _key = sha.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
    }

    /// <summary>
    /// Encrypts plaintext JSON string using AES-256-GCM.
    /// Format: [12-byte Nonce][16-byte Tag][Ciphertext] encoded as Base64.
    /// </summary>
    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(_key, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(nonce.Length);
            writer.Write(nonce);
            writer.Write(tag.Length);
            writer.Write(tag);
            writer.Write(cipherBytes.Length);
            writer.Write(cipherBytes);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// Decrypts Base64 payload using AES-256-GCM into plaintext JSON string.
    /// </summary>
    public string Decrypt(string cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64)) return "{}";

        try
        {
            var rawBytes = Convert.FromBase64String(cipherBase64);
            using var ms = new MemoryStream(rawBytes);
            using var reader = new BinaryReader(ms);

            var nonceLen = reader.ReadInt32();
            var nonce = reader.ReadBytes(nonceLen);

            var tagLen = reader.ReadInt32();
            var tag = reader.ReadBytes(tagLen);

            var cipherLen = reader.ReadInt32();
            var cipherBytes = reader.ReadBytes(cipherLen);

            var plainBytes = new byte[cipherBytes.Length];
            using var aesGcm = new AesGcm(_key, 16);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex)
        {
            // If it was stored as plaintext JSON during manual migration, safely fallback
            if (cipherBase64.TrimStart().StartsWith("{"))
            {
                return cipherBase64;
            }
            throw new CryptographicException("Failed to decrypt provider credentials payload", ex);
        }
    }

    /// <summary>
    /// Produces a sanitized, redacted configuration JSON representation safe to return to the frontend.
    /// Never returns full API secrets or auth tokens.
    /// </summary>
    public JsonNode MaskConfig(string providerType, string plainConfigJson)
    {
        JsonNode? rootNode = null;
        try
        {
            rootNode = JsonNode.Parse(plainConfigJson);
        }
        catch
        {
            rootNode = new JsonObject();
        }

        if (rootNode is not JsonObject obj)
        {
            return new JsonObject();
        }

        var p = providerType.ToLowerInvariant().Trim();

        if (p == "twilio")
        {
            if (obj.ContainsKey("authToken") && obj["authToken"] != null)
            {
                obj["authToken"] = MaskSecret(obj["authToken"]?.ToString());
            }
            if (obj.ContainsKey("auth_token") && obj["auth_token"] != null)
            {
                obj["auth_token"] = MaskSecret(obj["auth_token"]?.ToString());
            }
        }
        else if (p == "exotel")
        {
            if (obj.ContainsKey("apiToken") && obj["apiToken"] != null)
            {
                obj["apiToken"] = MaskSecret(obj["apiToken"]?.ToString());
            }
            if (obj.ContainsKey("api_token") && obj["api_token"] != null)
            {
                obj["api_token"] = MaskSecret(obj["api_token"]?.ToString());
            }
        }
        else if (p == "livekit")
        {
            if (obj.ContainsKey("apiSecret") && obj["apiSecret"] != null)
            {
                obj["apiSecret"] = MaskSecret(obj["apiSecret"]?.ToString());
            }
            if (obj.ContainsKey("api_secret") && obj["api_secret"] != null)
            {
                obj["api_secret"] = MaskSecret(obj["api_secret"]?.ToString());
            }
        }
        else if (p == "sip_trunk" || p == "sip")
        {
            if (obj.ContainsKey("authPassword") && obj["authPassword"] != null)
            {
                obj["authPassword"] = MaskSecret(obj["authPassword"]?.ToString());
            }
            if (obj.ContainsKey("auth_password") && obj["auth_password"] != null)
            {
                obj["auth_password"] = MaskSecret(obj["auth_password"]?.ToString());
            }
            if (obj.ContainsKey("password") && obj["password"] != null)
            {
                obj["password"] = MaskSecret(obj["password"]?.ToString());
            }
        }
        else if (p == "sip")
        {
            if (obj.ContainsKey("password") && obj["password"] != null)
            {
                obj["password"] = MaskSecret(obj["password"]?.ToString());
            }
        }

        return obj;
    }

    private static string MaskSecret(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Length <= 6) return "••••••••";
        return $"{val[..2]}••••••••{val[^4..]}";
    }
}
