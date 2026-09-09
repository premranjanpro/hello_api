using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace PruvaVoice.Api.Telephony;

public interface ITelephonyProvider
{
    string ProviderType { get; }
    string Name { get; }
    ProviderCapabilities GetCapabilities();

    Task<ValidationResult> ValidateCredentialsAsync(JsonElement config);
    Task<CallInitiationResult> MakeCallAsync(CallRequest request, JsonElement config);
    Task<bool> EndCallAsync(string providerCallSid, JsonElement config);
    Task<bool> TransferCallAsync(string providerCallSid, string targetNumber, JsonElement config);
    Task<CallStatusInfo> GetCallStatusAsync(string providerCallSid, JsonElement config);
    Task<NormalizedCallEvent> ParseWebhookAsync(HttpRequest request, JsonElement config);
}
