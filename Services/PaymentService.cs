namespace PruvaVoice.Api.Services;

public class PaymentService
{
    public bool VerifyWebhook(string provider, string signature, string body)
    {
        // Production: verify Cashfree/Razorpay signature here.
        return provider.Equals("mock", StringComparison.OrdinalIgnoreCase);
    }
}
