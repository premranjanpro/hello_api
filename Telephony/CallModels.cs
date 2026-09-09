using System.Text.Json;

namespace PruvaVoice.Api.Telephony;

public static class CallEventTypes
{
    public const string CallInitiated = "CALL_INITIATED";
    public const string CallRinging = "CALL_RINGING";
    public const string CallAnswered = "CALL_ANSWERED";
    public const string CallBusy = "CALL_BUSY";
    public const string CallNoAnswer = "CALL_NO_ANSWER";
    public const string CallFailed = "CALL_FAILED";
    public const string CallEnded = "CALL_ENDED";
    public const string RecordingReady = "RECORDING_READY";
    public const string MediaStreamConnected = "MEDIA_STREAM_CONNECTED";
    public const string MediaStreamDisconnected = "MEDIA_STREAM_DISCONNECTED";
}

public record NormalizedCallEvent(
    string EventType,
    Guid? CallId,
    string Provider,
    string ProviderCallSid,
    DateTimeOffset Timestamp,
    string Status,
    int DurationSeconds = 0,
    string? RecordingUrl = null,
    string? ErrorMessage = null,
    Dictionary<string, string>? Metadata = null
);

public record CallRequest(
    Guid TenantId,
    Guid TaskId,
    string AgentId,
    string CallerNumber,
    string DestinationNumber,
    string? ContactName = null,
    string? WebhookBaseUrl = null,
    string? MediaStreamUrl = null,
    Guid? CallSessionId = null,
    Dictionary<string, string>? CustomMetadata = null
);

public record CallInitiationResult(
    bool Success,
    string ProviderCallSid,
    string Status,
    string? ErrorMessage = null,
    string? RoomName = null,
    string? Token = null,
    object? RawResponse = null
);

public record CallStatusInfo(
    string Status,
    int DurationSeconds,
    DateTimeOffset? StartTime,
    DateTimeOffset? EndTime,
    string? RecordingUrl,
    string? ErrorMessage
);

public record ValidationResult(
    bool Success,
    string Message,
    ProviderCapabilities? Capabilities = null,
    Dictionary<string, object>? Details = null
);
