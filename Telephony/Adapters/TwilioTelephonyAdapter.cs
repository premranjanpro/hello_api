using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PruvaVoice.Api.Telephony.Adapters;

public class TwilioTelephonyAdapter : ITelephonyProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public string ProviderType => "twilio";
    public string Name => "Twilio Programmable Voice";

    public TwilioTelephonyAdapter(IHttpClientFactory httpClientFactory)
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
        string authToken = config.TryGetProperty("authToken", out var tok) ? tok.GetString() ?? "" : "";
        string phoneNumber = config.TryGetProperty("phoneNumber", out var num) ? num.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(accountSid))
        {
            return new ValidationResult(false, "Twilio Account SID is required (starts with AC...)");
        }
        if (string.IsNullOrWhiteSpace(authToken))
        {
            return new ValidationResult(false, "Twilio Auth Token is required");
        }

        // Sandbox / Test credentials support
        if (accountSid.StartsWith("AC_test") || accountSid.StartsWith("AC_sandbox") || accountSid.Equals("AC_mock", StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationResult(true, "Twilio Sandbox / Mock credentials verified successfully!", GetCapabilities(), new Dictionary<string, object>
            {
                ["account_sid"] = accountSid,
                ["phone_number"] = phoneNumber,
                ["mode"] = "sandbox"
            });
        }

        if (!accountSid.StartsWith("AC"))
        {
            return new ValidationResult(false, "Invalid Twilio Account SID format. Must begin with 'AC'");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var checkUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}.json";
            var response = await client.GetAsync(checkUrl);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var friendlyName = doc.RootElement.TryGetProperty("friendly_name", out var fn) ? fn.GetString() ?? "" : "Twilio Account";
                var status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() ?? "active" : "active";

                return new ValidationResult(true, $"Successfully verified Twilio Account: {friendlyName} (Status: {status})", GetCapabilities(), new Dictionary<string, object>
                {
                    ["account_sid"] = accountSid,
                    ["friendly_name"] = friendlyName,
                    ["status"] = status,
                    ["phone_number"] = phoneNumber
                });
            }
            else
            {
                return new ValidationResult(false, $"Twilio verification failed: HTTP {(int)response.StatusCode} ({response.ReasonPhrase})");
            }
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Connection error testing Twilio API: {ex.Message}");
        }
    }

    public async Task<CallInitiationResult> MakeCallAsync(CallRequest request, JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string authToken = config.TryGetProperty("authToken", out var tok) ? tok.GetString() ?? "" : "";
        string callerPhone = !string.IsNullOrWhiteSpace(request.CallerNumber) 
            ? request.CallerNumber 
            : (config.TryGetProperty("phoneNumber", out var num) ? num.GetString() ?? "" : "");

        if (string.IsNullOrWhiteSpace(callerPhone))
        {
            return new CallInitiationResult(false, "", "failed", "Twilio caller phone number is not configured");
        }

        // Sandbox execution
        if (accountSid.StartsWith("AC_test") || accountSid.StartsWith("AC_sandbox") || accountSid.Equals("AC_mock", StringComparison.OrdinalIgnoreCase))
        {
            var mockSid = $"CA_mock_{Guid.NewGuid():N}";
            return new CallInitiationResult(true, mockSid, "queued", RawResponse: new { mock = true, caller = callerPhone, destination = request.DestinationNumber });
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var postUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Calls.json";

            // If Media Stream WebSocket URL is provided, instruct Twilio TwiML to bi-directionally stream audio
            var mediaStreamUrl = request.MediaStreamUrl ?? $"wss://{request.WebhookBaseUrl?.Replace("http://", "").Replace("https://", "")}/api/telephony/stream/twilio";
            var twiml = $@"<Response>
                <Connect>
                    <Stream url=""{mediaStreamUrl}"">
                        <Parameter name=""taskId"" value=""{request.TaskId}""/>
                        <Parameter name=""agentId"" value=""{request.AgentId}""/>
                        <Parameter name=""contactName"" value=""{request.ContactName}""/>
                    </Stream>
                </Connect>
            </Response>";

            var formParams = new Dictionary<string, string>
            {
                ["From"] = callerPhone,
                ["To"] = request.DestinationNumber,
                ["Twiml"] = twiml
            };

            if (!string.IsNullOrWhiteSpace(request.WebhookBaseUrl))
            {
                formParams["StatusCallback"] = $"{request.WebhookBaseUrl.TrimEnd('/')}/api/webhooks/twilio/{request.TenantId}";
                formParams["StatusCallbackEvent"] = "initiated ringing answered completed";
            }

            var resp = await client.PostAsync(postUrl, new FormUrlEncodedContent(formParams));
            var respContent = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(respContent);
                var callSid = doc.RootElement.TryGetProperty("sid", out var csid) ? csid.GetString() ?? "" : "";
                var callStatus = doc.RootElement.TryGetProperty("status", out var cst) ? cst.GetString() ?? "queued" : "queued";

                return new CallInitiationResult(true, callSid, callStatus, RawResponse: doc.RootElement);
            }
            else
            {
                return new CallInitiationResult(false, "", "failed", $"Twilio Call initiation failed: {respContent}");
            }
        }
        catch (Exception ex)
        {
            return new CallInitiationResult(false, "", "failed", $"Exception initiating Twilio call: {ex.Message}");
        }
    }

    public async Task<bool> EndCallAsync(string providerCallSid, JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string authToken = config.TryGetProperty("authToken", out var tok) ? tok.GetString() ?? "" : "";

        if (providerCallSid.StartsWith("CA_mock_")) return true;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var postUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Calls/{providerCallSid}.json";
            var formParams = new Dictionary<string, string> { ["Status"] = "completed" };
            var resp = await client.PostAsync(postUrl, new FormUrlEncodedContent(formParams));
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TransferCallAsync(string providerCallSid, string targetNumber, JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string authToken = config.TryGetProperty("authToken", out var tok) ? tok.GetString() ?? "" : "";

        try
        {
            var client = _httpClientFactory.CreateClient();
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var postUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Calls/{providerCallSid}.json";
            var twiml = $"<Response><Dial>{targetNumber}</Dial></Response>";
            var formParams = new Dictionary<string, string> { ["Twiml"] = twiml };
            var resp = await client.PostAsync(postUrl, new FormUrlEncodedContent(formParams));
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<CallStatusInfo> GetCallStatusAsync(string providerCallSid, JsonElement config)
    {
        string accountSid = config.TryGetProperty("accountSid", out var sid) ? sid.GetString() ?? "" : "";
        string authToken = config.TryGetProperty("authToken", out var tok) ? tok.GetString() ?? "" : "";

        if (providerCallSid.StartsWith("CA_mock_"))
        {
            return new CallStatusInfo("completed", 60, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, null, null);
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            var getUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Calls/{providerCallSid}.json";
            var resp = await client.GetAsync(getUrl);
            if (resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() ?? "completed" : "completed";
                var durStr = doc.RootElement.TryGetProperty("duration", out var dur) ? dur.GetString() ?? "0" : "0";
                int.TryParse(durStr, out var duration);

                return new CallStatusInfo(status, duration, null, null, null, null);
            }
        }
        catch { }

        return new CallStatusInfo("unknown", 0, null, null, null, null);
    }

    public async Task<NormalizedCallEvent> ParseWebhookAsync(HttpRequest request, JsonElement config)
    {
        var form = await request.ReadFormAsync();
        var callSid = form["CallSid"].ToString();
        var rawStatus = form["CallStatus"].ToString().ToLowerInvariant();
        var recordingUrl = form["RecordingUrl"].ToString();
        var durationStr = form["CallDuration"].ToString();
        int.TryParse(durationStr, out var duration);

        string eventType = rawStatus switch
        {
            "queued" or "initiated" => CallEventTypes.CallInitiated,
            "ringing" => CallEventTypes.CallRinging,
            "in-progress" => CallEventTypes.CallAnswered,
            "busy" => CallEventTypes.CallBusy,
            "no-answer" => CallEventTypes.CallNoAnswer,
            "failed" => CallEventTypes.CallFailed,
            "completed" => CallEventTypes.CallEnded,
            _ => CallEventTypes.CallEnded
        };

        if (!string.IsNullOrWhiteSpace(recordingUrl))
        {
            eventType = CallEventTypes.RecordingReady;
        }

        var metadata = new Dictionary<string, string>();
        foreach (var key in form.Keys)
        {
            metadata[key] = form[key].ToString();
        }

        return new NormalizedCallEvent(
            EventType: eventType,
            CallId: null,
            Provider: "twilio",
            ProviderCallSid: callSid,
            Timestamp: DateTimeOffset.UtcNow,
            Status: rawStatus,
            DurationSeconds: duration,
            RecordingUrl: string.IsNullOrWhiteSpace(recordingUrl) ? null : recordingUrl,
            Metadata: metadata
        );
    }
}
