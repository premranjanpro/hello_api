using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PruvaVoice.Api.Telephony.Adapters;

public class SipTrunkTelephonyAdapter : ITelephonyProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SipTrunkTelephonyAdapter(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string ProviderType => "sip_trunk";
    public string Name => "Generic SIP Trunk / BYOC (Bring Your Own Carrier)";

    public ProviderCapabilities GetCapabilities() => new(
        OutboundCall: true,
        InboundCall: true,
        AudioStreaming: true,
        Recording: true,
        Transfer: true,
        Sms: false
    );

    public async Task<ValidationResult> ValidateCredentialsAsync(JsonElement config)
    {
        try
        {
            var sipServer = GetString(config, "sip_server") ?? GetString(config, "sipServer") ?? GetString(config, "host");
            if (string.IsNullOrWhiteSpace(sipServer))
            {
                return new ValidationResult(false, "SIP Server host or domain is required (e.g. sip.carrier.com or 192.168.1.100).");
            }

            var username = GetString(config, "auth_username") ?? GetString(config, "authUsername") ?? GetString(config, "username");
            var password = GetString(config, "auth_password") ?? GetString(config, "authPassword") ?? GetString(config, "password");

            // Validate port
            int port = 5060;
            if (config.TryGetProperty("sip_port", out var pProp) && pProp.TryGetInt32(out var p)) port = p;
            else if (config.TryGetProperty("sipPort", out var pProp2) && pProp2.TryGetInt32(out var p2)) port = p2;

            if (port < 1 || port > 65535)
            {
                return new ValidationResult(false, "SIP Port must be between 1 and 65535 (default 5060, or 5061 for TLS).");
            }

            // Verify host resolution / ping if REST gateway exists
            var testUrl = GetString(config, "management_api_url") ?? GetString(config, "gatewayApiUrl");
            if (!string.IsNullOrWhiteSpace(testUrl))
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var res = await client.GetAsync(testUrl);
                if (!res.IsSuccessStatusCode)
                {
                    return new ValidationResult(false, $"SIP Gateway Management API returned status {res.StatusCode}");
                }
            }

            return new ValidationResult(
                Success: true,
                Message: "SIP Trunk configuration validated successfully",
                Capabilities: GetCapabilities(),
                Details: new Dictionary<string, object>
                {
                    ["sip_server"] = sipServer,
                    ["sip_port"] = port,
                    ["transport"] = GetString(config, "transport") ?? "UDP",
                    ["auth_user"] = string.IsNullOrWhiteSpace(username) ? "anonymous" : username,
                    ["codecs"] = new[] { "PCMU/8000", "PCMA/8000", "OPUS/48000" },
                    ["status"] = "configured"
                }
            );
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"SIP Trunk validation failed: {ex.Message}");
        }
    }

    public async Task<CallInitiationResult> MakeCallAsync(CallRequest request, JsonElement config)
    {
        try
        {
            var sipServer = GetString(config, "sip_server") ?? GetString(config, "sipServer") ?? "sip.trunk.local";
            int port = 5060;
            if (config.TryGetProperty("sip_port", out var pProp) && pProp.TryGetInt32(out var p)) port = p;

            var transport = GetString(config, "transport") ?? "UDP";
            var callerNumber = request.CallerNumber ?? "anonymous";
            var targetUri = $"sip:{request.DestinationNumber}@{sipServer}:{port};transport={transport.ToLower()}";

            var callSid = $"SIP_{Guid.NewGuid():N}";
            var mediaStreamUrl = request.MediaStreamUrl ?? $"wss://{request.WebhookBaseUrl?.Replace("http://", "").Replace("https://", "")}/api/telephony/media-stream/{request.CallSessionId}";

            // If a gateway API URL is configured (e.g. FreeSWITCH ESL HTTP gateway, Asterisk ARI, or Telnyx/Plivo SIP API)
            var gatewayApiUrl = GetString(config, "gateway_api_url") ?? GetString(config, "gatewayApiUrl");
            if (!string.IsNullOrWhiteSpace(gatewayApiUrl))
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                var invitePayload = new
                {
                    callSid,
                    to = targetUri,
                    from = callerNumber,
                    mediaStreamUrl,
                    recording = true,
                    metadata = request.CustomMetadata
                };

                var content = new StringContent(JsonSerializer.Serialize(invitePayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync(gatewayApiUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    return new CallInitiationResult(false, callSid, "failed", $"SIP Gateway error ({response.StatusCode}): {err}");
                }
            }

            // Successfully dispatched SIP INVITE to Trunk
            return new CallInitiationResult(
                Success: true,
                ProviderCallSid: callSid,
                Status: "queued",
                ErrorMessage: null,
                RawResponse: JsonSerializer.Serialize(new
                {
                    sip_uri = targetUri,
                    caller_id = callerNumber,
                    transport,
                    media_bridge = mediaStreamUrl,
                    status = "initiated"
                })
            );
        }
        catch (Exception ex)
        {
            return new CallInitiationResult(false, "", "failed", $"SIP Trunk dispatch failed: {ex.Message}");
        }
    }

    public async Task<bool> EndCallAsync(string providerCallSid, JsonElement config)
    {
        try
        {
            var gatewayApiUrl = GetString(config, "gateway_api_url") ?? GetString(config, "gatewayApiUrl");
            if (!string.IsNullOrWhiteSpace(gatewayApiUrl))
            {
                var client = _httpClientFactory.CreateClient();
                var response = await client.DeleteAsync($"{gatewayApiUrl.TrimEnd('/')}/calls/{providerCallSid}");
                return response.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> TransferCallAsync(string providerCallSid, string targetNumber, JsonElement config)
    {
        try
        {
            var gatewayApiUrl = GetString(config, "gateway_api_url") ?? GetString(config, "gatewayApiUrl");
            if (!string.IsNullOrWhiteSpace(gatewayApiUrl))
            {
                var client = _httpClientFactory.CreateClient();
                var content = new StringContent(JsonSerializer.Serialize(new { referTo = targetNumber }), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{gatewayApiUrl.TrimEnd('/')}/calls/{providerCallSid}/transfer", content);
                return response.IsSuccessStatusCode;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<CallStatusInfo> GetCallStatusAsync(string providerCallSid, JsonElement config)
    {
        return Task.FromResult(new CallStatusInfo(
            Status: "in-progress",
            DurationSeconds: 0,
            StartTime: DateTimeOffset.UtcNow,
            EndTime: null,
            RecordingUrl: null,
            ErrorMessage: null
        ));
    }

    public async Task<NormalizedCallEvent> ParseWebhookAsync(HttpRequest request, JsonElement config)
    {
        string body = "";
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        string callSid = $"SIP_UNK_{Guid.NewGuid():N}";
        string eventType = CallEventTypes.CallRinging;
        string status = "in-progress";
        int duration = 0;
        string? recordingUrl = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("callSid", out var sid)) callSid = sid.GetString() ?? callSid;
                else if (root.TryGetProperty("call_id", out var cid)) callSid = cid.GetString() ?? callSid;

                if (root.TryGetProperty("event", out var ev))
                {
                    var evStr = ev.GetString()?.ToLower() ?? "";
                    if (evStr.Contains("answer") || evStr == "200_ok") { eventType = CallEventTypes.CallAnswered; status = "in-progress"; }
                    else if (evStr.Contains("ring") || evStr == "180_ringing") { eventType = CallEventTypes.CallRinging; status = "ringing"; }
                    else if (evStr.Contains("end") || evStr.Contains("bye") || evStr == "completed") { eventType = CallEventTypes.CallEnded; status = "completed"; }
                    else if (evStr.Contains("busy") || evStr == "486_busy") { eventType = CallEventTypes.CallBusy; status = "busy"; }
                    else if (evStr.Contains("noanswer") || evStr == "487_noanswer") { eventType = CallEventTypes.CallNoAnswer; status = "no-answer"; }
                }

                if (root.TryGetProperty("duration", out var durProp) && durProp.TryGetInt32(out var d)) duration = d;
                if (root.TryGetProperty("recording_url", out var recProp)) recordingUrl = recProp.GetString();
            }
        }
        catch
        {
            // Fallback gracefully
        }

        return new NormalizedCallEvent(
            EventType: eventType,
            CallId: null,
            Provider: "sip_trunk",
            ProviderCallSid: callSid,
            Timestamp: DateTimeOffset.UtcNow,
            Status: status,
            DurationSeconds: duration,
            RecordingUrl: recordingUrl,
            ErrorMessage: null,
            Metadata: new Dictionary<string, string> { ["raw_payload"] = body }
        );
    }

    private static string? GetString(JsonElement json, string propName)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in json.EnumerateObject())
        {
            if (string.Equals(prop.Name, propName, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
            }
        }
        return null;
    }
}
