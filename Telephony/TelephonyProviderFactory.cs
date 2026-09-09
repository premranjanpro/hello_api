using System.Data;
using System.Text.Json;
using Dapper;
using PruvaVoice.Api.Services;
using PruvaVoice.Api.Telephony.Adapters;

namespace PruvaVoice.Api.Telephony;

public class TelephonyProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly CredentialEncryptionService _encryptionService;
    private readonly Dictionary<string, Type> _adapterTypes = new(StringComparer.OrdinalIgnoreCase);

    public TelephonyProviderFactory(IServiceProvider serviceProvider, CredentialEncryptionService encryptionService)
    {
        _serviceProvider = serviceProvider;
        _encryptionService = encryptionService;

        RegisterAdapter<LiveKitTelephonyAdapter>("livekit");
        RegisterAdapter<TwilioTelephonyAdapter>("twilio");
        RegisterAdapter<ExotelTelephonyAdapter>("exotel");
        RegisterAdapter<SipTrunkTelephonyAdapter>("sip_trunk");
        RegisterAdapter<SipTrunkTelephonyAdapter>("sip");
    }

    public void RegisterAdapter<T>(string providerType) where T : ITelephonyProvider
    {
        _adapterTypes[providerType] = typeof(T);
    }

    public ITelephonyProvider GetProvider(string providerType)
    {
        if (_adapterTypes.TryGetValue(providerType, out var type))
        {
            return (ITelephonyProvider)_serviceProvider.GetRequiredService(type);
        }
        throw new NotSupportedException($"Telephony provider '{providerType}' is not supported or not registered.");
    }

    public bool IsProviderSupported(string providerType)
    {
        return _adapterTypes.ContainsKey(providerType);
    }

    public IEnumerable<string> GetSupportedProviders()
    {
        return _adapterTypes.Keys;
    }

    public async Task<(ITelephonyProvider Provider, JsonElement DecryptedConfig, dynamic CredentialRecord)> ResolveTaskProviderAsync(Guid credentialId, IDbConnection db)
    {
        var row = await db.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT * FROM telephony_provider_credential WHERE id = @credentialId AND is_active = true",
            new { credentialId });

        if (row == null)
        {
            throw new InvalidOperationException($"Active telephony credential with ID '{credentialId}' was not found.");
        }

        string providerType = (string)row.provider_type;
        string encryptedCreds = (string)row.encrypted_credentials;
        string plainJson = _encryptionService.Decrypt(encryptedCreds);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(plainJson) ? "{}" : plainJson);
        var config = doc.RootElement.Clone();

        var provider = GetProvider(providerType);
        return (provider, config, row);
    }
}
