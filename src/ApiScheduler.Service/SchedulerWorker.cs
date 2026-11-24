using System.Collections.Concurrent;
using ApiScheduler.Core;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Scheduling;
using ApiScheduler.Core.Storage;

namespace ApiScheduler.Service;

/// <summary>
/// Ticks every 15 seconds: reloads config.json when it changed, picks up "Run now" requests,
/// and starts every due job. A job never runs twice in parallel. Last-run times are persisted
/// in state.json, so a restart or reboot does not reset the schedule.
/// </summary>
public sealed class SchedulerWorker(ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly object _stateLock = new();
    private ServiceState _state = new();
    private AppConfig _config = new();
    private DateTime _configStamp = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ApiScheduler service started. Data folder: {Root}", AppPaths.Root);
        try { _state = StateStore.Load(); } catch (Exception ex) { logger.LogWarning(ex, "Could not read state; starting fresh."); }
        lock (_stateLock) foreach (var s in _state.Jobs.Values) s.IsRunning = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReloadConfigIfChanged();
                var manual = RunRequests.TakeAll().ToHashSet();
                var now = DateTime.UtcNow;

                foreach (var job in _config.Jobs)
                {
                    JobState js;
                    lock (_stateLock) js = _state.Jobs.TryGetValue(job.Id, out var existing) ? existing : _state.Jobs[job.Id] = new JobState();
                    js.NextRunUtc = job.Enabled ? Schedule.NextRunUtc(job, js.LastRunStartUtc, now) : null;

                    var isManual = manual.Contains(job.Id);
                    if (!isManual && !Schedule.IsDue(job, js.LastRunStartUtc, now)) continue;
                    if (_running.ContainsKey(job.Id)) continue;

                    StartJob(job, js, isManual ? "Manual" : "Schedule", stoppingToken);
                }

                lock (_stateLock)
                {
                    foreach (var orphan in _state.Jobs.Keys.Except(_config.Jobs.Select(j => j.Id)).ToList()) _state.Jobs.Remove(orphan);
                    _state.ServiceHeartbeatUtc = now;
                }
                SaveState();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed.");
            }

            try { await Task.Delay(Tick, stoppingToken); } catch (OperationCanceledException) { }
        }

        logger.LogInformation("Stopping: waiting for {Count} running job(s)…", _running.Count);
        await Task.WhenAll(_running.Values).WaitAsync(TimeSpan.FromSeconds(25)).ContinueWith(_ => { });
        SaveState();
    }

    private void StartJob(ApiJob job, JobState js, string trigger, CancellationToken ct)
    {
        // Snapshot config so edits made while running don't affect this run.
        var config = _config;
        lock (_stateLock)
        {
            js.IsRunning = true;
            js.LastRunStartUtc = DateTime.UtcNow;
            js.LastResult = "Running…";
        }
        SaveState();
        logger.LogInformation("Starting job {Job} ({Trigger}).", job.Name, trigger);

        var task = Task.Run(async () =>
        {
            var record = await new JobRunner(logger).RunAsync(job, config, trigger, ct);
            try { HistoryStore.Append(record); } catch (Exception ex) { logger.LogWarning(ex, "Could not write history."); }
            lock (_stateLock)
            {
                js.IsRunning = false;
                js.LastRunEndUtc = record.EndUtc;
                js.LastResult = $"{record.Status}: {record.Succeeded} sent, {record.Failed} failed" + (record.Error is null ? "" : $" — {record.Error}");
                js.NextRunUtc = Schedule.NextRunUtc(job, js.LastRunStartUtc, DateTime.UtcNow);
            }
            SaveState();
        }, CancellationToken.None);

        _running[job.Id] = task;
        task.ContinueWith(_ => _running.TryRemove(job.Id, out Task? _), TaskScheduler.Default);
    }

    private void ReloadConfigIfChanged()
    {
        var stamp = ConfigStore.LastWriteUtc();
        if (stamp == _configStamp) return;
        _config = ConfigStore.Load();
        _configStamp = stamp;
        logger.LogInformation("Configuration loaded: {Jobs} job(s), {Enabled} enabled.", _config.Jobs.Count, _config.Jobs.Count(j => j.Enabled));
    }

    private void SaveState()
    {
        try
        {
            ServiceState copy;
            lock (_stateLock)
                copy = new ServiceState
                {
                    ServiceHeartbeatUtc = _state.ServiceHeartbeatUtc,
                    Jobs = _state.Jobs.ToDictionary(kv => kv.Key, kv => new JobState
                    {
                        IsRunning = kv.Value.IsRunning, LastResult = kv.Value.LastResult, LastRunEndUtc = kv.Value.LastRunEndUtc,
                        LastRunStartUtc = kv.Value.LastRunStartUtc, NextRunUtc = kv.Value.NextRunUtc,
                    }),
                };
            StateStore.Save(copy);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save state.");
        }
    }
}
