using System.Data;
using System.Net.Http;
using System.Text.Json;
using Dapper;

namespace PruvaVoice.Api.Services;

public class OtpService
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public async Task<(string provider, string code, bool returnInResponse)> GenerateAndSendAsync(IDbConnection db, string phone)
    {
        // 1. Check if the phone is in the whitelist_user table
        var whitelist = await db.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM whitelist_user WHERE phone=@phone", new { phone });
        
        if (whitelist != null)
        {
            // Whitelisted user: do NOT send SMS, return the fixed OTP directly in response.
            string whitelistOtp = whitelist.fixed_otp ?? "1234";
            return ("whitelist", whitelistOtp, true);
        }

        // 2. Fetch the active OTP integration credential
        var cred = await db.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM integration_credential WHERE provider_type='otp' AND is_active=true ORDER BY updated_at DESC LIMIT 1");

        string provider = cred?.provider_name ?? "mock";
        string code = Random.Shared.Next(1000, 9999).ToString();
        bool returnOtp = false;

        if (provider == "mock")
        {
            // Mock provider: return "1234" in response for easy local testing
            code = "1234";
            returnOtp = true;
        }
        else
        {
            // Third-party SMS sending
            string configJsonStr = cred?.config_json ?? "{}";
            try
            {
                using var doc = JsonDocument.Parse(configJsonStr);
                var root = doc.RootElement;

                if (provider == "2factor")
                {
                    string apiKey = root.TryGetProperty("apiKey", out var keyProp) ? keyProp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        // 2Factor GET: https://2factor.in/API/V1/{apiKey}/SMS/{phone}/{otp}
                        string url = $"https://2factor.in/API/V1/{apiKey}/SMS/{phone}/{code}";
                        var response = await _httpClient.GetAsync(url);
                        response.EnsureSuccessStatusCode();
                    }
                }
                else if (provider == "msg91")
                {
                    string authKey = root.TryGetProperty("authKey", out var authProp) ? authProp.GetString() ?? "" : "";
                    string templateId = root.TryGetProperty("templateId", out var tempProp) ? tempProp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(authKey) && !string.IsNullOrWhiteSpace(templateId))
                    {
                        // MSG91 OTP: https://control.msg91.com/api/v5/otp?template_id={templateId}&mobile={phone}&authkey={authKey}&otp={otp}
                        string url = $"https://control.msg91.com/api/v5/otp?template_id={templateId}&mobile={phone}&authkey={authKey}&otp={code}";
                        var response = await _httpClient.PostAsync(url, null);
                        response.EnsureSuccessStatusCode();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending OTP via {provider}: {ex.Message}");
                // Fallback to mock on connection or credentials failure so the system remains testable
                code = "1234";
                returnOtp = true;
                provider = "mock-fallback";
            }
        }

        return (provider, code, returnOtp);
    }
}
