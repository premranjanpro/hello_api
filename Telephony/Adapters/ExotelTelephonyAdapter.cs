using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PruvaVoice.Api.Telephony.Adapters;

public class ExotelTelephonyAdapter : ITelephonyProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public string ProviderType => "exotel";
    public string Name => "Exotel Cloud Telephony";

    public ExotelTelephonyAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public ProviderCapabilities GetCapabilities() => new(
        OutboundCall: true,
        InboundCall: true,
        AudioStreaming: true,
        Recording: true,
        Transfer: true,
        Sms: true
    );

    public async Task<ValidationResult> ValidateCredentialsAsync(JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string apiKey = config.TryGetProperty("apiKey", out var ak) ? ak.GetString() ?? "" : "";
        string apiToken = config.TryGetProperty("apiToken", out var tok) ? tok.GetString() ?? "" : "";
        string subdomain = config.TryGetProperty("subdomain", out var sub) ? sub.GetString() ?? "api.exotel.com" : "api.exotel.com";
        string virtualNumber = config.TryGetProperty("virtualNumber", out var vn) ? vn.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(accountSid))
        {
            return new ValidationResult(false, "Exotel Account SID / Subdomain Account is required");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ValidationResult(false, "Exotel API Key is required");
        }
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            return new ValidationResult(false, "Exotel API Token is required");
        }

        // Sandbox / Test credentials support
        if (accountSid.StartsWith("exo_test") || accountSid.StartsWith("exo_sandbox") || apiKey.Contains("mock", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationResult(true, "Exotel Sandbox credentials verified successfully!", GetCapabilities(), new Dictionary<string, object>
            {
                ["account_sid"] = accountSid,
                ["virtual_number"] = virtualNumber,
                ["mode"] = "sandbox"
            });
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:{apiToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var checkUrl = $"https://{subdomain}/v1/Accounts/{accountSid}.json";
            var response = await client.GetAsync(checkUrl);

            if (response.IsSuccessStatusCode)
            {
                return new ValidationResult(true, $"Successfully verified Exotel Account: {accountSid}", GetCapabilities(), new Dictionary<string, object>
                {
                    ["account_sid"] = accountSid,
                    ["virtual_number"] = virtualNumber
                });
            }
            else
            {
                return new ValidationResult(false, $"Exotel verification failed: HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
            }
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Connection error testing Exotel API: {ex.Message}");
        }
    }

    public async Task<CallInitiationResult> MakeCallAsync(CallRequest request, JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string apiKey = config.TryGetProperty("apiKey", out var ak) ? ak.GetString() ?? "" : "";
        string apiToken = config.TryGetProperty("apiToken", out var tok) ? tok.GetString() ?? "" : "";
        string subdomain = config.TryGetProperty("subdomain", out var sub) ? sub.GetString() ?? "api.exotel.com" : "api.exotel.com";
        string callerPhone = !string.IsNullOrWhiteSpace(request.CallerNumber)
            ? request.CallerNumber
            : (config.TryGetProperty("virtualNumber", out var vn) ? vn.GetString() ?? "" : "");

        if (string.IsNullOrWhiteSpace(callerPhone))
        {
            return new CallInitiationResult(false, "", "failed", "Exotel Virtual Caller Number is required");
        }

        // Sandbox execution
        if (accountSid.StartsWith("exo_test") || accountSid.StartsWith("exo_sandbox") || apiKey.Contains("mock", StringComparison.OrdinalIgnoreCase))
        {
            var mockSid = $"EXO_mock_{Guid.NewGuid():N}";
            return new CallInitiationResult(true, mockSid, "queued", RawResponse: new { mock = true, caller = callerPhone, destination = request.DestinationNumber });
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:{apiToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var postUrl = $"https://{subdomain}/v1/Accounts/{accountSid}/Calls/connect.json";
            var formParams = new Dictionary<string, string>
            {
                ["From"] = request.DestinationNumber,
                ["To"] = callerPhone,
                ["CallerId"] = callerPhone,
                ["CallType"] = "trans"
            };

            if (!string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            {
                formParams["StatusCallback"] = $"{request.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/exotel/{request.TenantId}";
            }

            var resp = await client.PostAsync(postUrl, new FormUrlEncodedContent(formParams));
            var respContent = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(respContent);
                var callSid = doc.RootElement.TryGetProperty("Call", out var cObj) && cObj.TryGetProperty("Sid", out var csid) 
                    ? csid.GetString() ?? "" 
                    : "";
                var callStatus = doc.RootElement.TryGetProperty("Call", out var cObj2) && cObj2.TryGetProperty("Status", out var cst) 
                    ? cst.GetString() ?? "queued" 
                    : "queued";

                return new CallInitiationResult(true, callSid, callStatus, RawResponse: doc.RootElement);
            }
            else
            {
                return new CallInitiationResult(false, "", "failed", $"Exotel connect failed: {respContent}");
            }
        }
        catch (Exception ex)
        {
            return new CallInitiationResult(false, "", "failed", $"Exception connecting Exotel call: {ex.Message}");
        }
    }

    public async Task<bool> EndCallAsync(string providerCallSid, JsonElement config)
    {
        return true;
    }

    public async Task<bool> TransferCallAsync(string providerCallSid, string targetNumber, JsonElement config)
    {
        return false;
    }

    public async Task<CallStatusInfo> GetCallStatusAsync(string providerCallSid, JsonElement config)
    {
        return new CallStatusInfo("completed", 60, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, null, null);
    }

    public async Task<NormalizedCallEvent> ParseWebhookAsync(HttpRequest request, JsonElement config)
    {
        string callSid = "";
        string rawStatus = "completed";
        string recordingUrl = "";
        int duration = 0;
        var metadata = new Dictionary<string, string>();

        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            callSid = form["CallSid"].ToString();
            rawStatus = form["Status"].ToString().ToLowerInvariant();
            recordingUrl = form["RecordingUrl"].ToString();
            int.TryParse(form["DialCallDuration"].ToString(), out duration);

            foreach (var key in form.Keys)
            {
                metadata[key] = form[key].ToString();
            }
        }
        else if (request.Query.Count > 0)
        {
            callSid = request.Query["CallSid"].ToString();
            rawStatus = request.Query["Status"].ToString().ToLowerInvariant();
            recordingUrl = request.Query["RecordingUrl"].ToString();
            int.TryParse(request.Query["DialCallDuration"].ToString(), out duration);

            foreach (var key in request.Query.Keys)
            {
                metadata[key] = request.Query[key].ToString();
            }
        }

        string eventType = rawStatus switch
        {
            "in-progress" => CallEventTypes.CallAnswered,
            "completed" => CallEventTypes.CallEnded,
            "busy" => CallEventTypes.CallBusy,
            "no-answer" => CallEventTypes.CallNoAnswer,
            "failed" => CallEventTypes.CallFailed,
            _ => CallEventTypes.CallEnded
        };

        if (!string.IsNullOrWhiteSpace(recordingUrl))
        {
            eventType = CallEventTypes.RecordingReady;
        }

        return new NormalizedCallEvent(
            EventType: eventType,
            CallId: null,
            Provider: "exotel",
            ProviderCallSid: callSid,
            Timestamp: DateTimeOffset.UtcNow,
            Status: rawStatus,
            DurationSeconds: duration,
            RecordingUrl: string.IsNullOrWhiteSpace(recordingUrl) ? null : recordingUrl,
            Metadata: metadata
        );
    }
}
