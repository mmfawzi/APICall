using System.Globalization;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Sql;

namespace ApiScheduler.Core;

public static class ConfigValidator
{
    public static List<string> Validate(AppConfig config)
    {
        var errors = new List<string>();
        foreach (var c in config.Connections)
        {
            if (string.IsNullOrWhiteSpace(c.Server) || string.IsNullOrWhiteSpace(c.Database))
                errors.Add($"Connection '{c}': server and database are required.");
            if (c.AuthMode == DbAuthMode.Sql && (string.IsNullOrWhiteSpace(c.SqlUser) || string.IsNullOrEmpty(c.EncryptedSqlPassword)))
                errors.Add($"Connection '{c}': SQL authentication needs a user and password.");
        }
        foreach (var job in config.Jobs)
            errors.AddRange(ValidateJob(job, config).Select(e => $"Job '{job.Name}': {e}"));
        return errors;
    }

    public static List<string> ValidateJob(ApiJob job, AppConfig config)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(job.Name)) errors.Add("Name is required.");

        var urlError = ValidateUrl(job.Url);
        if (urlError != null) errors.Add(urlError);

        if (string.IsNullOrWhiteSpace(job.ApiKeyHeaderName) || job.ApiKeyHeaderName.Any(ch => ch <= ' ' || ch == ':'))
            errors.Add("API key header name is invalid.");
        if (job.Enabled && string.IsNullOrEmpty(job.EncryptedApiKey))
            errors.Add("An API key must be set before enabling the job.");

        if (job.IntervalMinutes < 5) errors.Add("Interval must be at least 5 minutes.");
        if (!string.IsNullOrWhiteSpace(job.ScheduleAnchor) &&
            !TimeOnly.TryParseExact(job.ScheduleAnchor, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add("Schedule anchor must be HH:mm (24h).");
        if (job.TimeoutSeconds is < 1 or > 600) errors.Add("Timeout must be 1–600 seconds.");
        if (job.MaxRowsPerRun is < 1 or > 100_000) errors.Add("Max rows per run must be 1–100000.");
        if (job.DelayBetweenCallsMs is < 0 or > 60_000) errors.Add("Delay between calls must be 0–60000 ms.");

        if (config.Connections.All(c => c.Id != job.ConnectionId)) errors.Add("Select a database connection.");
        errors.AddRange(SqlSafety.ValidateSelect(job.SelectQuery).Select(e => "Query: " + e));

        if (job.Mappings.Count == 0) errors.Add("At least one field mapping is required.");
        var dupes = job.Mappings.GroupBy(m => m.JsonField, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var d in dupes) errors.Add($"Duplicate JSON field '{d}'.");
        foreach (var m in job.Mappings)
        {
            if (string.IsNullOrWhiteSpace(m.JsonField)) errors.Add("A mapping has an empty JSON field name.");
            if (m.Source == MappingSource.Column && string.IsNullOrWhiteSpace(m.Value)) errors.Add($"Mapping '{m.JsonField}' has no column.");
            if (m.Source == MappingSource.Fixed && m.Type == FieldType.Number && !decimal.TryParse(m.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                errors.Add($"Mapping '{m.JsonField}': '{m.Value}' is not a number.");
            if (m.Source == MappingSource.Fixed && m.Type == FieldType.Boolean && !bool.TryParse(m.Value, out _))
                errors.Add($"Mapping '{m.JsonField}': '{m.Value}' is not true/false.");
        }

        var wb = job.WriteBack;
        if (wb.Enabled)
        {
            foreach (var (label, id) in new[] { ("Table", wb.TableName), ("Key column", wb.KeyColumn), ("Sent-call column", wb.SentCallColumn), ("Sent-date column", wb.SentDateColumn) })
                if (!SqlSafety.IsValidIdentifier(id)) errors.Add($"Write-back: {label} '{id}' is not a valid SQL identifier.");
            if (string.IsNullOrWhiteSpace(wb.SuccessValue) || wb.SuccessValue.Length > 50) errors.Add("Write-back: success value must be 1–50 characters.");
        }
        return errors;
    }

    /// <summary>HTTPS is mandatory (the API key travels in a header). Plain HTTP only for localhost testing.</summary>
    public static string? ValidateUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL is not a valid absolute URL.";
        if (uri.Scheme == Uri.UriSchemeHttps) return null;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return null;
        return "URL must use HTTPS.";
    }
}
