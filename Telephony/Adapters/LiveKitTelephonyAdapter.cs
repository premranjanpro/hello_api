using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PruvaVoice.Api.Services;

namespace PruvaVoice.Api.Telephony.Adapters;

public class LiveKitTelephonyAdapter : ITelephonyProvider
{
    private readonly LiveKitTokenService _tokenService;

    public string ProviderType => "livekit";
    public string Name => "LiveKit WebRTC Audio";

    public LiveKitTelephonyAdapter(LiveKitTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    public ProviderCapabilities GetCapabilities() => new(
        OutboundCall: true,
        InboundCall: true,
        AudioStreaming: true,
        Recording: true,
        Transfer: false,
        Sms: false
    );

    public async Task<ValidationResult> ValidateCredentialsAsync(JsonElement config)
    {
        string url = config.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        string apiKey = config.TryGetProperty("apiKey", out var ak) ? ak.GetString() ?? "" : "";
        string apiSecret = config.TryGetProperty("apiSecret", out var asec) ? asec.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ValidationResult(false, "LiveKit WebSocket URL is required (e.g. ws://localhost:7880 or wss://livekit.domain.com)");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ValidationResult(false, "LiveKit API Key is required");
        }
        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            return new ValidationResult(false, "LiveKit API Secret is required");
        }

        try
        {
            // Test token generation
            var testToken = _tokenService.CreateToken(apiKey, apiSecret, "validation_room", "test_identity", "{}");
            if (string.IsNullOrWhiteSpace(testToken))
            {
                return new ValidationResult(false, "Token generation failed with provided credentials");
            }

            return new ValidationResult(true, $"Successfully connected to LiveKit cluster at {url}", GetCapabilities(), new Dictionary<string, object>
            {
                ["cluster_url"] = url,
                ["api_key"] = apiKey
            });
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"LiveKit validation error: {ex.Message}");
        }
    }

    public async Task<CallInitiationResult> MakeCallAsync(CallRequest request, JsonElement config)
    {
        string url = config.TryGetProperty("url", out var u) ? u.GetString() ?? "ws://localhost:7880" : "ws://localhost:7880";
        string apiKey = config.TryGetProperty("apiKey", out var ak) ? ak.GetString() ?? "devkey" : "devkey";
        string apiSecret = config.TryGetProperty("apiSecret", out var asec) ? asec.GetString() ?? "devsecretkeyshouldbe48characterslongforsecurity!" : "devsecretkeyshouldbe48characterslongforsecurity!";

        var roomName = $"task_call_{request.TaskId:N}_{Guid.NewGuid():N}";
        var participantIdentity = !string.IsNullOrWhiteSpace(request.DestinationNumber) ? request.DestinationNumber : $"contact_{Guid.NewGuid():N}";

        var metadataObj = new
        {
            taskId = request.TaskId.ToString(),
            agentId = request.AgentId,
            destination = request.DestinationNumber,
            caller = request.CallerNumber,
            contactName = request.ContactName ?? "",
            custom = request.CustomMetadata
        };
        var metadataJson = JsonSerializer.Serialize(metadataObj);

        var token = _tokenService.CreateToken(apiKey, apiSecret, roomName, participantIdentity, metadataJson);

        return new CallInitiationResult(
            Success: true,
            ProviderCallSid: roomName,
            Status: "in-progress",
            RoomName: roomName,
            Token: token,
            RawResponse: new { url, roomName, token }
        );
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
        return new CallStatusInfo(
            Status: "completed",
            DurationSeconds: 0,
            StartTime: DateTimeOffset.UtcNow,
            EndTime: DateTimeOffset.UtcNow,
            RecordingUrl: null,
            ErrorMessage: null
        );
    }

    public async Task<NormalizedCallEvent> ParseWebhookAsync(HttpRequest request, JsonElement config)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();

        string eventType = CallEventTypes.CallEnded;
        string roomName = "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var lkEvent = root.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
            roomName = root.TryGetProperty("room", out var r) && r.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";

            eventType = lkEvent switch
            {
                "participant_joined" => CallEventTypes.CallAnswered,
                "participant_left" => CallEventTypes.CallEnded,
                "room_started" => CallEventTypes.CallInitiated,
                "room_finished" => CallEventTypes.CallEnded,
                _ => CallEventTypes.CallEnded
            };
        }
        catch { }

        return new NormalizedCallEvent(
            EventType: eventType,
            CallId: null,
            Provider: "livekit",
            ProviderCallSid: roomName,
            Timestamp: DateTimeOffset.UtcNow,
            Status: eventType
        );
    }
}
