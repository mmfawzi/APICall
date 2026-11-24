using ApiScheduler.Core;
using ApiScheduler.Core.Http;
using ApiScheduler.Core.Json;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Scheduling;
using ApiScheduler.Core.Security;
using ApiScheduler.Core.Sql;

namespace ApiScheduler.Tests;

public class SqlSafetyTests
{
    [Theory]
    [InlineData("select * from NG_MC_SURVEY_SMS_LOG")]
    [InlineData("SELECT TOP 10 a.* FROM dbo.T a WHERE a.SENT_DATE IS NULL ORDER BY 1;")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x")]
    [InlineData("SELECT * FROM T -- DROP TABLE T")]
    public void Accepts_single_select(string sql) => Assert.Empty(SqlSafety.ValidateSelect(sql));

    [Theory]
    [InlineData("DELETE FROM T")]
    [InlineData("UPDATE T SET a = 1")]
    [InlineData("SELECT * FROM T; DROP TABLE T")]
    [InlineData("SELECT * FROM T\nGO\nSELECT 1")]
    [InlineData("SELECT * INTO NewT FROM T")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("SELECT * FROM OPENQUERY(LinkedSrv, 'DELETE FROM T')")]
    [InlineData("")]
    public void Rejects_anything_else(string sql) => Assert.NotEmpty(SqlSafety.ValidateSelect(sql));

    [Theory]
    [InlineData("dbo.NG_MC_SURVEY_SMS_LOG", "[dbo].[NG_MC_SURVEY_SMS_LOG]")]
    [InlineData("[SENT_DATE]", "[SENT_DATE]")]
    public void Quotes_identifiers(string input, string expected) => Assert.Equal(expected, SqlSafety.QuoteIdentifier(input));

    [Theory]
    [InlineData("T]; DROP TABLE x;--")]
    [InlineData("a b")]
    [InlineData("a.b.c.d")]
    public void Rejects_bad_identifiers(string input) => Assert.False(SqlSafety.IsValidIdentifier(input));
}

public class JsonBodyBuilderTests
{
    [Fact]
    public void Builds_survey_body_from_row()
    {
        var job = JobTemplates.SurveySms(Guid.NewGuid());
        var row = new DataRowValues
        {
            ["TRIGGER_NAME"] = "Immediately after submission", ["POLICY_HOLDER_NAME"] = "أحمد محمد", ["POLICY_HOLDER_ID"] = "101002456789",
            ["MEMBER_NAME"] = "Sara", ["POLICY_NO"] = "POL-1", ["MOBILE_NO"] = "0552276159", ["CLAIM_NO"] = null, ["EMAIL"] = "a@b.c",
            ["GENDER"] = "Male", ["CLIENT_TYPE"] = "Comprehensive", ["SEGMENT"] = "Individual", ["CITY"] = "Riyadh",
            ["COMM_LANGUAGE"] = "English", ["BROKER_NAME"] = "Broker", ["REPAIR_FACILITY"] = "Center",
        };
        var json = JsonBodyBuilder.Serialize(JsonBodyBuilder.Build(row, job.Mappings));

        Assert.StartsWith("{\"serviceName\":1,\"SurveyType\":\"CSAT\",\"channel\":\"SMS\",\"trigger\":\"Immediately after submission\"", json);
        Assert.Contains("\"policyHolderName\":\"أحمد محمد\"", json);   // Arabic kept readable, not \u-escaped
        Assert.Contains("\"claimNo\":null", json);
        Assert.Contains("\"language\":\"English\"", json);
    }

    [Fact]
    public void Missing_column_is_an_error()
    {
        var maps = new[] { new FieldMapping { JsonField = "x", Source = MappingSource.Column, Value = "NOPE" } };
        Assert.Throws<KeyNotFoundException>(() => JsonBodyBuilder.Build(new DataRowValues(), maps));
    }

    [Fact]
    public void Null_default_is_applied()
    {
        var maps = new[] { new FieldMapping { JsonField = "x", Source = MappingSource.Column, Value = "A", DefaultIfNull = "N/A" } };
        Assert.Equal("{\"x\":\"N/A\"}", JsonBodyBuilder.Serialize(JsonBodyBuilder.Build(new DataRowValues { ["A"] = null }, maps)));
    }
}

public class ScheduleTests
{
    [Fact]
    public void Never_run_is_due_now()
    {
        var job = new ApiJob { Enabled = true, IntervalMinutes = 480 };
        var now = DateTime.UtcNow;
        Assert.True(Schedule.IsDue(job, null, now));
    }

    [Fact]
    public void Anchored_schedule_uses_fixed_slots()
    {
        var job = new ApiJob { Enabled = true, IntervalMinutes = 480, ScheduleAnchor = "00:00" };
        var last = new DateTime(2026, 11, 24, 9, 30, 0, DateTimeKind.Local).ToUniversalTime();
        var next = Schedule.NextRunUtc(job, last, last).ToLocalTime();
        Assert.Equal(new DateTime(2026, 11, 24, 16, 0, 0), next);
    }

    [Fact]
    public void Anchor_after_last_run_time_of_day()
    {
        var job = new ApiJob { Enabled = true, IntervalMinutes = 480, ScheduleAnchor = "22:00" };
        var last = new DateTime(2026, 11, 24, 7, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var next = Schedule.NextRunUtc(job, last, last).ToLocalTime();
        Assert.Equal(new DateTime(2026, 11, 24, 14, 0, 0), next); // 22:00 → 06:00 → 14:00
    }

    [Fact]
    public void Unanchored_is_interval_after_last()
    {
        var job = new ApiJob { Enabled = true, IntervalMinutes = 60, ScheduleAnchor = null };
        var last = DateTime.UtcNow.AddMinutes(-30);
        Assert.False(Schedule.IsDue(job, last, DateTime.UtcNow));
        Assert.True(Schedule.IsDue(job, last, DateTime.UtcNow.AddMinutes(31)));
    }

    [Fact]
    public void Disabled_is_never_due() => Assert.False(Schedule.IsDue(new ApiJob { Enabled = false }, null, DateTime.UtcNow));
}

public class SecurityTests
{
    [Fact]
    public void Secret_round_trips_and_is_not_plain_text()
    {
        const string key = "S81tc-test-key-123";
        var protectedValue = SecretProtector.Protect(key);
        Assert.DoesNotContain(key, protectedValue);
        Assert.StartsWith("dpapi:v1:", protectedValue);
        Assert.Equal(key, SecretProtector.Unprotect(protectedValue));
    }

    [Fact]
    public void Fingerprint_is_stable_and_hides_key()
    {
        var a = SecretProtector.Fingerprint("abc");
        Assert.Equal(a, SecretProtector.Fingerprint("abc"));
        Assert.NotEqual(a, SecretProtector.Fingerprint("abd"));
        Assert.DoesNotContain("abc", a);
    }

    [Theory]
    [InlineData("https://uat-proxy-internal.srv.local/survey/v1/send", true)]
    [InlineData("http://localhost:5000/x", true)]
    [InlineData("http://uat-proxy-internal.srv.local/survey/v1/send", false)]
    [InlineData("ftp://x", false)]
    [InlineData("not a url", false)]
    public void Url_must_be_https(string url, bool ok) => Assert.Equal(ok, ConfigValidator.ValidateUrl(url) is null);

    [Fact]
    public void Status_tags()
    {
        Assert.Equal("SUCCESS", new SendResult(true, 200, null, false).StatusTag("SUCCESS"));
        Assert.Equal("REJECTED-400", new SendResult(false, 400, "x", true).StatusTag("SUCCESS"));
        Assert.Equal("FAILED-503", new SendResult(false, 503, "x", false).StatusTag("SUCCESS"));
        Assert.Equal("FAILED-NETWORK", new SendResult(false, null, "x", false).StatusTag("SUCCESS"));
    }

    [Fact]
    public void Template_job_validates_once_key_and_connection_exist()
    {
        var conn = new DbConnectionProfile { Name = "c", Server = "s", Database = "d" };
        var job = JobTemplates.SurveySms(conn.Id);
        job.Enabled = true;
        job.EncryptedApiKey = SecretProtector.Protect("k");
        var errors = ConfigValidator.ValidateJob(job, new AppConfig { Connections = { conn }, Jobs = { job } });
        Assert.Empty(errors);
    }
}
