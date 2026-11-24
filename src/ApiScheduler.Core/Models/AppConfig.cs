using System.Text.Json.Serialization;

namespace ApiScheduler.Core.Models;

/// <summary>Root of the local configuration file (config.json).</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<DbConnectionProfile> Connections { get; set; } = new();
    public List<ApiJob> Jobs { get; set; } = new();
}

public enum DbAuthMode { Windows, Sql }

public sealed class DbConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DbAuthMode AuthMode { get; set; } = DbAuthMode.Windows;

    public string? SqlUser { get; set; }

    /// <summary>DPAPI-encrypted SQL password (only for SQL auth). Never stored in plain text.</summary>
    public string? EncryptedSqlPassword { get; set; }

    /// <summary>Encrypt the SQL connection (TLS). Strongly recommended.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Skip SQL Server certificate validation. Only for servers with self-signed certs.</summary>
    public bool TrustServerCertificate { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"{Server}/{Database}" : Name;
}

public sealed class ApiJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New job";
    public bool Enabled { get; set; }

    // ---- HTTP ----
    public string Url { get; set; } = "";
    public string ApiKeyHeaderName { get; set; } = "apiKey";

    /// <summary>DPAPI-encrypted API key (reversible only on this machine). Never stored in plain text.</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>SHA-256 fingerprint (hash) of the API key, used only to identify which key is stored.</summary>
    public string? ApiKeyFingerprint { get; set; }

    /// <summary>
    /// Optional SHA-256 (or SHA-1) thumbprint of the API server certificate. When set, that exact
    /// certificate is accepted even if its chain is not trusted (internal CA). Validation is never disabled.
    /// </summary>
    public string? PinnedCertificateThumbprint { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
    public int DelayBetweenCallsMs { get; set; } = 100;

    // ---- Schedule ----
    public int IntervalMinutes { get; set; } = 480;

    /// <summary>Optional local time "HH:mm" that anchors the schedule (e.g. 00:00 → 00:00, 08:00, 16:00).</summary>
    public string? ScheduleAnchor { get; set; } = "00:00";

    // ---- Data ----
    public Guid ConnectionId { get; set; }
    public string SelectQuery { get; set; } = "";
    public int MaxRowsPerRun { get; set; } = 1000;
    public List<FieldMapping> Mappings { get; set; } = new();
    public WriteBackSettings WriteBack { get; set; } = new();
}

public enum MappingSource { Column, Fixed }
public enum FieldType { String, Number, Boolean }

public sealed class FieldMapping
{
    public string JsonField { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MappingSource Source { get; set; } = MappingSource.Column;

    /// <summary>Column name (Source=Column) or the literal value (Source=Fixed).</summary>
    public string Value { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>Used when the column value is NULL. Empty = send JSON null.</summary>
    public string? DefaultIfNull { get; set; }
}

/// <summary>How a row is marked after its POST so it is not sent twice.</summary>
public sealed class WriteBackSettings
{
    public bool Enabled { get; set; } = true;
    public string TableName { get; set; } = "dbo.NG_MC_SURVEY_SMS_LOG";
    public string KeyColumn { get; set; } = "INSERTIONORDERID";
    public string SentCallColumn { get; set; } = "SENT_CALL";
    public string SentDateColumn { get; set; } = "SENT_DATE";
    public string SuccessValue { get; set; } = "SUCCESS";

    /// <summary>Write FAILED-xxx / REJECTED-xxx into SentCallColumn on failure (SentDate stays NULL so it retries).</summary>
    public bool WriteFailureStatus { get; set; } = true;
}
