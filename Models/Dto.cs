namespace PruvaVoice.Api.Models;

public record OtpRequestDto(string Phone);
public record VerifyOtpDto(string Phone, string Otp, string? ReferralCode = null);
public record ApplyReferralDto(string ReferralCode);
public record AiTextChatDto(string? PersonaId, string Message, Guid? UserId = null);
public record UpdateProfileDto(string Username, string? DisplayName, string DisplayGender, string ProfileIcon, int? Age = null, string? City = null, string? Languages = null);
public record AcceptTermsDto(Guid TermsVersionId);
public record RegisterDeviceDto(string? DeviceId, string FcmToken, string Platform, string? AppVersion);
public record HostPresenceDto(string Status, DateTimeOffset? ScheduleOnlineAt, DateTimeOffset? ScheduleOfflineAt);
public record StartCallDto(Guid HostUserId);
public record RateCallDto(int RatingCommunication, int RatingPoliteness, int RatingExpertise, int RatingAudioClarity, int RatingOverall, string? Comment);
public record ReportCallDto(Guid? CallSessionId, Guid ReportedUserId, string Reason, string? Description);
public record DevAddMoneyDto(decimal Amount);
public record PayoutMethodDto(string UpiId, string AccountHolder, string? QrImageUrl);
public record WithdrawDto(Guid PayoutMethodId, decimal Amount);
public record AdminMakeHostDto(Guid? CategoryId, decimal RatePerMinute, int SortOrder, bool Approved);
public record AdminStatusDto(string Status);
public record MarkPaidDto(string UtrReference, string? ProofImageUrl, string? AdminNote);
public record CreatePaymentOrderDto(decimal Amount, string Purpose, string Provider);
public record PaymentWebhookDto(string Provider, string EventId, string Signature, object Payload);
public record CategoryDto(string Name, string? Description, string? Icon, int SortOrder, bool IsActive);

public record OnboardingDto(string DisplayGender, DateTime Dob, bool Is18PlusConfirmed, string PreferredLanguage, int? Age = null, string? City = null, string? Languages = null);

public record AdminSendNotificationDto(Guid? UserId, string Title, string Body, string? ImageUrl, Dictionary<string, string>? Data);

