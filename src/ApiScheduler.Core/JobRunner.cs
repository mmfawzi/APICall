using System.Diagnostics;
using ApiScheduler.Core.Http;
using ApiScheduler.Core.Json;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Security;
using ApiScheduler.Core.Sql;
using Microsoft.Extensions.Logging;

namespace ApiScheduler.Core;

/// <summary>
/// Executes one job run: SELECT rows → build JSON per row → POST → mark the row (SENT_CALL / SENT_DATE).
/// Each row is marked immediately after its own POST, so a crash mid-run never re-sends finished rows.
/// Request bodies are never logged (they contain personal data); only the row key and the outcome.
/// </summary>
public sealed class JobRunner(ILogger logger)
{
    public async Task<RunRecord> RunAsync(ApiJob job, AppConfig config, string trigger, CancellationToken ct)
    {
        var record = new RunRecord { JobId = job.Id, JobName = job.Name, Trigger = trigger, StartUtc = DateTime.UtcNow };
        try
        {
            var errors = ConfigValidator.ValidateJob(job, config);
            if (string.IsNullOrEmpty(job.EncryptedApiKey)) errors.Add("API key is not set.");
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));

            var profile = config.Connections.Single(c => c.Id == job.ConnectionId);
            var apiKey = SecretProtector.Unprotect(job.EncryptedApiKey!);
            using var sender = new ApiSender(job.Url, job.ApiKeyHeaderName, apiKey, job.TimeoutSeconds, job.PinnedCertificateThumbprint);

            await using var conn = await SqlData.OpenAsync(profile, ct);
            var result = await SqlData.QueryAsync(conn, job.SelectQuery, job.MaxRowsPerRun, ct);
            record.Rows = result.Rows.Count;
            logger.LogInformation("Job {Job}: {Rows} row(s) selected.", job.Name, result.Rows.Count);

            var wb = job.WriteBack;
            if (wb.Enabled && result.Rows.Count > 0 && !result.Columns.Contains(Unbracket(wb.KeyColumn), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The query must return the key column '{wb.KeyColumn}' so rows can be marked as sent.");

            int consecutiveNetworkFailures = 0;
            foreach (var row in result.Rows)
            {
                ct.ThrowIfCancellationRequested();
                object? key = wb.Enabled ? row[Unbracket(wb.KeyColumn)] : null;

                SendResult send;
                try
                {
                    var body = JsonBodyBuilder.Serialize(JsonBodyBuilder.Build(row, job.Mappings));
                    send = await sender.PostJsonAsync(body, ct);
                }
                catch (Exception ex) when (ex is FormatException or KeyNotFoundException)
                {
                    send = new SendResult(false, null, "Mapping error: " + ex.Message, Permanent: true);
                }

                if (send.Success)
                {
                    record.Succeeded++;
                    consecutiveNetworkFailures = 0;
                }
                else
                {
                    record.Failed++;
                    logger.LogWarning("Job {Job}: row {Key} failed: {Error}", job.Name, key, send.Error);
                    consecutiveNetworkFailures = send.StatusCode is null && !send.Permanent ? consecutiveNetworkFailures + 1 : 0;
                }

                if (wb.Enabled && key is not null && (send.Success || wb.WriteFailureStatus))
                {
                    var tag = send.Error?.StartsWith("Mapping error") == true ? "REJECTED-MAPPING" : send.StatusTag(wb.SuccessValue);
                    await SqlData.MarkRowAsync(conn, wb, key, send.Success, tag, CancellationToken.None);
                }

                // API unreachable: stop early instead of hammering it; remaining rows retry next run.
                if (consecutiveNetworkFailures >= 5)
                {
                    record.Error = "Stopped after 5 consecutive network failures; remaining rows will retry next run.";
                    break;
                }
                if (job.DelayBetweenCallsMs > 0) await Task.Delay(job.DelayBetweenCallsMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            record.Error = "Cancelled (service stopping).";
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;
            logger.LogError(ex, "Job {Job} failed.", job.Name);
        }
        record.EndUtc = DateTime.UtcNow;
        logger.LogInformation("Job {Job} finished: {Ok} ok, {Failed} failed, error={Error}, {Elapsed}.",
            job.Name, record.Succeeded, record.Failed, record.Error ?? "none", record.Duration);
        return record;
    }

    private static string Unbracket(string name)
    {
        var last = name.Split('.')[^1].Trim();
        return last.StartsWith('[') && last.EndsWith(']') ? last[1..^1] : last;
    }
}
