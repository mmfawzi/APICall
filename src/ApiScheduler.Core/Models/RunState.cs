using System.Text.Json.Serialization;

namespace ApiScheduler.Core.Models;

/// <summary>Written by the service (state.json); read by the desktop app.</summary>
public sealed class ServiceState
{
    public DateTime? ServiceHeartbeatUtc { get; set; }
    public Dictionary<Guid, JobState> Jobs { get; set; } = new();
}

public sealed class JobState
{
    public DateTime? LastRunStartUtc { get; set; }
    public DateTime? LastRunEndUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public bool IsRunning { get; set; }
    public string? LastResult { get; set; }
}

/// <summary>One line of history.jsonl.</summary>
public sealed class RunRecord
{
    public Guid JobId { get; set; }
    public string JobName { get; set; } = "";
    public string Trigger { get; set; } = "Schedule";
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int Rows { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string? Error { get; set; }

    [JsonIgnore] public DateTime StartLocal => StartUtc.ToLocalTime();
    [JsonIgnore] public string Duration => (EndUtc - StartUtc).ToString(@"hh\:mm\:ss");
    [JsonIgnore] public string Status => Error != null ? "Error" : Failed > 0 ? "Partial" : "OK";
}
