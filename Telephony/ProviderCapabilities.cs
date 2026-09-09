namespace PruvaVoice.Api.Telephony;

public record ProviderCapabilities(
    bool OutboundCall = true,
    bool InboundCall = true,
    bool AudioStreaming = true,
    bool Recording = true,
    bool Transfer = false,
    bool Sms = false
);
