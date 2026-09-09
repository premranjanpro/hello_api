using System.Text.Json;

namespace PruvaVoice.Api.Services;

public static class CrmPayloadFormatter
{
    public static object FormatCallCompletedPayload(
        Guid tenantId,
        Guid callSessionId,
        Guid? campaignId,
        string campaignTitle,
        string contactName,
        string destinationNumber,
        string callerNumber,
        string callStatus,
        int durationSeconds,
        string? recordingUrl,
        string? summary,
        double? matchScore,
        string? qualificationStatus,
        Dictionary<string, string>? bantAnswers,
        string? nextAction
    )
    {
        return new
        {
            @event = "call.completed",
            timestamp = DateTime.UtcNow.ToString("o"),
            tenant_id = tenantId,
            call_session_id = callSessionId,
            campaign = new
            {
                id = campaignId,
                title = campaignTitle
            },
            contact = new
            {
                name = contactName,
                phone = destinationNumber,
                caller_id = callerNumber
            },
            metrics = new
            {
                status = callStatus,
                duration_seconds = durationSeconds,
                recording_url = recordingUrl
            },
            ai_evaluation = new
            {
                match_score = matchScore ?? 0,
                qualification = qualificationStatus ?? "Pending",
                next_action = nextAction ?? "None",
                bant = bantAnswers ?? new Dictionary<string, string>(),
                summary = summary ?? "Call completed."
            },
            crm_integrations = new
            {
                hubspot_contact = new
                {
                    firstname = contactName.Split(' ').FirstOrDefault(),
                    lastname = contactName.Split(' ').Skip(1).FirstOrDefault() ?? "",
                    phone = destinationNumber,
                    hs_lead_status = qualificationStatus == "Hot" ? "QUALIFIED" : "OPEN",
                    notes = summary
                },
                salesforce_lead = new
                {
                    LastName = contactName,
                    Phone = destinationNumber,
                    Status = qualificationStatus == "Hot" ? "Working - Contacted" : "Open - Not Contacted",
                    Description = $"{summary}\n\nMatch Score: {matchScore}/100"
                }
            }
        };
    }
}
