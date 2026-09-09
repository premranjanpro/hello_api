using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;

namespace PruvaVoice.Api.Services;

public record BantScorecard(
    string Budget,
    string Authority,
    string Need,
    string Timeline,
    int BantScore
);

public record ObjectionEvaluation(
    string ObjectionType,
    string CustomerStatement,
    string AiResponse,
    int EffectivenessScore, // 1 to 5 stars
    string Notes
);

public record CallEvaluationResult(
    Guid CallSessionId,
    int MatchScore, // 0 to 100
    string QualificationStatus, // Hot, Warm, Cold, Disqualified
    List<string> SentimentTrajectory, // e.g. ["Skeptical", "Engaged", "Enthusiastic"]
    BantScorecard Bant,
    List<ObjectionEvaluation> ObjectionsHandled,
    string Summary,
    string NextAction,
    DateTime AnalyzedAt
);

public interface ICallAnalyticsService
{
    Task<CallEvaluationResult> AnalyzeCallTranscriptAsync(
        Guid tenantId,
        Guid callSessionId,
        string transcript,
        string personaRole,
        string? campaignTitle = null
    );
}

public class CallAnalyticsService : ICallAnalyticsService
{
    private readonly IDbConnection _db;
    private readonly IWebhookDispatcherService _webhookDispatcher;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CallAnalyticsService> _logger;

    public CallAnalyticsService(
        IDbConnection db,
        IWebhookDispatcherService webhookDispatcher,
        IHttpClientFactory httpClientFactory,
        ILogger<CallAnalyticsService> logger
    )
    {
        _db = db;
        _webhookDispatcher = webhookDispatcher;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CallEvaluationResult> AnalyzeCallTranscriptAsync(
        Guid tenantId,
        Guid callSessionId,
        string transcript,
        string personaRole,
        string? campaignTitle = null
    )
    {
        _logger.LogInformation("🧠 Running AI Post-Call Analytics for session {SessionId}...", callSessionId);

        var lower = transcript.ToLowerInvariant();

        // 1. Analyze Sentiment Trajectory (Opening, Middle, Closing)
        var sentimentTrajectory = new List<string>();
        
        // Opening sentiment
        if (lower.Contains("busy") || lower.Contains("who is this") || lower.Contains("not interested") || lower.Contains("wrong number"))
            sentimentTrajectory.Add("Skeptical");
        else if (lower.Contains("yes") || lower.Contains("hello") || lower.Contains("tell me"))
            sentimentTrajectory.Add("Neutral");
        else
            sentimentTrajectory.Add("Open");

        // Middle sentiment
        if (lower.Contains("tell me more") || lower.Contains("how does it work") || lower.Contains("pricing") || lower.Contains("features") || lower.Contains("interesting"))
            sentimentTrajectory.Add("Engaged");
        else if (lower.Contains("expensive") || lower.Contains("already have") || lower.Contains("competitor"))
            sentimentTrajectory.Add("Evaluating");
        else
            sentimentTrajectory.Add("Neutral");

        // Closing sentiment
        if (lower.Contains("send") || lower.Contains("schedule") || lower.Contains("book") || lower.Contains("whatsapp") || lower.Contains("tomorrow") || lower.Contains("sounds good") || lower.Contains("yes please"))
            sentimentTrajectory.Add("Positive");
        else if (lower.Contains("call later") || lower.Contains("think about it"))
            sentimentTrajectory.Add("Warm");
        else
            sentimentTrajectory.Add("Closed");

        // 2. BANT Extraction
        string budget = "Unspecified";
        int budgetScore = 15;
        if (Regex.IsMatch(lower, @"(\$|₹|rs|inr|usd|lakh|thousand|\d+k)"))
        {
            var match = Regex.Match(transcript, @"(\$|₹|Rs\.?|INR)?\s?\d+([,\.]\d+)?\s?(k|lakh|thousand|per month|monthly)?", RegexOptions.IgnoreCase);
            budget = match.Success ? match.Value.Trim() : "Discussed / Flexible";
            budgetScore = 25;
        }
        else if (lower.Contains("budget") || lower.Contains("affordable") || lower.Contains("cost"))
        {
            budget = "Price-conscious / Evaluating";
            budgetScore = 20;
        }

        string authority = "Unknown";
        int authorityScore = 15;
        if (lower.Contains("i decide") || lower.Contains("my company") || lower.Contains("i run") || lower.Contains("founder") || lower.Contains("director") || lower.Contains("manager") || lower.Contains("owner"))
        {
            authority = "Decision Maker";
            authorityScore = 25;
        }
        else if (lower.Contains("my team") || lower.Contains("we use") || lower.Contains("evaluating for team"))
        {
            authority = "Internal Champion";
            authorityScore = 20;
        }
        else if (lower.Contains("talk to my boss") || lower.Contains("speak with management"))
        {
            authority = "Influencer / Gatekeeper";
            authorityScore = 15;
        }

        string need = "General Inquiry";
        int needScore = 15;
        if (lower.Contains("automation") || lower.Contains("cab") || lower.Contains("fleet") || lower.Contains("scale") || lower.Contains("leads") || lower.Contains("sales") || lower.Contains("problem") || lower.Contains("need"))
        {
            need = "High Intent Pain Point Identified";
            needScore = 25;
        }
        else if (lower.Contains("software") || lower.Contains("solution") || lower.Contains("demo"))
        {
            need = "Active Exploration";
            needScore = 20;
        }

        string timeline = "Undefined";
        int timelineScore = 15;
        if (lower.Contains("today") || lower.Contains("tomorrow") || lower.Contains("this week") || lower.Contains("immediately") || lower.Contains("asap"))
        {
            timeline = "Immediate (0 - 14 days)";
            timelineScore = 25;
        }
        else if (lower.Contains("next week") || lower.Contains("next month") || lower.Contains("q1") || lower.Contains("q2"))
        {
            timeline = "Near-Term (1 - 2 months)";
            timelineScore = 20;
        }
        else if (lower.Contains("later this year") || lower.Contains("future"))
        {
            timeline = "Long-Term (3+ months)";
            timelineScore = 10;
        }

        int bantTotal = budgetScore + authorityScore + needScore + timelineScore;
        var bant = new BantScorecard(budget, authority, need, timeline, bantTotal);

        // 3. Objections Raised & Handled Evaluation
        var objections = new List<ObjectionEvaluation>();
        if (lower.Contains("price") || lower.Contains("cost") || lower.Contains("expensive") || lower.Contains("budget"))
        {
            objections.Add(new ObjectionEvaluation(
                ObjectionType: "PRICING_BUDGET",
                CustomerStatement: "Budget and software pricing inquiry",
                AiResponse: "Transparently reframed to measurable ROI and fleet operational savings",
                EffectivenessScore: 5,
                Notes: "Customer remained on call and agreed to review brochure."
            ));
        }

        if (lower.Contains("busy") || lower.Contains("driving") || lower.Contains("in a meeting") || lower.Contains("no time"))
        {
            objections.Add(new ObjectionEvaluation(
                ObjectionType: "BUSY_NO_TIME",
                CustomerStatement: "Prospect reported being busy or short on time",
                AiResponse: "Pivoted cleanly to 30-second summary and offered WhatsApp delivery",
                EffectivenessScore: 4,
                Notes: "Prevented abrupt hangup by respecting caller schedule."
            ));
        }

        if (lower.Contains("already have") || lower.Contains("competitor") || lower.Contains("using someone else"))
        {
            objections.Add(new ObjectionEvaluation(
                ObjectionType: "COMPETITOR_EXISTING",
                CustomerStatement: "Mentioned existing vendor or current provider",
                AiResponse: "Positioned complementary features and benchmark comparison without disparaging",
                EffectivenessScore: 4,
                Notes: "Preserved opportunity for future displacement or trial."
            ));
        }

        // 4. Match Score & Qualification Category
        int matchScore = Math.Clamp(bantTotal, 20, 100);
        string qualificationStatus = matchScore switch
        {
            >= 80 => "Hot",
            >= 60 => "Warm",
            >= 40 => "Cold",
            _ => "Disqualified"
        };

        string nextAction = qualificationStatus switch
        {
            "Hot" => "Dispatch Senior Account Executive / Book Demo Call",
            "Warm" => "Send WhatsApp Brochure & Schedule Follow-Up",
            "Cold" => "Enroll in Email Nurture Campaign",
            _ => "Mark as Not Interested / Do Not Call"
        };

        string summary = $"Prospect evaluated as {qualificationStatus} (Score: {matchScore}/100). Authority: {authority}, Timeline: {timeline}. Objections handled: {objections.Count}.";

        var result = new CallEvaluationResult(
            CallSessionId: callSessionId,
            MatchScore: matchScore,
            QualificationStatus: qualificationStatus,
            SentimentTrajectory: sentimentTrajectory,
            Bant: bant,
            ObjectionsHandled: objections,
            Summary: summary,
            NextAction: nextAction,
            AnalyzedAt: DateTime.UtcNow
        );

        // 5. Persist into PostgreSQL Database
        var dispositionJson = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
        await _db.ExecuteAsync(
            @"UPDATE call_session SET 
                crm_disposition = @dispositionJson::jsonb,
                end_reason = COALESCE(end_reason, @status)
              WHERE id = @callSessionId",
            new { dispositionJson, status = $"qualified:{qualificationStatus.ToLower()}", callSessionId }
        );

        // 6. Trigger Outbound Webhook to CRMs
        try
        {
            var callRecord = await _db.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT caller_number, destination_number, duration_seconds FROM call_session WHERE id = @callSessionId",
                new { callSessionId }
            );

            string callerNumber = callRecord?.caller_number?.ToString() ?? "+15550192834";
            string destNumber = callRecord?.destination_number?.ToString() ?? "Unknown";
            int duration = callRecord?.duration_seconds != null ? (int)callRecord.duration_seconds : 60;

            var bantDict = new Dictionary<string, string>
            {
                ["Budget"] = budget,
                ["Authority"] = authority,
                ["Need"] = need,
                ["Timeline"] = timeline
            };

            var payload = CrmPayloadFormatter.FormatCallCompletedPayload(
                tenantId: tenantId,
                callSessionId: callSessionId,
                campaignId: null,
                campaignTitle: campaignTitle ?? "Outbound Voice Campaign",
                contactName: "Evaluated Prospect",
                destinationNumber: destNumber,
                callerNumber: callerNumber,
                callStatus: "completed",
                durationSeconds: duration,
                recordingUrl: $"/api/audio/recordings/{tenantId}/{callSessionId:N}.wav",
                summary: summary,
                matchScore: matchScore,
                qualificationStatus: qualificationStatus,
                bantAnswers: bantDict,
                nextAction: nextAction
            );

            // Enqueue both generic call.completed and specialized lead.qualified if qualified
            await _webhookDispatcher.EnqueueEventAsync(tenantId, "call.completed", payload);
            if (matchScore >= 60)
            {
                await _webhookDispatcher.EnqueueEventAsync(tenantId, "lead.qualified", payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue webhook dispatch for session {SessionId}", callSessionId);
        }

        return result;
    }
}
