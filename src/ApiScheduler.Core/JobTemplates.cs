using ApiScheduler.Core.Models;

namespace ApiScheduler.Core;

public static class JobTemplates
{
    public const string SurveyQuery =
        """
        SELECT *
        FROM dbo.NG_MC_SURVEY_SMS_LOG
        WHERE SENT_DATE IS NULL
          AND (SENT_CALL IS NULL OR SENT_CALL NOT LIKE 'REJECTED%')
        ORDER BY INSERTIONORDERID
        """;

    /// <summary>Pre-filled job for the Survey "send" API and the NG_MC_SURVEY_SMS_LOG table.</summary>
    public static ApiJob SurveySms(Guid connectionId) => new()
    {
        Name = "Survey SMS",
        Url = "https://uat-proxy-internal.srv.local/survey/v1/send",
        ApiKeyHeaderName = "apiKey",
        IntervalMinutes = 480,
        ScheduleAnchor = "00:00",
        ConnectionId = connectionId,
        SelectQuery = SurveyQuery,
        Mappings =
        [
            Fixed("serviceName", "1", FieldType.Number),
            Fixed("SurveyType", "CSAT"),
            Fixed("channel", "SMS"),
            Col("trigger", "TRIGGER_NAME"),
            Col("policyHolderName", "POLICY_HOLDER_NAME"),
            Col("policyHolderID", "POLICY_HOLDER_ID"),
            Col("memberName", "MEMBER_NAME"),
            Col("policyNo", "POLICY_NO"),
            Col("mobileNo", "MOBILE_NO"),
            Col("claimNo", "CLAIM_NO"),
            Col("email", "EMAIL"),
            Col("gender", "GENDER"),
            Col("clientType", "CLIENT_TYPE"),
            Col("segment", "SEGMENT"),
            Col("city", "CITY"),
            Col("language", "COMM_LANGUAGE"),
            Col("brokerName", "BROKER_NAME"),
            Col("repairFacility", "REPAIR_FACILITY"),
        ],
        WriteBack = new WriteBackSettings(),
    };

    /// <summary>Guess a camelCase JSON name from a column: POLICY_HOLDER_NAME → policyHolderName.</summary>
    public static string ToCamel(string column)
    {
        var parts = column.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return column;
        return string.Concat(parts.Select((p, i) =>
            i == 0 ? p.ToLowerInvariant() : char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static FieldMapping Fixed(string field, string value, FieldType type = FieldType.String) =>
        new() { JsonField = field, Source = MappingSource.Fixed, Value = value, Type = type };

    private static FieldMapping Col(string field, string column) =>
        new() { JsonField = field, Source = MappingSource.Column, Value = column };
}
