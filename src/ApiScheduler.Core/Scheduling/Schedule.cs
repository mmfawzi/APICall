using System.Globalization;
using ApiScheduler.Core.Models;

namespace ApiScheduler.Core.Scheduling;

public static class Schedule
{
    /// <summary>
    /// Next run time in UTC. With an anchor (e.g. "00:00" and 480 min) the slots are 00:00, 08:00, 16:00 local time;
    /// without one, it is simply last run + interval. A job that never ran is due immediately.
    /// </summary>
    public static DateTime NextRunUtc(ApiJob job, DateTime? lastRunUtc, DateTime nowUtc)
    {
        if (lastRunUtc is null) return nowUtc;
        var interval = TimeSpan.FromMinutes(Math.Max(5, job.IntervalMinutes));

        if (string.IsNullOrWhiteSpace(job.ScheduleAnchor) ||
            !TimeOnly.TryParseExact(job.ScheduleAnchor, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor))
            return lastRunUtc.Value + interval;

        var lastLocal = lastRunUtc.Value.ToLocalTime();
        var anchorLocal = lastLocal.Date.Add(anchor.ToTimeSpan());
        if (anchorLocal > lastLocal) anchorLocal = anchorLocal.AddDays(-1);

        // first slot strictly after the last run
        var steps = Math.Floor((lastLocal - anchorLocal) / interval) + 1;
        var nextLocal = anchorLocal + interval * steps;
        return DateTime.SpecifyKind(nextLocal, DateTimeKind.Local).ToUniversalTime();
    }

    public static bool IsDue(ApiJob job, DateTime? lastRunUtc, DateTime nowUtc) =>
        job.Enabled && NextRunUtc(job, lastRunUtc, nowUtc) <= nowUtc;
}
